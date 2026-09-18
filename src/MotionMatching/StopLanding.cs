using UnityEngine;

namespace Manimal.MotionMatching
{
    internal static class StopLanding
    {
        // A terminal hold/virtual edge is not an opportunity to reposition a foot.
        public static int LastStep(StrideFoot stride)
        {
            if (stride?.Cycles == null) return -1;
            for (int i = stride.Cycles.Length - 1; i >= 0; i--)
            {
                var cycle = stride.Cycles[i];
                if (!cycle.VirtualEnd && cycle.StrikeFrame > cycle.StartFrame
                    && cycle.StrikeCycle > cycle.LiftCycle + 0.0001f)
                    return i;
            }
            return -1;
        }

        public static bool CanEnter(StrideFoot[] strides, int frame, float fps)
        {
            // Old databases without stride metadata retain their existing fallback.
            if (strides == null || strides.Length < 2) return true;
            int lead = Mathf.Max(1, Mathf.CeilToInt(fps * 0.15f));
            for (int side = 0; side < 2; side++)
            {
                int last = LastStep(strides[side]);
                if (last < 0 || strides[side].Cycles[last].StrikeFrame - frame < lead)
                    return false;
            }
            return true;
        }

        // Foot progression is spatial and may reverse; touchdown authority uses time.
        public static float LandingWeight(StrideCycle cycle, float clock)
        {
            if (cycle == null || cycle.StrikeCycle <= cycle.LiftCycle) return 0f;
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(cycle.LiftCycle, cycle.StrikeCycle, clock));
        }
    }
}
