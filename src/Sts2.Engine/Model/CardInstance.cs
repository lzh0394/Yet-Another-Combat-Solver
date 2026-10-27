using Sts2.Engine.Data;

namespace Sts2.Engine.Model;

/// <summary>A concrete card in a pile: definition plus upgrade level.</summary>
public sealed class CardInstance
{
    public CardInstance(CardDef def, int upgrade = 0)
    {
        Def = def;
        Upgrade = upgrade;
    }

    public CardDef Def { get; }

    public int Upgrade { get; }

    public string Id => Def.Id;

    public string Entry => Def.Entry;

    public int Cost => Upgrade > 0 ? Def.CostUpgraded ?? Def.Cost ?? -1 : Def.Cost ?? -1;

    public bool CostsX => Def.CostsX;

    public int StarCost => Def.StarCost ?? -1;

    public string TargetType => Def.Target ?? "None";

    private readonly HashSet<string> _extraKeywords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Combat-only keyword added by effects such as Sculpting Strike (Ethereal) or Snap (Retain).</summary>
    public void AddKeyword(string keyword) => _extraKeywords.Add(keyword);

    public bool HasKeyword(string keyword)
    {
        if (_extraKeywords.Contains(keyword))
        {
            return true;
        }
        foreach (string k in Upgrade > 0 ? Def.KeywordsUpgraded : Def.Keywords)
        {
            if (string.Equals(k, keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public IReadOnlyList<string> Keywords
    {
        get
        {
            List<string> keywords = new(Upgrade > 0 ? Def.KeywordsUpgraded : Def.Keywords);
            keywords.AddRange(_extraKeywords);
            return keywords;
        }
    }

    public decimal Var(string name)
    {
        if (Def.Vars is null || !Def.Vars.TryGetValue(name, out VarDef? v))
        {
            return 0m;
        }
        return Upgrade > 0 ? v.Upgraded ?? v.Base : v.Base;
    }

    public CardInstance Upgraded() => new(Def, Upgrade + 1);

    public override string ToString() => Upgrade > 0 ? $"{Entry}+{Upgrade}" : Entry;
}
