using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

using BDArmory.Control;

namespace BDArmory.Utils
{
    /// <summary>
    /// Harness diagnostic (only with steering metrics on): how far each part of a piloted craft sits from where its design puts it relative to
    /// its parent, and whether that design pose itself moves. Logs a part when its offset grows past the last logged value, every joint
    /// component a part loses (a broken joint), vessel modifications, and a periodic summary with the servos' states and the G load.
    /// </summary>
    public class JointProbe : VesselModule
    {
        const float interval = 0.2f, summaryInterval = 5f, settleTime = 2f;
        const float stretchStep = 0.05f, twistStep = 3f, stretchFloor = 0.08f, twistFloor = 4f;

        class Track
        {
            public Part part, parent;
            public Vector3 offset0; // Physical offset from the design pose at the start, in the parent's frame.
            public Quaternion twist0;
            public Vector3 orgRelPos0;
            public Quaternion orgRelRot0;
            public int joints0;
            public Joint attach0;
            public string servoZero0;
            public float loggedStretch = stretchFloor, loggedTwist = twistFloor, stretch, twist, orgDrift, orgTwist;
        }

        readonly Dictionary<Part, Track> tracks = new Dictionary<Part, Track>();
        float nextUpdate = -1f, nextSummary, maxG, lastModified = -1f, startTime;
        bool started;

        static FieldInfo irPosition, irCommanded, irJoint, irCorrection0, irCorrection1, irConnectedZero;
        static bool irLooked;

        protected override void OnStart()
        {
            base.OnStart();
            if (!BDModulePilotAI.steeringMetricsEnabled || !HighLogic.LoadedSceneIsFlight) { enabled = false; return; }
            GameEvents.onVesselWasModified.Add(OnModified);
            GameEvents.onPartJointBreak.Add(LogPartBreak);
        }

        void OnDestroy()
        {
            GameEvents.onVesselWasModified.Remove(OnModified);
            GameEvents.onPartJointBreak.Remove(LogPartBreak);
        }

        void OnModified(Vessel v)
        {
            if (v != vessel || !started) return;
            lastModified = Time.time;
            Debug.Log($"[BDArmory.JointProbe]: {vessel.vesselName} modified t={Time.time - startTime:F1} parts={vessel.parts.Count} G={vessel.geeForce:F1}");
        }

        void LogPartBreak(PartJoint joint, float force)
        {
            if (!started || joint == null || joint.Child == null || joint.Child.vessel != vessel) return;
            Debug.Log($"[BDArmory.JointProbe]: {vessel.vesselName} attach joint broke {Name(joint.Child)} from {Name(joint.Parent)} force={force:F0} G={vessel.geeForce:F1}");
        }

        static string Name(Part p) => p == null ? "none" : $"{p.partInfo?.name}#{p.persistentId % 10000}";

        bool Piloted() => vessel != null && VesselModuleRegistry.GetModule<BDModulePilotAI>(vessel) != null;

        void FixedUpdate()
        {
            if (vessel == null || !vessel.loaded || vessel.packed) return;
            maxG = Mathf.Max(maxG, (float)vessel.geeForce);
            if (nextUpdate < 0f) { nextUpdate = Time.time + settleTime; return; }
            if (Time.time < nextUpdate) return;
            nextUpdate = Time.time + interval;
            if (!started)
            {
                if (!Piloted()) { enabled = false; return; }
                started = true; startTime = Time.time; nextSummary = Time.time + summaryInterval;
                foreach (var part in vessel.parts) Add(part);
                Debug.Log($"[BDArmory.JointProbe]: {vessel.vesselName} tracking {tracks.Count} parts");
                return;
            }
            Measure();
            if (Time.time >= nextSummary) { nextSummary = Time.time + summaryInterval; Summary(); maxG = 0f; }
        }

        void Add(Part part)
        {
            if (part == null || part.parent == null || tracks.ContainsKey(part)) return;
            var t = new Track { part = part, parent = part.parent, joints0 = part.GetComponents<Joint>().Length, attach0 = part.attachJoint?.Joint, servoZero0 = ServoZero(part) };
            Pose(t, out var offset, out var twist, out t.orgRelPos0, out t.orgRelRot0);
            t.offset0 = offset; t.twist0 = twist;
            tracks[part] = t;
        }

