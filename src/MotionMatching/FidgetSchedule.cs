using System;

namespace Manimal.MotionMatching
{
    // One deadline per local-player session. Weapon changes and busy states do
    // not pause or restart it; only an actual playback consumes the due request.
    internal sealed class FidgetSchedule
    {
        internal const float MinimumCooldownSeconds = 9f;
        internal const float MaximumCooldownSeconds = 15f;
        private readonly Func<float> _nextDelay;
        private bool _initialized;
        private float _lastTime, _nextTime;

        internal FidgetSchedule(Func<float> nextDelay)
            => _nextDelay = nextDelay ?? throw new ArgumentNullException(nameof(nextDelay));

        internal void Reset() => _initialized = false;

        internal bool IsDue(float now)
        {
            ValidateTime(now);
            if (!_initialized || now < _lastTime)
                Arm(now);
            _lastTime = now;
            return now >= _nextTime;
        }

        internal void PlaybackStarted(float now) => Arm(now);

        private void Arm(float now)
        {
            ValidateTime(now);
            float delay = _nextDelay();
            if (float.IsNaN(delay) || float.IsInfinity(delay)
                || delay < MinimumCooldownSeconds || delay > MaximumCooldownSeconds)
                throw new InvalidOperationException("Fidget delay must be between 9 and 15 seconds");
            _nextTime = now + delay;
            _lastTime = now;
            _initialized = true;
        }

        private static void ValidateTime(float now)
        {
            if (float.IsNaN(now) || float.IsInfinity(now))
                throw new ArgumentOutOfRangeException(nameof(now));
        }
    }
}
