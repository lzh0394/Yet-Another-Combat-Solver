using System.Text.Json;
using System.Text.Json.Nodes;
using Sts2.Contracts;
using Sts2.Engine.Data;
using Sts2.Engine.Model;
using Sts2.Engine.Random;
using Sts2.Engine.Rules;
using Snapshots = Sts2.Engine.Combat.Snapshots;
using Scenario = Sts2.Engine.Combat.Scenario;
using ScenarioAction = Sts2.Engine.Combat.ScenarioAction;
using CombatState = Sts2.Engine.Combat.Combat;
using CombatException = Sts2.Engine.Combat.CombatException;

namespace Sts2.Engine.Tests;

/// <summary>
/// Minimal dependency-free test runner (L1 + L3 + L5). Run with `dotnet run --project tests/Sts2.Engine.Tests`.
/// Exits non-zero when any check fails so CI can gate on it.
/// </summary>
internal static class Program
{
    private static int _failures;
    private static int _checks;

    private static int Main()
    {
        string root = FindRoot();

        Test("damage: vulnerable multiplies after additive strength", () =>
        {
            CombatState combat = NewCombat(root, out _);
            MonsterInstance monster = combat.Enemies[0];
            monster.AddPower(CombatState.VulnerablePower, 2);
            combat.Player.AddPower(CombatState.StrengthPower, 2);
            // (8 + 2) * 1.5 = 15
            Equal(15m, Damage.Modify(combat, combat.Player, monster, 8m));
        });

        Test("damage: 10.5 into 10 block loses 0 HP", () =>
        {
            int loss = Damage.Apply(10, 40, 10.5m, out int block);
            Equal(0, loss);
            Equal(0, block);
        });

        Test("damage: through damage truncates (7 * 1.5 = 10.5 -> 10)", () =>
        {
            int loss = Damage.Apply(0, 40, 10.5m, out int block);
            Equal(10, loss);
            Equal(0, block);
        });

        Test("block: gain is int-truncated after decimal add", () =>
        {
            PlayerState player = new(NewCombat(root, out _).Player.Character) { Block = 0 };
            Damage.GainBlock(player, 5.9m);
            Equal(5, player.Block);
        });

        Test("rng: shuffle stream state after the recorded combat setup", () =>
        {
            RunRngSet run = new("PARITY-1");
            run.Shuffle.Shuffle(Enumerable.Range(0, 10).ToList());
            run.Niche.NextItem(new[] { 42, 43, 44, 45, 46 });
            Equal(9, run.Shuffle.Counter);
            Equal(9354830562522647606UL, run.Shuffle.State.S0);
            Equal(12532625438637972642UL, run.Shuffle.State.S3);
        });

        Test("combat: full Ironclad vs NibbitsWeak matches the real transcript", () =>
        {
            string vectorPath = Path.Combine(root, "tests", "vectors", "real", "ironclad_nibbits.jsonl");
            Scenario scenario = JsonSerializer.Deserialize<Scenario>(
                File.ReadAllText(Path.Combine(root, "harness", "scenarios", "ironclad_nibbits.json")),
                Scenario.JsonOptions)!;
            DataPack pack = DataPack.Load(Path.Combine(root, "data", "sts2"));
            CombatState combat = new(pack, scenario);

            JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            List<JsonNode> expected = File.ReadAllLines(vectorPath)
                .Where(l => l.Length > 0)
                .Select(l => JsonNode.Parse(l)!)
                .ToList();

            List<JsonNode> actual = new()
            {
                JsonSerializer.SerializeToNode(Snapshots.Build(combat, new ActionInfo { Type = "start" }), options)!,
            };
            int index = 0;
            foreach (ScenarioAction action in scenario.Actions)
            {
                int actionIndex = action.Index ?? index++;
                ActionInfo info = new() { Type = action.Type, Index = actionIndex, Slot = action.Slot, Card = action.Card, Target = action.Target };
                if (action.Type == "play")
                {
                    combat.PlayCard(action.Slot, action.Card, action.Target, actionIndex);
                }
                else
                {
                    combat.EndTurn(actionIndex);
                }
                actual.Add(JsonSerializer.SerializeToNode(Snapshots.Build(combat, info), options)!);
                if (combat.CombatOver)
                {
                    break;
                }
            }

            Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                List<string> diffs = new();
                CompareJson(expected[i], actual[i], "", diffs);
                if (diffs.Count > 0)
                {
                    throw new Exception($"snapshot {i} diff: {string.Join("; ", diffs.Take(5))}");
                }
            }
        });

