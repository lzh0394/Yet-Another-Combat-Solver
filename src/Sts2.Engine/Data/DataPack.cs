using System.Text.Json;

namespace Sts2.Engine.Data;

/// <summary>
/// Loads data/sts2/*.json, the artifact produced by tools/export_sts2_data.py.
/// The pack is versioned by manifest.json; callers should check it before use.
/// </summary>
public sealed class DataPack
{
    private DataPack(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }
    public Dictionary<string, CardDef> Cards { get; private init; } = new();
    public Dictionary<string, MonsterDef> Monsters { get; private init; } = new();
    public Dictionary<string, EncounterDef> Encounters { get; private init; } = new();
    public Dictionary<string, CharacterDef> Characters { get; private init; } = new();
    public Dictionary<string, PowerDef> Powers { get; private init; } = new();
    public Dictionary<string, RelicDef> Relics { get; private init; } = new();
    public JsonElement Manifest { get; private init; }

    public static DataPack Load(string directory)
    {
        string full = Path.GetFullPath(directory);
        if (!File.Exists(Path.Combine(full, "manifest.json")))
        {
            throw new FileNotFoundException($"data package manifest not found under {full}");
        }

        return new DataPack(full)
        {
            Manifest = ReadElement(Path.Combine(full, "manifest.json")),
            Cards = ReadMap(full, "cards", PackJsonContext.Default.DictionaryStringCardDef),
            Monsters = ReadMap(full, "monsters", PackJsonContext.Default.DictionaryStringMonsterDef),
            Encounters = ReadMap(full, "encounters", PackJsonContext.Default.DictionaryStringEncounterDef),
            Characters = ReadMap(full, "characters", PackJsonContext.Default.DictionaryStringCharacterDef),
            Powers = ReadMap(full, "powers", PackJsonContext.Default.DictionaryStringPowerDef),
            Relics = ReadMap(full, "relics", PackJsonContext.Default.DictionaryStringRelicDef),
        };
    }

    public CardDef Card(string idOrEntry) => Find(Cards, "CARD.", idOrEntry);

    public MonsterDef Monster(string idOrEntry) => Find(Monsters, "MONSTER.", idOrEntry);

    public EncounterDef Encounter(string idOrEntry) => Find(Encounters, "ENCOUNTER.", idOrEntry);

    public CharacterDef Character(string idOrEntry) => Find(Characters, "CHARACTER.", idOrEntry);

    public string GameVersion => Manifest.TryGetProperty("game", out var game) && game.TryGetProperty("version", out var v)
        ? v.GetString() ?? ""
        : "";

    private static T Find<T>(Dictionary<string, T> map, string prefix, string idOrEntry)
    {
        if (map.TryGetValue(idOrEntry, out var exact))
        {
            return exact;
        }
        if (map.TryGetValue(prefix + idOrEntry, out var prefixed))
        {
            return prefixed;
        }
        throw new KeyNotFoundException($"{prefix}{idOrEntry} not in the data package");
    }

    private static Dictionary<string, T> ReadMap<T>(
        string directory, string name, System.Text.Json.Serialization.Metadata.JsonTypeInfo<Dictionary<string, T>> typeInfo)
    {
        string path = Path.Combine(directory, name + ".json");
        if (!File.Exists(path))
        {
            return new Dictionary<string, T>();
        }
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, typeInfo) ?? new Dictionary<string, T>();
    }

    private static JsonElement ReadElement(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using JsonDocument doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }
}
