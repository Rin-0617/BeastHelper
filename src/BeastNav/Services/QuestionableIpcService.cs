using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace BeastNav.Services;

/// <summary>
/// Tells the Questionable plugin (github.com/PunishXIV/Questionable) to stand
/// down via its own public IPC, so it doesn't fight BeastHelper for the
/// target/movement while <see cref="CrucibleEntryService"/> is trying to walk
/// to and talk to the Crucible entry NPC. Questionable exposes no "yield to
/// another plugin" flag — the only lever it publishes is
/// <c>Questionable.Stop(string label)</c>, which is the same call its own UI's
/// stop button makes, so that's what this uses.
/// </summary>
/// <remarks>
/// Does nothing if Questionable isn't installed (every call is wrapped and
/// swallows IPC errors). Never touches Questionable when it isn't already
/// running, so a user who doesn't have it active never sees this do anything.
/// </remarks>
public sealed class QuestionableIpcService
{
    private const string StopLabel = "BeastHelper: Crucible entry";

    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<bool> isRunning;
    private readonly ICallGateSubscriber<string, bool> stop;

    public QuestionableIpcService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        this.isRunning = pluginInterface.GetIpcSubscriber<bool>("Questionable.IsRunning");
        this.stop = pluginInterface.GetIpcSubscriber<string, bool>("Questionable.Stop");
    }

    /// <summary>
    /// If Questionable is installed and currently running, tells it to stop.
    /// Safe to call every tick — swallows the case where Questionable isn't
    /// installed (IPC not registered) as well as any other IPC failure.
    /// </summary>
    public void EnsureStopped()
    {
        try
        {
            if (!this.isRunning.InvokeFunc())
            {
                return;
            }

            this.stop.InvokeFunc(StopLabel);
            this.log.Information("[BeastHelper] Questionable was running — told it to stop so it doesn't fight over the Crucible entry NPC.");
        }
        catch
        {
            // Not installed, or its IPC isn't ready yet — nothing to do.
        }
    }
}
