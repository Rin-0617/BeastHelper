using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BeastNav.Services;

/// <summary>
/// Auto-selects the configured board on the list (<c>XBMStageList</c>) once
/// <see cref="Configuration.AutoEnterCrucibleEnabled"/> and
/// <see cref="Configuration.AutoSelectBoardEnabled"/> are both on, and
/// auto-clicks the "戦闘開始" (start battle) button on the resulting board
/// screen (<c>XBMStageDetailList</c>) once <see cref="Configuration.AutoStartBattleEnabled"/>
/// is also on — replaying the node's own already-registered click event back
/// at the addon for the button, the same trick community UI-automation
/// helpers (e.g. ECommons' <c>ClickAddonButton</c>) use, rather than
/// simulating a raw mouse click.
/// </summary>
/// <remarks>
/// Board selection used to fire a synthesized <c>FireCallback</c> the
/// instant <c>XBMStageList</c> became visible, and was removed entirely
/// after two separate crash logs both showed the game's own
/// AgentXBMStageDetailList.Update crashing inside AtkValue.SetManagedString
/// within a second of that callback firing, in both cases with no
/// BeastHelper frames anywhere else in the stack — the working theory was
/// that the synthesized callback didn't carry whatever real click-driven
/// data that agent expects, leaving it in a state that corrupts a string on
/// its next update. It's back, on the theory that firing immediately (with
/// the list's own data possibly still settling) was itself part of the
/// problem: <see cref="BoardSelectDelay"/> now holds off for a couple of
/// seconds after the list first becomes visible before firing the callback
/// once. This is unverified — the two crash logs don't prove timing was the
/// cause, only that no BeastHelper frames were in either stack — so it's
/// kept behind its own toggle (<see cref="Configuration.AutoSelectBoardEnabled"/>),
/// separate from and off even when AutoEnterCrucibleEnabled is on, rather
/// than silently folded back into the master switch.
/// </remarks>
/// <remarks>
/// XBMStageDetailList shows two different "戦闘開始" buttons depending on
/// which sub-screen is up: the route/board view uses a proper Button
/// Component Node (id 33), while the initial mode-confirm view draws its own
/// button as a plain Res Node (id 31) that has a click event attached
/// directly to it instead of going through a button component. Both are
/// tried every tick; whichever one is actually present gets clicked.
/// </remarks>
/// <remarks>
/// Unattended UI interaction — against the FFXIV ToS, self-responsibility.
/// Both toggles off by default. Never picks beasts or a route within a board.
/// </remarks>
public sealed unsafe class CrucibleBoardService
{
    private const string ListAddonName = "XBMStageList";
    private const string StageAddonName = "XBMStageDetailList";
    private const uint StartBattleButtonNodeId = 33;
    private const uint StartBattleResNodeId = 31;
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(1.5);

    // How long XBMStageList must have been continuously visible before the
    // auto-select callback fires — see the class remarks for why.
    private static readonly TimeSpan BoardSelectDelay = TimeSpan.FromSeconds(2);

    private readonly Configuration configuration;
    private readonly IGameGui gameGui;
    private readonly LuaAutomationService luaAutomation;
    private readonly IPluginLog log;
    private DateTime nextAttempt = DateTime.MinValue;
    private DateTime? listVisibleSinceUtc;
    private bool boardSelectedForThisVisibility;

    public CrucibleBoardService(Configuration configuration, IGameGui gameGui, LuaAutomationService luaAutomation, IPluginLog log)
    {
        this.configuration = configuration;
        this.gameGui = gameGui;
        this.luaAutomation = luaAutomation;
        this.log = log;
    }

    /// <summary>Short human status for the main window.</summary>
    public string Status { get; private set; } = "off";

    public void Tick()
    {
        if (!this.configuration.AutoEnterCrucibleEnabled)
        {
            this.Status = "off";
            this.listVisibleSinceUtc = null;
            this.boardSelectedForThisVisibility = false;
            return;
        }

        if (this.TickBoardSelect())
        {
            return;
        }

        this.TickStartBattle();
    }

