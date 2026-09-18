using System;
using System.IO;
using EFT;
using UnityEngine;

namespace Manimal.MotionMatching
{
    public sealed partial class Plugin
    {
        private bool _reactionsEnabled, _reactionTest;
        private PoseDatabase _reactionDatabase;
        private HitReactionPolicy _reactionPolicy = new HitReactionPolicy();
        private float _nextTestHit;
        private string _reactionNote = "disabled";
        private int _testHit;
        private int _startsWhenRequested;
        private int _lastReactionStarts, _lastUpperBodyFrames;
        private int _lastUpperHitStarts, _lastLandingStarts;
        private string _lastReactionResult;
        private PoseDatabase _raidReactionDatabase;
        private bool _raidReactionLoadAttempted;

        // Normal test releases attach the reaction library to each gameplay rig. The database remains
        // separate from the locomotion database, and a failed optional load simply disables reactions.
        internal PoseDatabase LoadRaidReactionDatabase()
        {
            if (!_raidReactions.Value) return null;
            if (_raidReactionLoadAttempted) return _raidReactionDatabase;
            _raidReactionLoadAttempted = true;
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(Info.Location), "reaction_posedb.json");
                if (!File.Exists(path))
                {
                    Logger.LogWarning("Automatic hit reactions are unavailable: reaction_posedb.json is missing.");
                    return null;
                }
                var database = PoseDatabase.Load(path);
                if (database == null || database.Clips.Count == 0)
                    throw new InvalidOperationException("Reaction database is empty.");
                _raidReactionDatabase = database;
                return _raidReactionDatabase;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Automatic hit reactions are unavailable: " + ex.GetType().Name);
                return null;
            }
        }

        internal bool ConfigureRaidReactions(PosePlayback pose, PoseDatabase locomotionDatabase)
        {
            if (!_raidReactions.Value || pose == null) return false;
            var database = LoadRaidReactionDatabase();
            if (database == null || locomotionDatabase == null) return false;
            try
            {
                pose.SetReactionDatabase(database, locomotionDatabase.Bones);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Automatic hit reactions are unavailable for a bot: " + ex.GetType().Name);
                return false;
            }
        }

        internal object SetReactions(bool enabled, bool synthetic)
        {
            _reactionsEnabled = false; _reactionTest = false;
            _pose?.CancelReaction();
            if (!enabled) { _reactionNote = "disabled"; return ReactionStatus(); }
            try
            {
                // Load the reaction database alongside the plugin assembly for manual and automatic paths.
                _reactionDatabase = PoseDatabase.Load(Path.Combine(Path.GetDirectoryName(Info.Location), "reaction_posedb.json"));
                if (_reactionDatabase.Clips.Count == 0) throw new InvalidOperationException("Reaction database is empty.");
                _reactionsEnabled = true;
                AttachReactions();
                _reactionTest = synthetic;
                _reactionNote = "armed:selected_test_bot_only";
            }
            catch (Exception ex) { _reactionsEnabled = false; _reactionNote = "unavailable: " + ex.Message; }
            return ReactionStatus();
        }
        private void AttachReactions()
        {
            _reactionPolicy = new HitReactionPolicy(); _testHit = 0; _nextTestHit = Time.time + 1f;
            _lastReactionStarts = _lastUpperBodyFrames = 0; _lastReactionResult = null;
            _lastUpperHitStarts = _lastLandingStarts = 0;
            _showcaseStep = -1;
            if (_reactionsEnabled && _pose != null) _pose.SetReactionDatabase(_reactionDatabase, _poseDatabase.Bones);
        }
        internal bool WatchesDamage(Player player)
        {
            if (_raidReactions.Value && _fleet != null && _fleet.WatchesDamage(player)) return true;
            return _reactionsEnabled && _enabled.Value && _capture != null
                && _pose != null && _pose.Enabled && player != null && player == Selected && player.IsAI
                && player.HealthController != null && player.HealthController.IsAlive;
        }
        internal object ReactionStatus() => new { Enabled = _reactionsEnabled, Synthetic = _reactionTest,
            Ready = _reactionsEnabled && _pose != null && _capture != null, Note = _reactionNote,
            Accepted = _reactionPolicy.Accepted, Suppressed = _reactionPolicy.Suppressed,
            Chance = _reactionPolicy.LastChance, Roll = _reactionPolicy.LastRoll, Decision = _reactionPolicy.LastDecision,
            UpperBodyFrames = _pose?.UpperBodyReactionFrames ?? _lastUpperBodyFrames,
            UpperHits = _pose?.UpperHitStarts ?? _lastUpperHitStarts, Landings = _pose?.LandingStarts ?? _lastLandingStarts,
            Starts = _pose?.ReactionStarts ?? _lastReactionStarts, Result = _pose?.ReactionResult ?? _lastReactionResult };
        internal object InjectReaction(float damage) { ReceiveReactionDamage(Selected, damage, "synthetic"); return ReactionStatus(); }
        internal void ReceiveReactionDamage(Player player, float damage, string source)
        {
            if (_raidReactions.Value && _fleet != null && _fleet.WatchesDamage(player))
            {
                _fleet.ReceiveReactionDamage(player, damage, source);
                return;
            }
            if (!WatchesDamage(player)) return;
            bool trigger = _reactionPolicy.AddDamage(Time.time, damage, UnityEngine.Random.value);
            _reactionNote = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}: {1:F1} damage; {2}; chance {3:P0}, roll {4:F2}", source, damage,
                _reactionPolicy.LastDecision, _reactionPolicy.LastChance, _reactionPolicy.LastRoll);
            Logger.LogInfo("Reaction " + _reactionNote);
            if (trigger) { _startsWhenRequested = _pose.ReactionStarts; _pose.RequestReaction(_reactionPolicy.LastChance); }
        }
        private void ResolveReactionRequest()
        {
            if (_reactionPolicy.Pending && _pose != null && !_pose.ReactionPending)
                _reactionPolicy.Resolve(Time.time, _pose.ReactionStarts > _startsWhenRequested);
        }
        private void TickReactionTest()
        {
            if (!_reactionTest || _puppet == null || !WatchesDamage(Selected) || Time.time < _nextTestHit) return;
            if (!_pose.ReadyForSyntheticReaction) return;
            // Alternating burst and single hits exercise the same policy as real bullets, without health writes.
            float damage = _testHit % 3 == 2 ? 30f : 20f;
            ReceiveReactionDamage(Selected, damage, "synthetic");
            _nextTestHit = Time.time + .25f;
            _testHit++;
        }
    }
}
