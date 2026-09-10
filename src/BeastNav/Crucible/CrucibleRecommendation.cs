namespace BeastNav.Crucible;

/// <summary>
/// The single advisory the <see cref="CrucibleDecisionEngine"/> produces from a
/// <see cref="CrucibleState"/>. This is a value object for display only: it has
/// no methods, holds no coordinates, and nothing here is meant to be executed.
/// </summary>
public sealed record CrucibleRecommendation
{
    public static CrucibleRecommendation Idle { get; } = new()
    {
        Type = RecommendationType.Idle,
        Priority = RecommendationPriority.None,
        Reason = "No action needed.",
    };

    public RecommendationType Type { get; init; } = RecommendationType.Idle;

    public RecommendationPriority Priority { get; init; } = RecommendationPriority.None;

    /// <summary>Human-readable justification, e.g. "Enemy casting Fire IV".</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// An abstract, human-facing positioning hint. Never a coordinate — the
    /// player decides where to stand.
    /// </summary>
    public PositionHint Position { get; init; } = PositionHint.None;

    /// <summary>Display name of a beast to switch to, when <see cref="Type"/> is <see cref="RecommendationType.SwapBeast"/>.</summary>
    public string? RecommendedBeast { get; init; }
}

public enum RecommendationType
{
    /// <summary>Nothing to advise.</summary>
    Idle,

    /// <summary>A dangerous mechanic is resolving; get clear.</summary>
    AvoidDanger,

    /// <summary>Player is under pressure; favour mitigation / healing / defensive play.</summary>
    Defensive,

    /// <summary>The summoned beast is a poor match for the current enemy.</summary>
    SwapBeast,

    /// <summary>Target is low; commit damage.</summary>
    Burst,

    /// <summary>Normal state; keep up the highest-value rotation.</summary>
    Sustain,
}

public enum RecommendationPriority
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

/// <summary>
/// Abstract movement advice. Intentionally coarse and coordinate-free so it can
/// only ever be read by a human, not consumed by a mover.
/// </summary>
public enum PositionHint
{
    None,
    Avoid,
    MoveAway,
    MoveBehind,
    MoveOutside,
    MoveInside,
    NorthSideRecommended,
}