    /// <summary>Returns true if this tick's status was decided by the board-select step (so the caller should skip 戦闘開始 handling this frame).</summary>
    private bool TickBoardSelect()
    {
        var listAddon = this.FindAddon(ListAddonName);
        if (listAddon is null || !listAddon->IsVisible)
        {
            this.listVisibleSinceUtc = null;
            this.boardSelectedForThisVisibility = false;
            return false;
        }

        if (!this.configuration.AutoSelectBoardEnabled)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        this.listVisibleSinceUtc ??= now;

        if (this.boardSelectedForThisVisibility)
        {
            return false;
        }

        var elapsed = now - this.listVisibleSinceUtc.Value;
        if (elapsed < BoardSelectDelay)
        {
            this.Status = $"盤面選択まで待機中 ({(BoardSelectDelay - elapsed).TotalSeconds:0.0}秒)";
            return true;
        }

        if (!this.configuration.CrucibleBoardScripts.TryGetValue(this.configuration.SelectedCrucibleBoard, out var script)
            || string.IsNullOrWhiteSpace(script))
        {
            this.Status = $"盤面スクリプト未設定: {this.configuration.SelectedCrucibleBoard}";
            return true;
        }

        this.boardSelectedForThisVisibility = true;
        this.luaAutomation.Run(script);
        this.Status = $"盤面を選択: {this.configuration.SelectedCrucibleBoard}";
        this.log.Information("[BeastHelper] Crucible board: selecting {Board} after {Delay:0.0}s.", this.configuration.SelectedCrucibleBoard, elapsed.TotalSeconds);
        return true;
    }

    private void TickStartBattle()
    {
        if (!this.configuration.AutoStartBattleEnabled)
        {
            this.Status = "戦闘開始は手動(盤面選択・呼び笛設定後にクリック)";
            return;
        }

        var now = DateTime.UtcNow;
        if (now < this.nextAttempt)
        {
            return;
        }

        var addon = this.FindAddon(StageAddonName);
        if (addon is null || !addon->IsVisible)
        {
            this.Status = "盤面画面待ち";
            return;
        }

        if (this.TryClickButtonComponent(addon, StartBattleButtonNodeId)
            || this.TryClickEventNode(addon, StartBattleResNodeId))
        {
            this.nextAttempt = now + Throttle;
            this.Status = "戦闘開始をクリック";
            this.log.Information("[BeastHelper] Crucible board: clicked 戦闘開始.");
            return;
        }

        this.Status = "ボタン未検出";
    }

    private AtkUnitBase* FindAddon(string name)
    {
        try
        {
            var ptr = this.gameGui.GetAddonByName(name);
            return ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible board: addon lookup failed.");
            return null;
        }
    }

    private bool TryClickButtonComponent(AtkUnitBase* addon, uint nodeId)
    {
        var node = addon->GetNodeById(nodeId);
        if (node is null)
        {
            return false;
        }

        // Not node->Type != NodeType.Component: the raw Type field on a
        // component-backed node holds an internal component-kind value
        // (e.g. 1002 for a button), not the normalized NodeType.Component
        // (10000) that only GetNodeType() returns — comparing against that
        // directly always failed. GetComponent() sidesteps it entirely.
        var button = (AtkComponentButton*)node->GetComponent();
        if (button is null)
        {
            return false;
        }

        var ownerNode = button->AtkComponentBase.OwnerNode;
        if (ownerNode is null)
        {
            return false;
        }

        return this.TryReplayEvent(addon, &ownerNode->AtkResNode);
    }

    private bool TryClickEventNode(AtkUnitBase* addon, uint nodeId)
    {
        var node = addon->GetNodeById(nodeId);
        return node is not null && this.TryReplayEvent(addon, node);
    }

    private bool TryReplayEvent(AtkUnitBase* addon, AtkResNode* node)
    {
        var evt = node->AtkEventManager.Event;
        if (evt is null)
        {
            return false;
        }

        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        return true;
    }
}
