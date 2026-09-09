using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace BeastNav.Services;

public unsafe sealed class TeleportService
{
    private readonly IAetheryteList aetherytes;
    private readonly IPluginLog log;

    public TeleportService(IAetheryteList aetherytes, IPluginLog log)
    {
        this.aetherytes = aetherytes;
        this.log = log;
    }

    public bool TryTeleport(uint territoryId)
    {
        var telepo = Telepo.Instance();
        if (telepo is null)
        {
            this.log.Error("Telepo is unavailable.");
            return false;
        }

        telepo->UpdateAetheryteList();
        for (var index = 0; index < this.aetherytes.Length; index++)
        {
            var aetheryte = this.aetherytes[index];
            if (aetheryte is null)
            {
                continue;
            }

            if (aetheryte.TerritoryId != territoryId || !aetheryte.AetheryteData.IsValid)
            {
                continue;
            }

            var aetheryteData = aetheryte.AetheryteData.Value;

            if (!aetheryteData.PlaceName.IsValid)
            {
                continue;
            }

            var name = aetheryteData.PlaceName.Value.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            telepo->Teleport(aetheryte.AetheryteId, aetheryte.SubIndex);
            this.log.Information("[BeastHelper] Teleport requested to {Aetheryte}.", name);
            return true;
        }

        return false;
    }
}
