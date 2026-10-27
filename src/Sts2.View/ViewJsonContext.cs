using System.Text.Json;
using System.Text.Json.Serialization;
using Sts2.Contracts;

namespace Sts2.View;

/// <summary>Source-generated JSON for transcripts, so the viewer works under native AOT.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Snapshot))]
[JsonSerializable(typeof(List<Snapshot>))]
[JsonSerializable(typeof(JsonElement))]
public partial class ViewJsonContext : JsonSerializerContext
{
}
