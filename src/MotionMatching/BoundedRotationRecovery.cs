using UnityEngine;

namespace Manimal.MotionMatching
{
    // Captured offsets decay along their shortest arc; no velocity extrapolation or overshoot.
    internal sealed class BoundedRotationRecovery
    {
        private Quaternion[] _offsets;
        private float _elapsed, _duration;
        public bool Active => _offsets != null && _elapsed < _duration;
        public void Begin(Quaternion[] shown, Quaternion[] native, float duration)
        {
            if (_offsets == null || _offsets.Length != shown.Length) _offsets = new Quaternion[shown.Length];
            for (int i = 0; i < shown.Length; i++) _offsets[i] = shown[i] * Quaternion.Inverse(native[i]);
            _elapsed = 0f;
            _duration = duration;
        }
        public void Advance(float dt)
        {
            if (dt > 0f && !float.IsInfinity(dt)) _elapsed += dt;
        }
        public Quaternion Rotation(int index)
        {
            if (!Active) return Quaternion.identity;
            float t = Mathf.Clamp01(_elapsed / _duration);
            float weight = 1f - t * t * (3f - 2f * t);
            return Quaternion.Slerp(Quaternion.identity, _offsets[index], weight);
        }
        public void Cancel() => _elapsed = _duration;
    }
}
