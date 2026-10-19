using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BeastNav.Services;

/// <summary>
/// Auto-closes the mid-board shop screen (<c>XBMContentsItemShop</c>, node
/// id 40 — a Button Component Node) without buying anything, then confirms
/// the "アイテム購入を終了してよろしいですか?" Yes/No prompt that follows it
/// by clicking the shared <c>SelectYesno</c> addon's own <c>YesButton</c>
/// field directly.
/// </summary>
/// <remarks>
/// The close button is its own dedicated addon (XBMContentsItemShop) that
/// only ever appears for this one purpose, so it's safe to click directly.
/// The Yes/No prompt afterward is the game's generic, shared SelectYesno
/// addon — used for hundreds of unrelated confirmations — so it's only ever
/// confirmed when its prompt text contains <see cref="ConfirmMarker"/>. Same
/// pattern as CrucibleLootService's own claim confirmation.
/// </remarks>
/// <remarks>
/// A plain ReceiveEvent replay, not a FireCallback — confirmed via Addon
/// Inspector to be an ordinary Button Component Node, the same kind
/// CrucibleResultService's and CrucibleTreasureService's own buttons
/// already are. Neither has ever been implicated in a crash (unlike the
/// board-select FireCallback — see CrucibleBoardService's remarks), so this
/// uses the identical click mechanism.
/// </remarks>
/// <remarks>
/// Unattended UI interaction — against the FFXIV ToS, self-responsibility.
/// Off unless AutoEnterCrucibleEnabled is on. Never picks or buys anything;
/// only closes the shop.
/// </remarks>
public sealed unsafe class CrucibleShopService
{
    private const string ShopAddonName = "XBMContentsItemShop";
    private const uint CloseButtonNodeId = 40;
    private const string ConfirmAddonName = "SelectYesno";
    private const string ConfirmMarker = "アイテム購入を終了";
    private static readonly TimeSpan CloseThrottle = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ConfirmThrottle = TimeSpan.FromSeconds(1.5);

    private readonly Configuration configuration;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private DateTime nextCloseAttempt = DateTime.MinValue;
    private DateTime nextConfirmAttempt = DateTime.MinValue;

    // The confirm is only answered right after this service clicked its own
    // button, so a SelectYesno with similar text from anything else is never touched.
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(5);
    private DateTime lastOwnClick = DateTime.MinValue;

    public CrucibleShopService(Configuration configuration, IGameGui gameGui, IPluginLog log)
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

        if (this.TryConfirmClose())
        {
            return;
        }

        this.TryClickClose();
    }

    private void TryClickClose()
    {
        var now = DateTime.UtcNow;
        if (now < this.nextCloseAttempt)
        {
            return;
        }

        AtkUnitBase* addon;
        try
        {
            var ptr = this.gameGui.GetAddonByName(ShopAddonName);
            addon = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible shop: addon lookup failed.");
            return;
        }

        if (addon is null || !addon->IsVisible)
        {
            this.Status = "ショップ画面待ち";
            return;
        }

        var node = addon->GetNodeById(CloseButtonNodeId);
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

        this.nextCloseAttempt = now + CloseThrottle;
        this.lastOwnClick = now;
        this.Status = "何も買わずに終了をクリック";
        this.log.Information("[BeastHelper] Crucible shop: clicked 終了.");
    }

    /// <summary>
    /// Returns true (and consumes the tick) when the shared SelectYesno addon
    /// is open with our close-confirmation text — regardless of whether it
    /// actually clicked yet, so an unrelated SelectYesno never falls through
    /// into the close-button click above and races it.
    /// </summary>
    private bool TryConfirmClose()
    {
        if (DateTime.UtcNow - this.lastOwnClick > ConfirmWindow)
        {
            return false;
        }

        AtkUnitBase* addon;
        try
        {
            var ptr = this.gameGui.GetAddonByName(ConfirmAddonName);
            addon = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible shop: SelectYesno lookup failed.");
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
            this.Status = "終了確認(はい)をクリック";
            this.log.Information("[BeastHelper] Crucible shop: clicked はい to confirm closing.");
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
