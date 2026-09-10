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

    public override bool DrawConditions()
    {
        // Show while enabled, but only when actually in the Crucible unless the
        // user has pinned it open for debugging.
        if (!this.configuration.CrucibleOverlayEnabled)
        {
            return false;
        }

        return this.configuration.CrucibleOverlayAlwaysShow || this.reader.Current.InCrucible;
    }

    public override void Draw()
    {
        var state = this.reader.Current;
        var recommendation = this.engine.Evaluate(state);

        var danger = recommendation.Type == RecommendationType.AvoidDanger;

        StatusLine("Combat Assist", state.InCrucible ? (state.InCombat ? "ACTIVE" : "STANDBY") : "IDLE");
        StatusLine("Auto Dodge", danger ? "ALERT" : "READY");
        StatusLine("Danger Detected", danger ? "YES" : "no", danger);

        ImGui.Separator();

        ImGui.TextUnformatted("Recommended Action");
        ImGui.SameLine();
        var color = recommendation.Priority switch
        {
            RecommendationPriority.Critical => new Vector4(1f, 0.35f, 0.35f, 1f),
            RecommendationPriority.High => new Vector4(1f, 0.7f, 0.3f, 1f),
            RecommendationPriority.Medium => new Vector4(1f, 0.9f, 0.5f, 1f),
            _ => new Vector4(0.7f, 0.85f, 0.7f, 1f),
        };
        ImGui.TextColored(color, recommendation.Type.ToString());

        if (recommendation.Position != PositionHint.None)
        {
            ImGui.TextColored(color, $"  → {Humanise(recommendation.Position)}");
        }

        if (!string.IsNullOrWhiteSpace(recommendation.Reason))
        {
            ImGui.TextWrapped(recommendation.Reason);
        }

        ImGui.TextUnformatted("Recommended Beast");
        ImGui.SameLine();
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(recommendation.RecommendedBeast) ? "—" : recommendation.RecommendedBeast!);

        this.DrawDebug(state);
    }

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
