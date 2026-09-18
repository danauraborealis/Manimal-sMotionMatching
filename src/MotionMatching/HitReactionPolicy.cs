using System;
using System.Collections.Generic;

namespace Manimal.MotionMatching
{
    internal sealed class HitReactionPolicy
    {
        internal const float BaseChance = .10f, ChancePerDamage = .005f, MaximumChance = .85f;
        internal const float QuietResetSeconds = 2f, CooldownSeconds = 2.5f;
        private float _lastTime = float.NegativeInfinity;
        private float _next = float.NegativeInfinity;
        private struct Hit { public float Time, Damage; }
        private readonly Queue<Hit> _hits = new Queue<Hit>();
        private double _recentDamage;
        public int Accepted { get; private set; }
        public int Suppressed { get; private set; }
        public bool Pending { get; private set; }
        public float LastChance { get; private set; }
        public float LastRoll { get; private set; }
        public double RecentDamage => _recentDamage;
        public float CooldownRemaining(float time) => Math.Max(0f, _next - time);
        public string LastDecision { get; private set; } = "idle";
        // Post-armor health damage weights chance continuously, with no minimum threshold.
        // Caller supplies a uniform [0,1] roll; cooldown starts only after playback begins.
        public bool AddDamage(float time, float damage, float roll)
        {
            if (!Finite(time) || !Finite(damage) || damage <= 0f || !Finite(roll) || roll < 0f || roll > 1f)
            { LastDecision = "invalid_or_zero_damage"; return false; }
            if (time < _lastTime) { ClearDamage(); _next = float.NegativeInfinity; Pending = false; }
            _lastTime = time;
            if (time < _next)
            {
                ClearDamage();
                LastDecision = "cooldown"; Suppressed++; return false;
            }
            while (_hits.Count > 0 && time - _hits.Peek().Time >= QuietResetSeconds)
                _recentDamage -= _hits.Dequeue().Damage;
            _hits.Enqueue(new Hit { Time = time, Damage = damage });
            _recentDamage += damage;
            LastChance = (float)Math.Min(MaximumChance, BaseChance + ChancePerDamage * _recentDamage);
            if (Pending) { LastDecision = "entry_pending"; return false; }
            LastRoll = roll;
            if (roll >= LastChance) { LastDecision = "roll_missed"; return false; }
            Pending = true; Accepted++; LastDecision = "entry_requested"; return true;
        }
        public void Resolve(float time, bool started)
        {
            if (!Pending) return;
            Pending = false;
            if (!started) { LastDecision = "entry_rejected"; return; }
            ClearDamage();
            _next = time + CooldownSeconds; LastDecision = "playing";
        }
        private void ClearDamage() { _hits.Clear(); _recentDamage = 0; }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
