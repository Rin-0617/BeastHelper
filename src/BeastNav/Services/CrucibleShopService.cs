using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BeastNav.Services;

/// <summary>
/// On the mid-board shop screen (<c>XBMContentsItemShop</c>), optionally buys
/// items in a fixed priority order — healing items first (up to a held cap),
/// then gear not already owned, then at most one feed item per visit — then
/// closes the shop (<c>XBMContentsItemShop</c> node id 40, a Button
/// Component Node) and confirms the "アイテム購入を終了してよろしいですか?"
/// Yes/No prompt that follows.
/// </summary>
/// <remarks>
/// The buying logic and its three item-id lists are ported (as data and
/// technique, not code) from erdelf/AutoDuty's own Crucible shop automation
/// (<c>CrucibleMenus.ChooseBuy</c>/<c>CrucibleItemData</c>), read this
/// session. Buying itself is a FireCallback (<c>callback(shop, 2, index)</c>
/// — AutoDuty's own <c>Screens.ItemShop.Buy</c>), the same mechanism already
/// used safely elsewhere in this codebase; the shop's own stock/coins/held/
/// owned-gear data is read from its AtkValues by the same fixed indices
/// AutoDuty's own <c>CrucibleUi.ShopStock</c>/<c>ShopHeldItems</c>/
/// <c>ShopOwnedGear</c> use. Item ids are XBMItem sheet rows (a Crucible-only
/// catalog, unrelated to the game's normal Item sheet) — the shop's own UI
/// doesn't expose a readable per-slot name text node the way the treasure
/// screen's cards do, so this uses AutoDuty's own pre-verified id lists
/// rather than a free-text keyword list like CrucibleTreasureKeywords.
/// </remarks>
/// <remarks>
/// Unattended UI interaction and purchases — against the FFXIV ToS, self-
/// responsibility. Off unless AutoEnterCrucibleEnabled is on; buying itself
/// is further gated by AutoCrucibleShopBuyEnabled (off by default — with it
/// off, behavior is unchanged from before: close without buying anything).
/// </remarks>
public sealed unsafe class CrucibleShopService
{
    private const string ShopAddonName = "XBMContentsItemShop";
    private const uint CloseButtonNodeId = 40;
    private const string ConfirmAddonName = "SelectYesno";
    private const string CloseConfirmMarker = "アイテム購入を終了";
    private const string BuyConfirmMarker = "購入";
    private static readonly TimeSpan ActionThrottle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ConfirmThrottle = TimeSpan.FromSeconds(1.5);

    // XBMItem sheet rows (a Crucible-only item catalog). Ported from
    // erdelf/AutoDuty's own CrucibleItemData — priority order matters,
    // most-preferred first within each category.
    private static readonly uint[] HealingItems =
    [
        140, 79, 78, 77, 76, 82, 81, 80,
    ];

    private static readonly uint[] GearItems =
    [
        23, 3, 73, 2, 27, 24, 15, 49, 21, 47, 25, 16, 35, 44, 9, 11, 51, 58, 72, 32,
        62, 48, 28, 14, 54, 26, 66, 52, 70, 34, 13, 69, 29, 1, 20, 12, 31, 37, 53, 10,
        56, 63, 17, 33, 36, 75, 6, 5, 68, 60, 61, 50, 71, 65, 19, 57, 7, 8, 30, 38,
        39, 40, 41, 42, 43, 64, 22, 46, 55, 74, 45, 59, 67, 18, 4,
    ];

    private static readonly uint[] FeedItems =
    [
        144, 145, 149, 153, 155, 163, 165, 174, 185, 188, 191, 202, 203, 148, 154,
        157, 158, 159, 160, 161, 166, 169, 171, 175, 176, 177, 178, 179, 180, 181,
        182, 183, 184, 187, 194, 195, 197, 146, 147, 150, 151, 152, 156, 162, 164,
        167, 170, 172, 173, 186, 189, 190, 192, 193, 196, 198, 199, 200, 201, 168,
    ];

