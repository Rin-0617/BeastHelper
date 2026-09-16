using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BeastNav.Services;

/// <summary>
/// Auto-clicks "すべて入手" (claim all) on the post-battle Crucible loot
/// screen (<c>XBMContentsBooty</c>, node id 46 — a Button Component Node),
/// then confirms the "を全て入手します。よろしいですか?" Yes/No prompt that
/// follows it by clicking the shared <c>SelectYesno</c> addon's own
/// <c>YesButton</c> field directly.
/// </summary>
/// <remarks>
/// The claim button is its own dedicated addon (XBMContentsBooty) that only
/// ever appears for this one purpose, so it's safe to click directly. The
/// Yes/No prompt afterward is the game's generic, shared SelectYesno addon —
/// used for hundreds of unrelated confirmations — so it's only ever confirmed
/// when its prompt text contains <see cref="ConfirmMarker"/>.
/// </remarks>
/// <remarks>
/// Unattended UI interaction — against the FFXIV ToS, self-responsibility.
/// Off by default. Never picks which items to keep individually.
/// </remarks>
public sealed unsafe class CrucibleLootService
{
    private const string LootAddonName = "XBMContentsBooty";
    private const uint ClaimAllNodeId = 46;
    private const string ConfirmAddonName = "SelectYesno";
    private const string ConfirmMarker = "入手します";
    private static readonly TimeSpan ClaimThrottle = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ConfirmThrottle = TimeSpan.FromSeconds(1.5);

    private readonly Configuration configuration;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private DateTime nextClaimAttempt = DateTime.MinValue;
    private DateTime nextConfirmAttempt = DateTime.MinValue;

    public CrucibleLootService(Configuration configuration, IGameGui gameGui, IPluginLog log)
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

        if (this.TryConfirmClaim())
        {
            return;
        }

        this.TryClickClaimAll();
    }

    private void TryClickClaimAll()
    {
        var now = DateTime.UtcNow;
        if (now < this.nextClaimAttempt)
        {
            return;
        }

        AtkUnitBase* addon;
        try
        {
            var ptr = this.gameGui.GetAddonByName(LootAddonName);
            addon = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible loot: addon lookup failed.");
            return;
        }

        if (addon is null || !addon->IsVisible)
        {
            this.Status = "戦利品画面待ち";
            return;
        }

        var node = addon->GetNodeById(ClaimAllNodeId);
        if (node is null)
        {
            this.Status = "ボタン未検出";
            return;
        }

        // Not node->Type != NodeType.Component: the raw Type field on a
        // component-backed node holds an internal component-kind value
        // (e.g. 1002 for a button), not the normalized NodeType.Component
        // (10000) that only GetNodeType() returns — comparing against that
        // directly always failed. GetComponent() sidesteps it entirely.
        var button = (AtkComponentButton*)node->GetComponent();
        if (button is null)
        {
            this.Status = "ボタン未検出";
            return;
        }

        if (!this.TryClick(addon, &button->AtkComponentBase))
        {
            this.Status = "クリック失敗";
            return;
        }

        this.nextClaimAttempt = now + ClaimThrottle;
        this.Status = "すべて入手をクリック";
        this.log.Information("[BeastHelper] Crucible loot: clicked すべて入手.");
    }

    /// <summary>
    /// Returns true (and consumes the tick) when the shared SelectYesno addon
    /// is open with our claim-confirmation text — regardless of whether it
    /// actually clicked yet, so an unrelated SelectYesno never falls through
    /// into the claim-all click above and races it.
    /// </summary>
    private bool TryConfirmClaim()
    {
        AtkUnitBase* addon;
        try
        {
            var ptr = this.gameGui.GetAddonByName(ConfirmAddonName);
            addon = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible loot: SelectYesno lookup failed.");
            return false;
        }

        if (addon is null || !addon->IsVisible)
        {
            return false;
        }

        var selectYesno = (AddonSelectYesno*)addon;
        var promptText = selectYesno->PromptText is not null ? selectYesno->PromptText->NodeText.ToString() : string.Empty;
        if (!promptText.Contains(ConfirmMarker, StringComparison.Ordinal))
        {
            // Some other Yes/No confirmation — leave it alone.
            return false;
        }

        var now = DateTime.UtcNow;
        if (now < this.nextConfirmAttempt)
        {
            this.Status = "確認待ち";
            return true;
        }

        var yesButton = selectYesno->YesButton;
        if (yesButton is null)
        {
            this.Status = "はいボタン未検出";
            return true;
        }

        this.nextConfirmAttempt = now + ConfirmThrottle;
        if (this.TryClick(addon, &yesButton->AtkComponentBase))
        {
            this.Status = "入手確認(はい)をクリック";
            this.log.Information("[BeastHelper] Crucible loot: clicked はい to confirm the claim.");
        }
        else
        {
            this.Status = "クリック失敗";
        }

        return true;
    }

    private bool TryClick(AtkUnitBase* addon, AtkComponentBase* component)
    {
        var ownerNode = component->OwnerNode;
        if (ownerNode is null)
        {
            return false;
        }

        var evt = ownerNode->AtkResNode.AtkEventManager.Event;
        if (evt is null)
        {
            return false;
        }

        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        return true;
    }
}
