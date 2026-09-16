using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BeastNav.Services;

/// <summary>
/// Auto-selects the configured board on the list (<c>XBMStageList</c>) once
/// <see cref="Configuration.AutoEnterCrucibleEnabled"/> and
/// <see cref="Configuration.AutoSelectBoardEnabled"/> are both on.
/// </summary>
/// <remarks>
/// Used to also auto-click the "戦闘開始" (start battle) button on the
/// resulting board screen (<c>XBMStageDetailList</c>), but that was removed
/// again — with whistle/beast loadout selection and board-tile movement
/// both staying manual (see MainWindow's own notice on the 闘獣練 tab),
/// auto-starting the fight risked doing so before the player had actually
/// finished setting up, which is a worse failure mode than just requiring a
/// manual click here.
/// </remarks>
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
/// Unattended UI interaction — against the FFXIV ToS, self-responsibility.
/// Off by default. Never picks beasts, whistles, or a route within a board,
/// and never starts the fight itself.
/// </remarks>
public sealed unsafe class CrucibleBoardService
{
    private const string ListAddonName = "XBMStageList";

    // How long XBMStageList must have been continuously visible before the
    // auto-select callback fires — see the class remarks for why.
    private static readonly TimeSpan BoardSelectDelay = TimeSpan.FromSeconds(2);

    private readonly Configuration configuration;
    private readonly IGameGui gameGui;
    private readonly LuaAutomationService luaAutomation;
    private readonly IPluginLog log;
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

        var listAddon = this.FindAddon(ListAddonName);
        if (listAddon is null || !listAddon->IsVisible)
        {
            this.listVisibleSinceUtc = null;
            this.boardSelectedForThisVisibility = false;
            this.Status = "盤面選択画面待ち";
            return;
        }

        if (!this.configuration.AutoSelectBoardEnabled)
        {
            this.Status = "盤面選択は手動";
            return;
        }

        var now = DateTime.UtcNow;
        this.listVisibleSinceUtc ??= now;

        if (this.boardSelectedForThisVisibility)
        {
            this.Status = $"盤面を選択済み: {this.configuration.SelectedCrucibleBoard}";
            return;
        }

        var elapsed = now - this.listVisibleSinceUtc.Value;
        if (elapsed < BoardSelectDelay)
        {
            this.Status = $"盤面選択まで待機中 ({(BoardSelectDelay - elapsed).TotalSeconds:0.0}秒)";
            return;
        }

        if (!this.configuration.CrucibleBoardScripts.TryGetValue(this.configuration.SelectedCrucibleBoard, out var script)
            || string.IsNullOrWhiteSpace(script))
        {
            this.Status = $"盤面スクリプト未設定: {this.configuration.SelectedCrucibleBoard}";
            return;
        }

        this.boardSelectedForThisVisibility = true;
        this.luaAutomation.Run(script);
        this.Status = $"盤面を選択: {this.configuration.SelectedCrucibleBoard}";
        this.log.Information("[BeastHelper] Crucible board: selecting {Board} after {Delay:0.0}s.", this.configuration.SelectedCrucibleBoard, elapsed.TotalSeconds);
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

}
