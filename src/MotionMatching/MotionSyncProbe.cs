using EFT;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // Small, stage-safe signals for comparing the native arm swing with the leg swing. The probe owns only cached
    // transform references; Capture() reads their current pose on the caller's Unity thread.
    internal sealed class MotionSyncProbe
    {
        private readonly Transform _root;
        private readonly Transform _leftShoulder;
        private readonly Transform _leftElbow;
        private readonly Transform _rightShoulder;
        private readonly Transform _rightElbow;
        private readonly Transform _leftHip;
        private readonly Transform _leftKnee;
        private readonly Transform _rightHip;
        private readonly Transform _rightKnee;

        private MotionSyncProbe(
            Transform root,
            Transform leftShoulder,
            Transform leftElbow,
            Transform rightShoulder,
            Transform rightElbow,
            Transform leftHip,
            Transform leftKnee,
            Transform rightHip,
            Transform rightKnee)
        {
            _root = root;
            _leftShoulder = leftShoulder;
            _leftElbow = leftElbow;
            _rightShoulder = rightShoulder;
            _rightElbow = rightElbow;
            _leftHip = leftHip;
            _leftKnee = leftKnee;
            _rightHip = rightHip;
            _rightKnee = rightKnee;
        }

        public static MotionSyncProbe Create(Player player)
        {
            if (player == null)
                return null;

            Transform root = null;
            try
            {
                var references = player.Grounder?.ik?.references;
                root = references?.pelvis ? references.pelvis.parent : null;

                if (!root)
                {
                    Component component = player as Component;
                    root = component == null ? null : component.transform;
                }

                Transform armRoot = root;
                Transform leftShoulder = references?.leftUpperArm;
                Transform leftElbow = references?.leftForearm;
                Transform rightShoulder = references?.rightUpperArm;
                Transform rightElbow = references?.rightForearm;
                if (!leftShoulder) leftShoulder = FindChild(armRoot, "Base HumanLUpperarm");
                if (!leftElbow) leftElbow = FindChild(armRoot, "Base HumanLForearm1");
                if (!rightShoulder) rightShoulder = FindChild(armRoot, "Base HumanRUpperarm");
                if (!rightElbow) rightElbow = FindChild(armRoot, "Base HumanRForearm1");
                Transform leftHip = references?.leftThigh;
                Transform leftKnee = references?.leftCalf;
                Transform rightHip = references?.rightThigh;
                Transform rightKnee = references?.rightCalf;
                return new MotionSyncProbe(root, leftShoulder, leftElbow, rightShoulder, rightElbow, leftHip, leftKnee, rightHip, rightKnee);
            }
            catch
            {
                // A partially initialized player remains a valid probe. Each signal carries its own availability.
                return new MotionSyncProbe(root, null, null, null, null, null, null, null, null);
            }
        }

        // Reads sagittal segment angles in Root_Joint space. A false Has* value means the corresponding angle was
        // unavailable; its zero-valued storage field must not be interpreted as a measured angle.
        public MotionSyncProbeSnapshot Capture()
        {
            MotionSyncProbeSnapshot snapshot = new MotionSyncProbeSnapshot
            {
                HasRoot = _root
            };
            snapshot.HasLeftArmAngle = TrySagittal(_leftShoulder, _leftElbow, out snapshot.LeftArmAngle);
            snapshot.HasRightArmAngle = TrySagittal(_rightShoulder, _rightElbow, out snapshot.RightArmAngle);
            snapshot.HasLeftThighAngle = TrySagittal(_leftHip, _leftKnee, out snapshot.LeftThighAngle);
            snapshot.HasRightThighAngle = TrySagittal(_rightHip, _rightKnee, out snapshot.RightThighAngle);
            return snapshot;
        }

        private bool TrySagittal(Transform proximal, Transform distal, out float angle)
        {
            angle = 0f;
            if (!_root || !proximal || !distal)
                return false;

            try
            {
                Vector3 local = _root.InverseTransformDirection(distal.position - proximal.position);
                float magnitude = local.magnitude;
                if (magnitude < 1e-6f || float.IsNaN(magnitude) || float.IsInfinity(magnitude))
                    return false;

                local /= magnitude;
                // Body-forward is +Z and down is -Y in Root_Joint space. The signed angle is useful for lag
                // analysis while avoiding any dependence on the segment's length.
                angle = Mathf.Atan2(local.z, -local.y) * Mathf.Rad2Deg;
                return !float.IsNaN(angle) && !float.IsInfinity(angle);
            }
            catch
            {
                return false;
            }
        }

        private static Transform FindChild(Transform root, string name)
        {
            if (!root)
                return null;
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChild(root.GetChild(i), name);
                if (found)
                    return found;
            }
            return null;
        }
    }

    internal struct MotionSyncProbeSnapshot
    {
        public bool HasRoot;
        public bool HasLeftArmAngle;
        public float LeftArmAngle;
        public bool HasRightArmAngle;
        public float RightArmAngle;
        public bool HasLeftThighAngle;
        public float LeftThighAngle;
        public bool HasRightThighAngle;
        public float RightThighAngle;
    }
}
