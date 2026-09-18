using System;

namespace Manimal.MotionMatching
{
    // Keeps the paired leg cycle continuous while native Tarkov walk phases are
    // being entered, blended, or corrected. The instance owns only phase state;
    // callers decide when the native walk is eligible for admission.
    internal sealed class NativeWalkPhase
    {
        internal const float MaximumDeltaTime = .05f;
        internal const float MaximumOffsetSlewFraction = .15f;
        internal const float MinimumCadenceScale = .85f;
        internal const float MaximumCadenceScale = 1.15f;

        private const double MinimumPhaseDeltaDiscontinuityTolerance = .01d;
        private const double PhaseDeltaDiscontinuityToleranceScale = 1.5d;
        private const double PhaseConvergenceEpsilon = .000001d;
        private const double MaximumUnambiguousAdvance = .5d;

        private bool _initialized;
        private bool _firstStepAfterReset;
        private double _legPhase;
        private double _nativePhase;
        private int _stateHash;

        internal float OffsetCycles { get; private set; }
        internal float CadenceScale { get; private set; } = 1f;
        internal bool Rebased { get; private set; }

        internal void Reset(float legPhase, float nativePhase, int stateHash)
        {
            _legPhase = Finite(legPhase) ? Wrap01(legPhase) : 0d;
            _nativePhase = Finite(nativePhase) ? Wrap01(nativePhase) : 0d;
            _stateHash = stateHash;
            _initialized = Finite(legPhase) && Finite(nativePhase);
            _firstStepAfterReset = _initialized;
            OffsetCycles = _initialized ? ToSignedFloat(_legPhase - _nativePhase) : 0f;
            CadenceScale = 1f;
            Rebased = false;
        }

        // Returns the wrapped leg phase. OffsetCycles and CadenceScale describe the
        // same processed sample and can be read immediately after this call.
        internal float Step(float nativePhase, float length, float dt, int stateHash, bool inTransition)
        {
            Rebased = false;
            CadenceScale = 1f;
            if (!_initialized || !Finite(nativePhase) || !Finite(length) || length <= 0f
                || !Finite(dt) || dt <= 0f || dt > MaximumDeltaTime)
                return ToFloat(_legPhase);

            double nominalAdvance = (double)dt / length;
            // A per-frame phase delta of half a cycle or more is ambiguous after
            // wrapping, so hold the last safe sample until cadence becomes usable.
            if (!Finite(nominalAdvance) || nominalAdvance <= 0d
                || nominalAdvance >= MaximumUnambiguousAdvance)
                return ToFloat(_legPhase);

            double nextNativePhase = Wrap01(nativePhase);
            double nativeDelta = WrapSigned(nextNativePhase - _nativePhase);
            double discontinuityTolerance = Math.Max(MinimumPhaseDeltaDiscontinuityTolerance,
                nominalAdvance * PhaseDeltaDiscontinuityToleranceScale);
            bool discontinuous = Math.Abs(nativeDelta - nominalAdvance) > discontinuityTolerance;

            if (_firstStepAfterReset || stateHash != _stateHash || inTransition || discontinuous)
            {
                Rebase(nextNativePhase, nominalAdvance, stateHash);
                return ToFloat(_legPhase);
            }

            double predictedNextLegPhase = Wrap01(_legPhase + nominalAdvance);
            double targetError = WrapSigned(nextNativePhase - predictedNextLegPhase);
            double maximumSlew = nominalAdvance * MaximumOffsetSlewFraction;
            double phaseCorrection = Math.Abs(targetError) <= PhaseConvergenceEpsilon
                ? 0d
                : Math.Max(-maximumSlew, Math.Min(targetError, maximumSlew));

            _legPhase = Wrap01(predictedNextLegPhase + phaseCorrection);
            _nativePhase = nextNativePhase;
            _stateHash = stateHash;
            OffsetCycles = ToSignedFloat(_legPhase - _nativePhase);
            double actualAdvance = nominalAdvance + phaseCorrection;
            CadenceScale = ToCadenceFloat(actualAdvance / nominalAdvance);
            return ToFloat(_legPhase);
        }

        private void Rebase(double nextNativePhase, double nominalAdvance, int stateHash)
        {
            // Do not inherit a phase jump from the native clock. Continue the
            // selected leg trajectory at nominal cadence, then preserve its shortest
            // signed offset from the newly observed native phase.
            _legPhase = Wrap01(_legPhase + nominalAdvance);
            _nativePhase = nextNativePhase;
            _stateHash = stateHash;
            _firstStepAfterReset = false;
            OffsetCycles = ToSignedFloat(_legPhase - _nativePhase);
            CadenceScale = 1f;
            Rebased = true;
        }

        private static float ToCadenceFloat(double value)
        {
            if (value < MinimumCadenceScale) return MinimumCadenceScale;
            if (value > MaximumCadenceScale) return MaximumCadenceScale;
            return (float)value;
        }

        private static double Wrap01(double value)
        {
            double wrapped = value % 1d;
            if (wrapped < 0d) wrapped += 1d;
            if (wrapped >= 1d) wrapped = 0d;
            return wrapped;
        }

        private static double WrapSigned(double value)
        {
            return Wrap01(value + .5d) - .5d;
        }

        private static float ToFloat(double value)
        {
            float wrapped = (float)Wrap01(value);
            return wrapped >= 1f ? 0f : wrapped;
        }

        private static float ToSignedFloat(double value)
        {
            float wrapped = (float)WrapSigned(value);
            return wrapped >= .5f ? -.5f : wrapped;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
