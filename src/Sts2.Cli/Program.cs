using System.Text.Json;
using System.Text.Json.Serialization;
using Sts2.Contracts;
using Sts2.Engine.Combat;
using Sts2.Engine.Data;
using Sts2.Engine.Model;
using Sts2.Engine.Random;
using Sts2.View;

int failures = 0;

if (args.Length > 0 && args[0] == "rng-verify")
{
    // Expected states come from tests/vectors/real/ironclad_nibbits.jsonl (seed "PARITY-1",
    // combat setup completed: one 10-card shuffle, one niche roll for monster HP).
    var run = new RunRngSet("PARITY-1");

    var deck = Enumerable.Range(0, 10).ToList();
    run.Shuffle.Shuffle(deck);
    run.Niche.NextItem(new[] { 42, 43, 44, 45, 46 });

    Check("shuffle", run.Shuffle, 9, 9354830562522647606UL, 12224141739328947074UL, 14677165190884953689UL, 12532625438637972642UL);
    Check("niche", run.Niche, 1, 16526584385261898338UL, 6393358066446383996UL, 1857259407708438500UL, 13048542318185815018UL);
    Check("monster_ai", run.MonsterAi, 0, 2538025522533721116UL, 17756323427396098298UL, 4031729422628591458UL, 2019482087996196458UL);
    Check("combat_targets", run.CombatTargets, 0, 6383949868402663893UL, 17544667559032548663UL, 6762597697159269467UL, 4332708678823456066UL);
    Check("combat_card_generation", run.CombatCardGeneration, 0, 6543229380541925722UL, 14404786570205565891UL, 329090813339345672UL, 1817688835981333544UL);
    Check("combat_card_selection", run.CombatCardSelection, 0, 11552474273586065932UL, 13418836563959089519UL, 6294659267123674603UL, 2558970975449206895UL);
    Check("combat_energy_costs", run.CombatEnergyCosts, 0, 6780398525937188782UL, 8011987414589096198UL, 13713642367950639335UL, 3182507349583641859UL);

    foreach (var (label, deckOrder) in new[]
    {
        ("start-order", new[] { "S", "S", "S", "S", "S", "D", "D", "D", "D", "B" }),
        ("id-sorted", new[] { "B", "D", "D", "D", "D", "S", "S", "S", "S", "S" }),
    })
    {
        var shuffleStream = new Rng(run.Seed, "shuffle");
        var cards = deckOrder.ToList();
        shuffleStream.Shuffle(cards);
        Console.WriteLine($"{label}: hand=[{string.Join(",", cards.Take(5))}] draw=[{string.Join(",", cards.Skip(5))}]");
    }
    return failures == 0 ? 0 : 1;
}

if (args.Length > 0 && args[0] == "run")
{
    string? scenarioPath = null;
    string packDir = Path.Combine("data", "sts2");
    string? outPath = null;
    for (int i = 1; i + 1 < args.Length; i += 2)
    {
        switch (args[i])
        {
            case "--scenario": scenarioPath = args[i + 1]; break;
            case "--pack": packDir = args[i + 1]; break;
            case "--out": outPath = args[i + 1]; break;
            default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
        }
    }
    if (scenarioPath is null || outPath is null)
    {
        Console.Error.WriteLine("usage: sts2 run --scenario <file> [--pack <dir>] --out <file>");
        return 2;
    }

    Scenario scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(scenarioPath), Scenario.JsonOptions)
        ?? throw new InvalidOperationException("scenario is empty");
    DataPack pack = DataPack.Load(packDir);
    Combat combat = new(pack, scenario);

    JsonSerializerOptions options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    using StreamWriter writer = new(outPath);
    void Emit(ActionInfo action) => writer.WriteLine(JsonSerializer.Serialize(Snapshots.Build(combat, action), options));

    Emit(new ActionInfo { Type = "start" });
    int actionIndex = 0;
    foreach (ScenarioAction action in scenario.Actions)
    {
        int index = action.Index ?? actionIndex++;
        ActionInfo info = new()
        {
            Type = action.Type,
            Index = index,
            Slot = action.Slot,
            Card = action.Card,
            Target = action.Target,
        };
        switch (action.Type)
        {
            case "play":
                combat.PlayCard(action.Slot, action.Card, action.Target, index);
                break;
            case "end_turn":
                combat.EndTurn(index);
                break;
            case "auto_turn":
                combat.AutoTurn(index);
                break;
            default:
                throw new InvalidOperationException($"action type not implemented: {action.Type}");
        }
        Emit(info);
        if (combat.CombatOver)
        {
            break;
        }
    }
    return 0;
}

