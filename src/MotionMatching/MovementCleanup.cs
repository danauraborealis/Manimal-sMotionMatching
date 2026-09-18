using EFT;

namespace Manimal.MotionMatching
{
    internal static class MovementCleanup
    {
        // Each owner must release even if another encounters a destroyed game object.
        internal static void Detach(Player player)
        {
            try { SpeedRampPatch.Detach(player); } catch { }
            try { BotInertiaPatch.Detach(player); } catch { }
            try { SprintInertia.Detach(player); } catch { }
        }
    }
}
