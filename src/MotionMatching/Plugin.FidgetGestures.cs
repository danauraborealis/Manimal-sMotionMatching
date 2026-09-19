using System;
using BepInEx.Configuration;
using EFT;
using UnityEngine;

namespace Manimal.MotionMatching
{
    public sealed partial class Plugin
    {
        private ConfigEntry<float> _fidgetHandStrength, _fidgetFingerStrength;
        private FidgetGestureClip _fidgetGesture;
        private FidgetHandRig _fidgetHandRig;
        private bool _fidgetGestureUnavailable;

        private void InitializeFidgetGestures()
        {
            _fidgetHandStrength = Config.Bind("Fidgets", "Hand strength", 1f,
                new ConfigDescription("Support-hand movement relative to the resolved weapon grip. 0 disables. Multiplied by overall Strength.", new AcceptableValueRange<float>(0f, 2f)));
            _fidgetFingerStrength = Config.Bind("Fidgets", "Finger strength", 1f,
                new ConfigDescription("Left-finger rotation offsets over the current grip pose. 0 disables; reduce if a grip clips. Multiplied by overall Strength.", new AcceptableValueRange<float>(0f, 2f)));
        }

        private void BeginFidgetGestures()
        {
            _fidgetHandRig?.Restore();
            _fidgetHandRig = null;
            _fidgetGestureUnavailable = false;
        }

        private bool TryFidgetGestureFrame(Player player, out float seconds, out float weight)
        {
            seconds = weight = 0;
            if (player != _fidgetPlayer || !_fidgetPlayback.Active || _fidgetGesture == null || _fidgetGestureUnavailable
                || !isActiveAndEnabled || !_enabled.Value || !_fidgetEnabled.Value
                || player.HandsController != _fidgetController || !player.FirstPersonPointOfView) return false;
            _fidgetPlayback.Evaluate(Time.time, out seconds, out weight);
            weight *= Mathf.Clamp(_fidgetStrength.Value, 0, 2);
            return _fidgetPlayback.Active;
        }

        // IkProcess has selected the native attachment grip. Adjust its solver target
        // now, so IkApply solves the entire arm rather than moving a wrist afterward.
        internal void ApplyFidgetHandTarget(Player player)
        {
            try
            {
                if (!TryFidgetGestureFrame(player, out var seconds, out var weight) || _fidgetHandRig == null) return;
                float strength = GestureStrength(_fidgetHandStrength.Value);
                _fidgetHandRig.ApplyHand(_fidgetGesture, seconds, weight * strength, Mathf.Clamp(_fidgetScale.Value, 0.001f, 0.1f));
            }
            catch (Exception error) { DisableFidgetGestures(error); }
        }

        // Native IkApply includes HandPoser.ManualUpdate. Its output is the baseline
        // for the additive finger rotations; weapon grip assets are never modified.
        internal void ApplyFidgetFingerPose(Player player)
        {
            if (player != _fidgetPlayer) return;
            try
            {
                _fidgetHandRig?.RestoreHandTarget();
                if (!TryFidgetGestureFrame(player, out var seconds, out var weight)) return;
                if (_fidgetHandRig == null)
                {
                    _fidgetHandRig = FidgetHandRig.Capture(player);
                    Logger.LogInfo("Fidget mapped: support-hand IK and 15 left-finger joints over the resolved grip.");
                }
                _fidgetHandRig.ApplyFingers(_fidgetGesture, seconds, weight * GestureStrength(_fidgetFingerStrength.Value));
            }
            catch (Exception error) { DisableFidgetGestures(error); }
        }

        private static float GestureStrength(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidOperationException("Gesture strength must be finite");
            return Mathf.Clamp(value, 0, 2);
        }

        private void DisableFidgetGestures(Exception error)
        {
            _fidgetHandRig?.Restore();
            _fidgetHandRig = null;
            _fidgetGestureUnavailable = true;
            Logger.LogWarning("Hand/finger layer skipped for this playback: " + error.Message);
        }
    }
}

