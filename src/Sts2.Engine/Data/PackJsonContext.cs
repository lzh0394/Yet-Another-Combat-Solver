using System.Text.Json;
using System.Text.Json.Serialization;
using Sts2.Engine.Combat;

namespace Sts2.Engine.Data;

/// <summary>
/// Source-generated JSON metadata, required for native AOT (reflection-based serialization is
/// disabled there). Every type the engine reads from the data package or a scenario goes here.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Dictionary<string, CardDef>))]
[JsonSerializable(typeof(Dictionary<string, MonsterDef>))]
[JsonSerializable(typeof(Dictionary<string, PowerDef>))]
[JsonSerializable(typeof(Dictionary<string, RelicDef>))]
[JsonSerializable(typeof(Dictionary<string, EncounterDef>))]
[JsonSerializable(typeof(Dictionary<string, CharacterDef>))]
[JsonSerializable(typeof(Scenario))]
public partial class PackJsonContext : JsonSerializerContext
{
}
