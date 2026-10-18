using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace BeastNav.Services;

/// <summary>
/// Hands the whole Crucible board loop off to erdelf/AutoDuty via its own
/// public IPC (<c>AutoDuty.Run</c>/<c>Stop</c>/<c>IsNavigating</c>/
/// <c>IsLooping</c>/<c>IsStopped</c>) — its generic duty-path-replay engine
/// handles board-tile movement, something this plugin deliberately doesn't
/// implement itself (an earlier native attempt was scaled back per git
/// history, and a 2026-09 vnavmesh-waypoint version built this session
/// didn't work reliably either). <c>AutoDuty.Run</c> takes a
/// <c>territoryType</c> — the same Crucible arena territory ids (1339-1343)
/// this plugin's own <c>IsArenaTerritory</c> checks already use elsewhere —
/// plus a loop count, and from there runs its own registered Crucible
/// automation (NPC interaction, board select, team setup, treasure, shop,
/// whistle picks, battle confirm, result, loot).
/// </summary>
/// <remarks>
/// Because AutoDuty's own Crucible automation covers the same ground as
/// most of this plugin's own Crucible* services, running both at once would
/// have them fight over the same UI (double-clicking buttons, racing
/// FireCallbacks). <see cref="Configuration.CrucibleAutoDutyModeEnabled"/>
/// is the single switch Plugin.cs gates all of the competing services'
/// Tick()s behind — only the genuinely independent, read-mostly services
/// (図鑑/level sync, BMR dodge) keep running regardless.
/// </remarks>
/// <remarks>
/// Does nothing if AutoDuty isn't installed — every call is wrapped and
/// swallows IPC errors, the same defensive style as QuestionableIpcService.
/// </remarks>
public sealed class CrucibleAutoDutyIpcService
{
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<uint, int, bool, object> run;
    private readonly ICallGateSubscriber<object> stop;
    private readonly ICallGateSubscriber<bool> isStopped;
    private readonly ICallGateSubscriber<bool> isLooping;
    private readonly ICallGateSubscriber<bool> isNavigating;

    public CrucibleAutoDutyIpcService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        this.run = pluginInterface.GetIpcSubscriber<uint, int, bool, object>("AutoDuty.Run");
        this.stop = pluginInterface.GetIpcSubscriber<object>("AutoDuty.Stop");
        this.isStopped = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped");
        this.isLooping = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsLooping");
        this.isNavigating = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsNavigating");
    }

    /// <summary>True once an IPC call has succeeded — lets the UI tell "not installed" apart from "installed but idle".</summary>
    public bool IpcAvailable { get; private set; }

    /// <summary>Short human status for the main window.</summary>
    public string Status { get; private set; } = "未確認";

    /// <summary>Starts (or restarts) AutoDuty on the given Crucible arena territory. loops matches Configuration.CrucibleLoopCount's own semantics (0 = unlimited).</summary>
    public void Run(uint territoryType, int loops)
    {
        try
        {
            this.run.InvokeAction(territoryType, loops, false);
            this.IpcAvailable = true;
            this.log.Information("[BeastHelper] AutoDuty: Run(territoryType={Territory}, loops={Loops}).", territoryType, loops);
        }
        catch (Exception ex)
        {
            this.IpcAvailable = false;
            this.Status = "AutoDuty未導入または起動失敗";
            this.log.Warning(ex, "[BeastHelper] AutoDuty: Run IPC call failed (not installed?).");
        }
    }

    public void Stop()
    {
        try
        {
            this.stop.InvokeAction();
            this.log.Information("[BeastHelper] AutoDuty: Stop().");
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] AutoDuty: Stop IPC call failed.");
        }
    }

    /// <summary>Refreshes Status from AutoDuty's own IsStopped/IsLooping/IsNavigating. Safe to call every tick.</summary>
    public void Tick()
    {
        try
        {
            var stopped = this.isStopped.InvokeFunc();
            var looping = this.isLooping.InvokeFunc();
            var navigating = this.isNavigating.InvokeFunc();
            this.IpcAvailable = true;
            this.Status = stopped ? "停止中" : navigating ? "移動中" : looping ? "周回中" : "実行中";
        }
        catch
        {
            this.IpcAvailable = false;
            this.Status = "AutoDuty未導入";
        }
    }
}
