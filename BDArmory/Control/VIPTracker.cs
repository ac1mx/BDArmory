using System.Collections.Generic;
using UnityEngine;

using BDArmory.Competition;
using BDArmory.Extensions;
using BDArmory.UI;

namespace BDArmory.Control
{
    /// <summary>
    /// Which craft each team has marked as its VIP, and whether that VIP still has an escort.
    /// Looked up by pilots and weapon managers many times per second, so it's refreshed at most once a second.
    /// </summary>
    public static class VIPTracker
    {
        struct TeamState { public Vessel vip; public int escorts; }
        static readonly Dictionary<string, TeamState> teams = new Dictionary<string, TeamState>();
        static float lastRefresh = -10f;

        /// <summary>Weights used when the craft's own VIP target priorities are left at 0, so that marking a VIP is all the setup needed.</summary>
        public const float implicitProtectWeight = 5f, implicitAttackWeight = 5f;

        static void Refresh()
        {
            if (Time.time - lastRefresh < 1f && Time.time >= lastRefresh) return;
            lastRefresh = Time.time;
            previousVIPs.Clear();
            foreach (var team in teams) if (team.Value.vip != null) previousVIPs[team.Key] = team.Value.vip;
            teams.Clear();
            foreach (var vessel in BDATargetManager.LoadedVessels)
            {
                if (vessel == null || !vessel.loaded || vessel.packed) continue;
                var controller = vessel.ActiveController();
                var weaponManager = controller.WM;
                if (weaponManager == null || weaponManager.Team == null || weaponManager.Team.Neutral) continue;
                teams.TryGetValue(weaponManager.Team.Name, out TeamState state);
                if (weaponManager.isVIP) { if (state.vip == null) state.vip = vessel; }
                else if (controller.AI != null && controller.AI.pilotEnabled) ++state.escorts;
                teams[weaponManager.Team.Name] = state;
            }
            // A team that had a VIP a moment ago and has none now has lost it.
            foreach (var previous in previousVIPs)
            {
                if (teams.TryGetValue(previous.Key, out TeamState now) && now.vip != null) continue;
                if (previous.Value != null && previous.Value.state != Vessel.State.DEAD) continue; // Unloaded or switched off, not destroyed.
                var competition = BDACompetitionMode.Instance;
                if (competition == null || !competition.competitionIsActive) continue;
                competition.competitionStatus.Add($"Team {previous.Key} has lost its VIP.");
                Debug.Log($"[BDArmory.VIPTracker]: Team {previous.Key} has lost its VIP.");
                if (BDArmory.Settings.BDArmorySettings.VIP_DEATH_ENDS_ROUND) { competition.StopCompetition(); break; }
            }
        }
        static readonly Dictionary<string, Vessel> previousVIPs = new Dictionary<string, Vessel>();

        /// <summary>The team's living VIP, or null.</summary>
        public static Vessel GetVIP(BDTeam team)
        {
            if (team == null) return null;
            Refresh();
            return teams.TryGetValue(team.Name, out TeamState state) ? state.vip : null;
        }

        /// <summary>Number of the team's piloted craft that aren't VIPs.</summary>
        public static int EscortCount(BDTeam team)
        {
            if (team == null) return 0;
            Refresh();
            return teams.TryGetValue(team.Name, out TeamState state) ? state.escorts : 0;
        }

        public static float ProtectWeight(MissileFire weaponManager)
        {
            if (weaponManager.targetWeightProtectVIP != 0) return weaponManager.targetWeightProtectVIP;
            return GetVIP(weaponManager.Team) != null ? implicitProtectWeight : 0f;
        }

        public static float AttackWeight(MissileFire weaponManager)
        {
            return weaponManager.targetWeightAttackVIP != 0 ? weaponManager.targetWeightAttackVIP : implicitAttackWeight;
        }
    }
}
