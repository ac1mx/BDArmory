using System;
using System.Collections.Generic;
using UnityEngine;

using BDArmory.CounterMeasure;
using BDArmory.Extensions;
using BDArmory.Radar;
using BDArmory.Settings;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.Weapons;
using BDArmory.Weapons.Missiles;
using BDAStealth;

namespace BDArmory.Control
{
    /// <summary>
    /// How many missiles to have in flight at a target and how many countermeasures to release, decided from the situation instead of
    /// fixed per-craft numbers. A missile salvo grows while each further missile still adds enough kill chance, within the share of the
    /// missiles the other enemies leave for this target; countermeasure releases are planned against the decoy models in BDAStealth, and
    /// priced by how the stock compares with what the enemies can still throw at us.
    /// </summary>
    public static class SalvoDecisions
    {
        #region Missiles
        /// <summary>
        /// Missiles fired at one target close together don't fail independently: they meet the same countermeasures and the same defensive
        /// turn. k of them count as 1 + (k - 1)(1 - correlation) independent shots. Assumed, not fitted: to be checked against fight logs with
        /// several missiles in flight at one target.
        /// </summary>
        public const float missileCorrelation = 0.5f;
        /// <summary>A further missile is fired only if it adds at least this much kill chance.</summary>
        public const float minMarginalKill = 0.08f;

        /// <summary>Chance that at least one of k missiles, each with chance p, kills.</summary>
        public static float SalvoKill(float p, int k) => p > 0f && k > 0 ? 1f - Mathf.Pow(1f - Mathf.Min(p, 0.999f), 1f + (k - 1) * (1f - missileCorrelation)) : 0f;

        /// <summary>The smallest salvo that reaches the kill goal, stopping early where another missile adds too little, within the allowance.</summary>
        public static int MissilesOnTarget(float p, float killGoal, int allowance)
        {
            int limit = Math.Max(1, Math.Min(allowance, (int)MissileFire.maxAllowableMissilesOnTarget));
            int k = 1;
            if (!(p > 0f)) return k;
            while (k < limit && SalvoKill(p, k) < killGoal && SalvoKill(p, k + 1) - SalvoKill(p, k) >= minMarginalKill) ++k;
            return k;
        }
        #endregion

        #region Countermeasures
        static readonly float[] spacings = { 0.25f, 0.5f, 1f, 1.5f, 2.5f };
        const int maxRelease = 8;

        /// <summary>
        /// What one countermeasure must buy to be released, as a fraction of what it would buy at its best (against an unfatigued seeker, or
        /// beaming in the doppler notch), which is what it is worth kept for the next missile: a small fraction while the stock covers what
        /// the enemies can still throw at us, most of it once it doesn't.
        /// </summary>
        public static float CountermeasurePrice(int stock, int reserve) => 0.15f + 0.5f * Mathf.Clamp01(reserve / (float)Math.Max(stock, 1));

        /// <summary>
        /// Chance that n flares, spaced dt apart, decoy a seeker that an unfatigued one decoys with chance p, starting from its current
        /// fatigue: every flare it rejects adds one (halving the next one's chance), and fatigue fades between flares (StealthMath.FlareFatigue).
        /// </summary>
        public static float FlareScheduleChance(float p, float fatigue, int n, float dt)
        {
            float miss = 1f;
            for (int i = 0; i < n; ++i)
            {
                if (i > 0) fatigue = StealthMath.FlareFatigue(fatigue, dt);
                miss *= 1f - p * StealthMath.FlareFatigueFactor(fatigue);
                fatigue += 1f;
            }
            return 1f - miss;
        }

