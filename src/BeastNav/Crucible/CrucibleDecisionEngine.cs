namespace BeastNav.Crucible;

/// <summary>
/// Rule-based advisor. Given a <see cref="CrucibleState"/> it returns the single
/// highest-priority <see cref="CrucibleRecommendation"/>. No AI, no LLM, no
/// coordinates, no execution — the rules are plain if/else and the output is a
/// label plus a reason.
/// </summary>
public sealed class CrucibleDecisionEngine
{
    private const float LowHpFraction = 0.35f;
    private const float ExecuteHpFraction = 0.25f;

    private readonly CrucibleMechanicDetector mechanicDetector;
    private readonly CrucibleBeastSelector beastSelector;

    public CrucibleDecisionEngine(CrucibleMechanicDetector mechanicDetector, CrucibleBeastSelector beastSelector)
    {
        this.mechanicDetector = mechanicDetector;
        this.beastSelector = beastSelector;
    }

    /// <summary>Latest assessment of every casting enemy, for the overlay's debug view.</summary>
    public IReadOnlyList<MechanicAssessment> LastMechanics { get; private set; } = [];

    public CrucibleRecommendation Evaluate(CrucibleState state)
    {
        if (!state.InCrucible || !state.HasPlayer)
        {
            this.LastMechanics = [];
            return CrucibleRecommendation.Idle;
        }

        // Rule 1 — a dangerous cast is resolving. Highest priority.
        var mechanics = state.Enemies
            .Where(static enemy => enemy.IsCasting)
            .Select(this.mechanicDetector.Assess)
            .Where(static assessment => assessment.IsCasting)
            .OrderByDescending(static assessment => assessment.Severity)
            .ToList();
        this.LastMechanics = mechanics;

        if (mechanics.FirstOrDefault(static m => m.IsThreat && m.Severity >= RecommendationPriority.Medium) is { } danger)
        {
            return new CrucibleRecommendation
            {
                Type = RecommendationType.AvoidDanger,
                Priority = danger.Severity,
                Reason = string.IsNullOrEmpty(danger.EnemyName)
                    ? $"詠唱: {danger.DisplayName}"
                    : $"{danger.EnemyName}: {danger.DisplayName}",
                Position = danger.Hint,
            };
        }

        // Rule 2 — player is under pressure.
        if (state.PlayerMaxHp > 0 && state.PlayerHpFraction <= LowHpFraction)
        {
            return new CrucibleRecommendation
            {
                Type = RecommendationType.Defensive,
                Priority = RecommendationPriority.High,
                Reason = $"HP low ({state.PlayerHpFraction:P0}) — mitigate or heal",
            };
        }

        // Rule 3 — the summoned beast is a poor match for the target.
        var beast = this.beastSelector.Evaluate(state);
        if (beast.ShouldSwap)
        {
            return new CrucibleRecommendation
            {
                Type = RecommendationType.SwapBeast,
                Priority = RecommendationPriority.Medium,
                Reason = state.PrimaryTarget is { } t
                    ? $"Current beast weak against {t.Name}"
                    : "Current beast is a poor match",
                RecommendedBeast = beast.RecommendedBeastName,
            };
        }

        // Rule 4 — target is almost dead.
        if (state.PrimaryTarget is { } target && target.MaxHp > 0 && target.HpFraction <= ExecuteHpFraction)
        {
            return new CrucibleRecommendation
            {
                Type = RecommendationType.Burst,
                Priority = RecommendationPriority.Medium,
                Reason = $"{target.Name} at {target.HpFraction:P0} — commit damage",
            };
        }

        // Rule 5 — nothing special; keep the rotation going while in combat.
        if (state.InCombat)
        {
            return new CrucibleRecommendation
            {
                Type = RecommendationType.Sustain,
                Priority = RecommendationPriority.Low,
                Reason = "No threat detected — sustain damage",
            };
        }

        return CrucibleRecommendation.Idle;
    }
}
