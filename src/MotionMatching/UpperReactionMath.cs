using UnityEngine;

namespace Manimal.MotionMatching
{
    internal static class UpperReactionMath
    {
        internal static Quaternion Delta(Quaternion source, Quaternion reference) => source * Quaternion.Inverse(reference);
        internal static Quaternion ChestDelta(Quaternion sourcePelvis, Quaternion sourceChest,
            Quaternion referencePelvis, Quaternion referenceChest, Quaternion nativePelvis)
        {
            var rootDelta = Delta(sourcePelvis * sourceChest, referencePelvis * referenceChest);
            return Quaternion.Inverse(nativePelvis) * rootDelta * nativePelvis;
        }
        internal static Quaternion Apply(Quaternion native, Quaternion delta, float weight)
            => Quaternion.Slerp(Quaternion.identity, delta, Mathf.Clamp01(weight)) * native;
    }
}
