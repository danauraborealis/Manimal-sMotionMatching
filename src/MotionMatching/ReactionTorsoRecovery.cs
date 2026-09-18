using UnityEngine;

namespace Manimal.MotionMatching
{
    // Rotation offsets live in character-facing space, so a turn cannot leave recoil fixed in world space.
    internal sealed class ReactionTorsoRecovery
    {
        private readonly Inertializer _inertia = new Inertializer();
        private readonly Quaternion[] _current = { Quaternion.identity };
        private readonly Quaternion[] _previous = { Quaternion.identity };
        private readonly Quaternion[] _neutral = { Quaternion.identity };
        private float _sampleDt;
        private bool _tracked, _first;
        public bool Active => _inertia.Active;

        public void Track(Quaternion offset, float dt)
        {
            _previous[0] = _tracked ? _current[0] : offset;
            _current[0] = offset;
            _sampleDt = _tracked && dt > 0f && dt <= .05f ? dt : 0f;
            _tracked = true;
            _inertia.Cancel();
        }

        public void Release(float duration)
        {
            if (!_tracked) return;
            _inertia.Begin(_current, _neutral, Vector3.zero, Vector3.zero, duration,
                _previous, _neutral, Vector3.zero, Vector3.zero, _sampleDt);
            _tracked = false;
            _first = true;
        }

        public Quaternion Advance(float dt)
        {
            if (!_first && dt > 0f && !float.IsInfinity(dt)) _inertia.Advance(dt);
            _first = false;
            return _inertia.Rotation(0);
        }

        public void Cancel()
        {
            _inertia.Cancel();
            _tracked = _first = false;
            _sampleDt = 0f;
        }
    }
}
