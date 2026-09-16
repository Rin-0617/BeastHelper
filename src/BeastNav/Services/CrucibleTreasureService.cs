using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BeastNav.Services;

/// <summary>
/// Auto-picks the top-left temporary item on the mid-board treasure screen
/// (<c>XBMContentsTreasure</c>, node id 10 — a Button Component Node)
/// whenever it opens.
/// </summary>
/// <remarks>
/// The 4 items offered are random each time and evaluating which one
/// actually suits the current run isn't attempted here — this always takes
/// whichever one happens to be first (top-left) rather than reading names/
/// effects and picking by preference, by explicit design choice (simple and
/// deterministic over "smart").
/// </remarks>
/// <remarks>
/// A plain ReceiveEvent replay, not a FireCallback — confirmed via Addon
/// Inspector to be an ordinary Button Component Node, the same kind
/// CrucibleResultService's and CrucibleLootService's own buttons already
/// are. Neither has ever been implicated in a crash (unlike the board-select
/// FireCallback — see CrucibleBoardService's remarks), so this uses the
/// identical click mechanism.
/// </remarks>
/// <remarks>
/// Unattended UI interaction — against the FFXIV ToS, self-responsibility.
/// Off unless AutoEnterCrucibleEnabled is on.
/// </remarks>
public sealed unsafe class CrucibleTreasureService
{
    private const string TreasureAddonName = "XBMContentsTreasure";
    private const uint FirstItemButtonNodeId = 10;
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(1.5);

    private readonly Configuration configuration;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private DateTime nextAttempt = DateTime.MinValue;

    public CrucibleTreasureService(Configuration configuration, IGameGui gameGui, IPluginLog log)
    {
        this.configuration = configuration;
        this.gameGui = gameGui;
        this.log = log;
    }

    /// <summary>Short human status for the main window.</summary>
    public string Status { get; private set; } = "off";

    public void Tick()
    {
        if (!this.configuration.AutoEnterCrucibleEnabled)
        {
            this.Status = "off";
            return;
        }

        var now = DateTime.UtcNow;
        if (now < this.nextAttempt)
        {
            return;
        }

        AtkUnitBase* addon;
        try
        {
            var ptr = this.gameGui.GetAddonByName(TreasureAddonName);
            addon = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible treasure: addon lookup failed.");
            return;
        }

        if (addon is null || !addon->IsVisible)
        {
            this.Status = "宝箱画面待ち";
            return;
        }

        var node = addon->GetNodeById(FirstItemButtonNodeId);
        if (node is null)
        {
            this.Status = "ボタン未検出";
            return;
        }

        // Not node->Type != NodeType.Component: the raw Type field on a
        // component-backed node holds an internal component-kind value, not
        // the normalized NodeType.Component (10000) that only GetNodeType()
        // returns — comparing against that directly always failed.
        // GetComponent() sidesteps it entirely.
        var button = (AtkComponentButton*)node->GetComponent();
        var ownerNode = button is null ? null : button->AtkComponentBase.OwnerNode;
        if (ownerNode is null)
        {
            this.Status = "ボタン未検出";
            return;
        }

        var evt = ownerNode->AtkResNode.AtkEventManager.Event;
        if (evt is null)
        {
            this.Status = "ボタン未検出";
            return;
        }

        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        this.nextAttempt = now + Throttle;
        this.Status = "1番目のアイテムを選択";
        this.log.Information("[BeastHelper] Crucible treasure: picked the first item.");
    }
}