        /// <summary>Physical pose relative to the parent compared with the design pose (part.orgPos/orgRot, which servos keep up to date as they move).</summary>
        static void Pose(Track t, out Vector3 offset, out Quaternion twist, out Vector3 orgRelPos, out Quaternion orgRelRot)
        {
            var inverseParentOrg = Quaternion.Inverse(t.parent.orgRot);
            orgRelPos = inverseParentOrg * (t.part.orgPos - t.parent.orgPos);
            orgRelRot = inverseParentOrg * t.part.orgRot;
            Vector3 physRelPos = t.parent.transform.InverseTransformPoint(t.part.transform.position);
            Quaternion physRelRot = Quaternion.Inverse(t.parent.transform.rotation) * t.part.transform.rotation;
            offset = physRelPos - orgRelPos;
            twist = Quaternion.Inverse(orgRelRot) * physRelRot;
        }

        void Measure()
        {
            List<Part> gone = null;
            foreach (var t in tracks.Values)
            {
                if (t.part == null || t.part.vessel != vessel || t.parent == null || t.part.parent != t.parent) { (gone ??= new List<Part>()).Add(t.part); continue; }
                Pose(t, out var offset, out var twist, out var orgRelPos, out var orgRelRot);
                t.stretch = (offset - t.offset0).magnitude;
                t.twist = Quaternion.Angle(t.twist0, twist);
                t.orgDrift = (orgRelPos - t.orgRelPos0).magnitude;
                t.orgTwist = Quaternion.Angle(t.orgRelRot0, orgRelRot);
                int joints = t.part.GetComponents<Joint>().Length;
                if (joints != t.joints0)
                {
                    Debug.Log($"[BDArmory.JointProbe]: {vessel.vesselName} joint count {Name(t.part)} {t.joints0}->{joints} (parent {Name(t.parent)}) G={vessel.geeForce:F1} maxG={maxG:F1} {Servo(t.part)}");
                    t.joints0 = joints;
                }
                var attach = t.part.attachJoint?.Joint;
                if (attach != t.attach0)
                {
                    Debug.Log($"[BDArmory.JointProbe]: {vessel.vesselName} attach joint replaced {Name(t.part)} on {Name(t.parent)} stretch={t.stretch:F2}m twist={t.twist:F1}deg G={vessel.geeForce:F1} sinceModified={(lastModified > 0 ? Time.time - lastModified : -1):F1}s {Servo(t.part)}");
                    t.attach0 = attach;
                }
                var zero = ServoZero(t.part);
                if (zero != t.servoZero0)
                {
                    Debug.Log($"[BDArmory.JointProbe]: {vessel.vesselName} servo re-zeroed {Name(t.part)} {t.servoZero0} -> {zero} stretch={t.stretch:F2}m twist={t.twist:F1}deg G={vessel.geeForce:F1} maxG={maxG:F1} sinceModified={(lastModified > 0 ? Time.time - lastModified : -1):F1}s {Servo(t.part)}");
                    t.servoZero0 = zero;
                }
                if (t.stretch > t.loggedStretch + stretchStep || t.twist > t.loggedTwist + twistStep)
                {
                    t.loggedStretch = Mathf.Max(t.loggedStretch, t.stretch); t.loggedTwist = Mathf.Max(t.loggedTwist, t.twist);
                    Debug.Log($"[BDArmory.JointProbe]: {vessel.vesselName} displaced {Name(t.part)} on {Name(t.parent)} stretch={t.stretch:F2}m twist={t.twist:F1}deg designDrift={t.orgDrift:F2}m/{t.orgTwist:F1}deg G={vessel.geeForce:F1} maxG={maxG:F1} q={vessel.dynamicPressurekPa:F0} sinceModified={(lastModified > 0 ? Time.time - lastModified : -1):F1}s {Servo(t.part)} {Servo(t.parent)}");
                }
            }
            if (gone != null) foreach (var p in gone) tracks.Remove(p);
        }

        void Summary()
        {
            var worst = tracks.Values.OrderByDescending(t => t.twist + 20f * t.stretch).Take(3).Select(t => $"{Name(t.part)}:{t.stretch:F2}m/{t.twist:F1}deg/design {t.orgDrift:F2}m/{t.orgTwist:F1}deg");
            var servos = vessel.parts.Where(p => p != null && p.Modules.Cast<PartModule>().Any(m => IsServo(m))).Select(p => $"{Name(p)}[{Servo(p)}{Carried(p)}]");
            Debug.Log($"[BDArmory.JointProbe]: {vessel.vesselName} summary t={Time.time - startTime:F0} maxG={maxG:F1} q={vessel.dynamicPressurekPa:F0} worst={string.Join(",", worst)} servos={string.Join(",", servos)}");
        }