    // Shop's own AtkValues layout — ported from AutoDuty's own CrucibleUi.ShopValues.
    private const int CoinsIndex = 1;
    private const int StockCountIndex = 2;
    private const int StockStart = 3;
    private const int StockStride = 5;
    private const int StockListedOffset = 0;
    private const int StockItemOffset = 1;
    private const int StockPriceOffset = 2;
    private const int StockBoughtOffset = 4;
    private const int HeldStart = 154;
    private const int HeldItemOffset = 3;
    private const int GearStart = 205;
    private const int GearOwnedOffset = 0;
    private const int GearItemOffset = 3;
    private const int ShopSlots = 10;

    private readonly Configuration configuration;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private DateTime nextActionAttempt = DateTime.MinValue;
    private DateTime nextConfirmAttempt = DateTime.MinValue;
    private readonly HashSet<int> triedThisVisit = [];
    private bool fedThisVisit;
    private bool wasOpen;

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

        if (this.TryConfirmPrompt())
        {
            return;
        }

        var addon = this.FindAddon(ShopAddonName);
        if (addon is null || !addon->IsVisible)
        {
            this.Status = "ショップ画面待ち";
            this.wasOpen = false;
            return;
        }

        if (!this.wasOpen)
        {
            this.wasOpen = true;
            this.triedThisVisit.Clear();
            this.fedThisVisit = false;
        }

        if (this.configuration.AutoCrucibleShopBuyEnabled && this.TryBuyNext(addon))
        {
            return;
        }

