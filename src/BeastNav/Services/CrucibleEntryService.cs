using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BeastNav.Services;

/// <summary>
/// Walks to the Crucible (闘獣練) entry NPC in Central Shroud, interacts with
/// it, and picks "「闘獣練」に挑戦する" from the resulting dialogue choice
/// list. Picking a content variant, a beast loadout, or the board itself is
/// not handled here yet, since those screens haven't been identified.
/// </summary>
/// <remarks>
/// Native TargetSystem/SelectString manipulation was tried first, abandoned
/// for an external SomethingNeedDoing macro when it seemed unreliable, and
/// then reinstated once both real root causes were found: interacting always
/// reported the NPC untargetable because BossModDodgeService's preset was
/// pathing the character outside of combat too (see its own Tick), and the
/// dialogue's row never got clicked because of a node-type comparison bug
/// (see the GetComponent() note below) — not because native UI manipulation
/// is inherently unreliable here. With both fixed, this needs no external
/// plugin dependency.
/// </remarks>
/// <remarks>
/// Unattended movement, NPC interaction, and UI interaction — against the
/// FFXIV ToS, self-responsibility. Off by default. Does nothing outside
/// Central Shroud, so it never interferes once a run has actually started.
/// The dialogue choice is only ever clicked after checking its label mentions
/// 闘獣練 — an unrelated SelectString happening to be open is left alone.
/// </remarks>
public sealed unsafe class CrucibleEntryService
{
    private const uint EntryTerritoryId = 148; // Central Shroud (黒衣森: 中央森林)
    private const uint EntryNpcDataId = 1059759; // ラウダ
    private const string EntryNpcName = "ラウダ";
    private const float InteractRange = 3.5f;
    private static readonly TimeSpan InteractThrottle = TimeSpan.FromSeconds(2);

    private const string SelectStringAddonName = "SelectString";
    private const uint SelectStringListNodeId = 3;
    private const int ChallengeChoiceIndex = 0;

    // The first ListItemRenderer keeps this static node id (extra rows beyond
    // the template count get high dynamic ids instead); its own text node is
    // this id within that renderer's own local node scope.
    private const uint FirstChoiceRowNodeId = 5;
    private const uint FirstChoiceTextNodeId = 2;
    private const string ChallengeChoiceMarker = "闘獣練";

    private static readonly TimeSpan DiagnosticLogThrottle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QuestionableStopThrottle = TimeSpan.FromSeconds(2);

    // A full run finishing and immediately re-entering (walk to NPC, talk,
    // pick a board) left less than 5 seconds between leaving the previous
    // board and the next board's callback firing — a crash log traced back
    // to the game's own XBMStageDetailList agent update right after that,
    // consistent with its previous board's data not having been torn down
    // yet. This delay gives the game a moment to finish cleaning up before
    // the loop starts driving the next entry.
    private static readonly TimeSpan ZoneReturnCooldown = TimeSpan.FromSeconds(8);

    private readonly Configuration configuration;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly NavmeshService navmesh;
    private readonly QuestionableIpcService questionable;
    private readonly IPluginLog log;

    private DateTime nextInteractAttempt = DateTime.MinValue;
    private DateTime nextChoiceAttempt = DateTime.MinValue;
    private DateTime nextDiagnosticLog = DateTime.MinValue;
    private DateTime nextQuestionableStop = DateTime.MinValue;
    private DateTime zoneReturnCooldownUntil = DateTime.MinValue;
    private uint lastTerritoryType;

    public CrucibleEntryService(
        Configuration configuration,
        IClientState clientState,
        IObjectTable objectTable,
        IGameGui gameGui,
        NavmeshService navmesh,
        QuestionableIpcService questionable,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.gameGui = gameGui;
        this.navmesh = navmesh;
        this.questionable = questionable;
        this.log = log;
    }

    /// <summary>Logs at most once every <see cref="DiagnosticLogThrottle"/> — these run every tick and would otherwise flood /xllog.</summary>
    private void LogDiagnostic(string message)
    {
        var now = DateTime.UtcNow;
        if (now < this.nextDiagnosticLog)
        {
            return;
        }

        this.nextDiagnosticLog = now + DiagnosticLogThrottle;
        this.log.Information(message);
    }

