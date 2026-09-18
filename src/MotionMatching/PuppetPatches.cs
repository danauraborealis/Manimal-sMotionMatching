using System;
using System.Reflection;
using EFT;
using SPT.Reflection.Patching;

namespace Manimal.MotionMatching
{
    // BotOwner.UpdateManual ticks mover, steering, look, shooting and doors; skipping it is what makes the puppet
    // (and frozen bots) ignore the AI. inert with no puppet active
    internal sealed class PuppetPatches
    {
        private readonly ModulePatch[] _patches =
        {
            new UpdatePatch(), new FixedUpdatePatch(), new SpeedRampPatch(), new BotInertiaPatch(),
            new SprintInertia.SpeedPatch(), new SprintInertia.AccelerationPatch(), new SprintInertia.ExitPatch()
        };

        internal void Enable() { foreach (var patch in _patches) patch.Enable(); }

        internal void Disable()
        {
            foreach (var patch in _patches)
            {
                try { if (patch.IsActive) patch.Disable(); }
                catch (Exception) { }
            }
        }

        private sealed class UpdatePatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(BotOwner).GetMethod(nameof(BotOwner.UpdateManual), BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);

            [PatchPrefix]
            private static bool Prefix(BotOwner __instance)
            {
                if (!PuppetSession.ShouldSkipAi(__instance))
                    return true;
                PuppetSession.DriveFromAiUpdate(__instance);
                return false;
            }
        }

        private sealed class FixedUpdatePatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(BotOwner).GetMethod(nameof(BotOwner.FixedUpdate), BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);

            [PatchPrefix]
            private static bool Prefix(BotOwner __instance) => !PuppetSession.ShouldSkipAi(__instance);
        }
    }
}
