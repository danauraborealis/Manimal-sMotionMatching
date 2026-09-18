using System.Collections.Generic;
using System.Reflection;
using EFT;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // EFT bots reach full speed in ~0.3 s while every Alyx start takes about a second, so either the clip races at
    // 2.5x (scrambling feet) or the planted foot is left behind the body. capping how fast a bot's speed may rise
    // gives a ramp instead. scoped to bots an Alyx playback is attached to, and budgeted per bot per frame: the
    // mover's own speed write and its slow-at-end write can both land in one update, so a per-call cap was not a
    // per-second cap. braking and sprint are untouched (sprint acceleration never comes through ChangeSpeed).
    // Player.IsAI reads AIData.IsAI, so SAIN-driven bots are covered too (SAIN only patches MovementContext.IsAI)
    internal sealed class SpeedRampPatch : ModulePatch
    {
        // Player.Speed units (0-1) per second; 0 disables. EFT's own ramp is ~2/s
        internal static float LimitPerSecond;

        private sealed class Budget
        {
            public int Frame = -1;
            public float Spent;
            // Player.Speed the bot may not exceed this frame: the start clip's own root speed, so the body follows
            // the animation's acceleration instead of the clip racing to catch a body already at full speed
            public float? Ceiling;
        }

        internal static void SetCeiling(Player player, float? ceiling)
        {
            Budget budget;
            if (player && Attached.TryGetValue(player.GetInstanceID(), out budget))
                budget.Ceiling = ceiling;
        }

        private static readonly Dictionary<int, Budget> Attached = new Dictionary<int, Budget>();

        internal static void Attach(Player player)
        {
            if (player)
                Attached[player.GetInstanceID()] = new Budget();
        }

        internal static void Detach(Player player)
        {
            if (player)
                Attached.Remove(player.GetInstanceID());
        }

        internal static int AttachedCount => Attached.Count;

        protected override MethodBase GetTargetMethod() => typeof(Player).GetMethod(nameof(Player.ChangeSpeed), new[] { typeof(float) });

        [PatchPrefix]
        private static void Prefix(Player __instance, ref float speedDelta)
        {
            float limit = LimitPerSecond;
            if (limit <= 0f || speedDelta <= 0f || __instance == null || !__instance.IsAI)
                return;
            Budget budget;
            if (!Attached.TryGetValue(__instance.GetInstanceID(), out budget))
                return;
            if (budget.Frame != Time.frameCount)
            {
                budget.Frame = Time.frameCount;
                budget.Spent = 0f;
            }
            float left = limit * Time.deltaTime - budget.Spent;
            if (budget.Ceiling.HasValue)
                left = Mathf.Min(left, budget.Ceiling.Value - __instance.Speed);
            if (left <= 0f)
            {
                speedDelta = 0f;
                return;
            }
            if (speedDelta > left)
                speedDelta = left;
            budget.Spent += speedDelta;
        }
    }
}
