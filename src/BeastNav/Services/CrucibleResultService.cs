using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BeastNav.Services;

/// <summary>
/// Auto-clicks "次のページ" (next page) on the post-battle results screen
/// (<c>XBMResult</c>, node id 61 — a Button Component Node) so the score
/// summary doesn't sit there waiting for a manual click before the loot
/// screen (<c>XBMContentsBooty</c>, handled by <see cref="CrucibleLootService"/>)
/// can appear.
/// </summary>
/// <remarks>
/// A plain ReceiveEvent replay, not a FireCallback — confirmed via Addon
/// Inspector to be an ordinary Button Component Node, the same kind
/// CrucibleBoardService's 戦闘開始 button and CrucibleLootService's すべて
/// 入手 button already are. Neither of those has ever been implicated in a
/// crash (unlike the board-select FireCallback — see CrucibleBoardService's
/// remarks), so this uses the identical click mechanism.
/// </remarks>
/// <remarks>
/// Unattended UI interaction — against the FFXIV ToS, self-responsibility.
/// Off unless AutoEnterCrucibleEnabled is on. Never reads or acts on the
/// score/reward contents themselves — only advances the page.
/// </remarks>
public sealed unsafe class CrucibleResultService
{
    private const string ResultAddonName = "XBMResult";
    private const uint NextPageButtonNodeId = 61;
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(1.5);

    private readonly Configuration configuration;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private DateTime nextAttempt = DateTime.MinValue;

    public CrucibleResultService(Configuration configuration, IGameGui gameGui, IPluginLog log)
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
            var ptr = this.gameGui.GetAddonByName(ResultAddonName);
            addon = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible result: addon lookup failed.");
            return;
        }

        if (addon is null || !addon->IsVisible)
        {
            this.Status = "結果画面待ち";
            return;
        }

        var node = addon->GetNodeById(NextPageButtonNodeId);
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
        this.Status = "次のページをクリック";
        this.log.Information("[BeastHelper] Crucible result: clicked 次のページ.");
    }
}
