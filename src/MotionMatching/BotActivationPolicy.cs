using System;

namespace Manimal.MotionMatching
{
    internal static class BotActivationPolicy
    {
        internal const float ExitMargin = 15f, NearDistance = 20f, HiddenGrace = 2f;

        // Null means eligible. Visibility is EFT's existing render flag, not a LOS test.
        internal static string IneligibleReason(bool active, float distanceSquared, float range,
            bool visible, bool simplified, bool viewerValid, float unseenSeconds, bool cullUnseen)
        {
            if (!viewerValid) return "no_viewer";
            if (simplified) return "simplified";
            if (float.IsNaN(distanceSquared) || float.IsInfinity(distanceSquared) || distanceSquared < 0f
                || float.IsNaN(range) || float.IsInfinity(range) || range <= 0f) return "distance";
            float limit = range + (active ? ExitMargin : 0f);
            if (distanceSquared > limit * limit) return "distance";
            if (!cullUnseen || visible || distanceSquared <= NearDistance * NearDistance) return null;
            if (active && !float.IsNaN(unseenSeconds) && unseenSeconds >= 0f && unseenSeconds < HiddenGrace) return null;
            return "unseen";
        }
    }
}
