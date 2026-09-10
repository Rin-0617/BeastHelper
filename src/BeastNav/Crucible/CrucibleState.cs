using System.Numerics;

namespace BeastNav.Crucible;

/// <summary>
/// Immutable snapshot of everything the Crucible (闘獣練 / Crucible of the
/// Unbroken) assist needs, captured once per framework tick by
/// <see cref="CrucibleStateReader"/>.
/// </summary>
/// <remarks>
/// This type holds observed state only and exposes no method that acts on the
/// game. It does carry world coordinates (enemy and player positions, facings) —
/// the dodge assist needs the geometry — but movement, if it is ever added, is a
/// separate opt-in actuator, not something wired into this snapshot.
/// </remarks>
public sealed record CrucibleState
{
    public static CrucibleState Empty { get; } = new();

    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;

    // --- context -----------------------------------------------------------
    public bool InCrucible { get; init; }

    public bool InCombat { get; init; }

    public uint TerritoryId { get; init; }

    /// <summary>How the Crucible was detected, for diagnostics.</summary>
    public string DetectionSource { get; init; } = "none";

    // --- player ----------------------------------------------------------------
    public bool HasPlayer { get; init; }

    public uint PlayerCurrentHp { get; init; }

    public uint PlayerMaxHp { get; init; }

    public float PlayerHpFraction => this.PlayerMaxHp == 0 ? 1f : (float)this.PlayerCurrentHp / this.PlayerMaxHp;

    public Vector3 PlayerPosition { get; init; }

    /// <summary>Player facing, radians (game convention: 0 = south / +Z).</summary>
    public float PlayerRotation { get; init; }

    // --- beast state ---------------------------------------------------------
    /// <summary><c>true</c> once <c>XBMManager</c> has received its pet-list packet.</summary>
    public bool BeastDataReady { get; init; }

    public int UnlockedBeastCount { get; init; }

    /// <summary>Pet row ids the player has unlocked, per <c>XBMManager.IsPetUnlocked</c>.</summary>
    public IReadOnlyList<uint> UnlockedBeastPetRowIds { get; init; } = [];

    /// <summary>Best-effort id of the beast currently summoned, if it could be identified.</summary>
    public uint? ActiveBeastPetRowId { get; init; }

    public string ActiveBeastName { get; init; } = string.Empty;

    // --- enemies -----------------------------------------------------------
    public IReadOnlyList<CrucibleEnemy> Enemies { get; init; } = [];

    /// <summary>The player's current target if it is one of <see cref="Enemies"/>.</summary>
    public CrucibleEnemy? PrimaryTarget { get; init; }

    public bool AnyEnemyCasting => this.Enemies.Any(static enemy => enemy.IsCasting);
}

/// <summary>A hostile battle NPC observed in the arena. Observed values only.</summary>
public sealed record CrucibleEnemy
{
    public ulong GameObjectId { get; init; }

    public uint NameId { get; init; }

    public string Name { get; init; } = string.Empty;

    public uint CurrentHp { get; init; }

    public uint MaxHp { get; init; }

    public float HpFraction => this.MaxHp == 0 ? 1f : (float)this.CurrentHp / this.MaxHp;

    public bool IsCasting { get; init; }

    public uint CastActionId { get; init; }

    public float CastCurrent { get; init; }

    public float CastTotal { get; init; }

    public float CastRemaining => MathF.Max(0f, this.CastTotal - this.CastCurrent);

    public bool CastTargetsPlayer { get; init; }

    /// <summary>What this enemy is currently targeting (0 if nothing).</summary>
    public ulong TargetObjectId { get; init; }

    /// <summary>The enemy is targeting the player.</summary>
    public bool AggroOnPlayer { get; init; }

    /// <summary>Distance from the player, in yalms.</summary>
    public float Distance { get; init; }

    public Vector3 Position { get; init; }

    /// <summary>Enemy facing, radians (game convention: 0 = south / +Z).</summary>
    public float Rotation { get; init; }
}
