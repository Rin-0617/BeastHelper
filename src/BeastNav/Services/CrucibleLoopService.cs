using Dalamud.Plugin.Services;

namespace BeastNav.Services;

/// <summary>
/// Counts completed Crucible 突入→クリア→戦利品受取→NPC前に戻る cycles and
/// turns <see cref="Configuration.AutoEnterCrucibleEnabled"/> back off once
/// <see cref="Configuration.CrucibleLoopCount"/> is reached, so an
/// unattended run can be capped at a fixed number of clears instead of
/// always running until manually stopped. The same idea as AutoDuty's own
/// loop count.
/// </summary>
/// <remarks>
/// A loop boundary is <see cref="CrucibleEntryService.NpcDetected"/>
/// transitioning from false to true — i.e. the entry NPC being genuinely
/// found again, not merely a screen closing or a claim being confirmed.
/// Everything from plugin start (or from AutoEnterCrucibleEnabled being
/// turned on) up to the first time the NPC is detected counts as still
/// being "inside" the first loop, so that detection doesn't complete
/// anything by itself; the *second* detection is what closes loop 1 (and
/// starts loop 2), the third closes loop 2, and so on.
/// </remarks>
/// <remarks>
/// CrucibleLoopCount == 0 means unlimited (the only behavior before this
/// existed). LoopsCompleted resets to 0 whenever AutoEnterCrucibleEnabled
/// transitions from off to on, so re-enabling by hand always starts a fresh
/// count rather than remembering runs from a previous session.
/// </remarks>
public sealed class CrucibleLoopService
{
    private readonly Configuration configuration;
    private readonly Action saveConfiguration;
    private readonly IPluginLog log;
    private bool wasEnabled;
    private bool wasNpcDetected;
    private bool hasSeenNpcThisSession;

    public CrucibleLoopService(Configuration configuration, Action saveConfiguration, IPluginLog log)
    {
        this.configuration = configuration;
        this.saveConfiguration = saveConfiguration;
        this.log = log;
    }

    public int LoopsCompleted { get; private set; }

    /// <summary>Short human status for the main window.</summary>
    public string Status { get; private set; } = "off";

    /// <summary>Call once per framework tick with CrucibleEntryService's current NpcDetected value.</summary>
    public void Tick(bool npcDetected)
    {
        var enabled = this.configuration.AutoEnterCrucibleEnabled;
        if (enabled && !this.wasEnabled)
        {
            this.LoopsCompleted = 0;
            this.hasSeenNpcThisSession = false;
            this.wasNpcDetected = false;
        }

        this.wasEnabled = enabled;

        if (!enabled)
        {
            this.Status = "off";
            this.wasNpcDetected = npcDetected;
            return;
        }

        var justDetected = npcDetected && !this.wasNpcDetected;
        this.wasNpcDetected = npcDetected;

        if (!justDetected)
        {
            this.Status = this.Describe();
            return;
        }

        if (!this.hasSeenNpcThisSession)
        {
            // First-ever detection this session: the start of loop 1, not a completion.
            this.hasSeenNpcThisSession = true;
            this.Status = this.Describe();
            return;
        }

        this.OnLoopBoundary();
    }

    private void OnLoopBoundary()
    {
        this.LoopsCompleted++;
        var target = this.configuration.CrucibleLoopCount == 0 ? "無制限" : this.configuration.CrucibleLoopCount.ToString();
        this.log.Information("[BeastHelper] Crucible loop: completed {Count}/{Target} (NPC re-detected).", this.LoopsCompleted, target);

        if (this.configuration.CrucibleLoopCount > 0 && this.LoopsCompleted >= this.configuration.CrucibleLoopCount)
        {
            this.configuration.AutoEnterCrucibleEnabled = false;
            this.saveConfiguration();
            this.wasEnabled = false;
            this.Status = $"{this.LoopsCompleted}周完了、自動突入をOFFにしました";
            this.log.Information("[BeastHelper] Crucible loop: reached the configured loop count; auto-entry turned off.");
            return;
        }

        this.Status = this.Describe();
    }

    private string Describe()
        => this.configuration.CrucibleLoopCount == 0
            ? $"{this.LoopsCompleted}周完了 (無制限)"
            : $"{this.LoopsCompleted}/{this.configuration.CrucibleLoopCount}周完了";
}
