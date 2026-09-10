using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BeastNav.Crucible;

/// <summary>
/// The on-screen advisor. It only ever <b>displays</b> the reader's state and the
/// engine's recommendation — there is no button or code path here that acts on
/// the game.
/// </summary>
public sealed class CrucibleOverlay : Window
{
    private readonly Configuration configuration;
    private readonly CrucibleStateReader reader;
    private readonly CrucibleDecisionEngine engine;

    public CrucibleOverlay(Configuration configuration, CrucibleStateReader reader, CrucibleDecisionEngine engine)
        : base("BeastHelper Crucible###BeastHelperCrucibleOverlay",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.configuration = configuration;
        this.reader = reader;
        this.engine = engine;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(240, 90),
            MaximumSize = new Vector2(560, 900),
        };
        this.RespectCloseHotkey = true;
    }

    /// <summary>
    /// Whether the window should currently be visible. <see cref="Plugin"/> pushes
    /// this into <see cref="Window.IsOpen"/> every tick — the WindowSystem skips
    /// windows whose <c>IsOpen</c> is false before it ever consults
    /// <see cref="DrawConditions"/>.
    /// </summary>
    public bool ShouldBeOpen
        => this.configuration.CrucibleOverlayEnabled
           && (this.configuration.CrucibleOverlayAlwaysShow || this.reader.Current.InCrucible);

    public override bool DrawConditions() => this.configuration.CrucibleOverlayEnabled;

    private static readonly Vector4 Red = new(1f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 Orange = new(1f, 0.7f, 0.3f, 1f);
    private static readonly Vector4 Yellow = new(1f, 0.9f, 0.5f, 1f);
    private static readonly Vector4 Green = new(0.6f, 0.85f, 0.6f, 1f);
    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1f);

    public override void Draw()
    {
        var state = this.reader.Current;
        var recommendation = this.engine.Evaluate(state);
        var danger = recommendation.Type == RecommendationType.AvoidDanger;

        StatusLine("Combat Assist", state.InCrucible ? (state.InCombat ? "ACTIVE" : "STANDBY") : "IDLE");
        StatusLine("Auto Dodge", danger ? "ALERT" : "READY");
        StatusLine("Danger Detected", danger ? "YES" : "no", danger);

        ImGui.Separator();

        // The one-line call. Always says something.
        var color = Colour(recommendation.Priority);
        if (danger || recommendation.Type == RecommendationType.Defensive)
        {
            ImGui.TextColored(color, recommendation.Reason);
            if (recommendation.Position != PositionHint.None)
            {
                ImGui.TextColored(color, $"→ {Humanise(recommendation.Position)}");
            }
        }
        else if (recommendation.Type == RecommendationType.SwapBeast)
        {
            ImGui.TextColored(color, $"魔獣変更: {recommendation.RecommendedBeast ?? "?"}");
            ImGui.TextWrapped(recommendation.Reason);
        }
        else if (state.InCombat)
        {
            ImGui.TextColored(Green, "危険なし — 攻撃継続");
        }
        else
        {
            ImGui.TextColored(Grey, "待機中");
        }

        ImGui.Separator();

        // One line per casting enemy — this is the "脳死" view.
        var casts = this.engine.LastMechanics;
        if (casts.Count == 0)
        {
            ImGui.TextColored(Grey, "詠唱なし");
        }
        else
        {
            foreach (var cast in casts)
            {
                var c = cast.IsThreat ? Colour(cast.Severity) : Grey;
                var mark = cast.IsThreat ? "⚠" : "・";
                ImGui.TextColored(c, $"{mark} {cast.EnemyName} — {cast.DisplayName}  {cast.Remaining:0.0}s");
                if (!string.IsNullOrEmpty(cast.Advice))
                {
                    ImGui.TextColored(c, $"    {cast.Advice}");
                }
            }
        }

        this.DrawDebug(state);
    }

    private static Vector4 Colour(RecommendationPriority priority)
        => priority switch
        {
            RecommendationPriority.Critical => Red,
            RecommendationPriority.High => Orange,
            RecommendationPriority.Medium => Yellow,
            RecommendationPriority.Low => Green,
            _ => Grey,
        };

    private void DrawDebug(CrucibleState state)
    {
        if (!ImGui.CollapsingHeader("Debug state"))
        {
            return;
        }

        ImGui.TextUnformatted($"InCrucible: {state.InCrucible} ({state.DetectionSource})");
        ImGui.TextUnformatted($"InCombat: {state.InCombat}   Territory: {state.TerritoryId}");
        ImGui.TextUnformatted(state.HasPlayer
            ? $"Player HP: {state.PlayerCurrentHp}/{state.PlayerMaxHp} ({state.PlayerHpFraction:P0})"
            : "Player: n/a");
        ImGui.TextUnformatted(state.BeastDataReady
            ? $"Beasts unlocked: {state.UnlockedBeastPetRowIds.Count} (XBMManager count {state.UnlockedBeastCount})"
            : "Beasts: XBMManager not ready");
        ImGui.TextUnformatted($"Active beast: {(string.IsNullOrEmpty(state.ActiveBeastName) ? "unidentified" : state.ActiveBeastName)}");

        ImGui.Separator();
        ImGui.TextUnformatted($"Enemies ({state.Enemies.Count}):");
        foreach (var enemy in state.Enemies)
        {
            var tag = ReferenceEquals(enemy, state.PrimaryTarget) ? "▶ " : "  ";
            ImGui.TextUnformatted($"{tag}{enemy.Name}  {enemy.HpFraction:P0}  {enemy.Distance:0.0}y");
            if (enemy.IsCasting)
            {
                ImGui.TextUnformatted($"    casting {enemy.CastActionId} {enemy.CastCurrent:0.0}/{enemy.CastTotal:0.0}s{(enemy.CastTargetsPlayer ? " @you" : string.Empty)}");
            }
        }

        if (this.engine.LastMechanics.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Mechanic assessments:");
            foreach (var mechanic in this.engine.LastMechanics)
            {
                ImGui.TextUnformatted($"  {mechanic.Kind} · {mechanic.DisplayName} · {mechanic.Severity} · {mechanic.Source}");
            }
        }
    }

    private static void StatusLine(string label, string value, bool warn = false)
    {
        ImGui.TextUnformatted($"{label}:");
        ImGui.SameLine();
        if (warn)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), value);
        }
        else
        {
            ImGui.TextUnformatted(value);
        }
    }

    private static string Humanise(PositionHint hint)
        => hint switch
        {
            PositionHint.Avoid => "Avoid",
            PositionHint.MoveAway => "Move away",
            PositionHint.MoveBehind => "Move behind the enemy",
            PositionHint.MoveOutside => "Move out of the AoE",
            PositionHint.MoveInside => "Move inside / under the enemy",
            PositionHint.NorthSideRecommended => "Favour the north side",
            _ => string.Empty,
        };
}
