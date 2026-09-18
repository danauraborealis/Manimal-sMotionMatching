using UnityEngine;
using ZLinq;

namespace Manimal.MotionMatching
{
    internal sealed partial class PosePlayback
    {
        private PoseClip _overlayClip;
        private float _overlayStarted, _overlayTorso, _overlayArms;
        private string _overlayKind;
        internal string UpperOverlay => _overlayClip == null ? null : _overlayKind + ":" + _overlayClip.Name;
        internal float UpperOverlayFrame => _overlayClip == null ? 0f : Mathf.Min(_overlayClip.Frames - 1, (Time.time - _overlayStarted) * _overlayClip.Fps);
        internal int UpperHitStarts { get; private set; }
        private bool UpperOverlaySafe => Enabled && _player.HealthController.IsAlive && !NativeJumpOwnership.Owns(_player)
            && !_player.IsInPronePose && _player.MovementContext != null
            && FootPlacer.TryGetWeaponCarryContext(_player, out _, out _);
        private bool UpperOverlayEligible => UpperOverlaySafe && _player.MovementContext.IsGrounded;

        private void StartUpperOverlay(PoseClip clip, float torso, float arms, string kind)
        {
            _overlayClip = clip; _overlayStarted = Time.time;
            _overlayTorso = torso; _overlayArms = arms; _overlayKind = kind;
            _events.Add(string.Format("{0:F2}s UpperOverlay {1}:{2}", Time.time, kind, clip.Name));
        }

        private bool TryUpperHit(float severity, string exactName = null)
        {
            if (!UpperOverlayEligible || _overlayClip != null || _phase == Phase.Reaction || _upperRecovery.Active || _reactionClips == null) return false;
            bool heavy = UnityEngine.Random.value < severity;
            var candidates = _reactionClips.AsValueEnumerable().Where(c => c.HasRole("hitreact") && c.UpperBodyRotations != null
                && (exactName == null ? c.HasRole(heavy ? "heavy" : "light") : c.Name == exactName)).ToArray();
            if (candidates.Length == 0) return false;
            var clip = candidates[UnityEngine.Random.Range(0, candidates.Length)];
            StartUpperOverlay(clip, heavy ? .65f : .4f, heavy ? .5f : .25f, "hit");
            ReactionStarts++; UpperHitStarts++; ReactionResult = "upper_hit:" + clip.Name;
            return true;
        }

        // Source motion relative to its first frame overlays native pose. The chest includes
        // source pelvis tilt, rebased into the current pelvis, without translating any spine bone.
        private Quaternion OverlayTarget(PoseClip clip, int bone, float frame, Quaternion native)
        {
            int a = Mathf.Clamp(Mathf.FloorToInt(frame), 0, clip.Frames - 1), b = Mathf.Min(a + 1, clip.Frames - 1);
            var track = clip.UpperBodyRotations[ReactionUpperBones[bone]];
            Quaternion source = Quaternion.Slerp(track[a], track[b], frame - a), reference = track[0];
            Quaternion delta;
            if (bone == 0)
            {
                Quaternion pelvis = Quaternion.Slerp(clip.Rotations[0][a], clip.Rotations[0][b], frame - a);
                delta = UpperReactionMath.ChestDelta(pelvis, source, clip.Rotations[0][0], reference, _pelvis.localRotation);
            }
            else delta = UpperReactionMath.Delta(source, reference);
            return UpperReactionMath.Apply(native, delta, bone < 4 ? _overlayTorso : _overlayArms);
        }
    }
}
