using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using BDArmory.Competition;
using BDArmory.Control;
using BDArmory.CounterMeasure;
using BDArmory.Extensions;
using BDArmory.FX;
using BDArmory.Radar;
using BDArmory.Settings;
using BDArmory.Targeting;
using BDArmory.UI;
using BDArmory.Utils;
using BDArmory.VesselSpawning;
using BDArmory.Weapons;
using BDArmory.Weapons.Missiles;

namespace BDArmory.GameModes
{
    /// <summary>
    /// Battle damage without debris (BD_NO_DEBRIS): a part of a combat craft whose hit points run out is wrecked in place instead of
    /// exploding off the craft — its engines, lift, control, weapons, sensors and stores stop working, and a part at the end of the
    /// structure disappears from view — so no debris vessels are made. A craft is shot down once its pilot, its weapon manager or all
    /// of its engines are gone, or it has lost BD_NO_DEBRIS_LIFT_LOSS of its lift or BD_NO_DEBRIS_MASS_LOSS of its mass; it then drops
    /// out of the fight (the last craft to damage it gets the kill as usual) and is removed whole after BD_NO_DEBRIS_REMOVE_DELAY s.
    /// </summary>
    public static class CleanDamage
    {
        public static bool Active => BDArmorySettings.BATTLEDAMAGE && BDArmorySettings.BD_NO_DEBRIS && !BDArmorySettings.PAINTBALL_MODE;

        class Tally
        {
            public float lift, mass;            // At the first wreck: total lifting-surface coefficient and mass.
            public float liftLost, massLost;
            public int engines;
        }
        static readonly HashSet<Part> wrecked = new HashSet<Part>();
        static readonly Dictionary<Vessel, Tally> tallies = new Dictionary<Vessel, Tally>();
        static readonly HashSet<Vessel> shotDown = new HashSet<Vessel>();
        public static int PartsWrecked, CraftShotDown;

        public static bool IsWrecked(Part p) => p != null && wrecked.Contains(p);
        public static bool IsShotDown(Vessel v) => v != null && shotDown.Contains(v);

        /// <summary>Forget everything (flight scene start).</summary>
        public static void Reset() { wrecked.Clear(); tallies.Clear(); shotDown.Clear(); }

        /// <summary>Whether a part's destruction is handled here: a part of a flying combat craft, not a missile or a kerbal.</summary>
        static bool Applies(Part p)
        {
            if (!Active || p == null || p.vessel == null || !HighLogic.LoadedSceneIsFlight) return false;
            var v = p.vessel;
            if (p.IsKerbalEVA()) return false;
            if (shotDown.Contains(v)) return true; // Marked as debris when shot down, but still wrecked in place until it is removed.
            if (v.IsMissile() || v.vesselType == VesselType.Debris) return false;
            return VesselModuleRegistry.GetMissileFire(v, true) != null;
        }

        /// <summary>
        /// Wreck the part in place instead of exploding it. Returns false (explode as usual) when the mode is off or the part isn't on a
        /// combat craft.
        /// </summary>
        public static bool TryWreck(Part p, string cause)
        {
            if (!Applies(p)) return false;
            if (!wrecked.Add(p)) return true;
            if (wrecked.Count % 64 == 0) wrecked.RemoveWhere(x => x == null);
            var v = p.vessel;
            var tally = TallyOf(v);
            ++PartsWrecked;
            float liftShare = 0f;
            foreach (var module in p.Modules)
            {
                switch (module)
                {
                    case ModuleEngines engine:
                        if (engine.EngineIgnited) engine.Flameout("Engine destroyed", false, true);
                        engine.Shutdown();
                        engine.allowRestart = false;
                        engine.allowShutdown = false;
                        break;
                    case ModuleControlSurface surface:
                        liftShare += surface.deflectionLiftCoeff;
                        surface.ignorePitch = surface.ignoreYaw = surface.ignoreRoll = true;
                        surface.authorityLimiter = 0f;
                        surface.deflectionLiftCoeff = 0f;
                        break;
                    case ModuleLiftingSurface wing:
                        liftShare += wing.deflectionLiftCoeff;
                        wing.deflectionLiftCoeff = 0f;
                        break;
                    case ModuleWeapon weapon:
                        weapon.DisableWeapon();
                        weapon.engageEnabled = false;
                        break;
                    case MissileBase missile:
                        if (!missile.HasFired) { missile.engageAir = missile.engageGround = missile.engageMissile = missile.engageSLW = false; }
                        break;
                    case ModuleRadar radar:
                        if (radar.radarEnabled) radar.DisableRadar();
                        break;
                    case ModuleECMJammer jammer:
                        if (jammer.jammerEnabled) jammer.DisableJammer();
                        break;
                    case CMDropper dropper:
                        dropper.cmCount = 0;
                        break;
                }
            }
            foreach (var resource in p.Resources) resource.amount = 0;
            p.ShieldedFromAirstream = true; // No lift or drag from the wreck.
            tally.liftLost += liftShare;
            tally.massLost += p.mass;
            if (p.children.Count == 0) Hide(p); // A part at the end of the structure looks shot off.
            if (BDArmorySettings.DEBUG_DAMAGE) Debug.Log($"[BDArmory.CleanDamage]: {p.partInfo.title} on {v.vesselName} wrecked ({cause})");
            var weaponManager = VesselModuleRegistry.GetMissileFire(v, true);
            if (weaponManager != null) weaponManager.UpdateList();
            Evaluate(v, p);
            return true;
        }

