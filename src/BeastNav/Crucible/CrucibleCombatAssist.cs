using System.Numerics;
using BeastNav.Services;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BeastNav.Crucible;

/// <summary>
/// The opt-in combat autopilot for 闘獣練 第一盤. It targets the nearest live
/// enemy and drives the tank-beast kit directly (the actions can't be put on a
/// hotbar): the axe combo, an AoE line when packs are stacked, the beast art on
/// cooldown, the gap closer, and — the point of the exercise — the enmity tools
/// <c>ひきつけろ</c> and <c>ちょうはつ</c> when something breaks off onto the beast.
/// </summary>
/// <remarks>
/// Combat automation, against the FFXIV ToS. Off by default; dodging pauses it.
/// </remarks>
public sealed unsafe class CrucibleCombatAssist
{
    // --- 闘獣練 第一盤 tank-beast action ids (from crucible-my-actions.json) ---
    private static readonly uint[] Combo = [44879, 44883, 44885]; // スマッシュ → アクスバイト → シールドスプリッター
    private static readonly uint[] AoeGcd = [44887, 44884, 44888]; // ミストラル / アバランチ / スピニング アクス
    private static readonly uint[] OffGcd = [44886, 44905, 47093]; // 魔獣技 / きあい / おおわざ
    private const uint GapCloser = 44893; // シールドチャージ
    private const uint Provoke = 46750;   // ちょうはつ
    private const uint DrawIn = 46751;    // ひきつけろ

    private const float EnmityRange = 25f;
    private const float GapCloserRange = 12f;
    private const float ClusterRangeSq = 25f;
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ComboWindow = TimeSpan.FromSeconds(8);

    private readonly Configuration configuration;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICondition condition;
    private readonly WrathComboBridge wrath;
    private readonly IPluginLog log;

    private DateTime lastAttempt;
    private DateTime comboAt;
    private int comboStep;
    private bool wrathRotationOn;

    public CrucibleCombatAssist(
        Configuration configuration,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICondition condition,
        WrathComboBridge wrath,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.condition = condition;
        this.wrath = wrath;
        this.log = log;
    }

    /// <summary>Short human status for the overlay.</summary>
    public string Status { get; private set; } = "off";

    public void Release()
    {
        if (this.wrathRotationOn)
        {
            this.wrath.SetAutoRotation(false);
            this.wrathRotationOn = false;
        }

        this.wrath.Release();
    }

    public CombatIntent Tick(CrucibleState state, bool suppressed)
    {
        var enabled = this.configuration.CrucibleAutoCombat && !suppressed && state.InCrucible && state.HasPlayer;
        var want = enabled && state.InCombat;

        if (this.configuration.CrucibleAutoCombat && this.configuration.CrucibleUseWrathCombo && this.wrath.Available)
        {
            if (want != this.wrathRotationOn)
            {
                this.wrath.SetAutoRotation(want);
                this.wrathRotationOn = want;
            }

            this.Status = want ? "WrathCombo rotation on" : "WrathCombo (out of combat)";
            return this.Intent(state);
        }

        if (this.wrathRotationOn)
        {
            this.wrath.SetAutoRotation(false);
            this.wrathRotationOn = false;
        }

        if (!this.configuration.CrucibleAutoCombat)
        {
            this.Status = "off";
            return CombatIntent.None;
        }

        var intent = this.Intent(state);
        if (!want)
        {
            this.Status = suppressed ? "paused (dodge)" : "waiting for combat";
            return intent;
        }

        if (!intent.HasTarget)
        {
            this.Status = "no enemy";
            return intent;
        }

        var target = NearestLive(state)!;
        this.FaceTarget(target);

        if (this.condition[ConditionFlag.BetweenAreas] || this.condition[ConditionFlag.BetweenAreas51] || this.condition[ConditionFlag.Casting])
        {
            this.Status = "occupied";
            return intent;
        }

        var actions = ActionManager.Instance();
        if (actions is null)
        {
            this.Status = "no ActionManager";
            return intent;
        }

        // Enmity tools run regardless of the GCD.
        if (this.TryEnmity(state, actions, target))
        {
            return intent with { InActionRange = true };
        }

        var now = DateTime.UtcNow;
        if (actions->AnimationLock > 0.1f || now - this.lastAttempt < MinInterval)
        {
            return intent with { InActionRange = target.Distance <= EnmityRange };
        }

        this.lastAttempt = now;

        var clustered = state.Enemies.Count(e =>
            e.CurrentHp > 0 && Vector3.DistanceSquared(e.Position, target.Position) <= ClusterRangeSq) >= 3;

        // GCD.
        if (Ready(actions, Combo[0]))
        {
            if (target.Distance > GapCloserRange && Ready(actions, GapCloser))
            {
                this.Use(actions, GapCloser, target, "シールドチャージ");
            }
            else if (clustered && this.FirstReady(actions, AoeGcd, target) is { } aoe)
            {
                this.Use(actions, aoe, target, "AoE");
            }
            else
            {
                this.Use(actions, this.NextComboAction(now), target, "combo");
            }

            return intent with { InActionRange = true };
        }

        // oGCD weave.
        if (this.FirstReady(actions, OffGcd, target) is { } og)
        {
            this.Use(actions, og, target, "oGCD");
            return intent with { InActionRange = true };
        }

        this.Status = "GCD rolling";
        return intent with { InActionRange = true };
    }

