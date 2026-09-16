using System.Numerics;
using Dalamud.Plugin.Services;

namespace BeastNav.Services;

/// <summary>
/// One-off diagnostic: dumps every nearby GameObject (kind, base id, name,
/// position, distance, targetable) to /xllog. Built to find out what marks
/// a Crucible board's tile-trigger locations — the board plays as a
/// board-game map where walking onto an enemy tile changes area into a
/// separate combat sub-zone, so on the board's own overworld-map segment
/// there's no BattleNpc yet to detect the way CrucibleCombatService does
/// once inside a fight. If each tile has its own GameObject (an EventObj or
/// similar) sitting on the board, this reveals it so the same safe
/// detect-and-walk pattern can be reused instead of hardcoding waypoints.
/// </summary>
public sealed class NearbyObjectDumpService
{
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    public NearbyObjectDumpService(IObjectTable objectTable, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.log = log;
    }

    public void DumpNearbyObjects(float range = 100f)
    {
        var player = this.objectTable.LocalPlayer;
        if (player is null)
        {
            this.log.Information("[BeastHelper] Nearby objects: no local player.");
            return;
        }

        this.log.Information("[BeastHelper] Nearby objects within {Range}y of player at {Pos}:", range, player.Position);

        var count = 0;
        foreach (var obj in this.objectTable)
        {
            if (obj.Address == player.Address)
            {
                continue;
            }

            var distance = Vector3.Distance(player.Position, obj.Position);
            if (distance > range)
            {
                continue;
            }

            count++;
            this.log.Information(
                "  [{Kind}] BaseId={BaseId} Name='{Name}' Pos={Pos} Dist={Distance:0.0}y Targetable={Targetable}",
                obj.ObjectKind,
                obj.BaseId,
                obj.Name.TextValue,
                obj.Position,
                distance,
                obj.IsTargetable);
        }

        this.log.Information("[BeastHelper] Nearby objects: {Count} found within {Range}y.", count, range);
    }
}
