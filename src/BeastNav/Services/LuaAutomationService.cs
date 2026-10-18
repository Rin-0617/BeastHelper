using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MoonSharp.Interpreter;

namespace BeastNav.Services;

/// <summary>
/// Runs small, user-authored Lua scripts (via MoonSharp — a pure C# Lua
/// implementation, no native binary) that drive the same native game
/// primitives BeastHelper's other Crucible services use directly
/// (target/interact/click/callback), so per-board or per-screen variations
/// can be scripted and edited from BeastHelper's own UI instead of hardcoding
/// a C# branch for every case or depending on an external macro plugin.
/// </summary>
/// <remarks>
/// Scripts run as MoonSharp coroutines so a script's own <c>wait(seconds)</c>
/// suspends only the script, not the game — <see cref="Tick"/> resumes it on
/// a later frame once the requested time has passed, the same way SND's own
/// macro engine avoids blocking the framework thread on a wait.
/// </remarks>
/// <remarks>
/// Unattended movement, NPC interaction, and UI interaction — against the
/// FFXIV ToS, self-responsibility. Scripts only run when explicitly started
/// (<see cref="Run"/>); nothing here runs on its own.
/// </remarks>
/// <remarks>
/// Used to also expose <c>clickNested(addon, containerNodeId, childNodeId,
/// eventTypeName)</c>, for clicking a row nested inside another node's own
/// component (e.g. one row of the XBMPetParty beast list) by dispatching
/// the named event to that container component's own ReceiveEvent rather
/// than the addon's. Confirmed crash: even though the target was a proper
/// component-backed node (not a raw Collision node like the earlier
/// clickChild crash) and the event type itself needed no real coordinate
/// data, routing it through the component's own ReceiveEvent still crashed
/// the game. Root cause, found afterward by reading ECommons' own
/// Automation/UIInput helpers and FFXIVClientStructs' AtkEventData: native
/// ReceiveEvent takes a 4th <c>AtkEventData*</c> argument that clickNested
/// always passed as null — for ListItemClick/ListItemDoubleClick specifically,
/// that argument is a tagged union whose ListItemData variant carries the
/// AtkComponentListItemRenderer* and index the native handler needs to know
/// which row to act on; with it null, that handler likely dereferenced
/// garbage. <see cref="LuaClickListItem"/> replaces it, building a real
/// AtkEventData.ListItemData (via AtkComponentBase.GetComponentItemRendererById
/// and the renderer's own ListItemIndex field) instead of passing null. This
/// is still unverified against a real crash history on this exact screen —
/// treat any new attempt with the same caution as before.
/// </remarks>
/// <remarks>
/// The actual working technique for picking a XBMMonsterNotebook bestiary
/// row (confirmed by reading erdelf/AutoDuty's own Crucible automation,
/// 2026-09) turned out to be different from every attempt above: not
/// ButtonClick/ListItemClick with real ListItemData, but a plain
/// <c>MouseDown</c> event fired with a non-null pointer to a value-
/// initialized (all-zero) <see cref="AtkEventData"/> — see
/// <see cref="LuaClickMouseDown"/>. AutoDuty finds that event by recursively
/// searching the ENTIRE node tree for an (EventType, Param) match rather
/// than assuming it lives on any specific node, since it isn't on the
/// addon's own top-level node list. This is a genuinely different, promising
/// lead — still verify live before trusting it for real automation.
/// </remarks>
public sealed unsafe class LuaAutomationService
{
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    private Coroutine? running;
    private DateTime resumeAt = DateTime.MinValue;

    public LuaAutomationService(IObjectTable objectTable, ITargetManager targetManager, IGameGui gameGui, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.gameGui = gameGui;
        this.log = log;
    }

    /// <summary>Short human status for the main window.</summary>
    public string Status { get; private set; } = "待機中";

    public bool IsRunning => this.running is { State: CoroutineState.Suspended or CoroutineState.ForceSuspended };

