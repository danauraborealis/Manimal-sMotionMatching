using System;
using System.Reflection;
using EFT;
using SPT.Reflection.Patching;

namespace Manimal.MotionMatching
{
    internal sealed class FidgetPatches
    {
        private readonly ModulePatch[] _patches = { new HandIk(), new HandPose(), new BeforeVisual(), new BeforeComplex(), new BeforeArms(), new Trigger(), new Shot(), new LauncherShot(), new FlareShot(), new RocketShot(), new Overlap() };
        internal void Enable() { foreach (var patch in _patches) patch.Enable(); }
        internal void Disable()
        {
            foreach (var patch in _patches)
                try { if (patch.IsActive) patch.Disable(); } catch (Exception) { }
        }
        private sealed class HandIk : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(Player).GetMethod(nameof(Player.IkProcess));
            [PatchPrefix] private static void Prefix(Player __instance) => Plugin.Instance?.ApplyFidgetBeforeHandIk(__instance);
            [PatchPostfix] private static void Postfix(Player __instance) => Plugin.Instance?.ApplyFidgetHandTarget(__instance);
        }
        private sealed class HandPose : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(Player).GetMethod(nameof(Player.IkApply));
            [PatchPostfix] private static void Postfix(Player __instance) => Plugin.Instance?.ApplyFidgetFingerPose(__instance);
        }
        private sealed class BeforeVisual : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(Player).GetMethod(nameof(Player.VisualPass));
            [PatchPrefix] private static void Prefix(Player __instance) => Plugin.Instance?.BeforeFidgetNativeUpdate(__instance);
        }
        private sealed class BeforeComplex : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(Player).GetMethod(nameof(Player.ComplexUpdate));
            [PatchPrefix] private static void Prefix(Player __instance) => Plugin.Instance?.BeforeFidgetNativeUpdate(__instance);
        }
        private sealed class BeforeArms : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(Player).GetMethod(nameof(Player.ArmsUpdate));
            [PatchPrefix] private static void Prefix(Player __instance) => Plugin.Instance?.BeforeFidgetNativeUpdate(__instance);
        }
        private sealed class Trigger : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(Player.FirearmController).GetMethod(nameof(Player.FirearmController.SetTriggerPressed));
            [PatchPrefix] private static void Prefix(Player.FirearmController __instance, bool pressed) => Plugin.Instance?.BeforeFidgetTrigger(__instance, pressed);
        }
        // Delayed/automatic shots can enter without another input event. Restore
        // before these methods read Fireport, not InitiateShot (which receives an already computed ray).
        private sealed class Shot : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(Player.FirearmController).GetMethod(nameof(Player.FirearmController.Shot));
            [PatchPrefix] private static void Prefix(Player.FirearmController __instance) => Plugin.Instance?.BeforeFidgetTrigger(__instance, true);
        }
        private sealed class LauncherShot : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(Player.FirearmController).GetMethod(nameof(Player.FirearmController.LauncherShot));
            [PatchPrefix] private static void Prefix(Player.FirearmController __instance) => Plugin.Instance?.BeforeFidgetTrigger(__instance, true);
        }
        private sealed class FlareShot : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(Player.FirearmController).GetMethod(nameof(Player.FirearmController.LaunchFlareGunShot));
            [PatchPrefix] private static void Prefix(Player.FirearmController __instance) => Plugin.Instance?.BeforeFidgetTrigger(__instance, true);
        }
        private sealed class RocketShot : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(Player.FirearmController).GetMethod(nameof(Player.FirearmController.LaunchRocketShot));
            [PatchPrefix] private static void Prefix(Player.FirearmController __instance) => Plugin.Instance?.BeforeFidgetTrigger(__instance, true);
        }
        private sealed class Overlap : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(Player.FirearmController).GetMethod(nameof(Player.FirearmController.WeaponOverlapping));
            [PatchPrefix] private static void Prefix(Player.FirearmController __instance, out Plugin.FidgetSuspension __state)
                => __state = Plugin.Instance?.SuspendFidgetForOverlap(__instance);
            [PatchPostfix] private static void Postfix(Player.FirearmController __instance, Plugin.FidgetSuspension __state)
                => Plugin.Instance?.ResumeFidgetAfterOverlap(__instance, __state);
        }
    }
}
