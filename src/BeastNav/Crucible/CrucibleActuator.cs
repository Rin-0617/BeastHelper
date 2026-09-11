using System.Numerics;
using BeastNav.Services;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace BeastNav.Crucible;

/// <summary>
/// The movement brain for the Crucible assist: while a dangerous cast is up it
/// walks the character to the dodge solver's safe point via vnavmesh's path
/// follower, then stops. Combat and travel between packs are left entirely to
/// the player; this never targets or acts, only dodges.
/// </summary>
/// <remarks>
/// In-combat movement automation, against the FFXIV ToS. Gated behind
/// <see cref="Configuration.CrucibleAutoDodge"/>, off by default. Never runs
/// during a BeastHelper destination move.
/// </remarks>
public sealed class CrucibleActuator
{
    private const float DodgeArrive = 1.5f;
    private static readonly TimeSpan Reissue = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan DodgeTimeout = TimeSpan.FromSeconds(6);

    private readonly Configuration configuration;
    private readonly NavmeshService navmesh;
    private readonly ICondition condition;
    private readonly IPluginLog log;

    private bool dodging;
    private Vector3 target;
    private DateTime lastIssue;
    private DateTime dodgeUntil;

    public CrucibleActuator(Configuration configuration, NavmeshService navmesh, ICondition condition, IPluginLog log)
    {
        this.configuration = configuration;
        this.navmesh = navmesh;
        this.condition = condition;
        this.log = log;
    }

    public bool IsDodging => this.dodging;

    /// <summary>Short human status for the overlay.</summary>
    public string Status { get; private set; } = "off";

    public void Tick(CrucibleState state, DodgePlan plan)
    {
        // Safety: never drive a near-dead character anywhere.
        if (state.HasPlayer && state.PlayerMaxHp > 0 && state.PlayerHpFraction <= 0.1f)
        {
            this.Halt("player critical");
            this.Status = "halted (HP critical)";
            return;
        }

        if (!this.configuration.CrucibleAutoDodge
            || this.navmesh.HasActiveRequest
            || !state.InCrucible
            || !state.HasPlayer
            || this.condition[ConditionFlag.BetweenAreas]
            || this.condition[ConditionFlag.BetweenAreas51]
            || this.condition[ConditionFlag.Unconscious])
        {
            this.Halt("not actionable");
            this.Status = this.configuration.CrucibleAutoDodge ? "waiting" : "off";
            return;
        }

        var now = DateTime.UtcNow;

        if (plan.ShouldMove && !plan.NoSafeSpot)
        {
            this.Drive(plan.TargetXZ, now);
            this.dodgeUntil = this.dodging && this.dodgeUntil > now ? this.dodgeUntil : now + DodgeTimeout;
            return;
        }

        if (this.dodging)
        {
            var arrived = Vector3.Distance(state.PlayerPosition, this.target) <= DodgeArrive;
            if (!arrived && now < this.dodgeUntil && plan.ShouldMove)
            {
                return; // still resolving
            }

            this.Halt(arrived ? "dodge arrived" : "dodge clear");
            return;
        }

        this.Status = "clear";
    }

    private void Drive(Vector3 to, DateTime now)
    {
        var changed = !this.dodging || Vector3.Distance(to, this.target) > 2f;
        if (changed || now - this.lastIssue >= Reissue)
        {
            this.navmesh.WalkDirectlyTo(to);
            this.target = to;
            this.lastIssue = now;
        }

        this.dodging = true;
        this.Status = "dodging";
    }

    private void Halt(string why)
    {
        if (!this.dodging)
        {
            return;
        }

        this.dodging = false;
        this.navmesh.StopPath();
        this.Status = why;
        this.log.Debug("[BeastHelper] Crucible auto-dodge stopped ({Why}).", why);
    }
}