    /// <summary>Starts running the given script from the top, replacing any script already in progress.</summary>
    public void Run(string source)
    {
        try
        {
            var script = new Script(CoreModules.Basic | CoreModules.String | CoreModules.Table | CoreModules.Math | CoreModules.Coroutine);
            script.Globals["target"] = (Action<string>)this.LuaTarget;
            script.Globals["interact"] = (Action)this.LuaInteract;
            script.Globals["click"] = (Action<string, uint>)this.LuaClick;
            script.Globals["clickListItem"] = (Action<string, uint, uint, string, bool>)this.LuaClickListItem;
            script.Globals["clickMouseDown"] = (Action<string, uint>)this.LuaClickMouseDown;
            script.Globals["callback"] = DynValue.NewCallback(this.LuaCallback);
            script.Globals["selectItem"] = (Action<string, uint, int>)this.LuaSelectItem;
            script.Globals["log"] = (Action<string>)(message => this.log.Information("[BeastHelper] Lua: {Message}", message));

            // wait(n) has to be plain Lua so it can actually suspend the
            // coroutine (a C# callback can't yield Lua's own coroutine for
            // it) — Tick() resumes the coroutine once the requested time
            // has passed, the same way SND's own macro engine avoids
            // blocking the framework thread on a wait.
            script.DoString("function wait(seconds) coroutine.yield(seconds) end");

            var func = script.LoadString(source);
            this.running = script.CreateCoroutine(func).Coroutine;
            this.resumeAt = DateTime.MinValue;
            this.Status = "実行中";
            this.Resume();
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[BeastHelper] Lua: script failed to start.");
            this.Status = $"エラー: {ex.Message}";
            this.running = null;
        }
    }

    public void Stop()
    {
        this.running = null;
        this.Status = "停止";
    }

    public void Tick()
    {
        if (this.running is null || !this.IsRunning)
        {
            return;
        }

        if (DateTime.UtcNow < this.resumeAt)
        {
            return;
        }

        this.Resume();
    }

    private void Resume()
    {
        if (this.running is null)
        {
            return;
        }

        try
        {
            var result = this.running.Resume();
            if (this.running.State is CoroutineState.Dead)
            {
                this.Status = "完了";
                this.running = null;
                return;
            }

            // A yielded number is a wait(seconds) request; anything else just
            // means "come back next frame".
            var delaySeconds = result.Type == DataType.Number ? result.Number : 0;
            this.resumeAt = DateTime.UtcNow.AddSeconds(delaySeconds);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[BeastHelper] Lua: script raised an error.");
            this.Status = $"エラー: {ex.Message}";
            this.running = null;
        }
    }

    private void LuaTarget(string name)
    {
        foreach (var obj in this.objectTable)
        {
            if (string.Equals(obj.Name.TextValue, name, StringComparison.Ordinal))
            {
                this.targetManager.Target = obj;
                return;
            }
        }

        this.log.Information("[BeastHelper] Lua: target(\"{Name}\") — not found.", name);
    }

    private void LuaInteract()
    {
        var target = this.targetManager.Target;
        if (target is null)
        {
            this.log.Information("[BeastHelper] Lua: interact() — no current target.");
            return;
        }

        var targetSystem = TargetSystem.Instance();
        if (targetSystem is null)
        {
            return;
        }

        targetSystem->InteractWithObject((GameObject*)target.Address);
    }

    // In priority order: a node can have several event kinds chained via
    // AtkEvent.NextEvent (roll-over, roll-out, click, double-click, ...), and
    // AtkEventManager.Event only gives the head of that chain — which for a
    // list row is often a hover-type event, not the click. Search the whole
    // chain for the first one that actually looks like an activation.
    private static readonly AtkEventType[] ClickEventPriority =
    [
        AtkEventType.ListItemDoubleClick,
        AtkEventType.ListItemClick,
        AtkEventType.ButtonClick,
        AtkEventType.MouseClick,
        AtkEventType.IconTextClick,
        AtkEventType.LinkMouseClick,
        AtkEventType.DragDropClick,
    ];

