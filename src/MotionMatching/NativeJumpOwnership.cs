using EFT;

namespace Manimal.MotionMatching
{
    // Authored jump/landing states own the full pose, including ground contact.
    // IsGrounded flickers even in native Idle; that contact signal alone is not a jump.
    internal static class NativeJumpOwnership
    {
        public static bool Owns(Player player)
        {
            var context = player?.MovementContext;
            if (context == null) return false;
            return OwnsState(context.CurrentState?.Name)
                || OwnsState(player.CurrentManagedState?.Name);
        }

        internal static bool OwnsState(EPlayerState? state)
        {
            switch (state)
            {
                case EPlayerState.Jump:
                case EPlayerState.JumpLanding:
                case EPlayerState.FallDown:
                case EPlayerState.ClimbOver:
                case EPlayerState.ClimbUp:
                case EPlayerState.VaultingFallDown:
                case EPlayerState.VaultingLanding:
                    return true;
                default: return false;
            }
        }
    }
}
