using UnityEngine;

namespace BDArmory.Control
{
    /// <summary>
    /// What the Pilot Skill setting (Rookie, Trained, Veteran, Ace) means for each part of the pilot's behaviour, in one table. An ace
    /// flies everything (today's behaviour, and the default for every craft). Lower tiers lose the judgement layers first, then the
    /// tactics, react later to missiles, use less of the envelope, plan their close-in manoeuvres less far ahead and keep a poorer lookout.
    /// </summary>
    public partial class BDModulePilotAI
    {
        public enum SkillFeature
        {
            Tactics,            // Advanced tactics, close-in manoeuvre planner, tactic commitment, kinematic missile evasion.
            SectionTactics,     // Two-ship mutual support, bracket, sort.
            FightChoice,        // Pick a one-circle or two-circle fight from both aircraft's turning performance.
            EngagementPlanner,  // Long-range judgement: forcing shots, aborts, no-merge, press, cut-off.
            Matchup,            // Standoff or compress from both aircraft's reach.
            FlyoutEvasion,      // Fly the incoming missile's flyout to pick the break.
            Awareness,          // Warning-receiver picture, no-escape shots, lofted launches, double notch, drag, terrain masking.
        }

        // Lowest tier (0 rookie .. 3 ace) that flies each feature.
        static readonly int[] featureTier = { 1, 1, 2, 2, 2, 2, 2 };
        // Per tier, rookie to ace.
        static readonly float[] reactionTimes = { 1.4f, 0.8f, 0.4f, 0f };        // s from a missile warning to acting on it.
        static readonly float[] envelopeShares = { 0.6f, 0.75f, 0.9f, 1f };      // Share of the available lift used in the close-in planner.
        static readonly float[] planHorizons = { 2.4f, 2.4f, 3.2f, 4f };         // s the close-in planner looks ahead.
        static readonly float[] replanIntervals = { 0.45f, 0.45f, 0.33f, 0.25f }; // s between close-in plans.
        static readonly float[] lookouts = { 0.7f, 0.85f, 0.95f, 1f };           // Share of the visual detection range.

        public bool Knows(SkillFeature feature) => SkillLevel >= featureTier[(int)feature];
        public float ReactionTime => reactionTimes[SkillLevel];
        public float EnvelopeShare => envelopeShares[SkillLevel];
        public float LookoutFactor => lookouts[SkillLevel];
        float PlanHorizon => planHorizons[SkillLevel];
        float ReplanInterval => replanIntervals[SkillLevel];

        Vessel reactionMissile;
        float reactionSince;
        /// <summary>Whether the pilot has reacted to this incoming missile yet: its reaction time after the warning first named it.</summary>
        bool MissileNoticed(Vessel missile)
        {
            if (missile == null) return false;
            if (ReactionTime <= 0f) return true;
            if (missile != reactionMissile) { reactionMissile = missile; reactionSince = Time.time; }
            return Time.time - reactionSince >= ReactionTime;
        }

        /// <summary>One line for the AI window: what this tier flies.</summary>
        public string SkillSummary()
        {
            var flies = new System.Collections.Generic.List<string>();
            if (Knows(SkillFeature.Tactics)) flies.Add("tactics");
            if (Knows(SkillFeature.SectionTactics)) flies.Add("section");
            if (Knows(SkillFeature.FightChoice)) flies.Add("fight choice");
            if (Knows(SkillFeature.EngagementPlanner)) flies.Add("planner");
            if (Knows(SkillFeature.Matchup)) flies.Add("matchup");
            if (Knows(SkillFeature.FlyoutEvasion)) flies.Add("flyout");
            if (Knows(SkillFeature.Awareness)) flies.Add("awareness");
            return $"React {ReactionTime:0.0}s · lift {EnvelopeShare * 100f:0}% · look-ahead {PlanHorizon:0.0}s · lookout {LookoutFactor * 100f:0}% · {(flies.Count > 0 ? string.Join(", ", flies) : "basic")}";
        }
    }
}