    /// <summary>Generic node click: replays the node's own registered click-type event, same trick as CrucibleBoardService/CrucibleLootService.</summary>
    private void LuaClick(string addonName, uint nodeId)
    {
        var addon = this.FindAddon(addonName);
        if (addon is null)
        {
            this.log.Information("[BeastHelper] Lua: click(\"{Addon}\", {Node}) — addon not found.", addonName, nodeId);
            return;
        }

        var node = addon->GetNodeById(nodeId);
        if (node is null)
        {
            this.log.Information("[BeastHelper] Lua: click(\"{Addon}\", {Node}) — node not found.", addonName, nodeId);
            return;
        }

        var head = node->AtkEventManager.Event;
        var component = node->GetComponent();
        if (head is null && component is not null)
        {
            head = component->OwnerNode->AtkResNode.AtkEventManager.Event;
        }

        if (head is null)
        {
            this.log.Information("[BeastHelper] Lua: click(\"{Addon}\", {Node}) — node has no events.", addonName, nodeId);
            return;
        }

        var found = FindClickEvent(head);
        var evt = found is not null ? found : head;
        this.log.Information("[BeastHelper] Lua: click(\"{Addon}\", {Node}) replaying {EventType}.", addonName, nodeId, evt->State.EventType);
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
    }

    /// <summary>
    /// Clicks one row of a list component (e.g. XBMPetParty's beast list) by
    /// replaying the named event with a real AtkEventData.ListItemData
    /// populated — not null, and not a generic zeroed struct either. See
    /// this class's own remarks for why: ListItemClick/ListItemDoubleClick's
    /// native handler reads AtkEventData as an AtkListItemData carrying the
    /// AtkComponentListItemRenderer* and index of the row that was hit, and
    /// a prior version of this feature (clickNested) always passed null
    /// there and crashed the game.
    /// </summary>
    /// <param name="addonName">The addon (e.g. "XBMPetParty").</param>
    /// <param name="containerNodeId">The list's own top-level node id (e.g. 11).</param>
    /// <param name="childNodeId">The specific row's node id within that list's own component scope (e.g. 41001).</param>
    /// <param name="eventTypeName">Exact AtkEventType name to replay (e.g. "ListItemDoubleClick" or "ButtonClick") — no auto-priority search, since a row can have several plausible ones chained and guessing wrong could fire the wrong action.</param>
    private void LuaClickListItem(string addonName, uint containerNodeId, uint childNodeId, string eventTypeName, bool dispatchToContainer = false)
    {
        var addon = this.FindAddon(addonName);
        if (addon is null)
        {
            this.log.Information("[BeastHelper] Lua: clickListItem(\"{Addon}\", {Container}, {Child}, \"{EventType}\") — addon not found.", addonName, containerNodeId, childNodeId, eventTypeName);
            return;
        }

        var containerNode = addon->GetNodeById(containerNodeId);
        var containerComponent = containerNode is null ? null : containerNode->GetComponent();
        if (containerComponent is null)
        {
            this.log.Information("[BeastHelper] Lua: clickListItem(\"{Addon}\", {Container}, {Child}, \"{EventType}\") — container not found or has no component.", addonName, containerNodeId, childNodeId, eventTypeName);
            return;
        }

        // GetComponentItemRendererById didn't find rows by their own NodeId
        // in practice (confirmed: returned null for a NodeId visibly present
        // in the container's own node list) — so find the row node directly
        // in the container's node list instead, and cast its component,
        // the same way CrucibleLootService etc. already safely cast a
        // node's component to AtkComponentButton*.
        var childNode = FindNodeInComponent(containerComponent, childNodeId);
        var itemRenderer = (AtkComponentListItemRenderer*)(childNode is null ? null : childNode->GetComponent());
        if (itemRenderer is null)
        {
            this.log.Information("[BeastHelper] Lua: clickListItem(\"{Addon}\", {Container}, {Child}, \"{EventType}\") — child node not found, or has no component.", addonName, containerNodeId, childNodeId, eventTypeName);
            return;
        }

        if (!Enum.TryParse<AtkEventType>(eventTypeName, out var wantedType))
        {
            this.log.Information("[BeastHelper] Lua: clickListItem — \"{EventType}\" isn't a known AtkEventType.", eventTypeName);
            return;
        }

        var rendererComponent = &itemRenderer->AtkComponentButton.AtkComponentBase;
        var ownerNode = rendererComponent->OwnerNode;
        if (ownerNode is null)
        {
            this.log.Information("[BeastHelper] Lua: clickListItem(\"{Addon}\", {Container}, {Child}, \"{EventType}\") — item renderer has no owner node.", addonName, containerNodeId, childNodeId, eventTypeName);
            return;
        }

        AtkEvent* found = null;
        for (var evt = ownerNode->AtkResNode.AtkEventManager.Event; evt is not null; evt = evt->NextEvent)
        {
            if (evt->State.EventType == wantedType)
            {
                found = evt;
                break;
            }
        }

        if (found is null)
        {
            this.log.Information("[BeastHelper] Lua: clickListItem(\"{Addon}\", {Container}, {Child}) — event \"{EventType}\" not found on this row's chain.", addonName, containerNodeId, childNodeId, eventTypeName);
            return;
        }

        var eventData = new AtkEventData();
        eventData.ListItemData.ListItemRenderer = itemRenderer;
        eventData.ListItemData.SelectedIndex = itemRenderer->ListItemIndex;
        eventData.ListItemData.MouseButtonId = 0;
        eventData.ListItemData.MouseModifier = ModifierFlag.None;

        var dispatchTarget = dispatchToContainer ? containerComponent : rendererComponent;
        this.log.Information(
            "[BeastHelper] Lua: clickListItem(\"{Addon}\", {Container}, {Child}) replaying {EventType} on the {Target} (ListItemIndex={Index}) with a real AtkEventData.",
            addonName,
            containerNodeId,
            childNodeId,
            wantedType,
            dispatchToContainer ? "container component" : "item renderer",
            itemRenderer->ListItemIndex);
        dispatchTarget->ReceiveEvent(found->State.EventType, (int)found->Param, found, &eventData);
    }