        static Tally TallyOf(Vessel v)
        {
            if (tallies.TryGetValue(v, out var tally)) return tally;
            tally = new Tally();
            foreach (var part in v.Parts)
            {
                if (part == null) continue;
                tally.mass += part.mass + part.GetResourceMass();
                foreach (var module in part.Modules)
                {
                    if (module is ModuleLiftingSurface wing) tally.lift += wing.deflectionLiftCoeff;
                    else if (module is ModuleEngines) ++tally.engines;
                }
            }
            if (tallies.Count > 32) { var gone = new List<Vessel>(); foreach (var key in tallies.Keys) if (key == null) gone.Add(key); foreach (var key in gone) tallies.Remove(key); }
            tallies[v] = tally;
            return tally;
        }

        static void Hide(Part p)
        {
            foreach (var renderer in p.GetComponentsInChildren<MeshRenderer>(true)) renderer.enabled = false;
            foreach (var renderer in p.GetComponentsInChildren<SkinnedMeshRenderer>(true)) renderer.enabled = false;
            foreach (var collider in p.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        }

        /// <summary>After a crew member is killed: shoot the craft down if nobody is left to fly it.</summary>
        public static void CrewLost(Vessel v)
        {
            if (!Active || v == null || shotDown.Contains(v) || v.IsMissile()) return;
            if (!Controlled(v)) ShootDown(v, "pilot killed");
        }

        /// <summary>Whether the craft can still fight after a wreck, and if not, why.</summary>
        static void Evaluate(Vessel v, Part last)
        {
            if (shotDown.Contains(v)) return;
            var tally = TallyOf(v);
            string reason = null;
            var weaponManager = VesselModuleRegistry.GetMissileFire(v, true);
            if (weaponManager == null || wrecked.Contains(weaponManager.part)) reason = "weapon manager destroyed";
            else if (!Controlled(v)) reason = "cockpit destroyed";
            else if (tally.engines > 0 && LiveEngines(v) == 0) reason = "engines destroyed";
            else if (tally.lift > 0f && tally.liftLost >= BDArmorySettings.BD_NO_DEBRIS_LIFT_LOSS * tally.lift) reason = "wing destroyed";
            else if (tally.mass > 0f && tally.massLost >= BDArmorySettings.BD_NO_DEBRIS_MASS_LOSS * tally.mass) reason = "airframe destroyed";
            if (reason != null) ShootDown(v, reason);
        }

        /// <summary>A command part that isn't wrecked and has the crew it needs.</summary>
        static bool Controlled(Vessel v)
        {
            foreach (var command in VesselModuleRegistry.GetModuleCommands(v))
                if (command != null && !wrecked.Contains(command.part) && (command.minimumCrew <= 0 || command.part.protoModuleCrew.Count >= command.minimumCrew)) return true;
            foreach (var seat in VesselModuleRegistry.GetKerbalSeats(v))
                if (seat != null && !wrecked.Contains(seat.part) && seat.Occupant != null) return true;
            return false;
        }

        static int LiveEngines(Vessel v)
        {
            int live = 0;
            foreach (var engine in VesselModuleRegistry.GetModuleEngines(v))
                if (engine != null && !wrecked.Contains(engine.part)) ++live;
            return live;
        }

        /// <summary>
        /// Take a craft out of the fight whole: AI and weapons off, no longer a target, an explosion for the eye, removed after the delay
        /// (at once near the ground, where its crash would break it up).
        /// </summary>
        public static void ShootDown(Vessel v, string reason)
        {
            if (v == null || !shotDown.Add(v)) return;
            if (shotDown.Count % 16 == 0) shotDown.RemoveWhere(x => x == null);
            ++CraftShotDown;
            Debug.Log($"[BDArmory.CleanDamage]: {v.vesselName} shot down: {reason}");
            if (BDACompetitionMode.Instance != null && BDACompetitionMode.Instance.competitionIsActive) BDACompetitionMode.Instance.competitionStatus.Add($"{v.vesselName} shot down: {reason}");
            var controller = v.ActiveController();
            if (controller.AI != null) controller.AI.DeactivatePilot();
            if (controller.WM != null) controller.WM.guardMode = false;
            foreach (var engine in VesselModuleRegistry.GetModuleEngines(v)) if (engine != null && engine.EngineIgnited) engine.Shutdown();
            ExplosionFx.CreateExplosion(v.CoM, 1f, "BDArmory/Models/explosion/explosion", VesselUtils.explSoundPath, ExplosionSourceType.Other, 0, null, isfx: true, sourceVelocity: v.Velocity());
            v.vesselType = VesselType.Debris;
            var info = v.gameObject.GetComponent<TargetInfo>();
            if (info != null) BDATargetManager.RemoveTarget(info);
            BDATargetManager.LoadedVessels.Remove(v);
            if (BDArmorySetup.Instance != null) BDArmorySetup.Instance.StartCoroutine(RemoveLater(v));
            else SpawnUtils.RemoveVessel(v);
        }

        static IEnumerator RemoveLater(Vessel v)
        {
            float until = Time.time + BDArmorySettings.BD_NO_DEBRIS_REMOVE_DELAY;
            var wait = new WaitForFixedUpdate();
            while (v != null && Time.time < until && v.radarAltitude > Mathf.Max(30f, 2f * (float)v.srfSpeed * Time.fixedDeltaTime + 10f)) yield return wait;
            if (v != null) SpawnUtils.RemoveVessel(v);
        }
    }
}
