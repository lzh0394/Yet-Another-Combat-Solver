using System.Text.Json;

namespace Sts2.Engine.Data;

/// <summary>
/// Options backed by the source-generated <see cref="PackJsonContext"/>. Kept outside the context
/// class: referencing <c>Default</c> from its own static initializer can observe a half-built context.
/// </summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        TypeInfoResolver = PackJsonContext.Default,
    };
}