    /// <summary>Short human status for the main window.</summary>
    public string Status { get; private set; } = "off";

    /// <summary>
    /// True while the entry NPC is actually found this tick (i.e. Tick()
    /// reached the point of calling FindEntryNpc() and it returned something).
    /// False whenever Tick() returns earlier than that for any reason (off,
    /// wrong zone, still in the post-run cooldown, ...). Used by
    /// <see cref="CrucibleLoopService"/> to mark loop boundaries — a run is
    /// only considered complete once the NPC is genuinely found again, not
    /// just because a screen closed.
    /// </summary>
    public bool NpcDetected { get; private set; }

    public void Tick()
    {
        if (!this.configuration.AutoEnterCrucibleEnabled)
        {
            this.Status = "off";
            this.NpcDetected = false;
            return;
        }

        var currentTerritory = this.clientState.TerritoryType;
        if (currentTerritory != EntryTerritoryId)
        {
            this.lastTerritoryType = currentTerritory;
            this.Status = "対象ゾーン外 (中央森林で待機)";
            this.NpcDetected = false;
            return;
        }

        var now0 = DateTime.UtcNow;

        // Just arrived back from somewhere else (i.e. a board just ended) —
        // start the cooldown before doing anything at all.
        if (this.lastTerritoryType != EntryTerritoryId)
        {
            this.zoneReturnCooldownUntil = now0 + ZoneReturnCooldown;
        }

        this.lastTerritoryType = currentTerritory;

        if (now0 < this.zoneReturnCooldownUntil)
        {
            this.Status = "待機中 (盤面の後片付けを待機)";
            this.NpcDetected = false;
            return;
        }

        if (now0 >= this.nextQuestionableStop)
        {
            this.nextQuestionableStop = now0 + QuestionableStopThrottle;
            this.questionable.EnsureStopped();
        }

        if (this.TrySelectChallengeChoice())
        {
            return;
        }

        var player = this.objectTable.LocalPlayer;
        if (player is null)
        {
            return;
        }

        var npc = this.FindEntryNpc();
        if (npc is null)
        {
            this.Status = "NPC未検出";
            this.NpcDetected = false;
            return;
        }

        this.NpcDetected = true;

        var distance = Vector3.Distance(player.Position, npc.Position);
        if (distance > InteractRange)
        {
            this.navmesh.MoveCloseTo(npc.Position, false, InteractRange);
            this.Status = $"接近中: {npc.Name} ({distance:0.0}y)";
            return;
        }

        this.navmesh.Stop();

        var now = DateTime.UtcNow;
        if (now < this.nextInteractAttempt)
        {
            this.Status = "話しかけ待ち";
            return;
        }

        this.nextInteractAttempt = now + InteractThrottle;
        this.Interact(npc, distance);
    }