    /// <summary>
    /// Replays a MouseDown event with a non-null, value-initialized (all
    /// zero) <see cref="AtkEventData"/> — the technique erdelf/AutoDuty's
    /// own Crucible bestiary-row picker uses (its <c>Notebook.PickEntry</c>:
    /// a FireCallback to "select" the row, then this MouseDown replay to
    /// actually register the click). Different from every earlier attempt
    /// on this exact screen: not ButtonClick/ListItemClick, and the
    /// AtkEventData pointer is valid (pointing at zeroed memory) rather than
    /// null. Searches the whole node tree recursively for an event matching
    /// (MouseDown, param), since it isn't necessarily on any specific node's
    /// own event chain.
    /// </summary>
    private void LuaClickMouseDown(string addonName, uint param)
    {
        var addon = this.FindAddon(addonName);
        if (addon is null)
        {
            this.log.Information("[BeastHelper] Lua: clickMouseDown(\"{Addon}\", {Param}) — addon not found.", addonName, param);
            return;
        }

        var evt = FindEventInTree(&addon->UldManager, AtkEventType.MouseDown, param, 0);
        if (evt is null)
        {
            this.log.Information("[BeastHelper] Lua: clickMouseDown(\"{Addon}\", {Param}) — no MouseDown event with that param found in the node tree.", addonName, param);
            return;
        }

        var data = new AtkEventData();
        this.log.Information("[BeastHelper] Lua: clickMouseDown(\"{Addon}\", {Param}) replaying MouseDown with a zeroed AtkEventData.", addonName, param);
        addon->ReceiveEvent(AtkEventType.MouseDown, (int)param, evt, &data);
    }

    private static AtkEvent* FindEventInTree(AtkUldManager* uld, AtkEventType type, uint param, int depth)
    {
        if (uld is null || depth > 6)
        {
            return null;
        }

        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node is null || !node->IsVisible())
            {
                continue;
            }