if (args.Length > 0 && args[0] == "tui")
{
    string? scenarioPath = null;
    string packDir = Path.Combine("data", "sts2");
    for (int i = 1; i + 1 < args.Length; i += 2)
    {
        switch (args[i])
        {
            case "--scenario": scenarioPath = args[i + 1]; break;
            case "--pack": packDir = args[i + 1]; break;
            default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
        }
    }
    if (scenarioPath is null)
    {
        Console.Error.WriteLine("usage: sts2 tui --scenario <file> [--pack <dir>]");
        return 2;
    }

    Scenario scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(scenarioPath), Scenario.JsonOptions)
        ?? throw new InvalidOperationException("scenario is empty");
    Combat combat = new(DataPack.Load(packDir), scenario);
    int actionIndex = 0;

    Console.WriteLine("commands: play <hand-slot> [target] | end | map | quit");
    while (true)
    {
        Snapshot snapshot = Snapshots.Build(combat, new ActionInfo { Type = actionIndex == 0 ? "start" : "state" });
        Console.WriteLine(TuiView.Render(snapshot));
        if (snapshot.CombatOver)
        {
            Console.WriteLine("combat over.");
            return 0;
        }

        Console.Write("> ");
        string? line = Console.ReadLine();
        if (line is null)
        {
            return 0;
        }
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            continue;
        }

        try
        {
            switch (parts[0])
            {
                case "q" or "quit" or "exit":
                    return 0;
                case "play":
                {
                    if (parts.Length < 2 || !int.TryParse(parts[1], out int slot))
                    {
                        Console.WriteLine("usage: play <hand-slot> [target]");
                        continue;
                    }
                    if (slot < 0 || slot >= combat.Player.Hand.Count)
                    {
                        Console.WriteLine($"hand slot must be 0..{combat.Player.Hand.Count - 1}");
                        continue;
                    }
                    CardInstance card = combat.Player.Hand[slot];
                    string? target = parts.Length > 2 ? parts[2] : DefaultTarget(card);
                    combat.PlayCard(slot, card.Entry, target, actionIndex++);
                    break;
                }
                case "end":
                    combat.EndTurn(actionIndex++);
                    break;
                case "map":
                    Console.WriteLine(string.Join(", ", combat.Enemies.Select((e, i) => $"[{i}] {e.Id} {e.Hp}/{e.MaxHp}")));
                    break;
                case "help":
                    Console.WriteLine("commands: play <hand-slot> [target] | end | map | quit");
                    break;
                default:
                    Console.WriteLine("unknown command (try help)");
                    break;
            }
        }
        catch (CombatException ex)
        {
            Console.WriteLine("rejected: " + ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine("rejected: " + ex.Message);
        }
    }
}

Console.Error.WriteLine("usage: sts2 rng-verify | run --scenario <file> [--pack <dir>] --out <file> | tui --scenario <file> [--pack <dir>]");
return 2;

static string? DefaultTarget(CardInstance card) => card.Def.Target is "AnyEnemy" or "RandomEnemy" ? "0" : "player";

void Check(string name, Rng rng, int counter, ulong s0, ulong s1, ulong s2, ulong s3)
{
    var (a0, a1, a2, a3) = rng.State;
    bool ok = rng.Counter == counter && a0 == s0 && a1 == s1 && a2 == s2 && a3 == s3;
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}: counter={rng.Counter} (want {counter}) s0={a0} s1={a1} s2={a2} s3={a3}");
    if (!ok)
    {
        failures++;
        Console.WriteLine($"     want s0={s0} s1={s1} s2={s2} s3={s3}");
    }
}
