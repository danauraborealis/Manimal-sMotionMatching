using System;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Manimal.MotionMatching
{
    public sealed partial class Plugin
    {
        private ConfigEntry<bool> _fidgetEnabled;
        private ConfigEntry<bool> _fidgetAutomatic;
        private readonly FidgetSchedule _fidgetSchedule = new FidgetSchedule(
            () => UnityEngine.Random.Range(FidgetSchedule.MinimumCooldownSeconds, FidgetSchedule.MaximumCooldownSeconds));
        private Player _fidgetSchedulePlayer;
        private ConfigEntry<KeyboardShortcut> _fidgetKey;
        private ConfigEntry<KeyboardShortcut> _fidgetOneKey, _fidgetThreeKey;
        private FidgetPoolEntry[] _fidgetPool;
        private FidgetPoolEntry[] _longFidgetPool;
        private FidgetPoolEntry[] _smallFidgetPool;
        private string _fidgetName;
        private ConfigEntry<float> _fidgetScale, _fidgetStrength;
        private ConfigEntry<float> _fidgetPivotRight, _fidgetPivotUp, _fidgetPivotForward;
        private ConfigEntry<KeyboardShortcut> _fidgetPivotFarther, _fidgetPivotCloser, _fidgetPivotReset;
        private Vector3 _fidgetPivotForPlayback;
        private readonly FidgetPlayback _fidgetPlayback = new FidgetPlayback();
        private readonly FidgetPatches _fidgetPatches = new FidgetPatches();
        private FidgetClip _fidgetClip;
        private Player _fidgetPlayer;
        private Player.FirearmController _fidgetController;
        private Transform _fidgetRoot;
        private Vector3 _fidgetBasePosition, _fidgetWrittenPosition;
        private Quaternion _fidgetBaseRotation, _fidgetWrittenRotation;
        private bool _fidgetApplied, _fidgetHooksReady;
        private int _fidgetHookCount;
        private float _fidgetStartedAt;
        private float _nextFidgetBlockedLog;

        private void InitializeFidgets()
        {
            _fidgetEnabled = Config.Bind("Fidgets", "Enabled", true, "Enable first-person weapon fidgets.");
            _fidgetAutomatic = Config.Bind("Fidgets", "Automatic playback", true,
                "Play a random fidget when the shared 9-15 second cooldown has elapsed and the weapon is free. A fresh delay is picked at each fidget start. Weapon switches and busy actions do not reset the timer. Manual keys remain available.");
            _fidgetKey = Config.Bind("Fidgets", "Play fidget", new KeyboardShortcut(KeyCode.F8, KeyCode.LeftAlt), "Play fidget2 while the weapon is free; press again to fade out.");
            _fidgetOneKey = Config.Bind("Fidgets", "Play fidget1", new KeyboardShortcut(KeyCode.F7, KeyCode.LeftAlt), "Play fidget1 while the weapon is free. Any fidget key during playback fades out the current clip.");
            _fidgetThreeKey = Config.Bind("Fidgets", "Play fidget3", new KeyboardShortcut(KeyCode.F9, KeyCode.LeftAlt), "Play fidget3 while the weapon is free. Any fidget key during playback fades out the current clip.");
            _fidgetScale = Config.Bind("Fidgets", "Source unit metres", 0.0254f,
                new ConfigDescription("Provisional Source-inch conversion, not yet calibrated in game. Translation only; rotation retains the authored angle.", new AcceptableValueRange<float>(0.001f, 0.1f)));
            _fidgetStrength = Config.Bind("Fidgets", "Strength", 1f,
                new ConfigDescription("Scale all fidget layers: weapon, support hand and fingers.", new AcceptableValueRange<float>(0f, 2f)));
            _fidgetPivotRight = BindFidgetPivot("Pivot right metres", "Positive moves the rotation pivot toward screen-right.");
            _fidgetPivotUp = BindFidgetPivot("Pivot up metres", "Positive moves the rotation pivot toward screen-up.");
            _fidgetPivotForward = BindFidgetPivot("Pivot forward metres", "Positive moves the rotation pivot away from the camera; negative toward you.");
            _fidgetPivotFarther = Config.Bind("Fidgets", "Move pivot farther", new KeyboardShortcut(KeyCode.PageUp, KeyCode.LeftAlt), "Increase forward pivot offset by 1 cm for the next playback.");
            _fidgetPivotCloser = Config.Bind("Fidgets", "Move pivot closer", new KeyboardShortcut(KeyCode.PageDown, KeyCode.LeftAlt), "Decrease forward pivot offset by 1 cm for the next playback.");
            _fidgetPivotReset = Config.Bind("Fidgets", "Reset pivot", new KeyboardShortcut(KeyCode.Home, KeyCode.LeftAlt), "Reset all three pivot offsets to zero for the next playback.");
            try
            {
                _fidgetPool = FidgetPool.Load();
                _longFidgetPool = FidgetPool.Load(5);
                _smallFidgetPool = FidgetPool.Load(1);
                InitializeFidgetGestures();
                _fidgetPatches.Enable();
                _fidgetHooksReady = true;
                Logger.LogInfo("Fidgets ready: weapon-only revolver pool for widths 1/2, shotgun+MP5 for 3/4, sniper+tau for 5+; shared random 9-15 second cooldown.");
            }
            catch (Exception error)
            {
                _fidgetPatches.Disable();
                Logger.LogError("Fidget prototype unavailable; other MotionMagic features remain available: " + error);
            }
        }

        private ConfigEntry<float> BindFidgetPivot(string key, string direction)
            => Config.Bind("Fidgets", key, 0f, new ConfigDescription(direction
                + " Measured from Tarkov's animated weapon root in view-relative metres. Applies on the next playback; zero retains the original prototype pivot.",
                new AcceptableValueRange<float>(-0.5f, 0.5f)));

        private void TuneFidgetPivot()
        {
            bool changed = false;
            if (_fidgetPivotReset.Value.IsDown())
            {
                _fidgetPivotRight.Value = _fidgetPivotUp.Value = _fidgetPivotForward.Value = 0;
                changed = true;
            }
            else if (_fidgetPivotFarther.Value.IsDown())
            {
                _fidgetPivotForward.Value = Mathf.Clamp(_fidgetPivotForward.Value + 0.01f, -0.5f, 0.5f);
                changed = true;
            }
            else if (_fidgetPivotCloser.Value.IsDown())
            {
                _fidgetPivotForward.Value = Mathf.Clamp(_fidgetPivotForward.Value - 0.01f, -0.5f, 0.5f);
                changed = true;
            }
            if (changed) Logger.LogInfo($"Fidget pivot for next playback: right={_fidgetPivotRight.Value:F3} m, up={_fidgetPivotUp.Value:F3} m, forward={_fidgetPivotForward.Value:F3} m.");
        }

        private void TickFidgets()
        {
            if (!_fidgetHooksReady) return;
            try
            {
                var player = Singleton<GameWorld>.Instance?.MainPlayer;
                float now = Time.time;
                if (player != _fidgetSchedulePlayer)
                {
                    _fidgetSchedule.Reset();
                    _fidgetSchedulePlayer = player;
                    _nextFidgetBlockedLog = 0f;
                }
                bool due = player != null && _fidgetSchedule.IsDue(now);
                if (!_enabled.Value || !_fidgetEnabled.Value || !isActiveAndEnabled || player == null
                    || !player.FirstPersonPointOfView || player.HealthController == null || !player.HealthController.IsAlive
                    || (_fidgetPlayer != null && (player != _fidgetPlayer || player.HandsController != _fidgetController)))
                { StopFidget(); return; }
                TuneFidgetPivot();
                if (_fidgetPlayback.Active)
                {
                    if (!FidgetIdle(player, _fidgetController)) { StopFidget(); return; }
                    _fidgetPlayback.Evaluate(now, out _, out _);
                    if (!_fidgetPlayback.Active)
                    {
                        Logger.LogInfo($"Fidget {_fidgetName} finished: {_fidgetHookCount} hand-IK passes.");
                        StopFidget();
                    }
                    else if (_fidgetHookCount == 0 && now - _fidgetStartedAt > 0.25f)
                    {
                        Logger.LogWarning("Fidget stopped: no local hand-IK hook reached. Check first-person animation state.");
                        StopFidget();
                    }
                }
                int requested = _fidgetOneKey.Value.IsDown() ? 1 : _fidgetKey.Value.IsDown() ? 2 : _fidgetThreeKey.Value.IsDown() ? 3 : 0;
                bool manual = requested != 0;
                if (_fidgetPlayback.Active)
                {
                    if (manual) _fidgetPlayback.Cancel(now);
                    return;
                }
                if (!manual && (!_fidgetAutomatic.Value || !due)) return;
                var controller = player.HandsController as Player.FirearmController;
                string blocked = FidgetBlockedReason(player, controller);
                if (blocked != null)
                {
                    LogFidgetBlocked(controller, blocked, now, manual);
                    return;
                }
                int width = controller.Item.CalculateCellSize().X;
                if (!FidgetPool.SupportsWidth(width))
                {
                    LogFidgetBlocked(controller, "invalid width (must be positive)", now, manual);
                    return;
                }
                var pool = width <= 2 ? _smallFidgetPool : width >= 5 ? _longFidgetPool : _fidgetPool;
                if (!manual) requested = UnityEngine.Random.Range(1, pool.Length + 1);
                var entry = pool[requested - 1];
                _fidgetName = entry.Name;
                _fidgetClip = entry.Weapon;
                _fidgetGesture = entry.Gesture;
                _fidgetPlayer = player;
                _fidgetController = controller;
                _fidgetStartedAt = now;
                _fidgetHookCount = 0;
                _fidgetPivotForPlayback = new Vector3(_fidgetPivotRight.Value, _fidgetPivotUp.Value, _fidgetPivotForward.Value);
                if (!FinitePivot(_fidgetPivotForPlayback)) throw new InvalidOperationException("Fidget pivot must be finite");
                _fidgetPlayback.Start(now, _fidgetClip.Duration, entry.EndFadeSeconds);
                _fidgetSchedule.PlaybackStarted(now);
                BeginFidgetGestures();
                Logger.LogInfo($"Fidget {_fidgetName} started: width={width}, manual={manual}, weapon={controller.Item.TemplateId}, operation={controller.CurrentOperation.GetType().Name}, sourceUnitMetres={_fidgetScale.Value}, strength={_fidgetStrength.Value}, pivotMetres={_fidgetPivotForPlayback}.");
            }
            catch (Exception error) { FailFidget(error); }
        }

        private static bool FidgetIdle(Player player, Player.FirearmController controller)
            => FidgetBlockedReason(player, controller) == null;

        private void LogFidgetBlocked(Player.FirearmController controller, string reason, float now, bool manual)
        {
            if (!manual && now < _nextFidgetBlockedLog) return;
            _nextFidgetBlockedLog = now + 15f;
            Logger.LogInfo($"Fidget waiting: reason={reason}, weapon={controller?.Item?.TemplateId}, width={controller?.Item?.CalculateCellSize().X}, operation={controller?.CurrentOperation?.GetType().Name}, manual={manual}.");
        }

        private static string FidgetBlockedReason(Player player, Player.FirearmController controller)
        {
            if (controller == null || controller.Item == null) return "no firearm";
            if (!(controller.CurrentOperation is Player.FirearmController.Idling)) return "weapon operation busy";
            if (controller.CurrentOperation is Player.FirearmController.UtilityOperation) return "weapon utility operation";
            if (controller.FirearmsAnimator == null) return "no firearms animator";
            if (!controller.FirearmsAnimator.IsIdling()) return "animator state not recognized as idle";
            if (controller.FirearmsAnimator.Animator.IsInTransition(1)) return "animator transitioning";
            if (controller.IsTriggerPressed) return "trigger pressed";
            if (controller.IsAiming) return "aiming";
            if (controller.Blindfire) return "blindfire";
            if (controller.IsInInteractionStrictCheck()) return "weapon interaction";
            if (player.IsInventoryOpened) return "inventory open";
            if (player.IsSprintEnabled) return "sprinting";
            if (player.IsInPronePose) return "prone";
            if (player.CustomAnimationsAreProcessing) return "custom animation processing";
            if (player.MovementContext == null) return "no movement context";
            if (!player.MovementContext.IsGrounded) return "not grounded";
            if (player.MovementContext.IsInMountedState) return "mounted";
            if (player.MovementContext.IsStationaryWeaponInHands) return "stationary weapon";
            if (player.MovementContext.LeftStanceEnabled) return "left shoulder stance";
            if (player.ProceduralWeaponAnimation == null) return "no procedural animation";
            if (player.ProceduralWeaponAnimation.IsAiming) return "procedural aiming";
            if (player.ProceduralWeaponAnimation.HandsContainer.WeaponRootAnim == null) return "no weapon root";
            if (player.ProceduralWeaponAnimation.HandsContainer.CameraTransform == null) return "no camera transform";
            return null;
        }

        // Called after native procedural/camera calculations and weapon-root shifting,
        // before IkProcess reads grip targets. Native root caches never contain our offset.
        internal void ApplyFidgetBeforeHandIk(Player player)
        {
            if (!_fidgetPlayback.Active || player != _fidgetPlayer) return;
            try
            {
                _fidgetHandRig?.Restore();
                RestoreFidgetTransform();
                if (!isActiveAndEnabled || !_enabled.Value || !_fidgetEnabled.Value || !player.FirstPersonPointOfView
                    || player.HandsController != _fidgetController || !player.HealthController.IsAlive
                    || _fidgetController.IsTriggerPressed)
                { StopFidget(); return; }
                if (!FidgetIdle(player, _fidgetController)) { StopFidget(); return; }
                _fidgetPlayback.Evaluate(Time.time, out float sampleTime, out float weight);
                if (!_fidgetPlayback.Active) { StopFidget(); return; }
                _fidgetHookCount++;
                if (weight <= 0) return;
                var hands = player.ProceduralWeaponAnimation.HandsContainer;
                if (hands.WeaponRootAnim == null || hands.CameraTransform == null) { StopFidget(); return; }
                _fidgetClip.Sample(sampleTime, out var position, out var rotation);
                float strength = Mathf.Clamp(_fidgetStrength.Value, 0, 2);
                float scale = Mathf.Clamp(_fidgetScale.Value, 0.001f, 0.1f);
                if (float.IsNaN(strength) || float.IsInfinity(strength) || float.IsNaN(scale) || float.IsInfinity(scale))
                    throw new InvalidOperationException("Fidget strength/scale must be finite");
                var basis = hands.CameraTransform.rotation;
                var blendedRotation = Quaternion.SlerpUnclamped(Quaternion.identity, rotation, weight * strength);
                var viewOffset = FidgetPivot.CompensatedTranslation(position * (scale * weight * strength), blendedRotation, _fidgetPivotForPlayback);
                var worldOffset = basis * viewOffset;
                var worldRotation = basis * blendedRotation * Quaternion.Inverse(basis);
                _fidgetRoot = hands.WeaponRootAnim;
                _fidgetBasePosition = _fidgetRoot.localPosition;
                _fidgetBaseRotation = _fidgetRoot.localRotation;
                _fidgetRoot.SetPositionAndRotation(_fidgetRoot.position + worldOffset, worldRotation * _fidgetRoot.rotation);
                _fidgetWrittenPosition = _fidgetRoot.localPosition;
                _fidgetWrittenRotation = _fidgetRoot.localRotation;
                _fidgetApplied = true;
            }
            catch (Exception error) { FailFidget(error); }
        }

        private static bool FinitePivot(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        internal void BeforeFidgetNativeUpdate(Player player)
        {
            if (player == _fidgetPlayer) { _fidgetHandRig?.Restore(); RestoreFidgetTransform(); }
        }

        internal void BeforeFidgetTrigger(Player.FirearmController controller, bool pressed)
        {
            // Restore before the native trigger path can calculate a shot from Fireport.
            if (pressed && controller == _fidgetController) StopFidget();
        }

        internal sealed class FidgetSuspension
        {
            internal Transform Root;
            internal Vector3 BasePosition, WrittenPosition;
            internal Quaternion BaseRotation, WrittenRotation;
        }

        internal FidgetSuspension SuspendFidgetForOverlap(Player.FirearmController controller)
        {
            if (controller != _fidgetController || !_fidgetApplied || _fidgetRoot == null) return null;
            var state = new FidgetSuspension
            {
                Root = _fidgetRoot, BasePosition = _fidgetBasePosition, BaseRotation = _fidgetBaseRotation,
                WrittenPosition = _fidgetWrittenPosition, WrittenRotation = _fidgetWrittenRotation
            };
            RestoreFidgetTransform();
            return state;
        }

        internal void ResumeFidgetAfterOverlap(Player.FirearmController controller, FidgetSuspension state)
        {
            if (state == null || state.Root == null || controller != _fidgetController || !_fidgetPlayback.Active
                || !isActiveAndEnabled || !_enabled.Value || !_fidgetEnabled.Value) return;
            // Never reintroduce an old rendered pose if a nested callback replaced it,
            // switched controllers or stopped playback while overlap was being checked.
            if ((state.Root.localPosition - state.BasePosition).sqrMagnitude >= 1e-12f
                || Mathf.Abs(Quaternion.Dot(state.Root.localRotation, state.BaseRotation)) <= 0.9999999f) return;
            state.Root.localPosition = state.WrittenPosition;
            state.Root.localRotation = state.WrittenRotation;
            _fidgetRoot = state.Root;
            _fidgetBasePosition = state.BasePosition;
            _fidgetBaseRotation = state.BaseRotation;
            _fidgetWrittenPosition = state.WrittenPosition;
            _fidgetWrittenRotation = state.WrittenRotation;
            _fidgetApplied = true;
        }

        private void RestoreFidgetTransform()
        {
            if (!_fidgetApplied) return;
            _fidgetApplied = false;
            if (_fidgetRoot == null) return;
            // Animation or another mod may already have replaced a channel. Do not
            // overwrite that new pose with our saved pose from the previous frame.
            if ((_fidgetRoot.localPosition - _fidgetWrittenPosition).sqrMagnitude < 1e-12f)
                _fidgetRoot.localPosition = _fidgetBasePosition;
            if (Mathf.Abs(Quaternion.Dot(_fidgetRoot.localRotation, _fidgetWrittenRotation)) > 0.9999999f)
                _fidgetRoot.localRotation = _fidgetBaseRotation;
            _fidgetRoot = null;
        }

        private void StopFidget()
        {
            _fidgetHandRig?.Restore();
            _fidgetHandRig = null;
            RestoreFidgetTransform();
            _fidgetPlayback.Stop();
            _fidgetController = null;
            _fidgetPlayer = null;
        }

        private void FailFidget(Exception error)
        {
            StopFidget();
            _fidgetHooksReady = false;
            Logger.LogError("Fidget prototype stopped after an error: " + error);
        }
    }
}


