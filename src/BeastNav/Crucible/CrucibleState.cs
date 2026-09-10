namespace BeastNav.Crucible;

/// <summary>
/// Immutable snapshot of everything the Crucible (闘獣練 / Crucible of the
/// Unbroken) assist needs, captured once per framework tick by
/// <see cref="CrucibleStateReader"/>.
/// </summary>
/// <remarks>
/// This type is deliberately inert: it holds observed state only. It carries no
/// world coordinates that could be fed straight into automated movement, and it
/// exposes no methods that act on the game.
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

    /// <summary>
    /// Distance from the player, in yalms. A scalar only — enemy world
    /// coordinates are deliberately not carried on this type so nothing
    /// downstream can turn the state into a navigation target.
    /// </summary>
    public float Distance { get; init; }
}
