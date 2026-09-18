using System;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using SPT.Reflection.Patching;

namespace Manimal.MotionMatching
{
    internal sealed class CapturePatches
    {
        private readonly ModulePatch[] _patches = { new VisualPatch(), new BodyPatch(), new IkPatch(), new DamagePatch() };
        internal void Enable() { foreach (var patch in _patches) patch.Enable(); }
        internal void Disable()
        {
            foreach (var patch in _patches)
            {
                try { if (patch.IsActive) patch.Disable(); }
                catch (Exception) { /* Continue releasing the other hooks during shutdown. */ }
            }
        }
        private sealed class DamagePatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(Player).GetMethod(nameof(Player.ApplyDamageInfo),
                new[] { typeof(DamageInfo), typeof(EBodyPart), typeof(EBodyPartColliderType), typeof(float) });
            [PatchPrefix] private static void Prefix(Player __instance, DamageInfo damageInfo, out float __state)
            {
                __state = -1f;
                if (damageInfo.DamageType == EDamageType.Bullet && Plugin.Instance?.WatchesDamage(__instance) == true)
                    __state = __instance.HealthController.GetBodyPartHealth(EBodyPart.Common).Current;
            }
            [PatchPostfix] private static void Postfix(Player __instance, float __state)
            {
                if (__state >= 0f && Plugin.Instance?.WatchesDamage(__instance) == true)
                    Plugin.Instance.ReceiveReactionDamage(__instance,
                        __state - __instance.HealthController.GetBodyPartHealth(EBodyPart.Common).Current, "bullet");
            }
        }
        private sealed class VisualPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(Player).GetMethod(nameof(Player.VisualPass));
            [PatchPrefix] private static void Prefix(Player __instance) => Plugin.Instance?.BeforeVisual(__instance);
            [PatchPostfix] private static void Postfix(Player __instance) => Plugin.Instance?.AfterVisual(__instance);
        }
        private sealed class BodyPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(Player).GetMethod(nameof(Player.BodyUpdate));
            [PatchPostfix] private static void Postfix(Player __instance) => Plugin.Instance?.Record(__instance, "after_body");
        }
        private sealed class IkPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(Player).GetMethod(nameof(Player.FBBIKUpdate));
            [PatchPostfix] private static void Postfix(Player __instance) => Plugin.Instance?.Record(__instance, "after_ik");
        }
    }
}
