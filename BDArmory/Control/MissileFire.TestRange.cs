using System.Collections;
using UnityEngine;

using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Control
{
    /// <summary>
    /// Hooks for the missile range test harness (Testing/MissileRange.cs). Inactive unless the harness sets them.
    /// </summary>
    public partial class MissileFire
    {
        /// <summary>Targets, locks and manoeuvres as usual, but launches and shoots nothing on its own.</summary>
        public bool HoldFire;

        /// <summary>Only this vessel is ever made the (non-missile) target, so that several shooter/target pairs can share a range.</summary>
        public Vessel ForcedTarget;

        public enum TestLaunchResult { Fired, NoLock, Failed }

        /// <summary>
        /// Launch a missile that was just spawned beside this vessel (ModuleMissileRearm.CreatePart) at the target, the way a
        /// reloadable rail launches its spawned missile, after getting the lock the missile needs: a radar lock on the target,
        /// or the missile's own seeker on its heat.
        /// </summary>
        public IEnumerator TestLaunch(MissileLauncher ml, Vessel target, float lockTimeout, System.Action<MissileLauncher> prepare, System.Action<TestLaunchResult, string> done)
        {
            var wait = new WaitForFixedUpdate();
            float setupEnd = Time.time + 5f;
            while (ml != null && !ml.SetupComplete && Time.time < setupEnd) yield return wait;
            if (ml == null || !ml.SetupComplete || target == null || vessel == null) { done(TestLaunchResult.Failed, ml == null ? "missile gone" : !ml.SetupComplete ? "missile setup" : "vessel gone"); yield break; }
            var targetInfo = target.gameObject.GetComponent<TargetInfo>();
            float lockEnd = Time.time + lockTimeout;
            switch (ml.TargetingMode)
            {
                case MissileBase.TargetingModes.Radar:
                case MissileBase.TargetingModes.Inertial:
                    while (Time.time < lockEnd && target != null && ml != null && (vesselRadarData == null || !vesselRadarData.locked || vesselRadarData.lockedTargetData.vessel != target))
                    {
                        if (vesselRadarData != null && !(vesselRadarData.locked && vesselRadarData.SwitchActiveLockedTarget(target)))
                            vesselRadarData.TryLockTarget(target);
                        yield return new WaitForSecondsFixed(0.25f);
                    }
                    if (ml == null || target == null) { done(TestLaunchResult.Failed, "gone while locking"); yield break; }
                    if (vesselRadarData == null || !vesselRadarData.locked || vesselRadarData.lockedTargetData.vessel != target) { done(TestLaunchResult.NoLock, "radar"); yield break; }
                    yield return wait; // Let the locked target data update.
                    break;
                case MissileBase.TargetingModes.Heat:
                    if (guardFiringMissile) { done(TestLaunchResult.Failed, "weapon manager busy"); yield break; }
                    guardFiringMissile = true; // The weapon manager searches for a heat target with this missile's seeker while it's set.
                    testHeatSearch = true;
                    currFiringMissile = ml;
                    currFiringTarget = targetInfo;
                    while (Time.time < lockEnd && target != null && ml != null && (!heatTarget.exists || heatTarget.vessel != target)) yield return wait;
                    if (ml == null || target == null || !heatTarget.exists || heatTarget.vessel != target)
                    {
                        EndTestHeatSearch();
                        if (ml == null || target == null) done(TestLaunchResult.Failed, "gone while locking");
                        else done(TestLaunchResult.NoLock, heatTarget.exists ? "heat (other source)" : "heat");
                        yield break;
                    }
                    break;
                default:
                    done(TestLaunchResult.Failed, $"targeting mode {ml.TargetingMode}");
                    yield break;
            }

            // Put the missile back on the launcher's nose at its speed (it has been floating free while the seeker locked) for two physics
            // frames, so that its vessel's own velocity is current when the launch reads it; the heat seeker keeps tracking meanwhile.
            for (int i = 0; i < 2; ++i)
            {
                prepare?.Invoke(ml);
                yield return wait;
                if (ml == null || target == null || vessel == null) { EndTestHeatSearch(); done(TestLaunchResult.Failed, "gone before launch"); yield break; }
            }
            prepare?.Invoke(ml);
            if (ml.TargetingMode == MissileBase.TargetingModes.Heat && (!heatTarget.exists || heatTarget.vessel != target)) { EndTestHeatSearch(); done(TestLaunchResult.NoLock, "heat lost"); yield break; }
            ml.SourceVessel = vessel;
            ml.FiredByWM = this;
            ml.launched = true;
            ml.TimeFired = Time.time;
            ml.guidanceActive = true;
            ml.Team = Team;
            BDATargetManager.FiredMissiles.Add(ml);
            SendTargetDataToMissile(ml, target, true, null, true);
            heatTarget = TargetSignatureData.noTarget;
            ml.GpsUpdateMax = GpsUpdateMax;
            if (targetInfo != null) UpdateMissilesAway(targetInfo, ml);
            ml.TargetPosition = ml.transform.position + ml.GetForwardTransform() * 5000f;
            ml.MissileLaunch();
            EndTestHeatSearch();
            done(TestLaunchResult.Fired, null);
        }

        bool testHeatSearch;
        void EndTestHeatSearch()
        {
            if (!testHeatSearch) return;
            testHeatSearch = false;
            guardFiringMissile = false;
            currFiringMissile = null;
            currFiringTarget = null;
        }
    }
}