        /// <summary>Flares to release (and their spacing) against a seeker that an unfatigued one decoys with chance p, arriving in timeToGo.</summary>
        public static int FlarePlan(float p, float fatigue, float timeToGo, int stock, int reserve, out float spacing)
        {
            spacing = spacings[0];
            if (!(p > 0f) || stock <= 0) return 0;
            float price = CountermeasurePrice(stock, reserve) * p, best = 0f;
            int bestCount = 0;
            foreach (var dt in spacings)
                for (int n = 1; n <= Math.Min(stock, maxRelease); ++n)
                {
                    if ((n - 1) * dt > Mathf.Max(timeToGo - 0.5f, 0f)) break; // The last one has to be out before the missile arrives.
                    float value = FlareScheduleChance(p, fatigue, n, dt) - price * n;
                    if (value > best) { best = value; bestCount = n; spacing = dt; }
                }
            return bestCount;
        }

        /// <summary>
        /// Chance that a chaff seduction starting timeToGo before the missile arrives makes it miss. A seduction holds 1.5–3.5 s (ChaffPatches):
        /// one that outlasts the flight carries the missile past, a shorter one only costs it part of the turn it has left.
        /// </summary>
        public static float SeductionValue(float timeToGo)
        {
            float outlasts = Mathf.Clamp01((3.5f - timeToGo) / 2f);
            return outlasts + 0.15f * (1f - outlasts);
        }

        /// <summary>Chance that n bundles, spaced dt apart, each seducing with chance p, make a missile arriving in timeToGo miss.</summary>
        public static float ChaffScheduleChance(float p, float timeToGo, int n, float dt)
        {
            float miss = 1f;
            for (int i = 0; i < n; ++i) miss *= 1f - p * SeductionValue(Mathf.Max(timeToGo - i * dt, 0f));
            return 1f - miss;
        }

        /// <summary>
        /// Bundles to release now (and their spacing), each seducing with chance p at the current geometry and beamingChance in the notch;
        /// none while the geometry or the timing makes them worth less than they cost.
        /// </summary>
        public static int ChaffPlan(float p, float beamingChance, float timeToGo, int stock, int reserve, out float spacing)
        {
            spacing = spacings[1];
            if (!(p > 0f) || stock <= 0) return 0;
            float price = CountermeasurePrice(stock, reserve) * Mathf.Max(beamingChance, p), best = 0f;
            int bestCount = 0;
            foreach (var dt in spacings)
                for (int n = 1; n <= Math.Min(stock, maxRelease); ++n)
                {
                    if ((n - 1) * dt > timeToGo) break;
                    float value = ChaffScheduleChance(p, timeToGo, n, dt) - price * n;
                    if (value > best) { best = value; bestCount = n; spacing = dt; }
                }
            return bestCount;
        }

        /// <summary>
        /// Smoke, decoys and bubbles have no decoy model to plan against: release enough to meet each incoming threat, as far as the stock
        /// spares beyond what the enemies can still throw at us.
        /// </summary>
        public static int RationedRelease(int stock, int reserve, int threats)
        {
            if (stock <= 0) return 0;
            int spare = stock - reserve;
            return Mathf.Clamp(spare > 0 ? Mathf.CeilToInt(spare / (float)(reserve / Math.Max(threats, 1) + Math.Max(threats, 1))) : 1, 1, 4);
        }
        #endregion
    }

    public partial class MissileFire
    {
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Salvo Decisions"), // Missiles per target and countermeasure releases decided from the situation; off: the fixed numbers below.
            UI_Toggle(enabledText = "#LOC_BDArmory_Enabled", disabledText = "#LOC_BDArmory_Disabled", scene = UI_Scene.All)]
        public bool salvoDecisions = true;

        const float salvoDecisionInterval = 0.5f; // s: each decision may fly the missile through the shot model.
        Vessel salvoTarget;
        string salvoMissileType;
        float salvoTime = -1f;
        int salvoSize = 1;
        /// <summary>The last salvo decision: missile kill chance used, kill goal, allowance (for the metrics log).</summary>
        public float SalvoShotChance { get; private set; } = -1f;
        public float SalvoKillGoal { get; private set; }
        public int SalvoAllowance { get; private set; }
        public int salvoFlareReleases, salvoFlaresHeld, salvoChaffReleases, salvoChaffHeld;

