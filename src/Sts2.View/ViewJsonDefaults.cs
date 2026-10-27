using System.Text.Json;

namespace Sts2.View;

/// <summary>Why not on the context itself: a static initializer that touches <c>Default</c>
/// from inside the context observes a half-built instance (resolver null).</summary>
public static class ViewJsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        TypeInfoResolver = ViewJsonContext.Default,
    };
}
