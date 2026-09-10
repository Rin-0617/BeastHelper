namespace BeastNav.Crucible;

/// <summary>
/// Turns a casting enemy into an abstract "what is about to happen" assessment.
/// Uses the curated <see cref="CrucibleEncounterDatabase"/> first, then falls
/// back to the cast's shape from the game's own <c>Action</c> sheet.
/// </summary>
public sealed class CrucibleMechanicDetector
{
    private readonly CrucibleEncounterDatabase database;

    public CrucibleMechanicDetector(CrucibleEncounterDatabase database)
    {
        this.database = database;
    }

    public MechanicAssessment Assess(CrucibleEnemy enemy)
    {
        if (!enemy.IsCasting || enemy.CastActionId == 0)
        {
            return MechanicAssessment.None;
        }

        if (this.database.TryGetKnownMechanic(enemy.CastActionId, out var known))
        {
            return new MechanicAssessment
            {
                Detected = true,
                Kind = known.Kind,
                DisplayName = known.DisplayName,
                Severity = known.Severity,
                Hint = known.Hint,
                Source = "curated",
            };
        }

        var shape = this.database.DescribeCast(enemy.CastActionId);
        var name = this.database.ResolveActionName(enemy.CastActionId);
        var (kind, hint) = shape switch
        {
            ActionShape.CircleAoe => (MechanicKind.GroundAoe, PositionHint.MoveOutside),
            ActionShape.CircleAroundSelf => (MechanicKind.GroundAoe, PositionHint.MoveAway),
            ActionShape.Cone => (MechanicKind.Cleave, PositionHint.MoveBehind),
            ActionShape.Line => (MechanicKind.LineAoe, PositionHint.MoveAway),
            ActionShape.Donut => (MechanicKind.GroundAoe, PositionHint.MoveInside),
            _ => (MechanicKind.Unknown, PositionHint.None),
        };

        if (kind == MechanicKind.Unknown && !enemy.CastTargetsPlayer)
        {
            return MechanicAssessment.None;
        }

        // Unknown single-target casts aimed at the player are treated as a
        // possible tankbuster-style hit worth bracing for, but only mildly.
        if (kind == MechanicKind.Unknown)
        {
            kind = MechanicKind.Tankbuster;
        }

        var severity = kind switch
        {
            MechanicKind.GroundAoe => RecommendationPriority.High,
            MechanicKind.LineAoe => RecommendationPriority.High,
            MechanicKind.Cleave => enemy.CastTargetsPlayer ? RecommendationPriority.High : RecommendationPriority.Medium,
            MechanicKind.Tankbuster => RecommendationPriority.Medium,
            _ => RecommendationPriority.Low,
        };

        return new MechanicAssessment
        {
            Detected = true,
            Kind = kind,
            DisplayName = name,
            Severity = severity,
            Hint = hint,
            Source = "shape",
        };
    }
}

public sealed record MechanicAssessment
{
    public static MechanicAssessment None { get; } = new();

    public bool Detected { get; init; }

    public MechanicKind Kind { get; init; } = MechanicKind.Unknown;

    public string DisplayName { get; init; } = string.Empty;

    public RecommendationPriority Severity { get; init; } = RecommendationPriority.None;

    public PositionHint Hint { get; init; } = PositionHint.None;

    /// <summary>"curated" or "shape" — where the call came from.</summary>
    public string Source { get; init; } = "none";
}
