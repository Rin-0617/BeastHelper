using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BeastNav.Services;

/// <summary>
/// Picks a temporary item on the mid-board treasure screen
/// (<c>XBMContentsTreasure</c>) whenever it opens, based on
/// <see cref="Configuration.CrucibleTreasureKeywords"/> — the first keyword
/// (by list order) found on any of the 4 offered cards' effect text wins.
/// If none match (or none are configured), takes the first card anyway
/// rather than walking away with nothing — some stat boost beats none.
/// Either way, confirms the shared <c>SelectYesno</c> prompt that follows a
/// card click.
/// </summary>
/// <remarks>
/// The 4 cards are top-level Button Component Nodes (node ids 8, 9, 10, 12
/// — confirmed via NearbyObjectDumpService/Addon Inspector), the same kind
/// CrucibleResultService's and CrucibleLootService's own buttons already
/// are, so clicking one uses the identical, already-proven ReceiveEvent
/// replay on the addon itself. Reading a card's effect text is a separate,
/// purely read-only step: each card's full multi-line description (stat
/// lines followed by the item name) lives one component deeper inside it —
/// confirmed by drilling into each card's own node list and finding a
/// nested Button Component Node whose own text node holds it. Neither step
/// resembles the nested-list ReceiveEvent dispatch that crashed on
/// XBMPetParty's beast rows (see LuaAutomationService's own remarks) — that
/// crash was about *writing* an event into a nested component, not reading
/// its text, and these are still top-level addon-owned buttons to click.
/// </remarks>
/// <remarks>
/// Unattended UI interaction — against the FFXIV ToS, self-responsibility.
/// Off unless AutoEnterCrucibleEnabled is on.
/// </remarks>
public sealed unsafe class CrucibleTreasureService
{
    private const string TreasureAddonName = "XBMContentsTreasure";
    private static readonly uint[] CardNodeIds = [8, 9, 10, 12];
    private const string ConfirmAddonName = "SelectYesno";
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ConfirmThrottle = TimeSpan.FromSeconds(1.5);

    // Same class of race condition CrucibleBoardService's own remarks
    // documented for XBMStageList: acting the instant an addon becomes
    // visible can catch it before its own text/data has actually populated.
    // Here that meant reading all 4 cards as empty text, matching no
    // keyword, and clicking 終了 despite the real (not-yet-rendered) card
    // text actually matching one — a settle delay avoids reading too early.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(500);

    private readonly Configuration configuration;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private DateTime nextAttempt = DateTime.MinValue;
    private DateTime nextConfirmAttempt = DateTime.MinValue;
    private DateTime? visibleSinceUtc;

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

