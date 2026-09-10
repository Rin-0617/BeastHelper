using System.Numerics;
using BeastNav.Services;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Lumina.Excel.Sheets;
using CSInstanceContentType = FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentType;

namespace BeastNav.Crucible;

/// <summary>
/// Reads the observable game state the Crucible assist needs, once per framework
/// tick, into an immutable <see cref="CrucibleState"/>. Read-only: it calls no
/// game function that changes anything and sends no input.
/// </summary>
public sealed unsafe class CrucibleStateReader
{
    // TerritoryType.TerritoryIntendedUse for "Crucible of the Unbroken" (闘獣練).
    private const uint CrucibleIntendedUse = 62;

    // Ignore enemies further than this from the player (yalms).
    private const float EnemyScanRange = 60f;

    // BattleNpcSubKind underlying values (stable in FFXIVClientStructs). Compared
    // as bytes to sidestep the enum living in different namespaces per binding.
    private const byte SubKindPet = 2;
    private const byte SubKindBuddy = 3;
    private const byte SubKindCombatant = 5;

    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly IDataManager dataManager;
    private readonly BeastDataService beastData;
    private readonly IPluginLog log;

    public CrucibleStateReader(
        IClientState clientState,
        ICondition condition,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IDataManager dataManager,
        BeastDataService beastData,
        IPluginLog log)
    {
        this.clientState = clientState;
        this.condition = condition;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.dataManager = dataManager;
        this.beastData = beastData;
        this.log = log;
    }

    public CrucibleState Current { get; private set; } = CrucibleState.Empty;

    /// <summary>
    /// Debug override (<c>/beasthelper crucible force</c>): treat the current
    /// zone as the Crucible even when auto-detection says otherwise. Useful when
    /// watching a replay, where the instance director may not spin up.
    /// </summary>
    public bool ForceActive { get; set; }

    public void Update()
    {
        try
        {
            this.Current = this.Capture();
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible state read failed.");
            this.Current = CrucibleState.Empty;
        }
    }

    private CrucibleState Capture()
    {
        var (inCrucible, source) = this.DetectCrucible();
        if (!inCrucible && this.ForceActive)
        {
            (inCrucible, source) = (true, "forced");
        }

        var player = this.objectTable.LocalPlayer;

        var beast = this.ReadBeastState();
        var enemies = inCrucible && player is not null ? this.ReadEnemies(player) : [];
        var primary = this.ResolvePrimaryTarget(enemies);

        return new CrucibleState
        {
            InCrucible = inCrucible,
            DetectionSource = source,
            InCombat = this.condition[ConditionFlag.InCombat],
            TerritoryId = this.clientState.TerritoryType,
            HasPlayer = player is not null,
            PlayerCurrentHp = player?.CurrentHp ?? 0,
            PlayerMaxHp = player?.MaxHp ?? 0,
            PlayerPosition = player?.Position ?? default,
            PlayerRotation = player?.Rotation ?? 0f,
            BeastDataReady = beast.Ready,
            UnlockedBeastCount = beast.Count,
            UnlockedBeastPetRowIds = beast.Unlocked,
            ActiveBeastPetRowId = beast.ActivePetRowId,
            ActiveBeastName = beast.ActiveName,
            Enemies = enemies,
            PrimaryTarget = primary,
        };
    }

    /// <summary>
    /// One-shot diagnostic (<c>/beasthelper crucible probe</c>): logs how
    /// <c>XBMManager.IsPetUnlocked</c> lines up with the 図鑑 capture list, so we
    /// can confirm whether it wants the <c>Pet</c> row id or the <c>XBMPet</c>
    /// row id before relying on it.
    /// </summary>
    public void LogUnlockProbe(IReadOnlyCollection<uint> knownCapturedPetRowIds)
    {
        try
        {
            var manager = XBMManager.Instance();
            if (manager is null)
            {
                this.log.Information("[BeastHelper] Crucible probe: XBMManager unavailable.");
                return;
            }

            var captured = knownCapturedPetRowIds.ToHashSet();
            int byPetRow = 0, byXbmRow = 0, agreePetRow = 0, agreeXbmRow = 0;
            foreach (var pet in this.beastData.Pets)
            {
                var uPet = SafeIsUnlocked(manager, pet.PetRowId);
                var uXbm = SafeIsUnlocked(manager, pet.XbmRowId);
                if (uPet)
                {
                    byPetRow++;
                }

                if (uXbm)
                {
                    byXbmRow++;
                }

                if (uPet == captured.Contains(pet.PetRowId))
                {
                    agreePetRow++;
                }

                if (uXbm == captured.Contains(pet.PetRowId))
                {
                    agreeXbmRow++;
                }
            }

            var total = this.beastData.Pets.Count;
            this.log.Information(
                "[BeastHelper] Crucible probe: State={State} NumUnlockedPets={Num} 図鑑captured={Captured}. "
                + "IsPetUnlocked(PetRowId): {ByPet} unlocked, agrees {AgreePet}/{Total}. "
                + "IsPetUnlocked(XbmRowId): {ByXbm} unlocked, agrees {AgreeXbm}/{Total}.",
                manager->State,
                manager->NumUnlockedPets,
                captured.Count,
                byPetRow,
                agreePetRow,
                total,
                byXbmRow,
                agreeXbmRow,
                total);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[BeastHelper] Crucible probe failed.");
        }

        static bool SafeIsUnlocked(XBMManager* manager, uint id)
        {
            try
            {
                return manager->IsPetUnlocked(id);
            }
            catch
            {
                return false;
            }
        }
    }

