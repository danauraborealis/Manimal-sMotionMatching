using System;

namespace Manimal.MotionMatching
{
    internal static class LandingPrediction
    {
        public static float VelocityCorrection(float delta, float horizon, float duration, float maximum)
        {
            if (float.IsNaN(delta) || float.IsInfinity(delta) || float.IsNaN(horizon) ||
                float.IsInfinity(horizon) || float.IsNaN(duration) || float.IsInfinity(duration) ||
                float.IsNaN(maximum) || float.IsInfinity(maximum) || duration <= 0f || horizon <= 0f || maximum <= 0f) return 0f;
            float t = Math.Min(horizon, duration);
            float displacement = delta * (t - t * t / (2f * duration));
            return Math.Max(-maximum, Math.Min(maximum, displacement));
        }
    }
}
