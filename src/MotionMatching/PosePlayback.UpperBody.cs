using System;
using UnityEngine;

namespace Manimal.MotionMatching
{
    internal sealed partial class PosePlayback
    {
        // Shared by reaction clips, independent of lower-body ownership and the native weapon IK pass.
        private static readonly string[] ReactionUpperBones = {
            "Base HumanSpine1", "Base HumanSpine2", "Base HumanSpine3", "Base HumanRibcage",
            "Base HumanLCollarbone", "Base HumanLUpperarm", "Base HumanLForearm1",
            "Base HumanRCollarbone", "Base HumanRUpperarm", "Base HumanRForearm1"
        };
        private Transform[] _upperBones;
        private Transform _reactionPalm;
        private readonly Inertializer _upperInertia = new Inertializer();
        private readonly BoundedRotationRecovery _upperRecovery = new BoundedRotationRecovery();
        private Quaternion[] _upperNative, _upperPreviousNative, _upperShown, _upperPreviousShown, _upperTarget, _upperTargetBefore;
        private PoseClip _upperClip;
        private bool _upperPlaying, _upperHistory;
        private float _upperTime = -1f;
        internal int UpperBodyReactionFrames { get; private set; }

        internal bool ApplyReactionUpperBody()
        {
            if (_overlayClip != null && (!UpperOverlaySafe || Time.time - _overlayStarted > (_overlayClip.Frames - 1) / _overlayClip.Fps))
                _overlayClip = null;
            bool overlay = _overlayClip != null;
            PoseClip sourceClip = overlay ? _overlayClip : _active;
            float sourceFrame = overlay ? UpperOverlayFrame : _frame;
            bool playing = Enabled && (overlay || _phase == Phase.Reaction) && sourceClip?.UpperBodyRotations != null;
            if (!playing && !_upperPlaying && !_upperInertia.Active && !_upperRecovery.Active) return false;
            if (NativeJumpOwnership.Owns(_player) || !_player.HealthController.IsAlive
                || !FootPlacer.TryGetWeaponCarryContext(_player, out var controller, out var weapon))
            {
                _upperInertia.Cancel(); _upperRecovery.Cancel(); _upperPlaying = _upperHistory = false; _upperClip = null;
                return false;
            }
            if (_upperBones == null)
            {
                _upperBones = new Transform[ReactionUpperBones.Length];
                for (int i = 0; i < _upperBones.Length; i++)
                {
                    _upperBones[i] = FindChild(_pelvis, ReactionUpperBones[i]);
                    if (!_upperBones[i]) throw new InvalidOperationException("Missing reaction bone: " + ReactionUpperBones[i]);
                }
                _reactionPalm = FindChild(_pelvis, "Base HumanRPalm");
                if (!_reactionPalm) throw new InvalidOperationException("Missing right reaction palm");
                _supportPalm = FindChild(_pelvis, "Base HumanLPalm");
                if (!_supportPalm) throw new InvalidOperationException("Missing left reaction palm");
                _upperNative = new Quaternion[_upperBones.Length];
                _upperPreviousNative = new Quaternion[_upperBones.Length];
                _upperShown = new Quaternion[_upperBones.Length];
                _upperPreviousShown = new Quaternion[_upperBones.Length];
                _upperTarget = new Quaternion[_upperBones.Length];
                _upperTargetBefore = new Quaternion[_upperBones.Length];
            }
            var follower = _player.PlayerBones?.Weapon_Root_Anim;
            if (!follower) return false;
            float dt = Time.time - _upperTime;
            bool history = _upperHistory && dt > 0f && dt <= .05f;
            for (int i = 0; i < _upperBones.Length; i++)
            {
                _upperPreviousNative[i] = _upperNative[i];
                _upperNative[i] = _upperBones[i].localRotation;
            }
            bool switching = playing && (!_upperPlaying || _upperClip != sourceClip);
            bool releasing = !playing && _upperPlaying;
            if (playing)
            {
                int a = Mathf.Clamp(Mathf.FloorToInt(sourceFrame), 0, sourceClip.Frames - 1);
                int b = Mathf.Min(a + 1, sourceClip.Frames - 1);
                float sampleDt = history ? dt : Mathf.Clamp(Time.deltaTime, .001f, .05f);
                float previousFrame = Mathf.Max(0f, sourceFrame - sampleDt * sourceClip.Fps * (overlay ? 1f : Rate));
                int pa = Mathf.FloorToInt(previousFrame), pb = Mathf.Min(pa + 1, sourceClip.Frames - 1);
                for (int i = 0; i < _upperBones.Length; i++)
                {
                    if (!sourceClip.UpperBodyRotations.TryGetValue(ReactionUpperBones[i], out var rotations))
                        throw new InvalidOperationException("Incomplete upper-body clip: " + sourceClip.Name);
                    _upperTarget[i] = overlay ? OverlayTarget(sourceClip, i, sourceFrame, _upperNative[i]) : Quaternion.Slerp(rotations[a], rotations[b], sourceFrame - a);
                    _upperTargetBefore[i] = overlay ? OverlayTarget(sourceClip, i, previousFrame, history ? _upperPreviousNative[i] : _upperNative[i]) : Quaternion.Slerp(rotations[pa], rotations[pb], previousFrame - pa);
                }
            }
            if (switching)
            {
                bool continuing = history && (_upperPlaying || _upperInertia.Active || _upperRecovery.Active);
                _upperInertia.Begin(continuing ? _upperShown : _upperNative, _upperTarget, Vector3.zero, Vector3.zero, .2f,
                    history ? (continuing ? _upperPreviousShown : _upperPreviousNative) : null,
                    _upperTargetBefore, Vector3.zero, Vector3.zero, history ? dt : Mathf.Clamp(Time.deltaTime, .001f, .05f));
                _upperRecovery.Cancel();
            }
            else if (releasing)
            {
                _upperRecovery.Begin(_upperShown, _upperNative, HandOffBlend);
                _upperInertia.Cancel();
            }
            else
            {
                if (_upperInertia.Active) _upperInertia.Advance(Mathf.Max(0f, dt));
                if (_upperRecovery.Active) _upperRecovery.Advance(dt);
            }

            // Preserve the active weapon's exact right-hand grip through this visual pass.
            Quaternion inversePalm = Quaternion.Inverse(_reactionPalm.rotation);
            Vector3 gripPosition = inversePalm * (follower.position - _reactionPalm.position);
            Quaternion gripRotation = inversePalm * follower.rotation;
            Quaternion inverseWeapon = Quaternion.Inverse(follower.rotation);
            Vector3 supportPosition = inverseWeapon * (_supportPalm.position - follower.position);
            Quaternion supportRotation = inverseWeapon * _supportPalm.rotation;
            for (int i = 0; i < _upperBones.Length; i++)
            {
                _upperPreviousShown[i] = _upperShown[i];
                _upperBones[i].localRotation = (playing ? _upperInertia.Rotation(i) : _upperRecovery.Rotation(i)) * (playing ? _upperTarget[i] : _upperNative[i]);
            }
            if (FootPlacer.IsSameWeaponCarryContext(_player, controller, weapon))
            {
                follower.SetPositionAndRotation(_reactionPalm.position + _reactionPalm.rotation * gripPosition,
                    _reactionPalm.rotation * gripRotation);
                PreserveSupportGrip(follower, gripPosition, gripRotation, supportPosition, supportRotation);
            }
            for (int i = 0; i < _upperBones.Length; i++) _upperShown[i] = _upperBones[i].localRotation;
            _upperTime = Time.time; _upperHistory = true;
            _upperPlaying = playing; _upperClip = playing ? sourceClip : null;
            UpperBodyReactionFrames++;
            return true;
        }
    }
}
