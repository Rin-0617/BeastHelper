namespace BeastNav.Models;

public enum CrucibleItemRuleConditionType
{
    SelfStatus,
    TargetStatus,
    SelfHp,
    TargetHp,
    TargetDataId,
}

public enum CrucibleItemRuleStatusCondition
{
    Present,
    Missing,
}

public enum CrucibleItemRuleHpCondition
{
    Above,
    Below,
}

public enum CrucibleItemRuleConditionJoinMode
{
    All,
    Any,
}

/// <summary>One condition inside a <see cref="CrucibleItemRule"/>.</summary>
public sealed class CrucibleItemRuleCondition
{
    public CrucibleItemRuleConditionType Type { get; set; } = CrucibleItemRuleConditionType.SelfHp;

    public CrucibleItemRuleStatusCondition StatusCondition { get; set; } = CrucibleItemRuleStatusCondition.Missing;

    public uint StatusId { get; set; }

    public CrucibleItemRuleHpCondition HpCondition { get; set; } = CrucibleItemRuleHpCondition.Below;

    public float HpThreshold { get; set; } = 50f;

    public uint DataId { get; set; }

    public bool IsStatusRule => this.Type is CrucibleItemRuleConditionType.SelfStatus or CrucibleItemRuleConditionType.TargetStatus;

    public bool IsHpRule => this.Type is CrucibleItemRuleConditionType.SelfHp or CrucibleItemRuleConditionType.TargetHp;

    public bool IsDataIdRule => this.Type is CrucibleItemRuleConditionType.TargetDataId;
}

/// <summary>
/// A user-configurable "if these conditions hold, use this Crucible item"
/// rule, evaluated by CrucibleRecoveryItemService in list order — the first
/// enabled rule whose conditions are satisfied (and whose item is actually
/// off cooldown and available) wins for that tick. Ported (as a design, not
/// code) from BOCXIV/Beastmaster-JP's own rule engine, scoped down to just
/// Crucible item use (no skill actions).
/// </summary>
public sealed class CrucibleItemRule
{
    public bool Enabled { get; set; } = true;

    public string Name { get; set; } = "新規ルール";

    public ushort ItemId { get; set; }

    public CrucibleItemRuleConditionJoinMode ConditionJoinMode { get; set; } = CrucibleItemRuleConditionJoinMode.All;

    public List<CrucibleItemRuleCondition> Conditions { get; set; } = [];
}
