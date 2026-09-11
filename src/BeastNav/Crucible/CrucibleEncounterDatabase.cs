using System.Text.Json;
using BeastNav.Services;
using Dalamud.Plugin;
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
    private const string LearnedFileName = "crucible-learned-geometry.json";

    // Minimum damage to count as evidence, and the safety margin added past the
    // distance we actually got hit from.
    private const float MinHitFraction = 0.02f;
    private const float LearnMargin = 3f;
    private const float MinLearnedRadius = 6f;

    private readonly BeastDataService beastData;
    private readonly IDataManager dataManager;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, GeometryOverride> learned = [];
    private readonly JsonSerializerOptions learnedJson = new() { WriteIndented = true };

    /// <summary>
    /// Curated overrides: enemy cast action id → how a human should react.
    /// Seeded empty; entries are added as encounters are confirmed in game so we
    /// never guess wrong about a named mechanic.
    /// </summary>
    private static readonly IReadOnlyDictionary<uint, KnownMechanic> KnownMechanics =
        new Dictionary<uint, KnownMechanic>
        {
            // --- 闘獣練 第一盤 (territory 1339) — seeded from observed casts ---
            // アビサルランス — 直線チャージ (castType 12, range 40)
            [46876] = new(MechanicKind.LineAoe, "アビサルチャージ (直線)", RecommendationPriority.High, PositionHint.MoveAway),
            // マイトリング・ピース — シルクスクリーン (直線レーザー)
            [46909] = new(MechanicKind.LineAoe, "シルクスクリーン (直線)", RecommendationPriority.High, PositionHint.MoveAway),
            // ナイト・ピース — テュムラス (範囲円 range 6)
            [46866] = new(MechanicKind.GroundAoe, "テュムラス (範囲)", RecommendationPriority.High, PositionHint.MoveOutside),
            // ベーンマイト・ピース — デッドリースラスト (対象への大ダメージ)
            [46906] = new(MechanicKind.Tankbuster, "デッドリースラスト (被弾注意)", RecommendationPriority.Medium, PositionHint.None),
            // ビショップ・ピース — ブラックエラプション。Action シートでは単体扱い
            // (castType 1) だが、実際は自分中心の範囲攻撃。GeometryOverrides で
            // ダッジソルバにも円として扱わせる。
            [46873] = new(MechanicKind.GroundAoe, "ブラックエラプション (自己中心範囲)", RecommendationPriority.High, PositionHint.MoveAway),
        };

    /// <summary>
    /// Geometry corrections for casts whose <c>Action</c> sheet shape lies —
    /// most often a point-blank burst filed as <c>CastType 1</c> (single-target).
    /// Found by watching <c>crucible-observations.jsonl</c> "resolved" hits on
    /// casts the shape-based solver had judged safe.
    /// </summary>
    private static readonly IReadOnlyDictionary<uint, GeometryOverride> GeometryOverrides =
        new Dictionary<uint, GeometryOverride>
        {
            [46873] = new GeometryOverride(DangerKind.Circle, 8f), // ブラックエラプション
        };

    public CrucibleEncounterDatabase(
        BeastDataService beastData,
        IDataManager dataManager,
        IDalamudPluginInterface pluginInterface,
        IPluginLog log)
    {
        this.beastData = beastData;
        this.dataManager = dataManager;
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.LoadLearned();
    }

    public bool TryGetKnownMechanic(uint castActionId, out KnownMechanic mechanic)
        => KnownMechanics.TryGetValue(castActionId, out mechanic);

    public GeometryOverride? TryGetGeometryOverride(uint castActionId)
        => GeometryOverrides.TryGetValue(castActionId, out var g) ? g
            : this.learned.TryGetValue(castActionId, out var l) ? l
            : null;

    /// <summary>
    /// Real-time learning: fed one resolved cast at a time (see
    /// <see cref="CrucibleCastLog"/>). If the player took real damage from a cast
    /// that wasn't aimed at them and isn't already shaped as an AoE, that is
    /// exactly what a mistagged point-blank burst (like ブラックエラプション)
    /// looks like — so it is auto-classified as a circle sized to the distance we
    /// got hit from, persisted, and avoided from then on without anyone having to
    /// spot and hand-curate it.
    /// </summary>
    public void ObserveResolution(uint actionId, bool castTargetsPlayer, float casterDistanceAtStart, bool hit, float hpLossFraction)
    {
        if (actionId == 0 || !hit || castTargetsPlayer || hpLossFraction < MinHitFraction)
        {
            return;
        }

        if (GeometryOverrides.ContainsKey(actionId))
        {
            return; // already curated by hand
        }

        var shape = this.DescribeCast(actionId);
        if (shape is not (ActionShape.Unknown or ActionShape.SingleTargetOrOther))
        {
            return; // already correctly shaped — this was just an ordinary hit
        }

        var radius = MathF.Max(MinLearnedRadius, casterDistanceAtStart + LearnMargin);
        if (this.learned.TryGetValue(actionId, out var existing) && existing.Size >= radius)
        {
            return; // already covers this distance
        }

        this.learned[actionId] = new GeometryOverride(DangerKind.Circle, radius);
        this.log.Information(
            "[BeastHelper] Crucible: learned {Action} (id {Id}) is a point-blank burst (~{Radius:0}y) — took {Pct:P0} damage, untargeted, {Dist:0.0}y away.",
            this.ResolveActionName(actionId),
            actionId,
            radius,
            hpLossFraction,
            casterDistanceAtStart);
        this.SaveLearned();
    }

    private void LoadLearned()
    {
        try
        {
            var path = this.LearnedPath();
            if (!File.Exists(path))
            {
                return;
            }

            var entries = JsonSerializer.Deserialize<List<LearnedEntry>>(File.ReadAllText(path)) ?? [];
            foreach (var e in entries.Where(e => e.ActionId != 0 && e.Size > 0))
            {
                this.learned[e.ActionId] = new GeometryOverride(e.Kind, e.Size);
            }

            if (this.learned.Count > 0)
            {
                this.log.Information("[BeastHelper] Loaded {Count} learned Crucible danger shapes.", this.learned.Count);
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[BeastHelper] Failed to load {File}.", LearnedFileName);
        }
    }

    private void SaveLearned()
    {
        try
        {
            Directory.CreateDirectory(this.pluginInterface.ConfigDirectory.FullName);
            var entries = this.learned.Select(kv => new LearnedEntry(kv.Key, kv.Value.Kind, kv.Value.Size)).ToList();
            File.WriteAllText(this.LearnedPath(), JsonSerializer.Serialize(entries, this.learnedJson));
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[BeastHelper] Failed to save {File}.", LearnedFileName);
        }
    }

    private string LearnedPath() => Path.Combine(this.pluginInterface.ConfigDirectory.FullName, LearnedFileName);

    private readonly record struct LearnedEntry(uint ActionId, DangerKind Kind, float Size);

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

    /// <summary>
    /// Raw shape numbers from the <c>Action</c> sheet: cast type, the primary
    /// size (radius for a circle/cone, length for a line, all in yalms), the
    /// line half-width, and the omen row id.
    /// </summary>
    public CastGeometry ReadGeometry(uint castActionId)
    {
        if (castActionId == 0)
        {
            return default;
        }

        try
        {
            var row = this.dataManager.GetExcelSheet<LuminaAction>()?.GetRowOrDefault(castActionId);
            if (row is null)
            {
                return default;
            }

            return new CastGeometry(
                row.Value.CastType,
                row.Value.EffectRange,
                row.Value.XAxisModifier,
                row.Value.Omen.RowId);
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible: failed to read Action geometry {Id}.", castActionId);
            return default;
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

    public readonly record struct GeometryOverride(DangerKind Kind, float Size);
}

/// <summary>Shape numbers straight from the <c>Action</c> sheet (yalms).</summary>
public readonly record struct CastGeometry(byte CastType, float Size, float HalfWidth, uint OmenId);

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
    LineAoe,
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
