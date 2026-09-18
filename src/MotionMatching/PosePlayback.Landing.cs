using EFT;
using UnityEngine;
using ZLinq;

namespace Manimal.MotionMatching
{
    internal sealed partial class PosePlayback
    {
        private readonly LandingRecoveryGate _landingGate = new LandingRecoveryGate();
        internal int LandingStarts { get; private set; }
        private static bool IsTraversal(EPlayerState? state) => state == EPlayerState.ClimbOver || state == EPlayerState.ClimbUp
            || state == EPlayerState.VaultingFallDown || state == EPlayerState.VaultingLanding;
        private void TickLandingRecovery()
        {
            if (!Enabled || _reactionClips == null || !_player.HealthController.IsAlive || _player.MovementContext == null)
            { _landingGate.Reset(); return; }
            bool owns = NativeJumpOwnership.Owns(_player);
            var state = _player.MovementContext.CurrentState?.Name;
            var managed = _player.CurrentManagedState?.Name;
            bool traversal = IsTraversal(state) || IsTraversal(managed);
            bool actual = !traversal && (state == EPlayerState.Jump || managed == EPlayerState.Jump);
            Vector3 velocity = _player.Velocity;
            float speed = new Vector2(velocity.x, velocity.z).magnitude;
            _landingGate.Observe(Time.time, owns, actual, traversal, speed);
            if (!_landingGate.Pending(Time.time)) return;
            if (!UpperOverlayEligible || speed < .6f || _overlayClip != null || _phase == Phase.Reaction
                || _reactionRequested || _upperRecovery.Active) return;
            float yaw = Mathf.DeltaAngle(_player.Rotation.x, Mathf.Atan2(velocity.x, velocity.z) * Mathf.Rad2Deg);
            PoseClip clip = _reactionClips.AsValueEnumerable().Where(c => !c.HasRole("hitreact") && c.UpperBodyRotations != null)
                .OrderBy(c => Mathf.Abs(Mathf.DeltaAngle(yaw, c.MoveYaw))).FirstOrDefault();
            if (clip == null || Mathf.Abs(Mathf.DeltaAngle(yaw, clip.MoveYaw)) > 45f) return;
            StartUpperOverlay(clip, .3f, .2f, "landing");
            LandingStarts++; _landingGate.Consume();
        }
    }
}