        /// <summary>Missiles to keep in flight at the current target with the selected missile (the fixed maxMissilesOnTarget with salvo decisions off).</summary>
        public int MissilesOnTargetLimit => MissileSalvo(currentTarget != null ? currentTarget.Vessel : null, CurrentMissile != null ? CurrentMissile : PreviousMissile);

        /// <summary>Missiles to keep in flight at a target with a given missile.</summary>
        public int MissileSalvo(Vessel target, MissileBase missile)
        {
            if (!salvoDecisions) return (int)maxMissilesOnTarget;
            if (target == null || missile == null) return target == salvoTarget ? salvoSize : 1;
            // The current target's decision is kept for a moment (it may fly the missile through the shot model); other targets (multi-target
            // and point-defence scans) are sized on the spot without the flyout.
            bool current = currentTarget != null && target == currentTarget.Vessel;
            string type = missile.GetShortName();
            if (current && target == salvoTarget && type == salvoMissileType && Time.time - salvoTime < salvoDecisionInterval) return salvoSize;

            bool ground = target.LandedOrSplashed, intercept = target.IsMissile();
            var pilot = intercept ? null : PilotAI;
            float p = pilot != null ? pilot.ShotEstimateFor(target, missile) : -1f;
            if (p < 0f && current && !ground && !intercept && missile.engageAir && !missile.HasFired) p = ShotModel.Estimate(missile, vessel, target);
            if (p < 0f) p = missile.GuidanceMode == MissileBase.GuidanceModes.None ? 0.35f : 0.6f; // No model for this shot: a guess, guided or not.

            // Shoot-look-shoot while nothing is pressing us (the planner re-shoots once a missile is judged defeated); shoot-shoot when we
            // may not get the time to look.
            float theirShot = -1f, health = 1f;
            bool theirInescapable = false;
            int otherThreats = 0;
            if (pilot != null && !pilot.SalvoContext(target, out theirShot, out theirInescapable, out otherThreats, out health)) theirShot = pilot.OpponentShotEstimate;
            bool pressed = intercept || missileIsIncoming || theirInescapable || theirShot >= 0.35f || otherThreats > 0 || health < 0.6f;
            bool canLook = !pressed && !ground && theirShot >= 0f && theirShot < 0.2f;
            float killGoal = canLook ? 0f : pressed ? 0.9f : 0.75f;

            // This target's share of the missiles that can engage it, the other enemies of its kind taking theirs.
            int spare = 0, enemies = 0;
            foreach (var other in VesselModuleRegistry.GetModules<MissileBase>(vessel))
            {
                if (other == null || other.HasFired || other.GetWeaponClass() != missile.GetWeaponClass() || (intercept ? !other.engageMissile : ground ? !other.engageGround : !other.engageAir)) continue;
                var rearm = other is MissileLauncher ml && ml.multiLauncher != null ? ml.multiLauncher.missileSpawner : other.reloadableRail;
                spare += rearm != null ? Math.Max(rearm.ammoCount, 1) : 1;
            }
            if (intercept) enemies = PDMslTgts.Count;
            else
                foreach (var enemy in BDATargetManager.TargetList(Team))
                    if (enemy != null && enemy.Vessel != null && !enemy.isMissile && enemy.Vessel.LandedOrSplashed == ground) ++enemies;
            int allowance = Mathf.CeilToInt((spare + firedMissiles) / (float)Math.Max(enemies, 1));
            int size = SalvoDecisions.MissilesOnTarget(p, killGoal, allowance);
            // The wingman's missiles already in the air at it count towards the salvo (grinder: while it defends, we close for the next shot).
            // Only beyond close range and for a modest shot of our own: a good shot, or one inside 10 km where there's no time to look, is
            // taken regardless (sec1: 95 holds in 8 km fights, mostly behind the wingman's poorer long shots).
            const float grinderRange = 10000f, grinderShotLimit = 0.45f;
            bool grinder = !intercept && !ground && pilot != null && p < grinderShotLimit && (target.CoM - vessel.CoM).sqrMagnitude > grinderRange * grinderRange;
            int wingmanMissiles = grinder ? pilot.SectionMissilesAt(target) : 0;
            if (wingmanMissiles > 0)
            {
                int own = Math.Max(size - wingmanMissiles, 0);
                if (current && own < size && firedMissiles >= own) pilot.NoteSharedMissileHold();
                size = own;
            }
            if (!current) return size;
            salvoTarget = target; salvoMissileType = type; salvoTime = Time.time;
            SalvoShotChance = p; SalvoKillGoal = killGoal; SalvoAllowance = allowance;
            return salvoSize = size;
        }