        this.TryClickClose(addon);
    }

    private bool TryBuyNext(AtkUnitBase* shop)
    {
        var now = DateTime.UtcNow;
        if (now < this.nextActionAttempt || shop->AtkValuesCount <= StockCountIndex)
        {
            return false;
        }

        var coins = ReadNumber(shop->AtkValues[CoinsIndex]);
        var stockCount = (int)ReadNumber(shop->AtkValues[StockCountIndex]);
        var heldCount = this.CountHeld(shop);
        var ownedGear = this.ReadOwnedGear(shop);

        for (var i = 0; i < stockCount; i++)
        {
            var at = StockStart + i * StockStride;
            if (at + StockBoughtOffset >= shop->AtkValuesCount || this.triedThisVisit.Contains(i))
            {
                continue;
            }

            if (shop->AtkValues[at + StockListedOffset].Byte == 0)
            {
                continue;
            }

            var row = ReadNumber(shop->AtkValues[at + StockItemOffset]);
            var price = ReadNumber(shop->AtkValues[at + StockPriceOffset]);
            var bought = shop->AtkValues[at + StockBoughtOffset].Byte != 0;
            if (row == 0 || bought || price > coins)
            {
                continue;
            }

            if (this.configuration.CrucibleShopBuyHealing && heldCount < this.configuration.CrucibleShopHealingCap && Array.IndexOf(HealingItems, row) >= 0)
            {
                return this.Buy(shop, i, row, "回復薬");
            }
        }

        for (var i = 0; i < stockCount; i++)
        {
            var at = StockStart + i * StockStride;
            if (at + StockBoughtOffset >= shop->AtkValuesCount || this.triedThisVisit.Contains(i))
            {
                continue;
            }

            var row = ReadNumber(shop->AtkValues[at + StockItemOffset]);
            var price = ReadNumber(shop->AtkValues[at + StockPriceOffset]);
            var bought = shop->AtkValues[at + StockBoughtOffset].Byte != 0;
            if (shop->AtkValues[at + StockListedOffset].Byte == 0 || row == 0 || bought || price > coins)
            {
                continue;
            }

            if (this.configuration.CrucibleShopBuyGear && !ownedGear.Contains(row) && Array.IndexOf(GearItems, row) >= 0)
            {
                return this.Buy(shop, i, row, "防具");
            }
        }

        if (this.configuration.CrucibleShopBuyFeed && !this.fedThisVisit)
        {
            for (var i = 0; i < stockCount; i++)
            {
                var at = StockStart + i * StockStride;
                if (at + StockBoughtOffset >= shop->AtkValuesCount || this.triedThisVisit.Contains(i))
                {
                    continue;
                }

                var row = ReadNumber(shop->AtkValues[at + StockItemOffset]);
                var price = ReadNumber(shop->AtkValues[at + StockPriceOffset]);
                var bought = shop->AtkValues[at + StockBoughtOffset].Byte != 0;
                if (shop->AtkValues[at + StockListedOffset].Byte == 0 || row == 0 || bought || price > coins)
                {
                    continue;
                }

                if (Array.IndexOf(FeedItems, row) >= 0)
                {
                    this.fedThisVisit = true;
                    return this.Buy(shop, i, row, "えさ");
                }
            }
        }

        return false;
    }

    private bool Buy(AtkUnitBase* shop, int slotIndex, uint itemRow, string category)
    {
        this.triedThisVisit.Add(slotIndex);
        FireCallback(shop, 2, slotIndex);
        this.nextActionAttempt = DateTime.UtcNow + ActionThrottle;
        this.Status = $"{category}(item {itemRow})を購入中…";
        this.log.Information("[BeastHelper] Crucible shop: buying {Category} item {Row} (slot {Slot}).", category, itemRow, slotIndex);
        return true;
    }

    private static int CountHeldInternal(AtkUnitBase* shop)
    {
        var held = 0;
        for (var k = 0; k < ShopSlots; k++)
        {
            var at = HeldStart + k * StockStride + HeldItemOffset;
            if (at < shop->AtkValuesCount && ReadNumber(shop->AtkValues[at]) != 0)
            {
                held++;
            }
        }

        return held;
    }

    private int CountHeld(AtkUnitBase* shop) => CountHeldInternal(shop);

    private HashSet<uint> ReadOwnedGear(AtkUnitBase* shop)
    {
        var owned = new HashSet<uint>();
        for (var k = 0; k < ShopSlots; k++)
        {
            var at = GearStart + k * StockStride;
            if (at + GearItemOffset >= shop->AtkValuesCount || shop->AtkValues[at + GearOwnedOffset].Byte == 0)
            {
                continue;
            }

            owned.Add(ReadNumber(shop->AtkValues[at + GearItemOffset]));
        }

        return owned;
    }

    private static uint ReadNumber(AtkValue value) => value.Type.ToString() switch
    {
        "Int" => value.Int >= 0 ? (uint)value.Int : 0,
        "UInt" => value.UInt,
        _ => 0,
    };

    private static void FireCallback(AtkUnitBase* addon, params int[] values)
    {
        var atkValues = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            atkValues[i] = new AtkValue();
            atkValues[i].SetInt(values[i]);
        }

        addon->FireCallback((uint)values.Length, atkValues, true);
    }

    private void TryClickClose(AtkUnitBase* addon)
    {
        var now = DateTime.UtcNow;
        if (now < this.nextActionAttempt)
        {
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

        this.nextActionAttempt = now + ActionThrottle;
        this.Status = "終了をクリック";
        this.log.Information("[BeastHelper] Crucible shop: clicked 終了.");
    }

    /// <summary>
    /// Returns true (and consumes the tick) when the shared SelectYesno addon
    /// is open with either the close-confirmation text or a purchase
    /// confirmation (matched more loosely, by "購入", since the exact
    /// purchase-prompt wording hasn't been confirmed live) — regardless of
    /// whether it actually clicked yet, so an unrelated SelectYesno never
    /// falls through into the buy/close logic above and races it.
    /// </summary>
    private bool TryConfirmPrompt()
    {
        var addon = this.FindAddon(ConfirmAddonName);
        if (addon is null || !addon->IsVisible)
        {
            return false;
        }

        var selectYesno = (AddonSelectYesno*)addon;
        var promptText = selectYesno->PromptText is not null ? selectYesno->PromptText->NodeText.ToString() : string.Empty;
        if (!promptText.Contains(CloseConfirmMarker, StringComparison.Ordinal) && !promptText.Contains(BuyConfirmMarker, StringComparison.Ordinal))
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
            this.Status = "確認(はい)をクリック";
            this.log.Information("[BeastHelper] Crucible shop: confirmed \"{Prompt}\".", promptText);
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

    private AtkUnitBase* FindAddon(string name)
    {
        try
        {
            var ptr = this.gameGui.GetAddonByName(name);
            return ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible shop: addon lookup failed.");
            return null;
        }
    }
}