    private bool TryEnmity(CrucibleState state, ActionManager* actions, CrucibleEnemy target)
    {
        var loose = state.Enemies
            .Where(e => e.CurrentHp > 0 && !e.AggroOnPlayer && e.Distance <= EnmityRange)
            .ToList();
        if (loose.Count == 0)
        {
            return false;
        }

        if (loose.Count >= 2 && Ready(actions, DrawIn))
        {
            this.Use(actions, DrawIn, target, "ひきつけろ");
            return true;
        }

        var peel = loose.OrderBy(e => e.Distance).First();
        if (Ready(actions, Provoke))
        {
            var obj = this.objectTable.FirstOrDefault(o => o.GameObjectId == peel.GameObjectId);
            var targetId = obj?.GameObjectId ?? peel.GameObjectId;
            actions->UseAction(ActionType.Action, Provoke, targetId);
            this.Status = $"ちょうはつ → {peel.Name}";
            return true;
        }

        return false;
    }

    private uint NextComboAction(DateTime now)
    {
        if (now - this.comboAt > ComboWindow)
        {
            this.comboStep = 0;
        }

        var id = Combo[this.comboStep % Combo.Length];
        this.comboStep = (this.comboStep + 1) % Combo.Length;
        this.comboAt = now;
        return id;
    }

    private uint? FirstReady(ActionManager* actions, uint[] ids, CrucibleEnemy target)
    {
        foreach (var id in ids)
        {
            if (actions->GetActionStatus(ActionType.Action, id, target.GameObjectId) == 0)
            {
                return id;
            }
        }

        return null;
    }

    private void Use(ActionManager* actions, uint id, CrucibleEnemy target, string label)
    {
        actions->UseAction(ActionType.Action, id, target.GameObjectId);
        this.Status = $"{label}: {id}";
    }

    private void FaceTarget(CrucibleEnemy target)
    {
        if (this.targetManager.Target?.GameObjectId == target.GameObjectId)
        {
            return;
        }

        var obj = this.objectTable.FirstOrDefault(o => o.GameObjectId == target.GameObjectId);
        if (obj is not null)
        {
            this.targetManager.Target = obj;
        }
    }

    private CombatIntent Intent(CrucibleState state)
        => NearestLive(state) is { } t
            ? new CombatIntent { HasTarget = true, TargetPosition = t.Position, TargetDistance = t.Distance }
            : CombatIntent.None;

    private static bool Ready(ActionManager* actions, uint id)
        => actions->GetActionStatus(ActionType.Action, id) == 0;

    private static CrucibleEnemy? NearestLive(CrucibleState state)
        => state.Enemies
            .Where(static enemy => enemy.CurrentHp > 0)
            .OrderBy(static enemy => enemy.Distance)
            .FirstOrDefault();
}

public readonly record struct CombatIntent
{
    public static CombatIntent None => default;

    public bool HasTarget { get; init; }

    public Vector3 TargetPosition { get; init; }

    public float TargetDistance { get; init; }

    /// <summary>The target is close enough to act on (no need to close in).</summary>
    public bool InActionRange { get; init; }
}
