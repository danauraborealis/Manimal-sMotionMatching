using UnityEngine;

namespace Manimal.MotionMatching
{
    internal static class FidgetPivot
    {
        // All arguments share a basis. Rotation is already blended by playback weight.
        // The point at pivotOffset follows the authored translation, while the root
        // orbits that point. Do not blend the correction a second time.
        internal static Vector3 CompensatedTranslation(Vector3 translation, Quaternion rotation, Vector3 pivotOffset)
            => translation + pivotOffset - rotation * pivotOffset;
    }
}
