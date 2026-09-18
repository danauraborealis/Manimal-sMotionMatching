namespace Manimal.MotionMatching
{
    internal sealed class LandingRecoveryGate
    {
        private bool _jump;
        private float _began, _takeoffSpeed, _deadline = -1f;
        internal bool Pending(float now) => _deadline >= now;
        internal void Reset() { _jump = false; _deadline = -1f; }
        internal void Consume() => _deadline = -1f;
        internal void Observe(float now, bool nativeOwns, bool actualJump, bool traversal, float speed)
        {
            if (nativeOwns)
            {
                Consume();
                if (traversal) { _jump = false; return; }
                if (actualJump && !_jump) { _jump = true; _began = now; _takeoffSpeed = speed; }
                return;
            }
            if (!_jump) return;
            _jump = false;
            if (_takeoffSpeed >= .8f && now - _began >= .1f) _deadline = now + .3f;
        }
    }
}
