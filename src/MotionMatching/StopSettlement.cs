using System;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // A bounded corrective landing that finishes a stop BEFORE handing off to idle.
    // The helper only plans one foot at a time. The caller owns applying Position and
    // Heading to the rendered foot and feeds that rendered pose back on the next frame.
    internal struct StopSettlementResult
    {
        public bool Active;
        public int Side;
        public Vector3 Position;
        public float Heading;
        public bool Started;
        public bool Completed;
        public bool Failed;
        public bool Ready;
    }

    internal sealed class StopSettlement
    {
        internal const float HorizontalTolerance = .025f;
        internal const float HeadingTolerance = 5f;
        private const float MaxHorizontalTravel = .25f;
        private const float StepSpeed = .6f;
        private const double MinimumDuration = .35d;
        private const float LiftHeight = .055f;
        private const int MaximumSteps = 4;
        private const double Epsilon = 0.000001d;

        private bool _active;
        private int _side = -1;
        private Vector3 _startPosition;
        private Vector3 _targetPosition;
        private float _startHeading;
        private float _targetHeading;
        private double _headingDelta;
        private double _elapsed;
        private double _duration;
        private float _liftHeight;
        private int _steps;

        public void Reset()
        {
            _active = false;
            _side = -1;
            _startPosition = new Vector3();
            _targetPosition = new Vector3();
            _startHeading = 0f;
            _targetHeading = 0f;
            _headingDelta = 0d;
            _elapsed = 0d;
            _duration = 0d;
            _liftHeight = 0f;
            _steps = 0;
        }

        public StopSettlementResult Update(
            bool eligible,
            float dt,
            Vector3 leftActual,
            Vector3 rightActual,
            Vector3 leftDesired,
            Vector3 rightDesired,
            float leftActualHeading,
            float rightActualHeading,
            float leftDesiredHeading,
            float rightDesiredHeading)
        {
            if (!eligible)
            {
                Reset();
                return Idle();
            }

            if (!Finite(leftActual) || !Finite(rightActual)
                || !Finite(leftDesired) || !Finite(rightDesired)
                || !Finite(leftActualHeading) || !Finite(rightActualHeading)
                || !Finite(leftDesiredHeading) || !Finite(rightDesiredHeading))
            {
                // Invalid pose data must never leak a non-finite correction into the
                // IK stage. A later valid eligible sample starts a fresh episode.
                Reset();
                return Failure();
            }

            // A clock glitch should hold the current sample. In particular, a negative
            // delta must not rewind a swing or make it jump past its fixed landing.
            double safeDt = Finite(dt) && dt >= 0f ? dt : 0d;
            if (_active)
                return Advance(safeDt);

            double leftHorizontal = HorizontalDistance(leftActual, leftDesired);
            double rightHorizontal = HorizontalDistance(rightActual, rightDesired);
            double leftHeading = Math.Abs(HeadingDelta(leftActualHeading, leftDesiredHeading));
            double rightHeading = Math.Abs(HeadingDelta(rightActualHeading, rightDesiredHeading));
            if (!Finite(leftHorizontal) || !Finite(rightHorizontal)
                || !Finite(leftHeading) || !Finite(rightHeading))
            {
                Reset();
                return Failure();
            }

            double leftUrgency = Urgency(leftHorizontal, leftHeading);
            double rightUrgency = Urgency(rightHorizontal, rightHeading);
            if (leftUrgency <= 1d + Epsilon && rightUrgency <= 1d + Epsilon)
                return Ready();
            if (_steps >= MaximumSteps)
                return Failure();

            // Normalize each error by its acceptance threshold so a large turn can
            // fairly compete with a large translation. Ties are deterministic.
            int side = leftUrgency >= rightUrgency ? 0 : 1;
            Begin(side,
                side == 0 ? leftActual : rightActual,
                side == 0 ? leftDesired : rightDesired,
                side == 0 ? leftActualHeading : rightActualHeading,
                side == 0 ? leftDesiredHeading : rightDesiredHeading);
            return Sample(0d, true, false);
        }

        private void Begin(int side, Vector3 actual, Vector3 desired, float actualHeading, float desiredHeading)
        {
            _active = true;
            _side = side;
            _startPosition = actual;
            _targetPosition = CappedTarget(actual, desired);
            _startHeading = actualHeading;
            _headingDelta = HeadingDelta(actualHeading, desiredHeading);

            double unwrappedHeading = (double)actualHeading + _headingDelta;
            // Normal gameplay headings are small, but canonicalizing an extreme finite
            // input keeps the result finite even when adding 180 degrees would exceed
            // the float range.
            _targetHeading = ToFiniteFloat(unwrappedHeading)
                ? (float)unwrappedHeading
                : (float)WrapDegrees(desiredHeading);

            double distance = HorizontalDistance(actual, _targetPosition);
            _liftHeight = (float)Math.Min(LiftHeight, Math.Max(.015d, distance * .22d));
            _duration = Math.Max(MinimumDuration, distance / StepSpeed);
            if (!Finite(_duration) || _duration < MinimumDuration)
                _duration = MinimumDuration;
            _elapsed = 0d;
        }

        private StopSettlementResult Advance(double dt)
        {
            _elapsed = Math.Min(_duration, _elapsed + dt);
            double progression = _duration <= Epsilon ? 1d : _elapsed / _duration;
            if (!Finite(progression))
                progression = 0d;
            progression = Clamp01(progression);
            bool completed = progression >= 1d - Epsilon;
            return Sample(progression, false, completed);
        }

        private StopSettlementResult Sample(double progression, bool started, bool completed)
        {
            int side = _side;
            double eased = SmoothStep(progression);
            Vector3 position = Interpolate(_startPosition, _targetPosition, eased,
                _liftHeight * Math.Sin(Math.PI * progression) * Math.Sin(Math.PI * progression));
            double heading = (double)_startHeading + _headingDelta * eased;

            if (completed)
            {
                // The exact target is emitted on the completion frame. The caller can
                // commit that rendered pose, and the next Update then measures it before
                // deciding whether another foot needs a correction.
                position = _targetPosition;
                heading = _targetHeading;
                _active = false;
                _side = -1;
                _steps++;
            }

            if (!Finite(position) || !Finite(heading))
            {
                // This is defensive for pathological finite float inputs. Never return
                // a partially invalid trajectory to the caller.
                _active = false;
                _side = -1;
                return Failure();
            }

            return new StopSettlementResult
            {
                Active = true,
                Side = side,
                Position = position,
                Heading = (float)heading,
                Started = started,
                Completed = completed,
                Failed = false,
                Ready = false
            };
        }

        private static Vector3 CappedTarget(Vector3 actual, Vector3 desired)
        {
            double dx = (double)desired.x - actual.x;
            double dz = (double)desired.z - actual.z;
            double distance = Math.Sqrt(dx * dx + dz * dz);
            if (!Finite(distance) || distance <= MaxHorizontalTravel)
                return desired;

            double scale = MaxHorizontalTravel / distance;
            double x = actual.x + dx * scale;
            double z = actual.z + dz * scale;
            if (!ToFiniteFloat(x) || !ToFiniteFloat(z))
                return actual;
            return new Vector3((float)x, desired.y, (float)z);
        }

        private static Vector3 Interpolate(Vector3 start, Vector3 target, double eased, double lift)
        {
            double x = start.x + ((double)target.x - start.x) * eased;
            double y = start.y + ((double)target.y - start.y) * eased + lift;
            double z = start.z + ((double)target.z - start.z) * eased;
            return new Vector3(ToFiniteFloat(x) ? (float)x : start.x,
                ToFiniteFloat(y) ? (float)y : start.y,
                ToFiniteFloat(z) ? (float)z : start.z);
        }

        private static double Urgency(double horizontal, double heading)
        {
            return Math.Max(horizontal / HorizontalTolerance, heading / HeadingTolerance);
        }

        private static double HorizontalDistance(Vector3 a, Vector3 b)
        {
            double x = (double)b.x - a.x;
            double z = (double)b.z - a.z;
            return Math.Sqrt(x * x + z * z);
        }

        private static double HeadingDelta(float actual, float desired)
        {
            return WrapDegrees((double)desired - actual);
        }

        private static double WrapDegrees(double degrees)
        {
            if (!Finite(degrees))
                return 0d;
            degrees %= 360d;
            if (degrees > 180d)
                degrees -= 360d;
            else if (degrees <= -180d)
                degrees += 360d;
            return degrees;
        }

        private static double SmoothStep(double value)
        {
            value = Clamp01(value);
            return value * value * (3d - 2d * value);
        }

        private static double Clamp01(double value)
        {
            return value <= 0d ? 0d : value >= 1d ? 1d : value;
        }

        private static StopSettlementResult Idle()
        {
            return new StopSettlementResult { Side = -1 };
        }

        private static StopSettlementResult Ready()
        {
            return new StopSettlementResult { Side = -1, Ready = true };
        }

        private static StopSettlementResult Failure()
        {
            return new StopSettlementResult { Side = -1, Failed = true };
        }

        private static bool Finite(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool ToFiniteFloat(double value)
        {
            return Finite(value) && Math.Abs(value) <= float.MaxValue;
        }
    }
}
