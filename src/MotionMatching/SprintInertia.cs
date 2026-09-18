using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using EFT;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // EFT's SprintAcceleration already contains the physical acceleration and yaw-inertia curve. The AI mover
    // defeats it by assigning SprintSpeed directly, often several times during one update. This patch lets the
    // native method own those writes while retaining the mover's latest requested speed as a short-lived ceiling.
    // The ceiling deliberately expires: a path replan or teleport that does not issue another speed request must
    // not inherit a stale low value forever. Native acceleration and the native sprint-exit reset have separate
    // guards so the setter can tell them apart from direct AI writes.
    internal static class SprintInertia
    {
        private const float StandingPoseLevel = 0.85f;
        private const float RequestLifetimeSeconds = 0.2f;
        private const float SpeedEpsilon = 0.00001f;

        private static bool _enabled = true;
        private static readonly Dictionary<int, Attachment> Attachments = new Dictionary<int, Attachment>();
        private static readonly Dictionary<MovementContext, Attachment> Contexts =
            new Dictionary<MovementContext, Attachment>(ReferenceComparer<MovementContext>.Instance);
        private static readonly Dictionary<MovementContext, GuardState> Guards =
            new Dictionary<MovementContext, GuardState>(ReferenceComparer<MovementContext>.Instance);

        public static bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value)
                    return;
                _enabled = value;
                // A/B toggles and a disabled pose must start with no request from the preceding policy epoch.
                ClearAllRequestCaps();
            }
        }

        public static int AttachedCount => Attachments.Count;

        public static void Attach(Player player, Func<bool> active)
        {
            if (player == null)
                return;

            try
            {
                int id = player.GetInstanceID();
                RemoveAttachment(id);
                var attachment = new Attachment(id, player, active);
                Attachments[id] = attachment;

                try
                {
                    MovementContext context = player.MovementContext;
                    if (context != null)
                    {
                        attachment.Context = context;
                        Contexts[context] = attachment;
                    }
                }
                catch
                {
                    attachment.Exceptions++;
                }
            }
            catch
            {
                // A bot can be torn down between selection and attachment. The optional correction must fail open.
            }
        }

        public static void Detach(Player player)
        {
            if (player == null)
                return;

            try
            {
                RemoveAttachment(player.GetInstanceID());
            }
            catch
            {
                // Teardown is best effort; no movement state is modified here.
            }
        }

        // Used by the plugin shutdown path after its ModulePatch instances have been disabled.
        internal static void DetachAll()
        {
            Attachments.Clear();
            Contexts.Clear();
            Guards.Clear();
        }

        public static SprintInertiaProbe Probe(Player player)
        {
            bool configured = Enabled;
            Attachment attachment = FindAttachment(player);
            bool attached = attachment != null;
            bool active = false;
            bool scoped = false;
            if (attachment != null)
            {
                scoped = EvaluateScope(attachment, out active);
            }

            bool hasSpeed = false;
            float speed = 0f;
            bool physicalSprint = false;
            if (player != null)
            {
                try
                {
                    MovementContext context = player.MovementContext;
                    if (context != null)
                    {
                        speed = context.SprintSpeed;
                        hasSpeed = IsFinite(speed);
                    }
                }
                catch
                {
                    if (attachment != null)
                        attachment.Exceptions++;
                }

                try
                {
                    physicalSprint = player.Physical != null && player.Physical.Sprinting;
                }
                catch
                {
                    try { physicalSprint = player.IsSprintEnabled; }
                    catch
                    {
                        if (attachment != null)
                            attachment.Exceptions++;
                    }
                }
            }

            if (attachment == null)
            {
                return new SprintInertiaProbe(
                    configured,
                    false,
                    false,
                    false,
                    physicalSprint,
                    hasSpeed,
                    speed,
                    0,
                    0,
                    0,
                    0,
                    false,
                    0f,
                    false,
                    0f,
                    false,
                    0f,
                    0);
            }

            return new SprintInertiaProbe(
                configured,
                attached,
                active,
                scoped,
                physicalSprint,
                hasSpeed,
                speed,
                attachment.NativeUpdates,
                attachment.SuppressedExternalWrites,
                attachment.LowerRequests,
                attachment.Resets,
                attachment.HasLastRequest,
                attachment.LastRequest,
                attachment.HasLastOutput,
                attachment.LastOutput,
                attachment.HasLastTime,
                attachment.LastTime,
                attachment.Exceptions);
        }

        // The following methods are the small seam used by the nested patches and by the production-source runtime
        // harness. Keeping the policy here means the harness executes the same source that the game patch calls.
        internal static bool BeforeSprintSpeedSet(MovementContext context, ref float value)
        {
            try
            {
                if (context == null)
                    return true;

                Attachment attachment;
                if (!TryGetAttachment(context, out attachment))
                {
                    // A finalizer can run after a teardown has removed the attachment. Never block a native call.
                    return true;
                }

                GuardState guard;
                if (Guards.TryGetValue(context, out guard))
                {
                    if (guard.ExitDepth > 0)
                    {
                        attachment.Resets++;
                        RecordOutput(attachment, value);
                        return true;
                    }

                    if (guard.NativeDepth > 0)
                    {
                        if (guard.HasNativeCap && IsFinite(value) && value > guard.NativeCap)
                            value = guard.NativeCap;
                        attachment.NativeUpdates++;
                        RecordOutput(attachment, value);
                        return true;
                    }
                }

                if (!Enabled)
                    return true;

                bool active;
                if (!EvaluateScope(attachment, out active))
                    return true;

                float current;
                if (!TryReadSpeed(context, out current) || !IsFinite(value) || !IsFinite(current))
                    return true;

                float now = Now();
                attachment.RecordRequest(value, now);
                if (value > current + SpeedEpsilon)
                {
                    attachment.SuppressedExternalWrites++;
                    RecordOutput(attachment, current);
                    return false;
                }

                if (value < current - SpeedEpsilon)
                    attachment.LowerRequests++;
                return true;
            }
            catch
            {
                RecordException(context);
                return true;
            }
        }

        internal static void AfterSprintSpeedSet(MovementContext context)
        {
            try
            {
                Attachment attachment;
                if (context != null && TryGetAttachment(context, out attachment))
                {
                    float output;
                    if (TryReadSpeed(context, out output))
                        RecordOutput(attachment, output);
                }
            }
            catch
            {
                RecordException(context);
            }
        }

        internal static void BeforeSprintAcceleration(MovementContext context)
        {
            try
            {
                if (context == null || !Enabled)
                    return;

                Attachment attachment;
                bool active;
                if (!TryGetAttachment(context, out attachment) || !EvaluateScope(attachment, out active))
                    return;

                GuardState guard;
                if (!Guards.TryGetValue(context, out guard))
                {
                    guard = new GuardState();
                    Guards[context] = guard;
                }

                if (guard.NativeDepth == 0)
                {
                    guard.NativeAttachment = attachment;
                    float cap;
                    guard.HasNativeCap = attachment.TryGetFreshCap(Now(), out cap);
                    guard.NativeCap = cap;
                }
                guard.NativeDepth++;
            }
            catch
            {
                RecordException(context);
            }
        }

        internal static Exception AfterSprintAcceleration(MovementContext context, Exception exception)
        {
            try
            {
                if (context == null)
                    return exception;

                GuardState guard;
                if (!Guards.TryGetValue(context, out guard))
                    return exception;

                if (guard.NativeDepth > 0)
                    guard.NativeDepth--;
                if (guard.NativeDepth == 0)
                {
                    guard.HasNativeCap = false;
                    guard.NativeAttachment = null;
                }
                RemoveGuardIfUnused(context, guard);
            }
            catch
            {
                RecordException(context);
            }
            return exception;
        }

        internal static void BeforeSprintExit(MovementContext context)
        {
            try
            {
                if (context == null || !Enabled)
                    return;

                Attachment attachment;
                bool active;
                if (!TryGetAttachment(context, out attachment) || !EvaluateScope(attachment, out active))
                    return;

                GuardState guard;
                if (!Guards.TryGetValue(context, out guard))
                {
                    guard = new GuardState();
                    Guards[context] = guard;
                }
                if (guard.ExitDepth == 0)
                    guard.ExitAttachment = attachment;
                guard.ExitDepth++;
            }
            catch
            {
                RecordException(context);
            }
        }

        internal static Exception AfterSprintExit(MovementContext context, Exception exception)
        {
            try
            {
                if (context == null)
                    return exception;

                GuardState guard;
                if (!Guards.TryGetValue(context, out guard))
                    return exception;

                if (guard.ExitDepth > 0)
                    guard.ExitDepth--;
                if (guard.ExitDepth == 0)
                {
                    // A sprint state transition is a new movement order. Do not carry a previous cap into it,
                    // including when the original method throws after unsubscribing its callback.
                    guard.ExitAttachment?.ClearRequestCap();
                    guard.ExitAttachment = null;
                }
                RemoveGuardIfUnused(context, guard);
            }
            catch
            {
                RecordException(context);
            }
            return exception;
        }

        public sealed class SpeedPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod()
            {
                PropertyInfo property = typeof(MovementContext).GetProperty(
                    nameof(MovementContext.SprintSpeed), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return property?.GetSetMethod(true);
            }

            [PatchPrefix]
            private static bool Prefix(MovementContext __instance, ref float value)
            {
                return BeforeSprintSpeedSet(__instance, ref value);
            }

            [PatchPostfix]
            private static void Postfix(MovementContext __instance)
            {
                AfterSprintSpeedSet(__instance);
            }
        }

        public sealed class AccelerationPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod()
            {
                return typeof(MovementContext).GetMethod(
                    nameof(MovementContext.SprintAcceleration),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(float) },
                    null);
            }

            [PatchPrefix]
            private static void Prefix(MovementContext __instance)
            {
                BeforeSprintAcceleration(__instance);
            }

            [PatchFinalizer]
            private static Exception Finalizer(MovementContext __instance, Exception __exception)
            {
                return AfterSprintAcceleration(__instance, __exception);
            }
        }

        public sealed class ExitPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod()
            {
                return typeof(MovementContext).GetMethod(
                    nameof(MovementContext.SprintExitDelegate),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(EPlayerState), typeof(EPlayerState) },
                    null);
            }

            [PatchPrefix]
            private static void Prefix(MovementContext __instance)
            {
                BeforeSprintExit(__instance);
            }

            [PatchFinalizer]
            private static Exception Finalizer(MovementContext __instance, Exception __exception)
            {
                return AfterSprintExit(__instance, __exception);
            }
        }

        private static bool EvaluateScope(Attachment attachment, out bool active)
        {
            active = false;
            if (attachment == null || attachment.Player == null)
                return false;

            try
            {
                Player player = attachment.Player;
                if (!player.IsAI || player.HealthController == null || !player.HealthController.IsAlive ||
                    player.MovementContext == null || player.IsInPronePose || player.PoseLevel < StandingPoseLevel)
                {
                    attachment.ClearRequestCap();
                    return false;
                }

                active = attachment.Active == null || attachment.Active();
                if (!active)
                    attachment.ClearRequestCap();
                return active;
            }
            catch
            {
                attachment.Exceptions++;
                attachment.ClearRequestCap();
                return false;
            }
        }

        private static Attachment FindAttachment(Player player)
        {
            if (player == null)
                return null;

            try
            {
                Attachment attachment;
                if (Attachments.TryGetValue(player.GetInstanceID(), out attachment) &&
                    attachment != null && ReferenceEquals(attachment.Player, player))
                {
                    // Refresh the one-to-one context index if EFT replaced the context during a respawn. The next
                    // setter lookup is then still O(1), and no movement state is changed by this observation.
                    MovementContext context = player.MovementContext;
                    if (context != null && !ReferenceEquals(attachment.Context, context))
                    {
                        if (attachment.Context != null)
                        {
                            Attachment mapped;
                            if (Contexts.TryGetValue(attachment.Context, out mapped) && ReferenceEquals(mapped, attachment))
                                Contexts.Remove(attachment.Context);
                        }
                        attachment.Context = context;
                        Contexts[context] = attachment;
                    }
                    return attachment;
                }
            }
            catch
            {
                // Probe is observational and must remain usable during teardown.
            }
            return null;
        }

        private static bool TryGetAttachment(MovementContext context, out Attachment attachment)
        {
            attachment = null;
            if (context == null)
                return false;

            if (Contexts.TryGetValue(context, out attachment))
            {
                Attachment current;
                if (attachment != null && Attachments.TryGetValue(attachment.Id, out current) && ReferenceEquals(current, attachment))
                {
                    try
                    {
                        // The context index is only valid while it is still the Player's live context. This check
                        // keeps a replaced context fail-open until Probe/Attach refreshes the index.
                        if (ReferenceEquals(attachment.Player.MovementContext, context))
                            return true;
                    }
                    catch
                    {
                        attachment.Exceptions++;
                    }
                }
                Contexts.Remove(context);
                attachment = null;
            }

            return false;
        }

        private static void RemoveAttachment(int id)
        {
            Attachment attachment;
            if (!Attachments.TryGetValue(id, out attachment))
                return;
            Attachments.Remove(id);
            if (attachment?.Context != null)
            {
                Attachment mapped;
                if (Contexts.TryGetValue(attachment.Context, out mapped) && ReferenceEquals(mapped, attachment))
                    Contexts.Remove(attachment.Context);
            }
        }

        private static void ClearAllRequestCaps()
        {
            foreach (Attachment attachment in Attachments.Values)
                attachment?.ClearRequestCap();
        }

        private static void RemoveGuardIfUnused(MovementContext context, GuardState guard)
        {
            if (guard.NativeDepth == 0 && guard.ExitDepth == 0)
                Guards.Remove(context);
        }

        private static bool TryReadSpeed(MovementContext context, out float speed)
        {
            try
            {
                speed = context.SprintSpeed;
                return true;
            }
            catch
            {
                speed = 0f;
                return false;
            }
        }

        private static void RecordRequestException(Attachment attachment)
        {
            if (attachment != null)
                attachment.Exceptions++;
        }

        private static void RecordOutput(Attachment attachment, float output)
        {
            if (attachment == null)
                return;
            attachment.HasLastOutput = true;
            attachment.LastOutput = output;
            attachment.HasLastTime = true;
            attachment.LastTime = Now();
        }

        private static void RecordException(MovementContext context)
        {
            try
            {
                Attachment attachment;
                if (context != null && TryGetAttachment(context, out attachment))
                    RecordRequestException(attachment);
            }
            catch
            {
                // The exception path itself must never escape a movement patch.
            }
        }

        private static float Now()
        {
            try { return Time.realtimeSinceStartup; }
            catch { return 0f; }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private sealed class Attachment
        {
            public readonly int Id;
            public readonly Player Player;
            public readonly Func<bool> Active;
            public MovementContext Context;
            public int NativeUpdates;
            public int SuppressedExternalWrites;
            public int LowerRequests;
            public int Resets;
            public int Exceptions;
            public bool HasLastRequest;
            public float LastRequest;
            public bool HasLastOutput;
            public float LastOutput;
            public bool HasLastTime;
            public float LastTime;
            private bool _hasRequestCap;
            private float _requestCap;
            private float _requestTime;

            public Attachment(int id, Player player, Func<bool> active)
            {
                Id = id;
                Player = player;
                Active = active;
                _requestTime = -1f;
            }

            public void RecordRequest(float value, float time)
            {
                HasLastRequest = true;
                LastRequest = value;
                HasLastTime = true;
                LastTime = time;
                _hasRequestCap = true;
                _requestCap = value;
                _requestTime = time;
            }

            public bool TryGetFreshCap(float now, out float cap)
            {
                cap = 0f;
                if (!_hasRequestCap || !IsFinite(_requestCap))
                    return false;
                if (IsFinite(now) && IsFinite(_requestTime) && now >= _requestTime && now - _requestTime > RequestLifetimeSeconds)
                {
                    ClearRequestCap();
                    return false;
                }
                cap = _requestCap;
                return true;
            }

            public void ClearRequestCap()
            {
                _hasRequestCap = false;
                _requestCap = 0f;
                _requestTime = -1f;
            }
        }

        private sealed class GuardState
        {
            public int NativeDepth;
            public bool HasNativeCap;
            public float NativeCap;
            public Attachment NativeAttachment;
            public int ExitDepth;
            public Attachment ExitAttachment;
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();

            public bool Equals(T x, T y) { return ReferenceEquals(x, y); }

            public int GetHashCode(T obj) { return RuntimeHelpers.GetHashCode(obj); }
        }
    }

    // Immutable value copied into DiagnosticCapture samples. Active is the pose callback result; Scoped is the
    // complete AI/alive/standing/pose gate. Enabled is the configured switch, so captures remain comparable when
    // the switch is off while still reporting the native current speed.
    internal readonly struct SprintInertiaProbe
    {
        public readonly bool ConfiguredEnabled;
        public readonly bool Attached;
        public readonly bool Active;
        public readonly bool Scoped;
        public readonly bool PhysicalSprint;
        public readonly bool HasCurrentSprintSpeed;
        public readonly float CurrentSprintSpeed;
        public readonly int NativeUpdates;
        public readonly int SuppressedExternalWrites;
        public readonly int LowerRequests;
        public readonly int Resets;
        public readonly bool HasLastRequest;
        public readonly float LastRequest;
        public readonly bool HasLastOutput;
        public readonly float LastOutput;
        public readonly bool HasLastTime;
        public readonly float LastTime;
        public readonly int Exceptions;

        public bool Enabled => ConfiguredEnabled;
        public bool Effective => ConfiguredEnabled && Scoped;

        public SprintInertiaProbe(
            bool configuredEnabled,
            bool attached,
            bool active,
            bool scoped,
            bool physicalSprint,
            bool hasCurrentSprintSpeed,
            float currentSprintSpeed,
            int nativeUpdates,
            int suppressedExternalWrites,
            int lowerRequests,
            int resets,
            bool hasLastRequest,
            float lastRequest,
            bool hasLastOutput,
            float lastOutput,
            bool hasLastTime,
            float lastTime,
            int exceptions)
        {
            ConfiguredEnabled = configuredEnabled;
            Attached = attached;
            Active = active;
            Scoped = scoped;
            PhysicalSprint = physicalSprint;
            HasCurrentSprintSpeed = hasCurrentSprintSpeed;
            CurrentSprintSpeed = currentSprintSpeed;
            NativeUpdates = nativeUpdates;
            SuppressedExternalWrites = suppressedExternalWrites;
            LowerRequests = lowerRequests;
            Resets = resets;
            HasLastRequest = hasLastRequest;
            LastRequest = lastRequest;
            HasLastOutput = hasLastOutput;
            LastOutput = lastOutput;
            HasLastTime = hasLastTime;
            LastTime = lastTime;
            Exceptions = exceptions;
        }
    }
}
