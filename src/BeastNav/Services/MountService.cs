using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BeastNav.Services;

public unsafe sealed class MountService
{
    private readonly IPluginLog log;

    public MountService(IPluginLog log)
    {
        this.log = log;
    }

    // General action 9 is "Mount Roulette" (summons a random owned mount).
    private const uint MountRouletteGeneralAction = 9;

    public bool TryMountRoulette()
    {
        var actionManager = ActionManager.Instance();
        if (actionManager is null)
        {
            this.log.Debug("Mount Roulette command was not accepted (ActionManager unavailable).");
            return false;
        }

        if (actionManager->GetActionStatus(ActionType.GeneralAction, MountRouletteGeneralAction) != 0)
        {
            this.log.Debug("Mount Roulette is not currently usable.");
            return false;
        }

        var used = actionManager->UseAction(ActionType.GeneralAction, MountRouletteGeneralAction);
        this.log.Information("[BeastHelper] Mount Roulette requested (accepted={Accepted}).", used);
        return used;
    }
}
