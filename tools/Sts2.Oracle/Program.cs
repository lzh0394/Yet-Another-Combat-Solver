using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sts2.Oracle;

/// <summary>
/// Reflects over the shipped sts2.dll and dumps numeric ground truth.
///
/// Only "value + pure function" paths are touched: model constructors, CanonicalVars,
/// MonsterModel.GenerateMoveStateMachine (pure data), RNG. Anything that needs a live
/// run/combat/Player is out of scope; see NOTE.md §1.
///
/// Each probe runs in its own process (--probe X) so that a native crash cannot take
/// down the whole batch (--probe all spawns children).
/// </summary>
public static class Program
{
    private const string DefaultGameDir =
        @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2";

    private static readonly string[] Probes =
    {
        "meta", "cards", "monsters", "powers", "relics", "potions",
        "encounters", "characters", "acts", "rng",
    };

    private static Assembly _asm;
    private static string _gameDir;
    private static readonly List<JsonObject> _errors = new();
    private static List<object> _models;

    public static int Main(string[] args)
    {
        var opts = ParseArgs(args);
        string probe = opts.GetValueOrDefault("probe", "all");
        if (!opts.ContainsKey("out"))
        {
            Console.Error.WriteLine("--out <path> is required");
            return 2;
        }

        if (probe == "all")
        {
            return RunAll(opts);
        }

        if (!Probes.Contains(probe))
        {
            Console.Error.WriteLine($"unknown probe: {probe}");
            return 2;
        }

        _gameDir = opts.GetValueOrDefault("game-dir", DefaultGameDir);
        string dll = opts.GetValueOrDefault("dll", Path.Combine(_gameDir, "data_sts2_windows_x86_64", "sts2.dll"));
        if (!File.Exists(dll))
        {
            Console.Error.WriteLine($"sts2.dll not found: {dll}");
            return 3;
        }

        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromGameDir;
        _asm = Assembly.LoadFrom(dll);
        RegisterModels(dll);

        JsonNode data = probe switch
        {
            "meta" => Meta(dll),
            "cards" => Group("CARD", CardEntry),
            "monsters" => Group("MONSTER", MonsterEntry),
            "powers" => Group("POWER", PowerEntry),
            "relics" => Group("RELIC", RelicEntry),
            "potions" => Group("POTION", PotionEntry),
            "encounters" => Group("ENCOUNTER", EncounterEntry),
            "characters" => Group("CHARACTER", CharacterEntry),
            "acts" => Group("ACT", ActEntry),
            "rng" => RngProbe(),
            _ => throw new InvalidOperationException(probe),
        };

        var root = new JsonObject
        {
            ["probe"] = probe,
            ["data"] = data,
            ["errors"] = ErrorsJson(_errors),
        };
        WriteJson(opts["out"], root);
        return 0;
    }

    // ------------------------------------------------------------------ driver

    private static int RunAll(Dictionary<string, string> opts)
    {
        string outPath = Path.GetFullPath(opts["out"]);
        string dir = Path.GetDirectoryName(outPath)!;
        Directory.CreateDirectory(dir);
        var merged = new JsonObject();
        var allErrors = new JsonArray();

        foreach (string probe in Probes)
        {
            string partPath = Path.Combine(dir, $".probe-{probe}.json");
            if (File.Exists(partPath)) File.Delete(partPath);

            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            string processPath = Environment.ProcessPath ?? "dotnet";
            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = processPath;
                psi.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            }
            else
            {
                psi.FileName = processPath;
            }
            if (opts.TryGetValue("game-dir", out string gameDir))
            {
                psi.ArgumentList.Add("--game-dir");
                psi.ArgumentList.Add(gameDir);
            }
            psi.ArgumentList.Add("--probe");
            psi.ArgumentList.Add(probe);
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add(partPath);

            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0 || !File.Exists(partPath))
            {
                Console.Error.WriteLine($"[{probe}] FAILED exit={proc.ExitCode} {stderr.Trim()}");
                allErrors.Add(new JsonObject
                {
                    ["probe"] = probe,
                    ["error"] = $"exit {proc.ExitCode}: {stderr.Trim()} {stdout.Trim()}",
                });
                continue;
            }

