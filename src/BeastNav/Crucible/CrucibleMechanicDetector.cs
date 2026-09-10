namespace BeastNav.Crucible;

/// <summary>
/// Classifies every casting enemy. The curated <see cref="CrucibleEncounterDatabase"/>
/// is consulted first, then the cast's shape from the game's own <c>Action</c>
/// sheet. A casting enemy always produces an assessment — harmless single-target
/// casts included — so the overlay can show one line per cast rather than going
/// silent.
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

        var name = this.database.ResolveActionName(enemy.CastActionId);
        var common = new MechanicAssessment
        {
            IsCasting = true,
            EnemyName = enemy.Name,
            DisplayName = name,
            Remaining = enemy.CastRemaining,
        };

        if (this.database.TryGetKnownMechanic(enemy.CastActionId, out var known))
        {
            return common with
            {
                IsThreat = known.Severity >= RecommendationPriority.Medium,
                Kind = known.Kind,
                Severity = known.Severity,
                Hint = known.Hint,
                Advice = Advise(known.Kind, known.Hint),
                Source = "curated",
            };
        }

        var shape = this.database.DescribeCast(enemy.CastActionId);
        var (kind, hint) = shape switch
        {
            ActionShape.CircleAoe => (MechanicKind.GroundAoe, PositionHint.MoveOutside),
            ActionShape.CircleAroundSelf => (MechanicKind.GroundAoe, PositionHint.MoveAway),
            ActionShape.Cone => (MechanicKind.Cleave, PositionHint.MoveBehind),
            ActionShape.Line => (MechanicKind.LineAoe, PositionHint.MoveAway),
            ActionShape.Donut => (MechanicKind.GroundAoe, PositionHint.MoveInside),
            _ => (MechanicKind.Unknown, PositionHint.None),
        };

        if (kind == MechanicKind.Unknown)
        {
            // Single-target or unrecognised. Only worth a note if it is aimed at
            // the player (a possible heavy hit); otherwise it is background noise.
            return common with
            {
                IsThreat = false,
                Kind = MechanicKind.Unknown,
                Severity = RecommendationPriority.None,
                Hint = PositionHint.None,
                Advice = enemy.CastTargetsPlayer ? "自分対象・軽減/回復" : "単体・位置取り不要",
                Source = enemy.CastTargetsPlayer ? "target" : "shape",
            };
        }

        var severity = kind switch
        {
            MechanicKind.GroundAoe or MechanicKind.LineAoe => RecommendationPriority.High,
            MechanicKind.Cleave => enemy.CastTargetsPlayer ? RecommendationPriority.High : RecommendationPriority.Medium,
            _ => RecommendationPriority.Medium,
        };

        return common with
        {
            IsThreat = true,
            Kind = kind,
            Severity = severity,
            Hint = hint,
            Advice = Advise(kind, hint),
            Source = "shape",
        };
    }

    private static string Advise(MechanicKind kind, PositionHint hint)
        => (kind, hint) switch
        {
            (MechanicKind.LineAoe, _) => "直線 → 横へ避ける",
            (MechanicKind.GroundAoe, PositionHint.MoveInside) => "ドーナツ → 内側/足元へ",
            (MechanicKind.GroundAoe, _) => "範囲 → 外へ出る",
            (MechanicKind.Cleave, _) => "前方範囲 → 背面へ回る",
            (MechanicKind.Gaze, _) => "視線 → 目を逸らす",
            (MechanicKind.Knockback, _) => "ノックバック → 壁を背にする / 耐性",
            (MechanicKind.Tankbuster, _) => "被弾注意・軽減",
            (MechanicKind.Raidwide, _) => "全体攻撃 → 軽減/回復",
            _ => string.Empty,
        };
}

public sealed record MechanicAssessment
{
    public static MechanicAssessment None { get; } = new();

    /// <summary>The enemy is mid-cast.</summary>
    public bool IsCasting { get; init; }

    /// <summary>The player needs to react (move or mitigate).</summary>
    public bool IsThreat { get; init; }

    public MechanicKind Kind { get; init; } = MechanicKind.Unknown;

    public string EnemyName { get; init; } = string.Empty;

    /// <summary>The cast's name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Short human instruction, e.g. "直線 → 横へ避ける".</summary>
    public string Advice { get; init; } = string.Empty;

    public RecommendationPriority Severity { get; init; } = RecommendationPriority.None;

    public PositionHint Hint { get; init; } = PositionHint.None;

    public float Remaining { get; init; }

    /// <summary>"curated", "shape", "target" or "none".</summary>
    public string Source { get; init; } = "none";
}
