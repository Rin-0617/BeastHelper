namespace BeastNav.Models;

public enum CrucibleTeamMode
{
    /// <summary>Auto-picks the lowest-ranked owned beasts first, so the whole roster levels up toward the rank cap over repeated runs.</summary>
    Leveling,

    /// <summary>Uses exactly the beasts the user checked off in 魔獣リスト (Configuration.CrucibleManualTeamPetRowIds).</summary>
    Manual,
}
