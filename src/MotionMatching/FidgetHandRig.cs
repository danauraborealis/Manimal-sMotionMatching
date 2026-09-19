using System;
using System.Reflection;
using EFT;
using RootMotion.FinalIK;
using UnityEngine;
using ZLinq;

namespace Manimal.MotionMatching
{
    // A mapping captured from the resolved target grip, once per playback.
    // Never edits the weapon's reusable grip assets or changes bone lengths.
    internal sealed class FidgetHandRig
    {
        private static readonly FieldInfo LimbsField = typeof(Player).GetField("_limbs", BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly IKSolverLimb _solver;
        private readonly Quaternion _handCanonicalToLocal;
        private readonly Finger[] _fingers;
        private bool _handApplied;
        private Vector3 _handBasePosition, _handWrittenPosition;
        private Quaternion _handBaseRotation, _handWrittenRotation;
        private Transform _handBaseTarget;

        private sealed class Finger
        {
            internal Transform Bone;
            internal int Digit, Segment;
            internal Quaternion CanonicalToLocal, BaseRotation, WrittenRotation;
            internal bool Applied;
        }

        private FidgetHandRig(IKSolverLimb solver, Quaternion handMap, Finger[] fingers)
        { _solver = solver; _handCanonicalToLocal = handMap; _fingers = fingers; }

        internal static FidgetHandRig Capture(Player player)
        {
            var limbs = LimbsField?.GetValue(player) as LimbIK[];
            var palm = player.PlayerBones?.LeftPalm;
            var poser = player.HandPosers != null && player.HandPosers.Length > 0 ? player.HandPosers[0] : null;
            if (palm == null || poser == null || limbs == null || limbs.Length == 0 || limbs[0] == null
                || limbs[0].solver.bone3.transform != palm)
                throw new InvalidOperationException("Left palm/hand solver mapping unavailable");
            var children = poser.Children;
            Transform Bone(int digit, int segment)
                => children.AsValueEnumerable().FirstOrDefault(t => t != null && t.name == $"Base HumanLDigit{digit + 1}{segment + 1}")
                   ?? throw new InvalidOperationException($"Missing left finger {digit}:{segment}");
            var bones = ValueEnumerable.Range(0, 15).Select(i => Bone(i / 3, i % 3)).ToArray();
            var across = bones[12].position - bones[3].position; // index -> little
            if (!FidgetGestureMath.TryPalmFrame(bones[6].position - palm.position, across, out var palmFrame))
                throw new InvalidOperationException("Degenerate target palm frame");
            var palmNormal = palmFrame * Vector3.forward;
            var fingers = ValueEnumerable.Range(0, 15).Select(i =>
            {
                int digit = i / 3, segment = i % 3;
                var bone = bones[i];
                Vector3 along;
                if (segment < 2)
                {
                    if (bones[i + 1].parent != bone) throw new InvalidOperationException("Unexpected target finger hierarchy");
                    along = bones[i + 1].position - bone.position;
                }
                else
                {
                    // EFT digit segments have a shared longitudinal local axis (verified
                    // from the installed skeleton). Leaves have no end transform; reuse
                    // the middle segment's outgoing axis in the leaf's own local frame.
                    if (bone.localPosition.sqrMagnitude < 1e-10f || bones[i - 1].localPosition.sqrMagnitude < 1e-10f
                        || Vector3.Dot(bone.localPosition.normalized, bones[i - 1].localPosition.normalized) < 0.99f)
                        throw new InvalidOperationException("Unsupported target fingertip axis convention");
                    along = bone.rotation * bone.localPosition.normalized;
                }
                if (!FidgetGestureMath.TryFingerFrame(along, across, palmNormal, digit == 0, out var frame))
                    throw new InvalidOperationException($"Degenerate target finger frame {digit}:{segment}");
                return new Finger { Bone = bone, Digit = digit, Segment = segment,
                    CanonicalToLocal = Quaternion.Inverse(bone.rotation) * frame };
            }).ToArray();
            return new FidgetHandRig(limbs[0].solver, Quaternion.Inverse(palm.rotation) * palmFrame, fingers);
        }

        internal void ApplyHand(FidgetGestureClip clip, float seconds, float weight, float sourceScale)
        {
            RestoreHandTarget();
            if (weight <= 0 || _solver.IKPositionWeight <= 0.001f) return;
            clip.SampleHand(seconds, out var position, out var rotation);
            _handBaseTarget = _solver.target;
            _handBasePosition = _solver.IKPosition;
            _handBaseRotation = _solver.IKRotation;
            var resolvedPosition = _handBaseTarget != null ? _handBaseTarget.position : _handBasePosition;
            var resolvedRotation = _handBaseTarget != null ? _handBaseTarget.rotation : _handBaseRotation;
            var delta = FidgetGestureMath.ToLocalDelta(_handCanonicalToLocal,
                Quaternion.SlerpUnclamped(Quaternion.identity, rotation, weight));
            _handWrittenPosition = resolvedPosition + resolvedRotation * (_handCanonicalToLocal * position) * (sourceScale * weight);
            _handWrittenRotation = resolvedRotation * delta;
            // FinalIK copies target.position/rotation over numeric IK targets in
            // OnUpdate. Temporarily use the numeric target without touching a grip asset.
            _solver.target = null;
            _solver.SetIKPosition(_handWrittenPosition);
            _solver.SetIKRotation(_handWrittenRotation);
            _handApplied = true;
        }

        internal void ApplyFingers(FidgetGestureClip clip, float seconds, float weight)
        {
            RestoreFingers();
            if (weight <= 0) return;
            foreach (var finger in _fingers)
            {
                if (finger.Bone == null) continue;
                var delta = FidgetGestureMath.ToLocalDelta(finger.CanonicalToLocal,
                    Quaternion.SlerpUnclamped(Quaternion.identity, clip.SampleFinger(finger.Digit, finger.Segment, seconds), weight));
                finger.BaseRotation = finger.Bone.localRotation;
                finger.WrittenRotation = finger.BaseRotation * delta;
                finger.Bone.localRotation = finger.WrittenRotation;
                finger.Applied = true;
            }
        }

        internal void RestoreHandTarget()
        {
            if (!_handApplied) return;
            _handApplied = false;
            if ((_solver.IKPosition - _handWrittenPosition).sqrMagnitude < 1e-12f) _solver.SetIKPosition(_handBasePosition);
            if (SameRotation(_solver.IKRotation, _handWrittenRotation)) _solver.SetIKRotation(_handBaseRotation);
            if (_solver.target == null) _solver.target = _handBaseTarget;
            _handBaseTarget = null;
        }

        private void RestoreFingers()
        {
            foreach (var finger in _fingers)
            {
                if (!finger.Applied) continue;
                finger.Applied = false;
                if (finger.Bone != null && SameRotation(finger.Bone.localRotation, finger.WrittenRotation))
                    finger.Bone.localRotation = finger.BaseRotation;
            }
        }

        internal void Restore() { RestoreHandTarget(); RestoreFingers(); }

        private static bool SameRotation(Quaternion a, Quaternion b)
        {
            float sign = Quaternion.Dot(a, b) < 0 ? -1 : 1;
            float x = a.x - sign * b.x, y = a.y - sign * b.y, z = a.z - sign * b.z, w = a.w - sign * b.w;
            return x * x + y * y + z * z + w * w < 1e-10f;
        }
    }
}
