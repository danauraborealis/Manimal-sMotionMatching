using System;

namespace Manimal.MotionMatching
{
    // Independent of Unity's frame rate: hooks may run more than once per rendered frame.
    internal sealed class FidgetPlayback
    {
        internal const float FadeSeconds = 0.10f;
        private float _started, _duration, _cancelled, _cancelWeight, _endFadeSeconds;
        private bool _fading;
        internal bool Active { get; private set; }

        internal void Start(float now, float duration, float endFadeSeconds = FadeSeconds)
        {
            if (!Finite(now) || !Finite(duration) || duration <= 0)
                throw new ArgumentOutOfRangeException(nameof(duration));
            if (!Finite(endFadeSeconds) || endFadeSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(endFadeSeconds));
            _started = now;
            _duration = duration;
            _endFadeSeconds = Math.Min(duration, endFadeSeconds);
            _fading = false;
            Active = true;
        }

        internal void Stop() { Active = false; }

        internal void Cancel(float now)
        {
            if (!Active || _fading) return;
            Evaluate(now, out _, out _cancelWeight);
            _cancelled = now;
            _fading = true;
        }

        internal void Evaluate(float now, out float sampleTime, out float weight)
        {
            sampleTime = 0;
            weight = 0;
            if (!Active) return;
            if (!Finite(now) || now < _started) { Stop(); return; }
            float elapsed = (_fading ? _cancelled : now) - _started;
            sampleTime = Math.Min(_duration, Math.Max(0, elapsed));
            if (_fading)
                weight = _cancelWeight * (1f - Smooth((now - _cancelled) / FadeSeconds));
            else
                weight = Smooth(elapsed / FadeSeconds) * Smooth((_duration - elapsed) / _endFadeSeconds);
            if ((_fading && now - _cancelled >= FadeSeconds) || (!_fading && elapsed >= _duration))
                Stop();
        }

        private static float Smooth(float value)
        {
            value = Math.Min(1f, Math.Max(0f, value));
            return value * value * (3f - 2f * value);
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