        if (this.TryConfirmClaim())
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now < this.nextAttempt)
        {
            return;
        }

        var addon = this.FindTreasureAddon();

        if (addon is null || !addon->IsVisible)
        {
            this.visibleSinceUtc = null;
            this.Status = "宝箱画面待ち";
            return;
        }

        this.visibleSinceUtc ??= now;
        var elapsed = now - this.visibleSinceUtc.Value;
        if (elapsed < SettleDelay)
        {
            this.Status = $"表示待機中 ({(SettleDelay - elapsed).TotalSeconds:0.0}秒)";
            return;
        }

        var cardTexts = new string[CardNodeIds.Length];
        for (var i = 0; i < CardNodeIds.Length; i++)
        {
            cardTexts[i] = ReadCardText(addon, CardNodeIds[i]);
        }

        uint? chosenNodeId = null;
        string? matchedKeyword = null;
        foreach (var keyword in this.configuration.CrucibleTreasureKeywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                continue;
            }

            for (var i = 0; i < CardNodeIds.Length; i++)
            {
                if (cardTexts[i].Contains(keyword, StringComparison.Ordinal))
                {
                    chosenNodeId = CardNodeIds[i];
                    matchedKeyword = keyword;
                    break;
                }
            }

            if (chosenNodeId is not null)
            {
                break;
            }
        }

        // No keyword matched (or none configured) — take the first card
        // rather than walking away empty-handed with 終了. A treasure card
        // is always at least some stat boost, so something beats nothing.
        var fallbackNodeId = CardNodeIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(cardTexts[Array.IndexOf(CardNodeIds, id)]));
        var targetNodeId = chosenNodeId ?? (fallbackNodeId != 0 ? fallbackNodeId : CardNodeIds[0]);
        if (!this.TryClickButton(addon, targetNodeId))
        {
            this.Status = "ボタン未検出";
            return;
        }

        this.nextAttempt = now + Throttle;
        this.visibleSinceUtc = null;
        if (chosenNodeId is not null)
        {
            this.Status = $"「{matchedKeyword}」に一致 → 取得";
            this.log.Information("[BeastHelper] Crucible treasure: NodeId {NodeId} matched \"{Keyword}\"; claimed.", chosenNodeId, matchedKeyword ?? string.Empty);
        }
        else
        {
            this.Status = $"該当なし → NodeId {targetNodeId} を取得";
            this.log.Information("[BeastHelper] Crucible treasure: no keyword matched any of the 4 cards; claimed NodeId {NodeId} instead of skipping.", targetNodeId);
        }
    }

    /// <summary>Reads a card's full effect text (its own direct text, plus its nested sub-component's text, concatenated) for keyword matching. Read-only — never touches this node's events.</summary>
    private static string ReadCardText(AtkUnitBase* addon, uint cardNodeId)
    {
        var cardNode = addon->GetNodeById(cardNodeId);
        var cardComponent = cardNode is null ? null : cardNode->GetComponent();
        if (cardComponent is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var nodeList = cardComponent->UldManager.NodeList;
        var count = cardComponent->UldManager.NodeListCount;
        for (var i = 0; i < count; i++)
        {
            var child = nodeList[i];
            if (child is null)
            {
                continue;
            }

            if (child->Type == NodeType.Text)
            {
                sb.Append(((AtkTextNode*)child)->NodeText.ToString()).Append('\n');
                continue;
            }

            // The actual multi-line effect description lives one level
            // deeper still, inside this component-backed child.
            var childComponent = child->GetComponent();
            if (childComponent is null)
            {
                continue;
            }

            var innerList = childComponent->UldManager.NodeList;
            var innerCount = childComponent->UldManager.NodeListCount;
            for (var j = 0; j < innerCount; j++)
            {
                var inner = innerList[j];
                if (inner is not null && inner->Type == NodeType.Text)
                {
                    sb.Append(((AtkTextNode*)inner)->NodeText.ToString()).Append('\n');
                }
            }
        }

        return sb.ToString();
    }

    private bool TryClickButton(AtkUnitBase* addon, uint nodeId)
    {
        var node = addon->GetNodeById(nodeId);
        if (node is null)
        {
            return false;
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
            return false;
        }

        return this.TryClick(addon, &button->AtkComponentBase);
    }

    private AtkUnitBase* FindTreasureAddon()
    {
        try
        {
            var ptr = this.gameGui.GetAddonByName(TreasureAddonName);
            return ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible treasure: addon lookup failed.");
            return null;
        }
    }

    /// <summary>
    /// Returns true (and consumes the tick) when the shared SelectYesno addon
    /// is open while XBMContentsTreasure is still visible behind it —
    /// regardless of whether it actually clicked yet, so an unrelated
    /// SelectYesno never falls through into the card-click logic above and
    /// races it. Matched by context, not prompt text: a card click's own
    /// "「〜」を入手します。よろしいですか?" confirm and 終了's own
    /// (differently-worded) confirm both needed handling, and guessing the
    /// exact wording of the latter wasn't necessary once "did we just act on
    /// this exact screen" was available as a safe signal instead. Missing
    /// any confirm handling at all was the original bug: every button click
    /// spawns one of these prompts, and without ever answering it, the next
    /// Tick() just clicked the (still-uncleared) button again, stacking up
    /// duplicate confirmation dialogs.
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
            this.log.Debug(ex, "[BeastHelper] Crucible treasure: SelectYesno lookup failed.");
            return false;
        }

        if (addon is null || !addon->IsVisible)
        {
            return false;
        }

        // Matched by context (XBMContentsTreasure still visible behind the
        // prompt) rather than by prompt text: clicking a card produces a
        // "「〜」を入手します。よろしいですか?" confirm, but clicking 終了
        // (no keyword matched) turned out to have its own, differently-worded
        // confirm that a text-marker check for the claim wording alone
        // never matched — leaving it unanswered and stacking up duplicate
        // dialogs the same way the original missing-confirm bug did. Since
        // we ourselves just deliberately clicked one of only two buttons on
        // this screen, any SelectYesno appearing while the treasure addon is
        // still open is safe to confirm without knowing its exact wording.
        var treasureAddon = this.FindTreasureAddon();
        if (treasureAddon is null || !treasureAddon->IsVisible)
        {
            return false;
        }

        var selectYesno = (AddonSelectYesno*)addon;
        var promptText = selectYesno->PromptText is not null ? selectYesno->PromptText->NodeText.ToString() : string.Empty;

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
            this.log.Information("[BeastHelper] Crucible treasure: confirmed \"{Prompt}\".", promptText);
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
