using System;
using System.Reflection;
using EFT;
using SPT.Reflection.Patching;
using ZLinq;

namespace Manimal.MotionMatching
{
    // Optional adapter for the installed SAIN API. No SAIN assembly is bundled or required.
    // Its mover prefixes call IsBotInCombat, which reads BotComponent.SAINLayersActive.
    internal static class RouteCompatibility
    {
        private static MethodInfo _manualUpdate, _layersActiveGetter;
        private static PropertyInfo _botOwnerProperty;
        private static ModulePatch _manualPatch, _layersPatch;
        internal static bool IsEnabled { get; private set; }

        internal static bool TryEnable()
        {
            if (IsEnabled) return true;
            var assembly = AppDomain.CurrentDomain.GetAssemblies().AsValueEnumerable()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "SAIN", StringComparison.OrdinalIgnoreCase));
            if (assembly == null) return true;
            var component = assembly.GetType("SAIN.Components.BotComponent", false);
            if (component == null) throw new InvalidOperationException("SAIN's bot interface is unsupported; walking test refused.");
            _manualUpdate = component.GetMethod("ManualUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(float), typeof(float) }, null);
            _layersActiveGetter = component.GetProperty("SAINLayersActive")?.GetGetMethod(true);
            _botOwnerProperty = component.GetProperty("BotOwner");
            if (_manualUpdate == null || _manualUpdate.ReturnType != typeof(void) || _layersActiveGetter?.ReturnType != typeof(bool) || _botOwnerProperty?.PropertyType != typeof(BotOwner))
                throw new InvalidOperationException("SAIN's selected-bot controls could not be resolved; walking test refused.");
            try
            {
                _manualPatch = new SainManualPatch();
                _layersPatch = new SainLayersPatch();
                _manualPatch.Enable();
                _layersPatch.Enable();
                IsEnabled = true;
                return true;
            }
            catch { Disable(); throw; }
        }

        internal static void Disable()
        {
            try { if (_layersPatch?.IsActive == true) _layersPatch.Disable(); }
            finally
            {
                try { if (_manualPatch?.IsActive == true) _manualPatch.Disable(); }
                finally { IsEnabled = false; _manualPatch = null; _layersPatch = null; }
            }
        }

        internal static bool IsSelectedInstance(object instance)
        {
            if (instance == null || _botOwnerProperty == null) return false;
            try { var owner = _botOwnerProperty.GetValue(instance, null) as BotOwner; return RouteSession.IsOwnerActive(owner) || PuppetSession.ShouldSkipAi(owner); }
            catch { return false; } // Never suppress another bot if identity cannot be resolved.
        }

        private sealed class SainManualPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => _manualUpdate;
            [PatchPrefix] private static bool Prefix(object __instance) => !IsSelectedInstance(__instance);
        }

        private sealed class SainLayersPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => _layersActiveGetter;
            [PatchPrefix] private static bool Prefix(object __instance, ref bool __result)
            {
                if (!IsSelectedInstance(__instance)) return true;
                __result = false;
                return false;
            }
        }
    }
}