            JsonNode part = JsonNode.Parse(File.ReadAllText(partPath))!;
            merged[probe] = part["data"]!.DeepClone();
            if (part["errors"] is JsonArray errs)
            {
                foreach (JsonNode e in errs)
                {
                    var tagged = (JsonObject)e!.DeepClone();
                    tagged["probe"] = probe;
                    allErrors.Add(tagged);
                }
            }
            File.Delete(partPath);
            int count = part["data"] is JsonObject o ? o.Count : 0;
            Console.WriteLine($"[{probe}] ok ({count} entries, {stderr.Trim().Length} stderr chars)");
        }

        merged["errors"] = allErrors;
        WriteJson(outPath, merged);
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    // ------------------------------------------------------------------- probes

    private static JsonNode Meta(string dll)
    {
        var game = new JsonObject();
        string releasePath = Path.Combine(_gameDir, "release_info.json");
        if (File.Exists(releasePath))
        {
            JsonNode release = JsonNode.Parse(File.ReadAllText(releasePath))!;
            game["version"] = release["version"]?.DeepClone();
            game["commit"] = release["commit"]?.DeepClone();
            game["main_assembly_hash"] = release["main_assembly_hash"]?.DeepClone();
        }
        var info = new FileInfo(dll);
        game["dll_name"] = info.Name;
        game["dll_size"] = info.Length;
        game["dll_sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))).ToLowerInvariant();
        game["assembly_version"] = _asm.GetName().Version?.ToString();

        var counts = new JsonObject();
        var mockCounts = new JsonObject();
        var categories = _models
            .Select(m => EntryOf(IdOf(m)).Category)
            .GroupBy(c => c)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var g in categories)
        {
            counts[g.Key] = g.Count();
            mockCounts[g.Key] = _models.Count(m => IdOf(m).StartsWith(g.Key + ".") && IsMock(m));
        }

        var byBase = new JsonObject();
        foreach (string baseName in new[]
                 {
                     "CardModel", "MonsterModel", "PowerModel", "RelicModel", "PotionModel",
                     "EncounterModel", "CharacterModel", "ActModel",
                 })
        {
            Type? t = _asm.GetType("MegaCrit.Sts2.Core.Models." + baseName);
            if (t == null) continue;
            byBase[baseName] = _models.Count(m => t.IsInstanceOfType(m));
        }

        return new JsonObject
        {
            ["game"] = game,
            ["model_count"] = _models.Count,
            ["counts"] = counts,
            ["mock_counts"] = mockCounts,
            ["by_base"] = byBase,
        };
    }

    private static JsonNode Group(string category, Func<object, string, JsonNode> make)
    {
        var obj = new JsonObject();
        foreach (object model in _models
                     .Where(m => IdOf(m).StartsWith(category + ".", StringComparison.Ordinal))
                     .OrderBy(IdOf, StringComparer.Ordinal))
        {
            string id = IdOf(model);
            try
            {
                obj[id] = make(model, id);
            }
            catch (Exception e)
            {
                _errors.Add(Error(id, e));
                obj[id] = new JsonObject { ["id"] = id, ["error"] = Describe(e) };
            }
        }
        return obj;
    }

    private static JsonNode CardEntry(object card, string id)
    {
        var o = new JsonObject
        {
            ["id"] = id,
            ["name"] = card.GetType().Name,
            ["type"] = Str(Get(card, "Type")),
            ["rarity"] = Str(Get(card, "Rarity")),
            ["target"] = Str(Get(card, "TargetType")),
            ["is_mock"] = (bool)Get(card, "IsMock"),
        };

        object ec = Get(card, "EnergyCost");
        o["cost"] = ToInt(Get(ec, "Canonical"));
        o["costs_x"] = (bool)Get(ec, "CostsX");
        o["cost_resolved"] = ToInt(Call(ec, "GetResolved"));
        o["star_cost"] = ToInt(SafeGet(card, "CanonicalStarCost") ?? 0);
        o["keywords"] = KeywordsJson(card);
        o["vars"] = VarsJson(Get(card, "DynamicVars"));
        int maxUpgrade = ToInt(SafeGet(card, "MaxUpgradeLevel") ?? 0);
        o["max_upgrade_level"] = maxUpgrade;

        if (!(bool)Get(card, "IsMock") && maxUpgrade > 0)
        {
            try
            {
                object clone = Call(card, "ToMutable");
                Call(clone, "UpgradeInternal");
                object cec = Get(clone, "EnergyCost");
                o["upgraded"] = new JsonObject
                {
                    ["cost"] = ToInt(Get(cec, "Canonical")),
                    ["costs_x"] = (bool)Get(cec, "CostsX"),
                    ["cost_resolved"] = ToInt(Call(cec, "GetResolved")),
                    ["star_cost"] = ToInt(SafeGet(clone, "CanonicalStarCost") ?? 0),
                    ["keywords"] = KeywordsJson(clone),
                    ["vars"] = VarsJson(Get(clone, "DynamicVars")),
                };
            }
            catch (Exception e)
            {
                _errors.Add(Error(id + ".upgraded", e));
                o["upgrade_error"] = Describe(e);
            }
        }

        return o;
    }

    private static JsonNode KeywordsJson(object card)
    {
        var arr = new JsonArray();
        if (Get(card, "Keywords") is IEnumerable keywords)
        {
            foreach (object k in keywords) arr.Add(Str(k));
        }
        var sorted = new JsonArray();
        foreach (string s in arr.Select(n => n!.GetValue<string>()).OrderBy(s => s, StringComparer.Ordinal))
        {
            sorted.Add(s);
        }
        return sorted;
    }

    private static JsonNode VarsJson(object varSet)
    {
        var o = new JsonObject();
        if (varSet is not IEnumerable items) return o;
        var entries = new List<(string Name, string Type, decimal Base)>();
        foreach (object item in items)
        {
            object v = Get(item, "Value");
            entries.Add((Str(Get(v, "Name")), v.GetType().Name, (decimal)Get(v, "BaseValue")));
        }
        foreach (var e in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            o[e.Name] = new JsonObject { ["type"] = e.Type, ["base"] = JsonValue.Create(e.Base) };
        }
        return o;
    }

    private static JsonNode MonsterEntry(object monster, string id)
    {
        var o = new JsonObject
        {
            ["id"] = id,
            ["name"] = monster.GetType().Name,
            ["min_hp"] = ToInt(Get(monster, "MinInitialHp")),
            ["max_hp"] = ToInt(Get(monster, "MaxInitialHp")),
            ["is_mock"] = (bool)Get(monster, "IsMock"),
        };

        try
        {
            object machine;
            try
            {
                machine = Call(monster, "GenerateMoveStateMachine");
            }
            catch (Exception e) when (Describe(e).Contains("CanonicalModelException"))
            {
                // Some monsters assign move states through property setters that call
                // AssertMutable(); a mutable clone works and needs no run state.
                object clone = Call(monster, "ToMutable");
                machine = Call(clone, "GenerateMoveStateMachine");
                o["moves_source"] = "mutable";
            }
            o["initial_state"] = Str(GetField(machine, "_initialState") is { } st ? Get(st, "Id") : null);
            var states = new JsonObject();
            if (Get(machine, "States") is IDictionary dict)
            {
                foreach (DictionaryEntry de in dict)
                {
                    states[(string)de.Key] = StateJson(de.Value);
                }
            }
            o["moves"] = states;
        }
        catch (Exception e)
        {
            _errors.Add(Error(id + ".moves", e));
            o["moves_error"] = Describe(e);
        }

        return o;
    }

    private static JsonNode StateJson(object state)
    {
        string kind = state.GetType().Name;
        var o = new JsonObject { ["kind"] = kind };

        if (kind == "MoveState")
        {
            var intents = new JsonArray();
            if (Get(state, "Intents") is IEnumerable list)
            {
                foreach (object intent in list) intents.Add(IntentJson(intent));
            }
            o["intents"] = intents;
            object followUp = SafeGet(state, "FollowUpStateId");
            if (followUp == null && SafeGet(state, "FollowUpState") is { } fs)
            {
                followUp = SafeGet(fs, "Id");
            }
            o["follow_up"] = followUp == null ? null : Str(followUp);
            o["must_perform_once"] = (bool)(SafeGet(state, "MustPerformOnceBeforeTransitioning") ?? false);
        }
        else if (kind == "ConditionalBranchState")
        {
            o["branches"] = BranchIds(state, "States", "id");
        }
        else if (kind == "RandomBranchState")
        {
            o["branches"] = BranchIds(state, "States", "stateId");
        }

        return o;
    }

    private static JsonArray BranchIds(object state, string memberName, string idField)
    {
        var arr = new JsonArray();
        object list = SafeGet(state, memberName) ?? GetField(state, memberName);
        if (list is IEnumerable items)
        {
            foreach (object item in items)
            {
                if (item == null) continue;
                arr.Add(Str(SafeGet(item, idField) ?? GetField(item, idField)));
            }
        }
        return arr;
    }

    private static JsonNode IntentJson(object intent)
    {
        var o = new JsonObject { ["type"] = Str(Get(intent, "IntentType")) };
        if (HasProp(intent.GetType(), "DamageCalc"))
        {
            try
            {
                object calc = Get(intent, "DamageCalc");
                if (calc != null)
                {
                    o["damage"] = N(CallDelegate(calc));
                }
                o["repeats"] = ToInt(SafeGet(intent, "Repeats") ?? 1);
            }
            catch (Exception e)
            {
                o["damage_error"] = Describe(e);
            }
        }
        if (o["type"]?.GetValue<string>() == "StatusCard")
        {
            o["cards"] = ToInt(SafeGet(intent, "CardCount") ?? 0);
        }
        return o;
    }

    private static JsonNode PowerEntry(object power, string id)
    {
        var o = new JsonObject
        {
            ["id"] = id,
            ["name"] = power.GetType().Name,
            ["type"] = Str(Get(power, "Type")),
            ["stack_type"] = Str(Get(power, "StackType")),
            ["is_mock"] = (bool)Get(power, "IsMock"),
        };
        try
        {
            o["vars"] = VarsJson(Get(power, "DynamicVars"));
        }
        catch (Exception e)
        {
            _errors.Add(Error(id + ".vars", e));
            o["vars_error"] = Describe(e);
        }
        return o;
    }

    private static JsonNode RelicEntry(object relic, string id)
    {
        var o = new JsonObject
        {
            ["id"] = id,
            ["name"] = relic.GetType().Name,
            ["rarity"] = Str(Get(relic, "Rarity")),
            ["is_mock"] = (bool)Get(relic, "IsMock"),
        };
        try
        {
            o["vars"] = VarsJson(Get(relic, "DynamicVars"));
        }
        catch (Exception e)
        {
            _errors.Add(Error(id + ".vars", e));
            o["vars_error"] = Describe(e);
        }
        object cost = SafeGet(relic, "MerchantCost");
        if (cost != null) o["merchant_cost"] = ToInt(cost);
        return o;
    }

    private static JsonNode PotionEntry(object potion, string id)
    {
        var o = new JsonObject
        {
            ["id"] = id,
            ["name"] = potion.GetType().Name,
            ["rarity"] = Str(Get(potion, "Rarity")),
            ["target"] = Str(Get(potion, "TargetType")),
            ["is_mock"] = (bool)Get(potion, "IsMock"),
        };
        try
        {
            o["vars"] = VarsJson(Get(potion, "DynamicVars"));
        }
        catch (Exception e)
        {
            _errors.Add(Error(id + ".vars", e));
            o["vars_error"] = Describe(e);
        }
        return o;
    }

    private static JsonNode EncounterEntry(object encounter, string id)
    {
        var o = new JsonObject
        {
            ["id"] = id,
            ["name"] = encounter.GetType().Name,
            ["room_type"] = Str(Get(encounter, "RoomType")),
            ["is_weak"] = (bool)Get(encounter, "IsWeak"),
            ["is_mock"] = (bool)Get(encounter, "IsMock"),
        };

        try
        {
            var all = new JsonArray();
            if (Get(encounter, "AllPossibleMonsters") is IEnumerable possible)
            {
                foreach (object m in possible) all.Add(IdOf(m));
            }
            o["all_possible_monsters"] = all;
        }
        catch (Exception e)
        {
            _errors.Add(Error(id + ".all_possible_monsters", e));
        }

        try
        {
            var arr = new JsonArray();
            if (Call(encounter, "GenerateMonsters") is IEnumerable generated)
            {
                foreach (object item in generated)
                {
                    object m = GetField(item, "Item1");
                    object slot = GetField(item, "Item2");
                    arr.Add(new JsonArray { IdOf(m), slot == null ? null : JsonValue.Create((string)slot) });
                }
            }
            o["monsters"] = arr;
        }
        catch (Exception e)
        {
            _errors.Add(Error(id + ".monsters", e));
            o["monsters_error"] = Describe(e);
        }

        return o;
    }

    private static JsonNode CharacterEntry(object character, string id)
    {
        var o = new JsonObject
        {
            ["id"] = id,
            ["name"] = character.GetType().Name,
            ["starting_hp"] = ToInt(Get(character, "StartingHp")),
            ["starting_gold"] = ToInt(Get(character, "StartingGold")),
            ["max_energy"] = ToInt(Get(character, "MaxEnergy")),
            ["is_mock"] = (bool)Get(character, "IsMock"),
        };

        try
        {
            var deck = new JsonArray();
            if (Get(character, "StartingDeck") is IEnumerable cards)
            {
                foreach (object c in cards) deck.Add(IdOf(c));
            }
            o["starting_deck"] = deck;
        }
        catch (Exception e)
        {
            _errors.Add(Error(id + ".starting_deck", e));
        }

        try
        {
            var relics = new JsonArray();
            if (Get(character, "StartingRelics") is IEnumerable rs)
            {
                foreach (object r in rs) relics.Add(IdOf(r));
            }
            o["starting_relics"] = relics;
        }
        catch (Exception e)
        {
            _errors.Add(Error(id + ".starting_relics", e));
        }

        object pool = SafeGet(character, "CardPool");
        if (pool != null) o["card_pool"] = IdOf(pool);
        return o;
    }

    private static JsonNode ActEntry(object act, string id)
    {
        var o = new JsonObject
        {
            ["id"] = id,
            ["name"] = act.GetType().Name,
            ["index"] = ToInt(Get(act, "Index")),
            ["is_default"] = (bool)Get(act, "IsDefault"),
            ["is_mock"] = (bool)Get(act, "IsMock"),
        };
        object rooms = SafeGet(act, "Rooms");
        o["has_rooms"] = rooms != null;
        return o;
    }

    private static JsonNode RngProbe()
    {
        Type mrType = _asm.GetType("MegaCrit.Sts2.Core.Random.MegaRandom")
                      ?? throw new InvalidOperationException("MegaRandom not found");
        Type rngType = _asm.GetType("MegaCrit.Sts2.Core.Random.Rng")
                       ?? throw new InvalidOperationException("Rng not found");

        var seeds = new ulong[] { 0, 1, 42, 12345, 0xDEADBEEF, ulong.MaxValue };
        var mega = new JsonObject();
        foreach (ulong seed in seeds)
        {
            object mr = Activator.CreateInstance(mrType, new object[] { seed })!;
            var seq = new JsonObject
            {
                ["next_int"] = ArrayOf(mr, "NextInt", 8),
                ["next_100"] = ArrayOf(mr, "Next", 8, 100),
                ["next_double"] = ArrayOf(mr, "NextDouble", 4),
                ["next_float"] = ArrayOf(mr, "NextFloat", 4),
                ["next_ulong"] = ArrayOf(mr, "NextULong", 4),
                ["next_bool"] = ArrayOf(mr, "NextBool", 8),
            };
            mega[seed.ToString(CultureInfo.InvariantCulture)] = seq;
        }

        var wrapper = new JsonObject();
        foreach (ulong seed in new ulong[] { 0, 7, 42 })
        {
            object rng = Activator.CreateInstance(rngType, new object[] { seed })!;
            var seq = new JsonObject
            {
                ["next_int_20"] = ArrayOf(rng, "NextInt", 10, 20),
                ["next_bool"] = ArrayOf(rng, "NextBool", 10),
            };

            MethodInfo shuffle = rngType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "Shuffle" && m.IsGenericMethodDefinition);
            var orders = new JsonArray();
            for (int k = 0; k < 3; k++)
            {
                var list = new List<int>(Enumerable.Range(0, 10));
                shuffle.MakeGenericMethod(typeof(int)).Invoke(rng, new object[] { list });
                orders.Add(new JsonArray(list.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()));
            }
            seq["shuffle_0_9"] = orders;
            wrapper[seed.ToString(CultureInfo.InvariantCulture)] = seq;
        }

        return new JsonObject { ["MegaRandom"] = mega, ["Rng"] = wrapper };
    }

    private static JsonArray ArrayOf(object target, string method, int count, params object[] extra)
    {
        MethodInfo mi = FindMethod(target.GetType(), method, extra.Length);
        var arr = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            arr.Add(N(mi.Invoke(target, extra)));
        }
        return arr;
    }

    // ------------------------------------------------------------------- model db

    private static void RegisterModels(string dll)
    {
        Type subtypes = _asm.GetType("MegaCrit.Sts2.Core.Models.AbstractModelSubtypes")
                        ?? throw new InvalidOperationException("AbstractModelSubtypes not found");
        var all = (IEnumerable)subtypes.GetProperty("All", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        Type modelDb = _asm.GetType("MegaCrit.Sts2.Core.Models.ModelDb")!;
        MethodInfo inject = modelDb.GetMethod("Inject", BindingFlags.Public | BindingFlags.Static)!;

        var types = all.Cast<Type>().ToArray();
        var sw = Stopwatch.StartNew();
        int failed = 0;
        foreach (Type t in types)
        {
            try
            {
                inject.Invoke(null, new object[] { t });
            }
            catch (Exception e)
            {
                failed++;
                _errors.Add(Error(t.FullName ?? t.Name, e));
            }
        }
        sw.Stop();

        var loaded = (IEnumerable)modelDb.GetProperty("All", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        _models = loaded.Cast<object>().ToList();
        Console.Error.WriteLine(
            $"registered {_models.Count}/{types.Length} models in {sw.ElapsedMilliseconds} ms ({failed} failures)");
    }

    private static IEnumerable<object> AllModels() => _models;

    private static bool IsMock(object model) => (bool)Get(model, "IsMock");

    private static string IdOf(object model)
    {
        object id = Get(model, "Id");
        return $"{Get(id, "Category")}.{Get(id, "Entry")}";
    }

    private static (string Category, string Entry) EntryOf(string id)
    {
        int dot = id.IndexOf('.');
        return dot < 0 ? (id, "") : (id[..dot], id[(dot + 1)..]);
    }

    // ------------------------------------------------------------------ reflection

    private static PropertyInfo FindProp(Type type, string name)
    {
        for (Type? t = type; t != null; t = t.BaseType)
        {
            PropertyInfo? p = t.GetProperty(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (p != null) return p;
        }
        throw new MissingMemberException(type.FullName, name);
    }

    private static bool HasProp(Type type, string name)
    {
        for (Type? t = type; t != null; t = t.BaseType)
        {
            if (t.GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) != null)
            {
                return true;
            }
        }
        return false;
    }

    private static object? Get(object target, string name) => FindProp(target.GetType(), name).GetValue(target);

    private static object? SafeGet(object target, string name)
    {
        try
        {
            return Get(target, name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object? Call(object target, string name, params object?[] args)
    {
        MethodInfo m = FindMethod(target.GetType(), name, args.Length);
        return m.Invoke(target, args);
    }

    private static MethodInfo FindMethod(Type type, string name, int argc)
    {
        for (Type? t = type; t != null; t = t.BaseType)
        {
            foreach (MethodInfo m in t.GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                         BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.Name == name && m.GetParameters().Length == argc) return m;
            }
        }
        throw new MissingMethodException(type.FullName, name);
    }

    private static object? GetField(object target, string name)
    {
        for (Type? t = target.GetType(); t != null; t = t.BaseType)
        {
            FieldInfo? f = t.GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (f != null) return f.GetValue(target);
        }
        return null;
    }

    private static object? CallDelegate(object del) => del.GetType().GetMethod("Invoke")!.Invoke(del, null);

    // --------------------------------------------------------------------- output

    private static JsonNode? N(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        decimal d => JsonValue.Create(d),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        int i => JsonValue.Create(i),
        uint u => JsonValue.Create(u),
        long l => JsonValue.Create(l),
        ulong ul => JsonValue.Create(ul),
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        Enum e => JsonValue.Create(e.ToString()),
        _ => JsonValue.Create(value.ToString()),
    };

    private static string Str(object? value) => value switch
    {
        null => "",
        Enum e => e.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static int ToInt(object? value) => value switch
    {
        null => 0,
        int i => i,
        decimal d => (int)d,
        long l => (int)l,
        uint u => (int)u,
        _ => Convert.ToInt32(value, CultureInfo.InvariantCulture),
    };

    private static JsonObject Error(string scope, Exception e) =>
        new() { ["scope"] = scope, ["error"] = Describe(e) };

    private static JsonArray ErrorsJson(List<JsonObject> errors)
    {
        var arr = new JsonArray();
        foreach (JsonObject e in errors) arr.Add(e.DeepClone());
        return arr;
    }

    private static string Describe(Exception e)
    {
        while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
        return e.GetType().Name + ": " + e.Message;
    }

    private static void WriteJson(string path, JsonNode node)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        string tmp = full + ".tmp";
        File.WriteAllText(tmp, node.ToJsonString(opts));
        File.Move(tmp, full, true);
    }

    private static Assembly? ResolveFromGameDir(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name);
        foreach (string dir in new[]
                 {
                     Path.Combine(_gameDir, "data_sts2_windows_x86_64"),
                     _gameDir,
                 })
        {
            string candidate = Path.Combine(dir, name.Name + ".dll");
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
        }
        return null;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                d[args[i][2..]] = args[++i];
            }
        }
        return d;
    }
}