            for (var evt = node->AtkEventManager.Event; evt is not null; evt = evt->NextEvent)
            {
                if (evt->State.EventType == type && evt->Param == param)
                {
                    return evt;
                }
            }

            var component = node->GetComponent();
            if (component is null)
            {
                continue;
            }

            var found = FindEventInTree(&component->UldManager, type, param, depth + 1);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static AtkResNode* FindNodeInComponent(AtkComponentBase* component, uint nodeId)
    {
        var nodeList = component->UldManager.NodeList;
        var count = component->UldManager.NodeListCount;
        for (var i = 0; i < count; i++)
        {
            var node = nodeList[i];
            if (node is not null && node->NodeId == nodeId)
            {
                return node;
            }
        }

        return null;
    }

    private static AtkEvent* FindClickEvent(AtkEvent* head)
    {
        foreach (var wanted in ClickEventPriority)
        {
            for (var evt = head; evt is not null; evt = evt->NextEvent)
            {
                if (evt->State.EventType == wanted)
                {
                    return evt;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Selects a row in an AtkComponentList-derived list (including
    /// AtkComponentTreeList, which inherits it) by index — for list/tree
    /// widgets whose rows don't carry their own click event and instead rely
    /// on the container's virtual SelectItem, the same mechanism used for
    /// SelectString's own dialogue choices. nodeId is the list/tree
    /// container's own node id (not an individual row's), and index is
    /// 0-based.
    /// </summary>
    private void LuaSelectItem(string addonName, uint nodeId, int index)
    {
        var addon = this.FindAddon(addonName);
        if (addon is null)
        {
            this.log.Information("[BeastHelper] Lua: selectItem(\"{Addon}\", {Node}, {Index}) — addon not found.", addonName, nodeId, index);
            return;
        }

        var node = addon->GetNodeById(nodeId);
        if (node is null)
        {
            this.log.Information("[BeastHelper] Lua: selectItem(\"{Addon}\", {Node}, {Index}) — node not found.", addonName, nodeId, index);
            return;
        }

        var list = (AtkComponentList*)node->GetComponent();
        if (list is null)
        {
            this.log.Information("[BeastHelper] Lua: selectItem(\"{Addon}\", {Node}, {Index}) — node has no list component.", addonName, nodeId, index);
            return;
        }

        list->SelectItem(index, dispatchEvent: true);
    }

    /// <summary>
    /// Generic addon callback: equivalent to a macro's
    /// "/callback &lt;addon&gt; true &lt;values...&gt;" — Lua usage is
    /// callback("SelectString", 0), callback("SelectYesno", 0), etc.
    /// Accepts numbers, booleans, and strings as the trailing values.
    /// </summary>
    private DynValue LuaCallback(ScriptExecutionContext context, CallbackArguments args)
    {
        var addonName = args.AsType(0, "callback", DataType.String, false).String;
        var addon = this.FindAddon(addonName);
        if (addon is null)
        {
            this.log.Information("[BeastHelper] Lua: callback(\"{Addon}\", ...) — addon not found.", addonName);
            return DynValue.Nil;
        }

        var valueCount = args.Count - 1;
        var atkValues = stackalloc AtkValue[valueCount];
        for (var i = 0; i < valueCount; i++)
        {
            var value = args[i + 1];
            atkValues[i] = new AtkValue();
            switch (value.Type)
            {
                case DataType.Number:
                    atkValues[i].SetInt((int)value.Number);
                    break;
                case DataType.Boolean:
                    atkValues[i].SetBool(value.Boolean);
                    break;
                case DataType.String:
                    atkValues[i].SetManagedString(value.String);
                    break;
            }
        }

        addon->FireCallback((uint)valueCount, atkValues, true);
        return DynValue.Nil;
    }

    private AtkUnitBase* FindAddon(string name)
    {
        var ptr = this.gameGui.GetAddonByName(name);
        return ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
    }
}
