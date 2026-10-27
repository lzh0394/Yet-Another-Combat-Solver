namespace Sts2.Engine.Model;

/// <summary>Shared surface of players and monsters for damage/block/power math.</summary>
public interface ICombatant
{
    string Id { get; }
    int Hp { get; set; }
    int MaxHp { get; set; }
    int Block { get; set; }
    List<PowerInstance> Powers { get; }
    int PowerAmount(string id);
    void AddPower(string id, int amount);
}