        Test("combat: playing without enough energy is rejected", () =>
        {
            CombatState combat = NewCombat(root, out _);
            combat.Player.Energy = 0;
            Throws<CombatException>(() => combat.PlayCard(0, "STRIKE_IRONCLAD", "0", null));
        });

        Test("combat: enemy attack includes strength and player block", () =>
        {
            CombatState combat = NewCombat(root, out _);
            combat.Player.Block = 5;
            // Nibbit BUTT_MOVE deals 12: 5 blocked, 7 through.
            combat.Player.Hp = 40;
            Damage.Attack(combat, combat.Enemies[0], combat.Player, 12m);
            Equal(33, combat.Player.Hp);
            Equal(0, combat.Player.Block);
        });

        Test("view: html replay is self-contained and embeds every snapshot", () =>
        {
            string transcript = Path.Combine(root, "tests", "vectors", "real", "ironclad_cards_showcase.jsonl");
            List<Snapshot> snapshots = File.ReadAllLines(transcript)
                .Where(line => line.Length > 0)
                .Select(line => JsonSerializer.Deserialize<Snapshot>(line, Sts2.View.HtmlView.JsonOptions)!)
                .ToList();
            string html = Sts2.View.HtmlView.Render(snapshots, "test");
            if (!html.StartsWith("<!DOCTYPE html>", StringComparison.Ordinal))
            {
                throw new Exception("html does not start with a doctype");
            }
            if (html.Contains("<script src=", StringComparison.Ordinal) || html.Contains("<link ", StringComparison.Ordinal))
            {
                throw new Exception("html references external assets");
            }
            int embedded = html.Split("\"type\":\"snapshot\"").Length - 1;
            Equal(snapshots.Count, embedded);
        });

        Console.WriteLine(_failures == 0 ? $"{_checks} checks passed" : $"{_failures} failure(s)");
        return _failures == 0 ? 0 : 1;
    }

    private static CombatState NewCombat(string root, out Scenario scenario)
    {
        scenario = JsonSerializer.Deserialize<Scenario>(
            File.ReadAllText(Path.Combine(root, "harness", "scenarios", "ironclad_nibbits.json")),
            Scenario.JsonOptions)!;
        return new CombatState(DataPack.Load(Path.Combine(root, "data", "sts2")), scenario);
    }

    private static string FindRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "data", "sts2")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new Exception("could not locate the repository root (data/sts2)");
    }

    private static void CompareJson(JsonNode? expected, JsonNode? actual, string path, List<string> diffs)
    {
        if (expected is null || actual is null)
        {
            if (!ReferenceEquals(expected, actual))
            {
                diffs.Add($"{path}: null mismatch");
            }
            return;
        }
        if (expected is JsonObject eo && actual is JsonObject ao)
        {
            foreach (string key in eo.Select(p => p.Key).Union(ao.Select(p => p.Key)).OrderBy(k => k))
            {
                if (!eo.TryGetPropertyValue(key, out JsonNode? ev))
                {
                    diffs.Add($"{path}/{key}: only in actual");
                }
                else if (!ao.TryGetPropertyValue(key, out JsonNode? av))
                {
                    diffs.Add($"{path}/{key}: only in expected");
                }
                else
                {
                    CompareJson(ev, av, $"{path}/{key}", diffs);
                }
            }
            return;
        }
        if (expected is JsonArray ea && actual is JsonArray aa)
        {
            if (ea.Count != aa.Count)
            {
                diffs.Add($"{path}: length {ea.Count} != {aa.Count}");
                return;
            }
            for (int i = 0; i < ea.Count; i++)
            {
                CompareJson(ea[i], aa[i], $"{path}[{i}]", diffs);
            }
            return;
        }
        if (expected.GetValueKind() != actual.GetValueKind())
        {
            diffs.Add($"{path}: kind {expected.GetValueKind()} != {actual.GetValueKind()}");
            return;
        }
        if (expected.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            if (Math.Abs(expected.GetValue<decimal>() - actual.GetValue<decimal>()) > 1e-9m)
            {
                diffs.Add($"{path}: {expected} != {actual}");
            }
            return;
        }
        if (!JsonNode.DeepEquals(expected, actual))
        {
            diffs.Add($"{path}: {expected} != {actual}");
        }
    }

    private static void Test(string name, Action body)
    {
        try
        {
            body();
            _checks++;
            Console.WriteLine($"ok   {name}");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"FAIL {name}: {ex.Message}");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"expected {expected}, got {actual}");
        }
    }

    private static void Throws<TException>(Action body) where TException : Exception
    {
        try
        {
            body();
        }
        catch (TException)
        {
            return;
        }
        throw new Exception($"expected {typeof(TException).Name}");
    }
}
