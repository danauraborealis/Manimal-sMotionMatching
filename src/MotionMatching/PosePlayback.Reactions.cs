using System;
using UnityEngine;

namespace Manimal.MotionMatching
{
    internal sealed partial class PosePlayback
    {
        private PoseClip[] _reactionClips;
        private bool _reactionRequested;
        private readonly ReactionTorsoRecovery _reactionTorso = new ReactionTorsoRecovery();
        private float _reactionDeadline;
        private float _reactionBlend;
        private float _reactionYaw;
        private float _reactionSeverity;
        private bool _preferUpperHit;
        private string _reactionNamedClip;
        internal bool PreviewReaction(string name)
        {
            if (_reactionClips == null) return false;
            if (name.StartsWith("new_flinch_", StringComparison.Ordinal))
                return TryUpperHit(name.Contains("_02_") || name.Contains("_35_") ? 0f : 1f, name);
            if (_phase == Phase.Reaction && _active?.Name == name) return true;
            if (_reactionRequested || _overlayClip != null || _upperRecovery.Active) return false;
            _reactionNamedClip = name; _preferUpperHit = false; _reactionSeverity = 1f;
            _reactionRequested = true; _reactionDeadline = Time.time + .2f;
            return false;
        }
        internal Vector2? ReactionRootDrive { get; private set; }
        internal string ReactionResult { get; private set; } = "idle";
        internal int ReactionStarts { get; private set; }
        internal bool ReactionPending => _reactionRequested;
        internal bool ReadyForSyntheticReaction => _phase == Phase.SprintCycle && _weight > 0.75f && _phaseFor > 0.4f;
        internal void SetReactionDatabase(PoseDatabase database, string[] boneNames)
        {
            if (database.Bones.Length != boneNames.Length) throw new InvalidOperationException("Reaction bone layout differs.");
            for (int i = 0; i < boneNames.Length; i++)
                if (database.Bones[i] != boneNames[i]) throw new InvalidOperationException("Reaction bone order differs.");
            foreach (var clip in database.Clips)
                if (clip.UpperBodyRotations != null)
                    foreach (string name in ReactionUpperBones)
                    {
                        if (!clip.UpperBodyRotations.TryGetValue(name, out var values) || values.Length != clip.Frames)
                            throw new InvalidOperationException("Incomplete upper-body tracks: " + clip.Name + " / " + name);
                        foreach (Quaternion value in values)
                        {
                            float norm = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
                            if (!IsFinite(norm) || Mathf.Abs(norm - 1f) > .01f)
                                throw new InvalidOperationException("Invalid upper-body rotation: " + clip.Name + " / " + name);
                        }
                    }
            _reactionClips = database.Clips.ToArray();
        }
        internal void RequestReaction(float chance = .5f)
        {
            _reactionNamedClip = null;
            _reactionSeverity = Mathf.InverseLerp(HitReactionPolicy.BaseChance, HitReactionPolicy.MaximumChance, chance);
            _preferUpperHit = UnityEngine.Random.value > Mathf.Lerp(.15f, .65f, _reactionSeverity);
            _reactionRequested = true; _reactionDeadline = Time.time + 0.20f; ReactionResult = "pending";
        }
        internal void CancelReaction()
        {
            _reactionRequested = false;
            _overlayClip = null;
            _reactionClips = null;
            if (_phase == Phase.Reaction) { ReactionResult = "cancelled"; Begin(Phase.HandOff, _active, _frame); }
        }
        private bool ApplyReaction(float dt)
        {
            ReactionRootDrive = null;
            bool eligible = Enabled && Inertialize && _player.HealthController.IsAlive
                && (_player.MovementContext.IsGrounded || _phase == Phase.Reaction)
                && !NativeJumpOwnership.Owns(_player) && !_player.IsSprintEnabled && !_player.IsInPronePose
                && (_phase == Phase.Reaction || (_speed >= 1.2f && _speed <= 3.8f)) && Mathf.Abs(_yawRate) < 40f
                && _lastIntent.HasValue;
            if (_reactionRequested && (_overlayClip != null || _phase == Phase.Reaction || _upperRecovery.Active))
            { _reactionRequested = false; ReactionResult = "busy"; }
            if (_reactionRequested)
            {
                if (_reactionNamedClip == null && (_preferUpperHit || !eligible) && TryUpperHit(_reactionSeverity))
                { _reactionRequested = false; return false; }
                ReactionResult = "native_flinch:movement_or_phase";
                if (eligible && (_phase == Phase.SprintCycle || _phase == Phase.Idle) && _reactionClips != null)
                {
                    PoseClip best = null; int entry = 0; float cost = float.MaxValue; float bestBlend = .12f;
                    string rejected = "speed_or_direction";
                    float yaw = Mathf.Atan2(_localVelocity.x, _localVelocity.y) * Mathf.Rad2Deg;
                    foreach (var clip in _reactionClips)
                    {
                        if (clip.HasRole("hitreact")) continue;
                        if (_reactionNamedClip != null && clip.Name != _reactionNamedClip) continue;
                        if (clip.Stride == null || clip.FootL == null || clip.Contact == null || Mathf.Abs(Mathf.DeltaAngle(yaw, clip.MoveYaw)) > 25f) continue;
                        float ratio = _speed / Mathf.Max(clip.SpeedMetersPerSecond, 0.1f);
                        if (ratio < 0.65f || ratio > 1.35f) continue;
                        // Include the first landing (forward stumble: frame 8/21), retaining
                        // at least half the authored reaction for visible follow-through.
                        for (int f = 0; f <= clip.Frames / 2; f++)
                        {
                            // Use sole support and physical displacement, as the placer does. A one-shot's
                            // normalized progression is not comparable to the native walk's cyclic phase.
                            Vector3? l, r, lv, rv; bool? lc, rc;
                            ReadCycleHandoffFoot(clip, f, 0, out l, out lv, out lc);
                            ReadCycleHandoffFoot(clip, f, 1, out r, out rv, out rc);
                            float lb, rb;
                            if (!ReactionEntry.Foot(_hasShownFootbaseL ? _shownFootbaseL : (Vector3?)null, l,
                                _hasShownSupport ? _shownPlantedL : (bool?)null, lc, out lb, out rejected)) continue;
                            if (!ReactionEntry.Foot(_hasShownFootbaseR ? _shownFootbaseR : (Vector3?)null, r,
                                _hasShownSupport ? _shownPlantedR : (bool?)null, rc, out rb, out rejected)) continue;
                            float candidate = (l.Value - _shownFootbaseL).sqrMagnitude + (r.Value - _shownFootbaseR).sqrMagnitude;
                            if (candidate < cost) { best = clip; entry = f; cost = candidate; bestBlend = Mathf.Max(lb, rb); }
                        }
                    }
                    ReactionResult = "native_flinch:" + rejected;
                    if (best != null)
                    {
                        Begin(Phase.Reaction, best, entry);
                        _reactionTorso.Cancel(); _weight = 1f; _reactionBlend = Mathf.Max(.25f, bestBlend);
                        _reactionYaw = _player.Rotation.x;
                        // Keep outgoing ownership: fading the weight up from zero briefly exposed
                        // the native legs and dropped the existing placed feet on the first frame.
                        var target = new Quaternion[_bones.Length]; Vector3 pelvis;
                        SampleEntryPose(best, entry, target, out pelvis);
                        float historyDt = _lastSampleTime - _previousSampleTime;
                        var targetBefore = new Quaternion[_bones.Length]; Vector3 pelvisBefore;
                        SampleEntryPose(best, entry - best.Fps * Mathf.Max(historyDt, 0f), targetBefore, out pelvisBefore);
                        _inertia.Begin(_lastSampled, target, _lastPelvis, pelvis, _reactionBlend,
                            _previousSampled, targetBefore, _previousSampledPelvis, pelvisBefore, historyDt,
                            Time.time - _lastSampleTime);
                        ReactionStarts++; ReactionResult = "stumble:" + best.Name;
                        _reactionRequested = false;
                    }
                }
                if (!eligible || Time.time >= _reactionDeadline)
                {
                    if (_reactionRequested && _reactionNamedClip == null) TryUpperHit(_reactionSeverity);
                    _reactionRequested = false;
                }
                if (!_reactionRequested) _events.Add(string.Format("{0:F2}s Reaction {1}", Time.time, ReactionResult));
            }
            if (_phase != Phase.Reaction) return false;
            if (!eligible || _frame >= _active.Frames - 1)
            {
                ReactionResult = eligible ? "completed" : "released:movement_changed";
                Begin(Phase.HandOff, _active, _frame);
                return false;
            }
            _phaseFor += dt;
            Rate = 1f;
            _frame = Mathf.Min(_active.Frames - 1, _frame + dt * _active.Fps * Rate);
            _strideScale = Mathf.Clamp(_speed / Mathf.Max(_active.SpeedMetersPerSecond * Rate, 0.1f), 0.7f, 1.3f);
            // Finish the authored recovery. The handoff preserves the final pose and velocity.
            _weight = 1f;
            int a = Mathf.Clamp(Mathf.FloorToInt(_frame), 0, _active.Frames - 1);
            if (_active.RootVelocity != null && a < _active.RootVelocity.Length)
            {
                int next = Mathf.Min(a + 1, _active.RootVelocity.Length - 1);
                Vector2 local = Vector2.Lerp(_active.RootVelocity[a], _active.RootVelocity[next], _frame - a);
                Vector3 world = Quaternion.Euler(0f, _reactionYaw, 0f) * new Vector3(local.x, 0f, local.y);
                ReactionRootDrive = new Vector2(world.x, world.z);
            }
            // Follow the authored hip recoil without replacing native hand/grip tracks.
            Quaternion spineRotation = _spine.rotation;
            Quaternion hipsRotationBefore = _pelvis.rotation;
            Write(_active, a, Mathf.Min(a + 1, _active.Frames - 1), _frame - a, _weight);
            Quaternion turn = Quaternion.RotateTowards(Quaternion.identity,
                _pelvis.rotation * Quaternion.Inverse(hipsRotationBefore), 10f);
            if (_active.UpperBodyRotations == null) _spine.rotation = turn * spineRotation;
            Quaternion facing = Quaternion.Euler(0f, _player.Rotation.x, 0f);
            if (_active.UpperBodyRotations == null) _reactionTorso.Track(Quaternion.Inverse(facing) * turn * facing, dt);
            // Hand back from the pose actually written, including the exit weight, not the
            // full-strength source pose which would reappear when the handoff inertia starts.
            for (int i = 0; i < _bones.Length; i++) _lastSampled[i] = _bones[i].localRotation;
            _lastPelvis = _pelvis.localPosition;
            return true;
        }
    }
}