    /// <summary>
    /// Returns true (and consumes the tick, whether or not it actually clicked)
    /// when a SelectString dialogue is open — regardless of whether it turns
    /// out to be ours, so an unrelated dialogue elsewhere doesn't fall through
    /// into the NPC-approach logic below and try to talk over it.
    /// </summary>
    private bool TrySelectChallengeChoice()
    {
        AtkUnitBase* addon;
        try
        {
            var ptr = this.gameGui.GetAddonByName(SelectStringAddonName);
            addon = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[BeastHelper] Crucible entry: SelectString lookup failed.");
            return false;
        }

        if (addon is null)
        {
            this.LogDiagnostic("[BeastHelper] Crucible entry: SelectString addon not found.");
            return false;
        }

        if (!addon->IsVisible)
        {
            this.LogDiagnostic("[BeastHelper] Crucible entry: SelectString found but not visible.");
            return false;
        }

        var node = addon->GetNodeById(SelectStringListNodeId);
        if (node is null)
        {
            this.LogDiagnostic($"[BeastHelper] Crucible entry: node {SelectStringListNodeId} not found in SelectString.");
            return false;
        }

        // Not node->Type != NodeType.Component: the raw Type field on a
        // component-backed node holds an internal component-kind value, not
        // the normalized NodeType.Component (10000) that only GetNodeType()
        // returns — comparing against that directly always failed.
        // GetComponent() sidesteps it entirely.
        var list = (AtkComponentList*)node->GetComponent();
        if (list is null)
        {
            this.LogDiagnostic($"[BeastHelper] Crucible entry: node {SelectStringListNodeId} has no Component.");
            return false;
        }

        // GetItemLabel reads the list's own ItemLabels array, which SelectString
        // may not populate (it can set each row's text node directly instead) —
        // so also read the first row's own text node as a fallback, and log
        // both since this is exactly the kind of thing that's invisible without
        // seeing the real values.
        var itemLabel = list->GetItemLabel(ChallengeChoiceIndex).ToString();
        var rowText = this.ReadFirstRowText(addon);
        this.LogDiagnostic($"[BeastHelper] Crucible entry: SelectString choice 0 — ItemLabel='{itemLabel}' RowText='{rowText}'.");

        var matched = itemLabel.Contains(ChallengeChoiceMarker, StringComparison.Ordinal)
            || rowText.Contains(ChallengeChoiceMarker, StringComparison.Ordinal);
        if (!matched)
        {
            // Some other SelectString (unrelated NPC dialogue) — leave it alone.
            this.Status = "SelectString(対象外)を検出";
            return true;
        }

        var now = DateTime.UtcNow;
        if (now < this.nextChoiceAttempt)
        {
            this.Status = "選択肢クリック待ち";
            return true;
        }

        this.nextChoiceAttempt = now + InteractThrottle;

        // Equivalent to a macro's "/callback SelectString true 0": SelectString
        // (like most standard list-choice addons) reads the chosen index back
        // from its own FireCallback rather than from a UI click event, so
        // that's what's replayed here instead of AtkComponentList.SelectItem.
        var choice = new AtkValue();
        choice.SetInt(ChallengeChoiceIndex);
        addon->FireCallback(1, &choice, true);

        this.Status = "「闘獣練に挑戦する」を選択";
        this.log.Information("[BeastHelper] Crucible entry: selected 「{Choice}」.", rowText.Length > 0 ? rowText : itemLabel);
        return true;
    }

    /// <summary>
    /// Reads the text node nested inside the first choice row directly, as a
    /// fallback for when the list's ItemLabels array isn't populated.
    /// </summary>
    private string ReadFirstRowText(AtkUnitBase* addon)
    {
        try
        {
            var rowNode = addon->GetNodeById(FirstChoiceRowNodeId);
            if (rowNode is null)
            {
                return string.Empty;
            }

            var rowComponent = rowNode->GetComponent();
            if (rowComponent is null)
            {
                return string.Empty;
            }

            var textNode = rowComponent->GetNodeById(FirstChoiceTextNodeId);
            if (textNode is null || textNode->Type != NodeType.Text)
            {
                return string.Empty;
            }

            return ((AtkTextNode*)textNode)->NodeText.ToString();
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible entry: failed to read first-row text.");
            return string.Empty;
        }
    }

    private IGameObject? FindEntryNpc()
    {
        IGameObject? first = null;
        var matchCount = 0;
        foreach (var obj in this.objectTable)
        {
            if (obj.BaseId != EntryNpcDataId && !string.Equals(obj.Name.TextValue, EntryNpcName, StringComparison.Ordinal))
            {
                continue;
            }

            matchCount++;
            first ??= obj;
        }

        if (matchCount > 1)
        {
            this.LogDiagnostic($"[BeastHelper] Crucible entry: {matchCount} objects matched 「{EntryNpcName}」/{EntryNpcDataId} — using the first one found.");
        }

        return first;
    }

    private void Interact(IGameObject npc, float distance)
    {
        try
        {
            var targetSystem = TargetSystem.Instance();
            if (targetSystem is null)
            {
                this.Status = "TargetSystem unavailable";
                return;
            }

            var result = targetSystem->InteractWithObject((GameObject*)npc.Address);
            this.Status = $"話しかけ要求: {npc.Name} ({distance:0.00}y, targetable={npc.IsTargetable}, result={result})";
            this.log.Information(
                "[BeastHelper] Crucible entry: InteractWithObject({Name}) distance={Distance:0.00}y kind={Kind} targetable={Targetable} -> {Result}.",
                npc.Name.TextValue,
                distance,
                npc.ObjectKind,
                npc.IsTargetable,
                result);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[BeastHelper] Crucible entry: interact failed.");
            this.Status = "話しかけ失敗";
        }
    }
}