        /// <summary>The last salvo decision and the countermeasure releases made and held so far (pilot AI metrics log).</summary>
        public string SalvoMetrics() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "salvo={0} salvoShot={1:F2} salvoGoal={2:F2} salvoAllowance={3} flareReleases={4}/{5} chaffReleases={6}/{7}",
            salvoDecisions ? salvoSize : (int)maxMissilesOnTarget, SalvoShotChance, SalvoKillGoal, SalvoAllowance, salvoFlareReleases, salvoFlaresHeld, salvoChaffReleases, salvoChaffHeld);

        /// <summary>How many more missiles a multi-launcher should send at the current target (at least one: it was told to fire).</summary>
        public int MissilesStillWanted() => Math.Max(1, MissilesOnTargetLimit - firedMissiles);

        /// <summary>Countermeasures of a type left on this craft.</summary>
        int CountermeasureStock(CMDropper.CountermeasureTypes type)
        {
            if (BDArmorySettings.INFINITE_COUNTERMEASURES) return maxRelease;
            int stock = 0;
            foreach (var cm in VesselModuleRegistry.GetModules<CMDropper>(vessel))
                if (cm != null && !cm.isMissileCM && cm.cmType == type) stock += cm.cmCount;
            return stock;
        }
        const int maxRelease = 8;

        /// <summary>Unfired missiles of the enemies that guide by a given mode: what the countermeasures left have to cover.</summary>
        int EnemyMissiles(MissileBase.TargetingModes mode)
        {
            int count = 0;
            foreach (var enemy in BDATargetManager.TargetList(Team))
            {
                if (enemy == null || enemy.Vessel == null || enemy.isMissile) continue;
                foreach (var missile in VesselModuleRegistry.GetModules<MissileBase>(enemy.Vessel))
                    if (missile != null && !missile.HasFired && missile.TargetingMode == mode) ++count;
            }
            return count;
        }

        /// <summary>The incoming missile guided by the given mode that arrives first, from the latest threat scan.</summary>
        IncomingMissile? FirstIncoming(MissileBase.TargetingModes mode)
        {
            IncomingMissile? first = null;
            if (lastIncomingMissiles == null) return null;
            foreach (var incoming in lastIncomingMissiles)
                if (incoming.vessel != null && incoming.guidanceType == mode && (first == null || incoming.time < first.Value.time)) first = incoming;
            return first;
        }
        List<IncomingMissile> lastIncomingMissiles;

        /// <summary>Flares to release now against the first incoming heat seeker (stock repetition with salvo decisions off or without the graded flare model).</summary>
        bool PlanFlares(out int count, out float interval)
        {
            count = (int)cmRepetition; interval = cmInterval;
            if (!salvoDecisions) return true;
            var threat = FirstIncoming(MissileBase.TargetingModes.Heat);
            var seeker = threat != null ? VesselModuleRegistry.GetMissileBase(threat.Value.vessel) : null;
            float p = HeatPatches.FlareChance(seeker, out float fatigue, out bool captured);
            if (p < 0f) return true;
            if (captured) { ++salvoFlaresHeld; return false; } // Already following a flare.
            int stock = CountermeasureStock(CMDropper.CountermeasureTypes.Flare);
            count = SalvoDecisions.FlarePlan(p, fatigue, threat.Value.time, stock, Math.Min(3 * EnemyMissiles(MissileBase.TargetingModes.Heat), 24), out interval);
            if (count > 0) ++salvoFlareReleases; else ++salvoFlaresHeld;
            return count > 0;
        }

        /// <summary>Chaff to release now against the first incoming radar missile.</summary>
        bool PlanChaff(out int count, out float interval)
        {
            count = (int)chaffRepetition; interval = chaffInterval;
            if (!salvoDecisions) return true;
            var threat = FirstIncoming(MissileBase.TargetingModes.Radar);
            if (threat == null) return true; // A radar lock without a missile yet (RWR): breaking it is worth the fixed release.
            var seeker = VesselModuleRegistry.GetMissileBase(threat.Value.vessel);
            float p = ChaffPatches.BundleChance(seeker, vessel, out float beamingChance, out bool seduced);
            if (p < 0f) return true;
            if (seduced) { ++salvoChaffHeld; return false; }
            int stock = CountermeasureStock(CMDropper.CountermeasureTypes.Chaff);
            count = SalvoDecisions.ChaffPlan(p, beamingChance, threat.Value.time, stock, Math.Min(2 * EnemyMissiles(MissileBase.TargetingModes.Radar), 24), out interval);
            if (count > 0) ++salvoChaffReleases; else ++salvoChaffHeld;
            return count > 0;
        }

        /// <summary>
        /// Flares put out ahead of a likely heat-seeker launch (a head-on pass, an opponent at our six), when no missile is in the air yet:
        /// up to two, as far as the stock spares beyond what the enemies' heat-seekers will need (the fixed release with salvo decisions off).
        /// </summary>
        public bool FirePreemptiveFlares()
        {
            if (isFlaring) return false;
            int count = (int)cmRepetition;
            float interval = cmInterval;
            if (salvoDecisions)
            {
                count = Math.Min(2, CountermeasureStock(CMDropper.CountermeasureTypes.Flare) - Math.Min(3 * EnemyMissiles(MissileBase.TargetingModes.Heat), 24));
                interval = 0.5f;
            }
            if (count <= 0) return false;
            StartCoroutine(FlareRoutine(count, interval));
            return true;
        }

        /// <summary>Whether the first incoming missile of this guidance is already following a decoy (a release in progress can stop).</summary>
        bool ThreatDecoyed(MissileBase.TargetingModes mode)
        {
            if (!salvoDecisions) return false;
            var threat = FirstIncoming(mode);
            var seeker = threat != null ? VesselModuleRegistry.GetMissileBase(threat.Value.vessel) : null;
            if (seeker == null) return false;
            bool decoyed = false;
            if (mode == MissileBase.TargetingModes.Heat) HeatPatches.FlareChance(seeker, out _, out decoyed);
            else if (mode == MissileBase.TargetingModes.Radar) ChaffPatches.BundleChance(seeker, vessel, out _, out decoyed);
            return decoyed;
        }

        /// <summary>Smoke, decoys or bubbles to release against the incoming threats of the given guidance.</summary>
        int RationedCountermeasures(CMDropper.CountermeasureTypes type, MissileBase.TargetingModes mode, int fixedCount)
        {
            if (!salvoDecisions) return fixedCount;
            int threats = 0;
            if (lastIncomingMissiles != null) foreach (var incoming in lastIncomingMissiles) if (incoming.guidanceType == mode) ++threats;
            return SalvoDecisions.RationedRelease(CountermeasureStock(type), EnemyMissiles(mode), threats);
        }
    }
}
