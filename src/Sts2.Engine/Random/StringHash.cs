using System.Text.RegularExpressions;

namespace Sts2.Engine.Random;

public static partial class StringHash
{
    // Mirrors MegaCrit.Sts2.Core.Helpers.StringHelper.GetDeterministicHashCode.
    public static ulong Deterministic(string value) => XxHash64.HashToUInt64(value, 0UL);

    [GeneratedRegex("([A-Za-z0-9]|\\G(?!^))([A-Z])")]
    private static partial Regex CamelCaseRegex();

    /// <summary>Mirrors StringHelper.SnakeCase: "MonsterAi" -> "monster_ai".</summary>
    public static string SnakeCase(string text) => CamelCaseRegex().Replace(text.Trim(), "$1_$2").ToLowerInvariant();
}
