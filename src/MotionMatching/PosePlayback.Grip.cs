using UnityEngine;

namespace Manimal.MotionMatching
{
    internal sealed class WeaponGripProbe
    {
        public float[] LeftPosition, RightPosition, LeftRotation, RightRotation;
        public float SupportError, TriggerError, ArmWeight;
        public int AppliedFrame;
    }
    internal sealed partial class PosePlayback
    {
        private int _gripAppliedFrame = -1;
        private Transform _supportPalm;
        private readonly Quaternion[] _gripArmPose = new Quaternion[6];
        internal float SupportGripError { get; private set; }
        internal float TriggerGripError { get; private set; }
        internal float GripArmWeight { get; private set; } = 1f;
        internal WeaponGripProbe ReadGripProbe()
        {
            var weapon = _player.PlayerBones?.Weapon_Root_Anim;
            if (!weapon || !_supportPalm || !_reactionPalm || !FootPlacer.TryGetWeaponCarryContext(_player, out _, out _)) return null;
            var inverse = Quaternion.Inverse(weapon.rotation);
            Vector3 left = inverse * (_supportPalm.position - weapon.position), right = inverse * (_reactionPalm.position - weapon.position);
            Quaternion l = inverse * _supportPalm.rotation, r = inverse * _reactionPalm.rotation;
            return new WeaponGripProbe { LeftPosition = new[] { left.x, left.y, left.z }, RightPosition = new[] { right.x, right.y, right.z },
                LeftRotation = new[] { l.x, l.y, l.z, l.w }, RightRotation = new[] { r.x, r.y, r.z, r.w },
                SupportError = SupportGripError, TriggerError = TriggerGripError, ArmWeight = GripArmWeight, AppliedFrame = _gripAppliedFrame };
        }

        // Keep the native support-hand task (including native reload motion) relative to
        // the carried gun. Rotations only: no bone stretching or hand teleporting.
        private void PreserveSupportGrip(Transform follower, Vector3 gripPosition, Quaternion gripRotation,
            Vector3 supportPosition, Quaternion supportRotation)
        {
            Transform upper = _upperBones[5], lower = _upperBones[6];
            float first = (lower.position - upper.position).magnitude;
            float second = (_supportPalm.position - lower.position).magnitude;
            if (first < .001f || second < .001f) return;
            for (int i = 0; i < 6; i++) _gripArmPose[i] = _upperBones[i + 4].localRotation;
            float reach = first + second - .0001f;
            float minimum = Mathf.Abs(first - second) + .0001f;
            GripArmWeight = 1f;
            Vector3 target = follower.position + follower.rotation * supportPosition;
            float distance = (target - upper.position).magnitude;
            if (distance > reach || distance < minimum)
            {
                // Both native arms carried by the reacted torso are a feasible baseline.
                // Reduce only arm excursion when the right-hand-driven gun exceeds left reach.
                float low = 0f, high = 1f;
                for (int pass = 0; pass < 8; pass++)
                {
                    float weight = (low + high) * .5f;
                    ApplyGripArmWeight(weight);
                    follower.SetPositionAndRotation(_reactionPalm.position + _reactionPalm.rotation * gripPosition,
                        _reactionPalm.rotation * gripRotation);
                    target = follower.position + follower.rotation * supportPosition;
                    distance = (target - upper.position).magnitude;
                    if (distance <= reach && distance >= minimum) low = weight; else high = weight;
                }
                GripArmWeight = low;
                ApplyGripArmWeight(low);
                follower.SetPositionAndRotation(_reactionPalm.position + _reactionPalm.rotation * gripPosition,
                    _reactionPalm.rotation * gripRotation);
                target = follower.position + follower.rotation * supportPosition;
            }
            Vector3 origin = upper.position;
            Vector3 direction = target - origin;
            float length = direction.magnitude;
            if (length < .0001f) return;
            Vector3 axis = direction / length;
            float clamped = Mathf.Clamp(length, minimum, reach);
            Vector3 pole = lower.position - origin;
            pole -= axis * Vector3.Dot(pole, axis);
            if (pole.sqrMagnitude < 1e-8f) pole = Vector3.Cross(axis, _pelvis.forward);
            if (pole.sqrMagnitude < 1e-8f) pole = Vector3.Cross(axis, _pelvis.up);
            float along = (first * first + clamped * clamped - second * second) / (2f * clamped);
            float height = Mathf.Sqrt(Mathf.Max(0f, first * first - along * along));
            Vector3 elbow = origin + axis * along + pole.normalized * height;
            upper.rotation = Quaternion.FromToRotation(lower.position - origin, elbow - origin) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(_supportPalm.position - lower.position, target - lower.position) * lower.rotation;
            _supportPalm.rotation = follower.rotation * supportRotation;
            SupportGripError = (_supportPalm.position - target).magnitude;
            TriggerGripError = (follower.position - (_reactionPalm.position + _reactionPalm.rotation * gripPosition)).magnitude;
            _gripAppliedFrame = Time.frameCount;
        }

        private void ApplyGripArmWeight(float weight)
        {
            for (int i = 0; i < 6; i++)
                _upperBones[i + 4].localRotation = Quaternion.Slerp(_upperNative[i + 4], _gripArmPose[i], weight);
        }
    }
}
