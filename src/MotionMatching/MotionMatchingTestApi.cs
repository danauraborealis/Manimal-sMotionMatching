using System;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // plain static entry points for SPT AI Bridge `type` + `invoke`; every call returns JSON text.
    // the bridge runs client commands on Unity's main thread, same as Plugin.Update
    public static class MotionMatchingTestApi
    {
        public static string Status()
        {
            var plugin = Plugin.Instance;
            return plugin == null ? Json(new { Loaded = false }) : Json(plugin.DescribeStatus());
        }

        public static string Scenarios()
        {
            return Json(new { Named = PuppetScript.NamedScenarios, Sweep = PuppetScript.Expand("sweep") });
        }

        public static string RaidReportStatus() => Json(Plugin.Instance?.RaidReportStatus());

        public static string SetHitReactions(bool enabled, bool synthetic) => Json(Plugin.Instance?.SetReactions(enabled, synthetic));
        public static string InjectHitReaction(float damage) => Json(Plugin.Instance?.InjectReaction(damage));
        public static string FindOpenTestArea(float radius)
        {
            var world = Singleton<GameWorld>.Instance;
            if (world?.MainPlayer == null) return Json(new { Ok = false, Message = "No raid running" });
            var result = OpenTestArea.Find(world.MainPlayer.Position, radius);
            if (result.Ok) PuppetSession.SetOpenTestArea(world.LocationId, result.Center, result.Radius);
            return Json(new { result.Ok, result.Message, Center = new[] { result.Center.x, result.Center.y, result.Center.z },
                result.Radius, result.Yaw, result.HeightVariation, result.TestedCandidates, LocationId = world.LocationId });
        }

        // the baseline runs need EFT's own acceleration; 0 disables the ramp, otherwise Player.Speed units per second
        public static string SetAccelerationLimit(float limit)
        {
            SpeedRampPatch.LimitPerSecond = limit;
            return "{\"Ok\":true,\"Limit\":" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        }

        public static string SetSprintInertia(bool enabled)
        {
            SprintInertia.Enabled = enabled;
            return Json(new { Ok = true, Enabled = SprintInertia.Enabled });
        }

        public static string SetAnimatorPhaseLock(bool enabled)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return Json(new { Ok = false, Message = "Plugin is not loaded." });
            plugin.SetPhaseLockForTest(enabled);
            return Json(new { Ok = true, Enabled = enabled });
        }

        public static string SetLocomotionRefinement(bool enabled)
        {
            LocomotionRefinement.Enabled = enabled;
            return Json(new { Ok = true, Enabled = enabled });
        }

        public static string SetPreferredTestArea(string locationId, float x, float y, float z, float yaw)
        {
            PuppetSession.ClearPreferredTestArea();
            if (string.IsNullOrWhiteSpace(locationId))
                return Json(new { Ok = false, MapMismatch = false, ExpectedLocationId = locationId, CurrentLocationId = (string)null, Message = "Preferred test area has no map id." });

            var world = Singleton<GameWorld>.Instance;
            string currentLocationId = world == null ? null : world.LocationId;
            if (world == null || world.MainPlayer == null)
                return Json(new { Ok = false, MapMismatch = false, ExpectedLocationId = locationId, CurrentLocationId = currentLocationId, Message = "No raid is running; preferred test area was not applied." });
            if (!string.Equals(currentLocationId, locationId, StringComparison.OrdinalIgnoreCase))
                return Json(new { Ok = false, MapMismatch = true, ExpectedLocationId = locationId, CurrentLocationId = currentLocationId, Message = "Preferred test area map mismatch: area is for '" + locationId + "', current map is '" + currentLocationId + "'." });

            string error;
            if (!PuppetSession.TrySetPreferredTestArea(locationId, new Vector3(x, y, z), yaw, out error))
                return Json(new { Ok = false, MapMismatch = false, ExpectedLocationId = locationId, CurrentLocationId = currentLocationId, Message = error });
            return Json(new { Ok = true, MapMismatch = false, ExpectedLocationId = locationId, CurrentLocationId = currentLocationId, Position = new[] { x, y, z }, Yaw = yaw, Message = "Preferred test area applied." });
        }

        public static string ClearPreferredTestArea()
        {
            PuppetSession.ClearPreferredTestArea();
            return Json(new { Ok = true, Message = "Preferred test area cleared." });
        }

        public static string StartPuppet(string scenario, bool freezeOthers, bool bringToPlayer, bool faceBot, bool footLock, string poseClip, bool bodyLean)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return Json(new { Ok = false, Message = "Plugin is not loaded." });
            string message = plugin.StartPuppet(scenario, freezeOthers, bringToPlayer, faceBot, footLock, poseClip, bodyLean);
            object lane = null;
            PuppetReport report;
            if (message.StartsWith("Puppet running", StringComparison.Ordinal) && PuppetSession.TryGetActiveReport(out report))
                lane = new { Start = report.LaneStart, End = report.LaneEnd, Yaw = report.LaneYaw, ClearMeters = report.LaneClearMeters };
            return Json(new { Ok = message.StartsWith("Puppet running", StringComparison.Ordinal), Message = message, Lane = lane });
        }

        // a live bot under its own AI (or SAIN) with the Alyx layer attached, for the driver-adapter tests
        public static string StartObserve(float seconds, string poseClip, bool bodyLean)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return Json(new { Ok = false, Message = "Plugin is not loaded." });
            string message = plugin.StartObserve(seconds, poseClip, bodyLean);
            return Json(new { Ok = message.StartsWith("Observing", StringComparison.Ordinal), Message = message });
        }

        // the Alyx layer on every live bot, streamed per bot to LogOutput; seconds then auto-stop
        // one signature only: the bridge resolves API members by name and refuses overloads
        public static string StartFleet(float seconds, string poseClip, int maxBots, bool control)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return Json(new { Ok = false, Message = "Plugin is not loaded." });
            string message = plugin.StartFleet(seconds, poseClip, maxBots, control);
            return Json(new { Ok = message.StartsWith("Fleet running", StringComparison.Ordinal), Message = message });
        }

        public static string StopFleet()
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return Json(new { Ok = false, Message = "Plugin is not loaded." });
            return Json(new { Ok = true, Message = plugin.StopFleet("Stopped by test API") });
        }

        public static string DumpSkeleton()
        {
            var plugin = Plugin.Instance;
            return Json(new { Message = plugin == null ? "Plugin is not loaded." : plugin.DumpSkeleton() });
        }

        public static string Stop()
        {
            var plugin = Plugin.Instance;
            return Json(new { Message = plugin == null ? "Plugin is not loaded." : plugin.StopFromApi() });
        }

        private static string Json(object value) => JsonConvert.SerializeObject(value);
    }
}
