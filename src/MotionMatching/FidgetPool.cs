using System.IO;
using System;
using ZLinq;

namespace Manimal.MotionMatching
{
    internal sealed class FidgetPoolEntry
    {
        internal readonly string Name;
        internal readonly FidgetClip Weapon;
        internal readonly FidgetGestureClip Gesture;
        internal readonly float EndFadeSeconds;

        internal FidgetPoolEntry(string name, FidgetClip weapon, FidgetGestureClip gesture, float endFadeSeconds)
        { Name = name; Weapon = weapon; Gesture = gesture; EndFadeSeconds = endFadeSeconds; }
    }

    internal static class FidgetPool
    {
        internal static bool SupportsWidth(int cells) => cells >= 1;

        // Shared pools: 1/2 weapon-only revolver, 3/4 shotgun+MP5, 5+ sniper+tau.
        internal static FidgetPoolEntry[] Load(int width = 3)
        {
            if (!SupportsWidth(width)) throw new ArgumentOutOfRangeException(nameof(width));
            if (width <= 2)
                return ValueEnumerable.Range(1, 3).Select(variant =>
                {
                    string action = variant == 1 ? "fidget" : "fidget" + variant;
                    // Intentionally no gesture track: native hand/finger poses stay in charge.
                    return new FidgetPoolEntry("Revolver/" + action,
                        FidgetClip.Load(action, "revolver"), null, 0.25f);
                }).ToArray();
            bool longGun = width >= 5;
            return ValueEnumerable.Range(0, 6).Select(index =>
            {
                bool secondSet = index >= 3;
                int variant = index % 3 + 1;
                string group = longGun ? (secondSet ? "tau" : "sniper") : (secondSet ? "mp5" : "");
                string action = group == "tau" ? "fidget " + variant
                    : group == "mp5" && variant == 1 ? "fidget" : "fidget" + variant;
                string label = longGun ? (secondSet ? "Tau" : "Sniper") : (secondSet ? "MP5" : "Shotgun");
                var weapon = FidgetClip.Load(action, group);
                var gesture = FidgetGestureClip.Load(action, group);
                if (Math.Abs(weapon.Duration - gesture.Duration) > 0.0001f)
                    throw new InvalidDataException($"Fidget timing mismatch: {group}/{action}");
                return new FidgetPoolEntry(label + "/" + action, weapon, gesture, 0.25f);
            }).ToArray();
        }
    }
}
