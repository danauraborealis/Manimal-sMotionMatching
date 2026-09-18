using EFT;

namespace Manimal.MotionMatching
{
    // Observations only: do not evaluate a driver callback or alter native movement state.
    internal struct SprintEntrySnapshot
    {
        public bool HasCommand;
        public bool Requested;
        public bool HasContext;
        public string State;
        public string ManagedState;
        public bool CanSprint;
        public int PhysicalCondition;
        public float DirectionX;
        public float DirectionY;
        public float SmoothedSpeed;
        public float CharacterSpeed;
        public float PoseLevel;
        public bool Grounded;
        public bool PhysicalSprint;
        public bool HasPhysical;
        public bool PhysicalCanSprint;
        public float Stamina;
        public bool StaminaExhausted;

        public static SprintEntrySnapshot Capture(Player player, bool? requested = null)
        {
            var value = new SprintEntrySnapshot { HasCommand = requested.HasValue, Requested = requested ?? false };
            try
            {
                var context = player?.MovementContext;
                if (context == null) return value;
                value.State = context.CurrentState?.Name.ToString();
                value.ManagedState = player.CurrentManagedState?.Name.ToString();
                value.CanSprint = context.CanSprint;
                value.PhysicalCondition = (int)context.PhysicalCondition;
                value.DirectionX = context.MovementDirection.x;
                value.DirectionY = context.MovementDirection.y;
                value.SmoothedSpeed = context.SmoothedCharacterMovementSpeed;
                value.CharacterSpeed = context.CharacterMovementSpeed;
                value.PoseLevel = context.PoseLevel;
                value.Grounded = context.IsGrounded;
                value.PhysicalSprint = context.IsSprintEnabled;
                value.HasContext = true;
            }
            catch { value.HasContext = false; }
            try
            {
                var physical = player?.Physical;
                if (physical != null && physical.Stamina != null)
                {
                    value.PhysicalCanSprint = physical.CanSprint;
                    value.Stamina = physical.Stamina.Current;
                    value.StaminaExhausted = physical.Stamina.Exhausted;
                    value.HasPhysical = true;
                }
            }
            catch { value.HasPhysical = false; }
            return value;
        }
    }
}
