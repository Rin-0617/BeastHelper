using BeastNav.Services;

namespace BeastNav.Crucible;

/// <summary>
/// Suggests which unlocked beast suits the current enemy. Reuses
/// <see cref="BeastDataService"/> for all monster data and
/// <see cref="CrucibleEncounterDatabase"/> for affinity.
/// </summary>
/// <remarks>
/// Returns a display name only. It never returns a beast-skill id or anything
/// else that could drive an automated summon.
/// </remarks>
public sealed class CrucibleBeastSelector
{
    private readonly BeastDataService beastData;
    private readonly CrucibleEncounterDatabase database;

    public CrucibleBeastSelector(BeastDataService beastData, CrucibleEncounterDatabase database)
    {
        this.beastData = beastData;
        this.database = database;
    }

    public BeastSuggestion Evaluate(CrucibleState state)
    {
        if (state.PrimaryTarget is not { } target)
        {
            return BeastSuggestion.None;
        }

        var currentAffinity = this.database.RateAffinity(state.ActiveBeastPetRowId, target.NameId);
        if (currentAffinity != BeastAffinity.Bad)
        {
            // Either fine, or we simply don't know enough to advise a swap.
            return BeastSuggestion.None with { CurrentAffinity = currentAffinity };
        }

        var better = state.UnlockedBeastPetRowIds
            .Select(this.beastData.FindPet)
            .Where(static pet => pet is not null)
            .Select(pet => pet!)
            .FirstOrDefault(pet => this.database.RateAffinity(pet.PetRowId, target.NameId) == BeastAffinity.Good);

        return new BeastSuggestion
        {
            ShouldSwap = better is not null,
            CurrentAffinity = currentAffinity,
            RecommendedBeastName = better?.Name,
        };
    }
}

public sealed record BeastSuggestion
{
    public static BeastSuggestion None { get; } = new();

    public bool ShouldSwap { get; init; }

    public BeastAffinity CurrentAffinity { get; init; } = BeastAffinity.Unknown;

    public string? RecommendedBeastName { get; init; }
}
