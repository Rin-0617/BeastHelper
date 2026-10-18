using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BeastNav.Services;

/// <summary>
/// One-off diagnostic: dumps every node in an addon's own flat node list
/// (<c>AtkUnitBase.UldManager.NodeList</c>) to /xllog — id, type, whether
/// it's component-backed, collision/mouse flags, its own click event type
/// (if any), and position/size. Built to speed up figuring out what kind of
/// node a screen's clickable row actually is (a safe Button Component Node
/// vs. a raw Collision Node that isn't safe to click synthetically — see
/// CrucibleEntryService's and LuaAutomationService's own remarks on the
/// clickChild crash) without walking every row by hand in Addon Inspector.
/// </summary>
/// <remarks>
/// Dumps one level at a time: the addon's own top-level node list by
/// default (<paramref name="drillIntoNodeId"/> 0), or — when a node from
/// that list turns out to be a container (e.g. a list component holding
/// per-row child nodes) — that one node's own component-local node list
/// instead, so a list's actual rows can be found without a blind, fully
/// recursive tree walk.
/// </remarks>
public sealed unsafe class AddonNodeDumpService
{
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    public AddonNodeDumpService(IGameGui gameGui, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.log = log;
    }

    /// <param name="addonName">The addon to inspect.</param>
    /// <param name="drillIntoNodeId">
    /// 0 dumps the addon's own top-level node list. A nonzero value looks
    /// that NodeId up in the top-level list first, then dumps *its*
    /// component's own node list instead (one level in) — use this once the
    /// top-level dump shows a component-backed node that's the right size/
    /// position to be a list container holding the actual rows.
    /// </param>
    public void DumpNodeTree(string addonName, uint drillIntoNodeId = 0)
    {
        if (string.IsNullOrWhiteSpace(addonName))
        {
            this.log.Information("[BeastHelper] Node dump: no addon name given.");
            return;
        }

        AtkUnitBase* addon;
        try
        {
            var ptr = this.gameGui.GetAddonByName(addonName);
            addon = ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[BeastHelper] Node dump: addon lookup failed.");
            return;
        }

        if (addon is null)
        {
            this.log.Information("[BeastHelper] Node dump: addon '{Addon}' not found (is it open?).", addonName);
            return;
        }

        var nodeList = addon->UldManager.NodeList;
        var count = addon->UldManager.NodeListCount;
        var label = $"{addonName} (visible={addon->IsVisible})";

        if (drillIntoNodeId != 0)
        {
            var target = FindNode(nodeList, count, drillIntoNodeId);
            var component = target is null ? null : target->GetComponent();
            if (component is null)
            {
                this.log.Information("[BeastHelper] Node dump: NodeId {NodeId} in '{Addon}' not found, or has no component to drill into.", drillIntoNodeId, addonName);
                return;
            }

            nodeList = component->UldManager.NodeList;
            count = component->UldManager.NodeListCount;
            label = $"{addonName} / NodeId {drillIntoNodeId}'s component";
        }

        this.log.Information("[BeastHelper] Node dump: {Label} — {Count} nodes.", label, count);

        for (var i = 0; i < count; i++)
        {
            var node = nodeList[i];
            if (node is null)
            {
                continue;
            }

            var nodeComponent = node->GetComponent();
            var eventChain = DescribeEventChain(node->AtkEventManager.Event);
            // Check GetComponent() first: a component-backed node's own Type
            // field holds an internal component-kind number, not the real
            // NodeType enum, so comparing it to NodeType.Text directly would
            // risk a coincidental numeric collision between the two.
            var nodeLabel = nodeComponent is not null ? FindLabelInComponent(nodeComponent) : node->Type == NodeType.Text ? ReadText(node) : string.Empty;

            // Raw Type: for a plain node this is the real NodeType (Text,
            // Image, Collision, Res, ...); for a component-backed node it's
            // an internal component-kind number instead (e.g. 1002 for a
            // button) — GetComponent() being non-null is what tells them
            // apart, not the Type value itself.
            this.log.Information(
                "  [{Index}] NodeId={NodeId} Type={Type} Component={HasComponent} Flags={Flags} Events=[{Events}] Pos=({X:0},{Y:0}) Size=({W:0}x{H:0}) Label='{Label}'",
                i,
                node->NodeId,
                node->Type,
                nodeComponent is not null,
                node->NodeFlags,
                eventChain,
                node->X,
                node->Y,
                node->Width,
                node->Height,
                nodeLabel);
        }
    }

    private static AtkResNode* FindNode(AtkResNode** nodeList, ushort count, uint nodeId)
    {
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

    /// <summary>
    /// A component-backed node (e.g. one row of a list) doesn't carry its
    /// own visible text directly — it's nested inside that component's own
    /// local node list (the same "ListItemRenderer" scope documented in
    /// CrucibleEntryService's ReadFirstRowText). Peeking one level in like
    /// this is what actually ties a NodeId back to "the ブギル row" instead
    /// of just a number. Collects every distinct text node found (up to 4)
    /// rather than just the first — a row can carry several (name, level,
    /// an EXP/HP label, ...) and the first one isn't necessarily the useful
    /// one for identifying the row.
    /// </summary>
    private static string FindLabelInComponent(AtkComponentBase* component)
    {
        var nodeList = component->UldManager.NodeList;
        var nodeListCount = component->UldManager.NodeListCount;
        var found = new List<string>();
        for (var i = 0; i < nodeListCount && found.Count < 4; i++)
        {
            var child = nodeList[i];
            if (child is null || child->Type != NodeType.Text)
            {
                continue;
            }

            var text = ReadText(child);
            if (!string.IsNullOrWhiteSpace(text) && !found.Contains(text))
            {
                found.Add(text);
            }
        }

        return string.Join(" | ", found);
    }

    /// <summary>Walks the AtkEvent.NextEvent chain and lists every event type found — AtkEventManager.Event only gives the head, which is often a hover-type event rather than the click (see LuaAutomationService's own FindClickEvent).</summary>
    private static string DescribeEventChain(AtkEvent* head)
    {
        if (head is null)
        {
            return "-";
        }

        var types = new List<string>();
        for (var evt = head; evt is not null; evt = evt->NextEvent)
        {
            types.Add(evt->State.EventType.ToString());
        }

        return string.Join(" -> ", types);
    }

    private static string ReadText(AtkResNode* node)
        => ((AtkTextNode*)node)->NodeText.ToString();
}