    // ------------------------------------------------------------- detection --

    private (bool InCrucible, string Source) DetectCrucible()
    {
        try
        {
            var director = EventFramework.Instance()->GetInstanceContentDirector();
            if (director is not null && director->InstanceContentType == CSInstanceContentType.CrucibleOfTheUnbroken)
            {
                return (true, "InstanceContentDirector");
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible: instance director probe failed.");
        }

        try
        {
            var territory = this.dataManager.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(this.clientState.TerritoryType);
            if (territory is not null && territory.Value.TerritoryIntendedUse.RowId == CrucibleIntendedUse)
            {
                return (true, "TerritoryIntendedUse");
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible: territory probe failed.");
        }

        return (false, "none");
    }

    // ------------------------------------------------------------------ beast --

    private BeastReadout ReadBeastState()
    {
        var unlocked = new List<uint>();
        var ready = false;
        var count = 0;

        try
        {
            var manager = XBMManager.Instance();
            if (manager is not null)
            {
                ready = manager->State == XBMManager.DataState.Received;
                count = manager->NumUnlockedPets;
                if (ready)
                {
                    // XBMManager.IsPetUnlocked keys on the XBMPet row id (the
                    // 図鑑 "No."), not the Pet row id — confirmed 50/50 against
                    // the monster note via `/beasthelper crucible probe`.
                    foreach (var pet in this.beastData.Pets)
                    {
                        if (manager->IsPetUnlocked(pet.XbmRowId))
                        {
                            unlocked.Add(pet.PetRowId);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible: XBMManager read failed.");
        }

        var (activeId, activeName) = this.ReadActiveBeast();
        return new BeastReadout(ready, count, unlocked, activeId, activeName);
    }

    /// <summary>
    /// Best-effort identification of the summoned beast from the object table
    /// (a player-owned pet). Left null when it cannot be matched confidently.
    /// </summary>
    private (uint? PetRowId, string Name) ReadActiveBeast()
    {
        try
        {
            var player = this.objectTable.LocalPlayer;
            if (player is null)
            {
                return (null, string.Empty);
            }

            foreach (var obj in this.objectTable)
            {
                if (obj is not IBattleNpc npc || npc.OwnerId != player.GameObjectId)
                {
                    continue;
                }

                if ((byte)npc.BattleNpcKind is not SubKindPet and not SubKindBuddy)
                {
                    continue;
                }

                var name = npc.Name.TextValue;
                var match = this.beastData.Pets.FirstOrDefault(pet =>
                    !string.IsNullOrEmpty(pet.Name) && string.Equals(pet.Name, name, StringComparison.OrdinalIgnoreCase));
                return (match?.PetRowId, name);
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible: active beast probe failed.");
        }

        return (null, string.Empty);
    }

    // ---------------------------------------------------------------- enemies --

    private IReadOnlyList<CrucibleEnemy> ReadEnemies(IPlayerCharacter player)
    {
        var enemies = new List<CrucibleEnemy>();

        foreach (var obj in this.objectTable)
        {
            if (obj is not IBattleNpc npc || (byte)npc.BattleNpcKind != SubKindCombatant)
            {
                continue;
            }

            if (npc.IsDead || npc.CurrentHp == 0)
            {
                continue;
            }

            var distance = Vector3.Distance(player.Position, npc.Position);
            if (distance > EnemyScanRange)
            {
                continue;
            }

            enemies.Add(new CrucibleEnemy
            {
                GameObjectId = npc.GameObjectId,
                NameId = npc.NameId,
                Name = npc.Name.TextValue,
                CurrentHp = npc.CurrentHp,
                MaxHp = npc.MaxHp,
                IsCasting = npc.IsCasting,
                CastActionId = npc.CastActionId,
                CastCurrent = npc.CurrentCastTime,
                CastTotal = npc.TotalCastTime,
                CastTargetsPlayer = npc.IsCasting && npc.CastTargetObjectId == player.GameObjectId,
                Distance = distance,
                Position = npc.Position,
                Rotation = npc.Rotation,
            });
        }

        return enemies
            .OrderBy(enemy => enemy.Distance)
            .ToList();
    }

    private CrucibleEnemy? ResolvePrimaryTarget(IReadOnlyList<CrucibleEnemy> enemies)
    {
        if (enemies.Count == 0)
        {
            return null;
        }

        var targetId = this.targetManager.Target?.GameObjectId;
        if (targetId is { } id)
        {
            var match = enemies.FirstOrDefault(enemy => enemy.GameObjectId == id);
            if (match is not null)
            {
                return match;
            }
        }

        // No hostile target selected — fall back to the closest casting enemy,
        // else just the closest.
        return enemies.FirstOrDefault(static enemy => enemy.IsCasting) ?? enemies[0];
    }

    private readonly record struct BeastReadout(
        bool Ready,
        int Count,
        IReadOnlyList<uint> Unlocked,
        uint? ActivePetRowId,
        string ActiveName);
}
