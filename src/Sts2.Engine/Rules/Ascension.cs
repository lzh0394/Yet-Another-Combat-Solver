using System.Text.Json;

namespace Sts2.Engine.Rules;

/// <summary>
/// Ascension thresholds. The real game asks <c>RunManager.Instance.HasAscension(level)</c>, which is
/// a comparison against the run's ascension level using the enum's declaration order; the data
/// package stores the level name so the engine can reproduce it without the game.
/// </summary>
public static class Ascension
{
    private static readonly Dictionary<string, int> Thresholds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NONE"] = 0,
        ["SWARMING_ELITES"] = 1,
        ["WEARY_TRAVELER"] = 2,
        ["POVERTY"] = 3,
        ["TIGHT_BELT"] = 4,
        ["ASCENDERS_BANE"] = 5,
        ["INFLATION"] = 6,
        ["SCARCITY"] = 7,
        ["TOUGH_ENEMIES"] = 8,
        ["DEADLY_ENEMIES"] = 9,
        ["DOUBLE_BOSS"] = 10,
    };

    public static bool Active(string? level, int ascension)
        => level is not null && Thresholds.TryGetValue(level, out int threshold) && ascension >= threshold;

    /// <summary>
    /// Resolves a data-package amount: either a plain number, or {"base","ascension","level"} where
    /// the game would pick the ascension value once the run reached that level.
    /// </summary>
    public static decimal Amount(JsonElement? amount, int ascension)
    {
        if (amount is not { } value)
        {
            return 0m;
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDecimal();
        }
        if (value.ValueKind != JsonValueKind.Object)
        {
            return 0m;
        }
        decimal b = value.TryGetProperty("base", out JsonElement baseValue) && baseValue.ValueKind == JsonValueKind.Number
            ? baseValue.GetDecimal()
            : 0m;
        if (!value.TryGetProperty("ascension", out JsonElement ascValue) || ascValue.ValueKind != JsonValueKind.Number)
        {
            return b;
        }
        string? level = value.TryGetProperty("level", out JsonElement levelValue) ? levelValue.GetString() : null;
        return Active(level, ascension) ? ascValue.GetDecimal() : b;
    }

    public static bool IsAscensionObject(JsonElement? amount)
        => amount is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("ascension", out _);
}