        readonly Dictionary<Part, Vector3> carriedBaseline = new Dictionary<Part, Vector3>();
        /// <summary>
        /// How far the parts a servo carries have moved, in world metres, from where they sat at the start relative to the part the servo
        /// is mounted on (design pose, so the servo's own commanded motion doesn't count): what an onlooker sees as a part floating loose.
        /// </summary>
        string Carried(Part servo)
        {
            var mount = servo.parent;
            if (mount == null || servo.children.Count == 0) return "";
            float worst = 0f;
            var inverseMount = Quaternion.Inverse(mount.transform.rotation);
            foreach (var child in servo.children)
            {
                if (child == null) continue;
                Vector3 expected = mount.transform.position + mount.transform.rotation * (Quaternion.Inverse(mount.orgRot) * (child.orgPos - mount.orgPos));
                Vector3 offset = inverseMount * (child.transform.position - expected);
                if (!carriedBaseline.TryGetValue(child, out var baseline)) carriedBaseline[child] = baseline = offset;
                worst = Mathf.Max(worst, (offset - baseline).magnitude);
            }
            return $" carried={worst:0.000}m";
        }

        static bool IsServo(PartModule m) => m != null && (m.moduleName == "ModuleIRServo_v3" || m is Expansions.Serenity.BaseServo);

        /// <summary>The zero reference of an IR servo on this part (its joint and the angles it measures from), which IR re-derives from the current physical pose when it re-initialises.</summary>
        static string ServoZero(Part p)
        {
            if (p == null) return null;
            foreach (PartModule m in p.Modules)
            {
                if (m == null || m.moduleName != "ModuleIRServo_v3") continue;
                Servo(p); // Looks up the fields.
                var joint = irJoint?.GetValue(m) as UnityEngine.Object;
                return $"joint#{(joint != null ? joint.GetInstanceID() : 0)} c0={irCorrection0?.GetValue(m):0.##} c1={irCorrection1?.GetValue(m):0.##} zero={irConnectedZero?.GetValue(m):0.##}";
            }
            return null;
        }

        /// <summary>The state of a servo on this part, if any: IR's commanded and measured positions, a stock servo's target and current angle.</summary>
        static string Servo(Part p)
        {
            if (p == null) return "";
            foreach (PartModule m in p.Modules)
            {
                if (m == null) continue;
                if (m.moduleName == "ModuleIRServo_v3")
                {
                    if (!irLooked)
                    {
                        irLooked = true;
                        var type = m.GetType();
                        irPosition = type.GetField("position", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        irCommanded = type.GetField("commandedPosition", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        irJoint = type.GetField("Joint", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        irCorrection0 = type.GetField("correction_0", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        irCorrection1 = type.GetField("correction_1", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        irConnectedZero = type.GetField("jointconnectedzero", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    }
                    string joint = "";
                    if (irJoint?.GetValue(m) is ConfigurableJoint j && j != null && j.connectedBody != null)
                    {
                        // Linear slip of the servo joint in world metres (anchor against connected anchor), its linear motions and drive springs.
                        float gap = (j.transform.TransformPoint(j.anchor) - j.connectedBody.transform.TransformPoint(j.connectedAnchor)).magnitude;
                        joint = $" gap={gap:0.000}m lin={j.xMotion.ToString()[0]}{j.yMotion.ToString()[0]}{j.zMotion.ToString()[0]} ang={j.angularXMotion.ToString()[0]}{j.angularYMotion.ToString()[0]}{j.angularZMotion.ToString()[0]}"
                            + $" linSpring={j.xDrive.positionSpring:G2}/{j.yDrive.positionSpring:G2} angSpring={j.angularXDrive.positionSpring:G2}/{j.angularYZDrive.positionSpring:G2} joints={j.gameObject.GetComponents<Joint>().Length}";
                    }
                    return $"IR cmd={irCommanded?.GetValue(m):0.#} pos={irPosition?.GetValue(m):0.#}{joint}";
                }
                if (m is Expansions.Serenity.ModuleRoboticServoHinge hinge)
                    return $"hinge target={hinge.targetAngle:0.#} current={hinge.currentAngle:0.#}";
            }
            return "";
        }
    }
}
