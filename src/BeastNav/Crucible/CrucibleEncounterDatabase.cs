using BeastNav.Services;
using Dalamud.Plugin.Services;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace BeastNav.Crucible;

/// <summary>
/// Hand-maintained knowledge about Crucible encounters, plus thin lookups over
/// the game's own sheets. Mirrors the approach already used by
/// <see cref="BeastmasterFieldSource"/>: the shipping sheets for this content
/// (<c>XBMContent</c>, <c>XBMContentBattle</c>, …) are still mostly empty, so
/// specifics are curated here and widened over time from observation.
/// </summary>
/// <remarks>
/// Beast/monster data is <b>not</b> duplicated here — it is read back through the
/// shared <see cref="BeastDataService"/>.
/// </remarks>
public sealed class CrucibleEncounterDatabase
{
    private readonly BeastDataService beastData;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    /// <summary>
    /// Curated overrides: enemy cast action id → how a human should react.
    /// Seeded empty; entries are added as encounters are confirmed in game so we
    /// never guess wrong about a named mechanic.
    /// </summary>
    private static readonly IReadOnlyDictionary<uint, KnownMechanic> KnownMechanics =
        new Dictionary<uint, KnownMechanic>();

    public CrucibleEncounterDatabase(BeastDataService beastData, IDataManager dataManager, IPluginLog log)
    {
        this.beastData = beastData;
        this.dataManager = dataManager;
        this.log = log;
    }

    public bool TryGetKnownMechanic(uint castActionId, out KnownMechanic mechanic)
        => KnownMechanics.TryGetValue(castActionId, out mechanic);

    /// <summary>
    /// Falls back to the game's <c>Action</c> sheet to describe an unknown cast
    /// from its shape. This is a shape hint only, not an authoritative call.
    /// </summary>
    public ActionShape DescribeCast(uint castActionId)
    {
        if (castActionId == 0)
        {
            return ActionShape.Unknown;
        }

        try
        {
            var sheet = this.dataManager.GetExcelSheet<LuminaAction>();
            var row = sheet?.GetRowOrDefault(castActionId);
            if (row is null)
            {
                return ActionShape.Unknown;
            }

            return row.Value.CastType switch
            {
                2 => ActionShape.CircleAoe,
                3 => ActionShape.Cone,
                4 => ActionShape.Line,
                5 => ActionShape.CircleAroundSelf,
                10 => ActionShape.Donut,
                11 => ActionShape.Cone,
                12 => ActionShape.Line,
                13 => ActionShape.Cone,
                _ => ActionShape.SingleTargetOrOther,
            };
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible: failed to read Action row {Id}.", castActionId);
            return ActionShape.Unknown;
        }
    }

    public string ResolveActionName(uint castActionId)
    {
        try
        {
            var sheet = this.dataManager.GetExcelSheet<LuminaAction>();
            var name = sheet?.GetRowOrDefault(castActionId)?.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? $"Action #{castActionId}" : name!;
        }
        catch
        {
            return $"Action #{castActionId}";
        }
    }

    /// <summary>
    /// How well the currently-summoned beast matches an enemy. The element
    /// scheme behind <c>XBMPet.Element</c> / <c>XBMElement</c> is not yet
    /// decoded, so this stays <see cref="BeastAffinity.Unknown"/> until a
    /// curated table is filled in — it never guesses.
    /// </summary>
    public BeastAffinity RateAffinity(uint? activeBeastPetRowId, uint enemyNameId)
    {
        _ = this.beastData;
        _ = activeBeastPetRowId;
        _ = enemyNameId;
        return BeastAffinity.Unknown;
    }

    public readonly record struct KnownMechanic(MechanicKind Kind, string DisplayName, RecommendationPriority Severity, PositionHint Hint);
}

public enum ActionShape
{
    Unknown,
    SingleTargetOrOther,
    CircleAoe,
    CircleAroundSelf,
    Cone,
    Line,
    Donut,
}

public enum MechanicKind
{
    Unknown,
    Raidwide,
    GroundAoe,
    Gaze,
    Tankbuster,
    Cleave,
    Knockback,
    Enrage,
}

public enum BeastAffinity
{
    Unknown,
    Bad,
    Neutral,
    Good,
}
