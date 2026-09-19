using System;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;
using ZLinq;

namespace Manimal.MotionMatching
{
    [BepInPlugin(ModInfo.Guid, ModInfo.Name, ModInfo.Version)]
    [BepInDependency("com.arys.unitytoolkit")]
    [BepInIncompatibility(Plugin.LegacyModGuid)]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; }
        internal Player Selected { get; private set; }
        private GameWorld _world;
        private DiagnosticCapture _capture;
        private RouteSession _route;
        private PuppetSession _puppet;
        private bool _faceBot;
        private FootLock _footLock;
        private FootPlacer _placer;
        private BodyLean _bodyLean;
        private Transform[] _hands;
        private PosePlayback _pose;
        private bool? _phaseLockOverride;
        private PoseDatabase _poseDatabase;
        private string _poseDatabaseStamp;
        private int _preRenderFrame = -1;
        private static readonly int FootStepHash = Animator.StringToHash("FootStep");
        internal static int FootStepHashValue => FootStepHash;
        private Fleet _fleet;
        private float _fleetSeconds;
        private float _fleetStarted;
        internal float HipShiftValue => _hipShift.Value;
        internal float KneeBendFloorValue => _kneeBendFloor.Value;
        internal bool BodyLeanWanted => _bodyLeanConfig.Value;
        internal bool FootPlacerWanted => _footPlacerConfig.Value;
        internal bool FootLockWanted => _footLockConfig.Value;
        internal float BodyLeanStrengthValue => _bodyLeanStrength.Value;
        internal float BodyLeanWeaponLevelValue => _bodyLeanWeaponLevel.Value;
        internal bool ClipDrivenStartsWanted => _clipDrivenStarts.Value;
        private Task<string> _save;
        private float _started;
        private bool _hooksReady;
        private string _status = "Ready. Select a nearby bot with Ctrl+F6.";
        private ConfigEntry<bool> _enabled, _overlay, _puppetFreeze, _puppetBring, _footLockConfig, _bodyLeanConfig, _footPlacerConfig, _debugDraw;
        private ConfigEntry<string> _puppetScenario, _poseDatabasePath, _poseClip;
        private ConfigEntry<float> _seconds, _range, _legLength, _moveSpeed;
        private ConfigEntry<float> _bodyLeanStrength, _bodyLeanWeaponLevel, _accelerationLimit, _pelvisTilt, _hipShift;
        private ConfigEntry<int> _walkFamily;
        private ConfigEntry<bool> _strideWarp, _sprintTransitions, _sprintCycleBridge, _repickStart, _inertialize, _alyxCycles, _clipDrivenStarts, _preferGrunt;
        private ConfigEntry<float> _botInertia;
        private ConfigEntry<int> _runBandLegs, _sprintLegs, _variations;
        private ConfigEntry<bool> _animatorPhaseLock, _turnInPlace, _turningStarts;
        private ConfigEntry<bool> _nativeSprintInertia;
        private ConfigEntry<float> _kneeBendFloor;
        private ConfigEntry<KeyboardShortcut> _select, _observe, _walk, _stop, _puppetKey, _footLockToggle;
        private readonly CapturePatches _patches = new CapturePatches();
        private readonly PuppetPatches _puppetPatches = new PuppetPatches();
        // a full sweep takes ~2 minutes; the cap only guards against a wedged scenario
        private const float PuppetMaximumSeconds = 300f;
        private const int PuppetMaximumSamples = 160000;

        internal string LastCapturePath { get; private set; }
        internal string LastEndReason { get; private set; }

        private void Awake()
        {
            Instance = this;
            MigrateLegacySettings();
            BindRaidSettings();
            _enabled = Config.Bind("General", "Enabled", true, "Master switch. Disabling releases the bot and saves any capture.");
            _overlay = Config.Bind("General", "Overlay", false, "Show test status and foot/path markers.");
            _debugDraw = Config.Bind("General", "Debug draw", false, "Draw the foot placer's markers in the world: red previous steps, blue predicted steps, green current feet, cyan path, yellow foot corrections.");
            _seconds = Config.Bind("Capture", "Seconds", 30f, new ConfigDescription("Automatically finish after this duration.", new AcceptableValueRange<float>(5f, 120f)));
            _range = Config.Bind("Test", "Selection range", 35f, new ConfigDescription("Maximum distance to the nearest test bot in metres.", new AcceptableValueRange<float>(5f, 100f)));
            _legLength = Config.Bind("Test", "Route leg length", 5f, new ConfigDescription("Requested length of each leg of the automatic L route.", new AcceptableValueRange<float>(2f, 15f)));
            _moveSpeed = Config.Bind("Test", "Route move speed", 0.35f, new ConfigDescription("Bot target move speed for the L route, as the game's 0-1 Player.Speed (not m/s); 1 ran at ~2.7 m/s. The reached m/s is logged when the route ends.", new AcceptableValueRange<float>(0.1f, 1f)));
            _select = Config.Bind("Controls", "Select nearest bot", new KeyboardShortcut(KeyCode.F6, KeyCode.LeftControl));
            _observe = Config.Bind("Controls", "Observe normal AI", new KeyboardShortcut(KeyCode.F7, KeyCode.LeftControl));
            _walk = Config.Bind("Controls", "Run walking route", new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl));
            _stop = Config.Bind("Controls", "Stop and release", new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl));
            _puppetKey = Config.Bind("Controls", "Run puppet scenario", new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl));
            _footLockToggle = Config.Bind("Controls", "Toggle foot lock live", new KeyboardShortcut(KeyCode.F11, KeyCode.LeftControl), "Flip the running foot lock off/on mid-capture for a side-by-side look. PIN markers show held feet.");
            _puppetScenario = Config.Bind("Puppet", "Scenario", "sweep", "Named scenario (sweep, walk, turns, stops) or steps like move:0.3:6;stop:1;turn:180:120;sprint:6;curve:0.3:180:45.");
            _puppetFreeze = Config.Bind("Puppet", "Freeze other bots", true, "Skip every other bot's AI update while the puppet runs so nothing wanders in or starts a fight.");
            _puppetBring = Config.Bind("Puppet", "Bring bot to you", true, "Teleport the puppet to a clear lane in front of you so its animation stays on screen.");
            _footLockConfig = Config.Bind("Correction", "Foot lock", true, "Pin planted feet during native animation handoffs (visual only).");
            // renamed from "AI acceleration limit" when it became scoped to bots with an Alyx playback attached
            _accelerationLimit = Config.Bind("Correction", "AI acceleration limit (Alyx bots)", 1f, new ConfigDescription("How fast a bot's move speed may rise while an Alyx clip set is attached to it, in Player.Speed (0-1) per second, budgeted per bot per frame; EFT's own ramp is about 2. Alyx starts need about 1. 0 disables. Reaches SAIN-driven bots too (they call the same speed method); sprint is untouched.", new AcceptableValueRange<float>(0f, 4f)));
            _accelerationLimit.SettingChanged += (s, e) => SpeedRampPatch.LimitPerSecond = _accelerationLimit.Value;
            SpeedRampPatch.LimitPerSecond = _accelerationLimit.Value;
            _botInertia = Config.Bind("Correction", "AI inertia", 4f, new ConfigDescription("Cap on how fast an attached bot's ground velocity may change, in m/s^2: speeding up and changing direction take time, as they do for a body with mass. Braking stays the game's. 0 disables.", new AcceptableValueRange<float>(0f, 30f)));
            _botInertia.SettingChanged += (s, e) => BotInertiaPatch.Acceleration = _botInertia.Value;
            BotInertiaPatch.Acceleration = _botInertia.Value;
            _nativeSprintInertia = Config.Bind("Correction", "Native sprint inertia", true, "Let the game's sprint acceleration and turn-speed inertia control attached bots instead of instant AI sprint-speed writes. Native stop commands and sprint exit remain in control. Disable for an A/B capture.");
            _nativeSprintInertia.SettingChanged += (s, e) => SprintInertia.Enabled = _nativeSprintInertia.Value;
            SprintInertia.Enabled = _nativeSprintInertia.Value;
            _hipShift = Config.Bind("Correction", "Hip shift", 0.5f, new ConfigDescription("How far the hips follow the planted feet's corrections (Valve's newer foot lock uses 0.75), capped at 6 cm. Reduces stretched legs when the body has moved on from where the feet are held; 0 disables.", new AcceptableValueRange<float>(0f, 1f)));
            _footPlacerConfig = Config.Bind("Correction", "Foot placer", true, "Place the clip's feet on predicted step targets (Valve's stride retargeting) while a pose clip plays. Replaces the foot lock and stride warp on those frames.");
            _bodyLeanConfig = Config.Bind("Correction", "Body lean", true, "Procedural weight shift: lean the torso into acceleration and turns instead of floating upright.");
            _bodyLeanStrength = Config.Bind("Correction", "Body lean strength", 1f, new ConfigDescription("Scales the lean angle.", new AcceptableValueRange<float>(0f, 2f)));
            _bodyLeanWeaponLevel = Config.Bind("Correction", "Body lean keeps weapon level", 0.6f, new ConfigDescription("How much of the lean is cancelled at the chest so the gun keeps pointing where the animator aimed it. 1 leans only the lower torso.", new AcceptableValueRange<float>(0f, 1f)));
            _poseDatabasePath = Config.Bind("Pose playback", "Database", "alyx_posedb.json", "Runtime pose database, relative to this plugin's folder.");
            _poseClip = Config.Bind("Pose playback", "Clip", "startstop", "Locomotion clip set. Empty disables playback.");
            _strideWarp = Config.Bind("Pose playback", "Stride warp", true, "Scale each step to the bot's speed instead of slowing the clip down (shorter strides, natural cadence).");
            _sprintTransitions = Config.Bind("Pose playback", "Sprint transitions", true, "Play Alyx turn-into-sprint and sprint-to-walk clips.");
            _walkFamily = Config.Bind("Pose playback", "Walk clips", 0, new ConfigDescription("Clips used at walking speed (1.2-1.9 m/s): 0 = heavy soldier walk family (grunt takes only below 1.2 m/s), 1 = grunt motion-match takes everywhere (stiff-legged), 2 = grunt hops as walking starts/stops with the heavy loop between. Strafes use hops in every family.", new AcceptableValueRange<int>(0, 2)));
            _preferGrunt = Config.Bind("Pose playback", "Prefer grunt clips", true, "Combine grunt clips are preferred over the heavy soldier's wherever both fit (starts, stops, run cycles); the grunt set is the dynamic, consistent one. Heavy clips still fill what the grunt has no clip for (the walk loop).");
            _clipDrivenStarts = Config.Bind("Pose playback", "Clip-driven starts", true, "While an Alyx start clip plays, the bot may not accelerate faster than the clip's own root speed, so the body follows the animation instead of the animation racing after a body already at full speed. AI only; sprint untouched.");
            _sprintCycleBridge = Config.Bind("Pose playback", "Sprint cycle bridge", true, "After a sprint transition, run the Alyx sprint loop until its feet line up with Tarkov's.");
            // renamed from "Pelvis tilt from clip": the first build saved 0.3 and BepInEx kept feeding it back after the default changed
            _pelvisTilt = Config.Bind("Pose playback", "Pelvis tilt from clip (experimental)", 1f, new ConfigDescription("Experimental. How much of the Alyx pelvis pitch and roll is written under Tarkov's torso (the yaw is always the clip's). At 1 the clip is used as is (the soldiers lean the pelvis forward, which reads as a stuck-out backside). Below 1 the heavy walk loop pitched the whole torso to -53 degrees in testing; the split needs verifying before it is the default.", new AcceptableValueRange<float>(0f, 1f)));
            _alyxCycles = Config.Bind("Pose playback", "Alyx cycles", true, "Stay in Alyx run/walk loops between starts, cuts and stops instead of handing back to Tarkov's own cycle.");
            _animatorPhaseLock = Config.Bind("Pose playback", "Animator phase lock", true, "While a Tarkov cycle plays on the placer, follow the animator's own phase of that clip so the legs stay in step with the arm swing.");
            _kneeBendFloor = Config.Bind("Foot placer", "Knee bend floor", 0f, new ConfigDescription("Lower the hips so no planted leg is straighter than this (hip-to-ankle over thigh plus shin; Tarkov's idle is 0.94, a locked knee 1.0). 0 disables.", new AcceptableValueRange<float>(0f, 0.99f)));
            _variations = Config.Bind("Pose playback", "Variations", 1, new ConfigDescription("Directional hops played from inside a loop and returned to it: 0 off, 1 on (combat on the strafing loops every 4-10 s, patrol on straight stretches every 15-40 s), 2 test mode (every 3-6 s anywhere).", new AcceptableValueRange<int>(0, 2)));
            _turningStarts = Config.Bind("Pose playback", "Turning starts", true, "Pick the grunt's turning starts (turn, then run out) when the controller is about to turn the body as it sets off; the clip follows the body's turn.");
            _turnInPlace = Config.Bind("Pose playback", "Turn in place", false, "Play Valve's turn-in-place clips (22, 90, 180 degrees) matched to the controller's own turn while a bot stands.");
            _sprintLegs = Config.Bind("Pose playback", "Sprint legs", 1, new ConfigDescription("Legs while sprinting: 0 Tarkov's animator (bridged by the Alyx transitions), 1 Tarkov's own sprint cycles on the placer.", new AcceptableValueRange<int>(0, 1)));
            _runBandLegs = Config.Bind("Pose playback", "Run band legs", 1, new ConfigDescription("Legs for forward movement: 0 brisk walk (heavy walk loop sped up), 1 Tarkov walk (default; with refinement, also used while accelerating and at walking speeds), 2 Alyx run and sprint loops. Strafes retain their directional selection.", new AcceptableValueRange<int>(0, 2)));
            _repickStart = Config.Bind("Pose playback", "Re-pick start direction", true, "Allow one clip correction when a start fires while the body is still turning.");
            _inertialize = Config.Bind("Pose playback", "Inertialize transitions", true, "Decay the pose difference at every switch instead of cross-fading between two poses (no snap).");
            InitializeFidgets();
            try
            {
                _patches.Enable();
                _puppetPatches.Enable();
                Camera.onPreCull += OnPreCull;
                DebugDraw.Install();
                _hooksReady = true;
                Logger.LogInfo($"{ModInfo.Name} {ModInfo.Version}: automatic player-test mode ready.");
                Logger.LogInfo($"Runtime: Unity {Application.unityVersion}; game {Application.version}; Assembly-CSharp MVID {typeof(Player).Assembly.ManifestModule.ModuleVersionId}; SPT reflection {typeof(SPT.Reflection.Patching.ModulePatch).Assembly.GetName().Version}.");
            }
            catch (Exception ex)
            {
                _patches.Disable();
                _puppetPatches.Disable();
                _enabled.Value = false;
                Report("Could not install diagnostic hooks. See BepInEx log.");
                Logger.LogError(ex);
            }
        }

        private void Update()
        {
            TickFidgets();
            try
            {
                PollSave();
                PollRaidReports();
                DebugDraw.Enabled = _enabled.Value && _overlay.Value && _debugDraw.Value;
                var world = Singleton<GameWorld>.Instance;
                if (world != _world)
                {
                    if (_fleet != null) StopFleet("Raid changed");
                    End("Raid changed");
                    Selected = null;
                    _world = world;
                    _raidAttempted = false;
                }
                if (!_enabled.Value || _world == null || _world.MainPlayer == null || _world.MainPlayer.HealthController == null || !_world.MainPlayer.HealthController.IsAlive)
                {
                    if (_fleet != null) StopFleet("Disabled or raid unavailable");
                    if (_capture != null || _route != null || _puppet != null) End("Disabled or raid unavailable");
                    return;
                }
                TickRaidReports();
                if (_developerControls.Value && _stop.Value.IsDown()) { if (_fleet != null) StopFleet("Stopped by user"); End("Stopped by user"); return; }
                if (_developerControls.Value && _select.Value.IsDown()) SelectNearest();
                if ((_capture != null && Selected == null) || (Selected != null && (!Selected.IsAI || Selected.HealthController == null || !Selected.HealthController.IsAlive || Selected.AIData?.BotOwner == null)))
                {
                    End("Selected bot unavailable");
                    Selected = null;
                }
                if (_developerControls.Value && _observe.Value.IsDown()) StartCapture(false);
                if (_developerControls.Value && _walk.Value.IsDown()) StartCapture(true);
                if (_developerControls.Value && _puppetKey.Value.IsDown()) Report(StartPuppet(_puppetScenario.Value, _puppetFreeze.Value, _puppetBring.Value, false, _footLockConfig.Value, _poseClip.Value, _bodyLeanConfig.Value));
                if (_developerControls.Value && _footLockToggle.Value.IsDown() && (_footLock != null || _pose != null))
                {
                    if (_pose != null)
                    {
                        _pose.Enabled = !_pose.Enabled;
                        Report("Pose playback " + (_pose.Enabled ? "ON" : "OFF") + ".");
                    }
                    else
                    {
                        _footLock.Active = !_footLock.Active;
                        Report("Foot lock " + (_footLock.Active ? "ON" : "OFF") + ".");
                    }
                }
                TickObservePending();
                if (_fleet != null)
                {
                    try
                    {
                        _fleet.Tick(_world);
                        if (_raidRecorder == null && Time.realtimeSinceStartup - _fleetStarted >= _fleetSeconds) StopFleet("Fleet duration reached");
                    }
                    catch (Exception ex) { Logger.LogError(ex); StopFleet("Fleet stopped after an error; see log"); }
                }
                if (_capture == null) return;
                _route?.Tick();
                _puppet?.Tick();
                TickReactionTest();
                TickReactionShowcase();
                if (_faceBot) _puppet?.FaceViewer(_world.MainPlayer);
                float elapsed = Time.realtimeSinceStartup - _started;
                if (_route?.IsComplete == true) End("Route completed");
                else if (_puppet?.IsComplete == true) End(_puppet.Failure == null ? "Puppet scenario completed" : "Puppet stopped: " + _puppet.Failure);
                else if (_capture.IsFull) End("Capture buffer limit reached");
                else if (_puppet == null && elapsed >= (_observeSeconds ?? _seconds.Value)) End("Capture duration reached");
                else if (_puppet != null && elapsed >= PuppetMaximumSeconds) End("Puppet time limit reached");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
                if (_fleet != null) StopFleet("Fleet stopped after an update error; see log");
                End("Test stopped after an error; see BepInEx log");
                Report("Test stopped: " + ex.Message);
            }
        }

        private void SelectNearest()
        {
            End("Selection changed");
            Vector3 origin = _world.MainPlayer.Position;
            float rangeSquared = _range.Value * _range.Value;
            Selected = _world.AllAlivePlayersList.AsValueEnumerable()
                .Where(p => p != null && p.IsAI && p.HealthController != null && p.HealthController.IsAlive && p.AIData?.BotOwner != null)
                .Where(p => (p.Position - origin).sqrMagnitude <= rangeSquared)
                .OrderBy(p => (p.Position - origin).sqrMagnitude).FirstOrDefault();
            Report(Selected == null ? "No living bot nearby. Move nearer and press Ctrl+F6." : "Bot selected. Ctrl+F7 observes; Ctrl+F8 runs the walking test.");
        }

        private void StartCapture(bool walk)
        {
            YieldRaidToDeveloper();
            if (!_hooksReady) { Report("Diagnostic hooks are unavailable. Check the log and restart after fixing the error."); return; }
            if (Selected == null) { Report("Select a bot first with Ctrl+F6."); return; }
            if (_capture != null) { Report("A capture is already running. Ctrl+F9 stops it."); return; }
            if (_save != null) { Report("Previous capture is still saving. Try again shortly."); return; }
            // The experiment operates on a local bot. Fika authority/replication is not implemented.
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.core"))
            { Report("This diagnostic build supports local SPT raids; Fika sessions are not supported."); return; }
            if (walk) _route = RouteSession.Start(Selected, _legLength.Value, _moveSpeed.Value);
            _capture = new DiagnosticCapture(Selected, Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput", ModInfo.Name), 60000);
            _footLock = _footLockConfig.Value ? FootLock.Create(Selected) : null;
            _bodyLean = _bodyLeanConfig.Value ? BodyLean.Create(Selected) : null;
            if (_bodyLean != null)
            {
                _bodyLean.Strength = _bodyLeanStrength.Value;
                _bodyLean.KeepWeaponLevel = _bodyLeanWeaponLevel.Value;
            }
            var references = Selected?.Grounder?.ik?.references;
            _hands = references == null ? null : new[] { references.leftHand, references.rightHand, references.pelvis?.parent };
            _capture.PoseProbe = ProbePose;
            // the live bot keeps its own brain: the clips follow whatever drives it (stock mover, SAIN) through the adapter
            string poseNote = walk ? AttachPose(null, null, _footLockConfig.Value) : AttachPose(_poseClip.Value, MoveIntent.ForBot(Selected), _footLockConfig.Value);
            _started = Time.realtimeSinceStartup;
            _status = (walk ? "Walking test recording." : "Recording normal AI movement" + poseNote + ".") + " Ctrl+F9 finishes.";
            Report(_status);
        }

        // observation through the test API: nearest living bot anywhere, this many seconds, the configured clip set
        internal string StartObserve(float seconds, string poseClip, bool bodyLean)
        {
            if (!_hooksReady) return "Diagnostic hooks are unavailable.";
            if (_world == null || _world.MainPlayer == null) return "No raid is running.";
            if (_capture != null) return "A capture is already running. Stop it first.";
            if (_save != null) return "Previous capture is still saving. Try again shortly.";
            _observeSeconds = Mathf.Clamp(seconds, 5f, 600f);
            _bodyLeanConfig.Value = bodyLean;
            if (poseClip != null) _poseClip.Value = poseClip;
            var moving = MovingBot();
            if (moving == null)
            {
                // two observe captures recorded a bot standing for its whole window; wait for one to walk a path
                _observePending = Time.realtimeSinceStartup + ObserveWaitSeconds;
                return "Observing: waiting up to " + ObserveWaitSeconds + " s for a bot to start moving.";
            }
            Selected = moving;
            StartCapture(false);
            return _capture != null ? "Observing: " + _status : "Observation did not start: " + _status;
        }
        private float? _observeSeconds;
        private float? _observePending;
        private const float ObserveWaitSeconds = 180f;

        private void TickObservePending()
        {
            if (_observePending == null || _capture != null || _save != null) return;
            if (_world == null || _world.MainPlayer == null) { _observePending = null; return; }
            var moving = MovingBot();
            if (moving != null)
            {
                _observePending = null;
                Selected = moving;
                StartCapture(false);
                Report(_capture != null ? "Observing: " + _status : "Observation did not start: " + _status);
            }
            else if (Time.realtimeSinceStartup > _observePending.Value)
            {
                _observePending = null;
                Report("Observation cancelled: no bot started moving within " + ObserveWaitSeconds + " s.");
            }
        }

        internal string StartPuppet(string scenario, bool freezeOthers, bool bringToPlayer, bool faceBot, bool footLock, string poseClip, bool bodyLean)
        {
            YieldRaidToDeveloper();
            if (!_hooksReady) return "Diagnostic hooks are unavailable.";
            if (_world == null || _world.MainPlayer == null) return "No raid is running.";
            if (_capture != null) return "A capture is already running. Stop it first.";
            if (_save != null) return "Previous capture is still saving. Try again shortly.";
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.core")) return "Fika sessions are not supported.";
            try { PuppetScript.Parse(scenario); }
            catch (ArgumentException ex) { return ex.Message; }

            if (!IsUsableBot(Selected))
            {
                // bringing the bot over makes distance irrelevant, so take the nearest anywhere on the map
                Selected = NearestBot(bringToPlayer ? float.MaxValue : _range.Value);
            }
            if (Selected == null) return "No living bot available.";
            try
            {
                _puppet = PuppetSession.Start(Selected, _world.MainPlayer, scenario, freezeOthers, bringToPlayer);
            }
            catch (Exception ex)
            {
                _puppet = null;
                Logger.LogError(ex);
                return ex.Message;
            }
            _faceBot = faceBot;
            _capture = new DiagnosticCapture(Selected, Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput", ModInfo.Name), PuppetMaximumSamples);
            _footLock = footLock ? FootLock.Create(Selected) : null;
            _bodyLean = bodyLean ? BodyLean.Create(Selected) : null;
            var lockRequested = footLock;
            if (_bodyLean != null)
            {
                _bodyLean.Strength = _bodyLeanStrength.Value;
                _bodyLean.KeepWeaponLevel = _bodyLeanWeaponLevel.Value;
            }
            var references = Selected?.Grounder?.ik?.references;
            _hands = references == null ? null : new[] { references.leftHand, references.rightHand, references.pelvis?.parent };
            _capture.PoseProbe = ProbePose;
            string poseNote = AttachPose(poseClip, MoveIntent.FromPuppet(_puppet), footLock);
            _started = Time.realtimeSinceStartup;
            LastEndReason = null;
            string leanNote = bodyLean ? (_bodyLean != null ? " with body lean" : " (body lean unavailable: spine bones missing)") : "";
            _status = "Puppet running: " + scenario + (footLock ? (_footLock != null ? " with foot lock" : " (foot lock unavailable: leg bones missing)") : "") + poseNote + leanNote + ". Ctrl+F9 releases the bot.";
            return _status;
        }

        // the Alyx layer for one bot: playback fed by whichever driver moves it, the placer on its legs, and Tarkov's
        // own legs still foot-locked in the phases the clips hand back. shared by the puppet and live observation
        private string AttachPose(string poseClip, MoveIntent intent, bool lockRequested)
        {
            _pose = null;
            _placer = null;
            if (string.IsNullOrEmpty(poseClip))
                return "";
            string error = null;
            var db = LoadPoseDatabase(ref error);
            _pose = db == null ? null : PosePlayback.Create(Selected, db, poseClip, intent, out error);
            if (_pose != null)
            {
                SpeedRampPatch.Attach(Selected); BotInertiaPatch.Attach(Selected);
                ConfigurePlayback(_pose);
                AttachReactions();
                SprintInertia.Attach(Selected, () => _pose != null && _pose.Enabled);
            }
            string poseNote = _pose != null ? " with pose " + _pose.ClipName : " (pose playback unavailable: " + error + ")";
            if (_pose != null && _footPlacerConfig.Value)
            {
                string placerError;
                _placer = FootPlacer.Create(Selected, db, out placerError);
                if (_placer != null && Selected.PlayerBones != null) { _placer.WeaponMount = Selected.PlayerBones.Weapon_Root_Third; _placer.WeaponFollower = Selected.PlayerBones.Weapon_Root_Anim; }
                if (_placer != null)
                {
                    // the placer owns stride length and plant positions now: the anchors are where the body goes
                    _pose.StrideWarp = false;
                    _pose.LeadSeconds = 0f;
                    // slide 50: steps march along the driver's path when it has one (the puppet has none)
                    _placer.PathProvider = () => intent.Corners?.Invoke();
                    _placer.HipShift = _hipShift.Value;
                    _placer.KneeBendFloor = _kneeBendFloor.Value;
                    var placer = _placer;
                    _pose.DrawnAnkle = side => placer.LastAnkle(side);
                    _pose.DrawnPlanted = side => placer.LastPlanted(side);
                    _pose.DrawnFootbase = side => placer.LastFootbase(side);
                    _pose.DrawnProgression = side => placer.LastProgression(side);
                    _pose.CurrentPlacementCost = placer.CurrentPlacementCost;
                    _pose.StopSettled = placer.MatchesNativeIdle;
                    // Tarkov's own legs (idle after a stop, sprint) still get the lock, as the old build had
                    if (!lockRequested && _footLock == null)
                        _footLock = FootLock.Create(Selected);
                    poseNote += " and foot placer";
                }
                else poseNote += " (foot placer unavailable: " + placerError + ")";
            }
            return poseNote;
        }

        // the F12 options, applied the same to the selected bot's playback and to every fleet rig
        internal void ConfigurePlayback(PosePlayback pose)
        {
            pose.StrideWarp = _strideWarp.Value;
            pose.SprintTransitions = _sprintTransitions.Value;
            pose.SprintCycleBridge = _sprintCycleBridge.Value;
            pose.RepickStart = _repickStart.Value;
            pose.AlyxCycles = _alyxCycles.Value;
            pose.RunBandLegs = _runBandLegs.Value;
            pose.SprintLegs = _sprintLegs.Value;
            pose.AnimatorPhaseLock = _phaseLockOverride ?? _animatorPhaseLock.Value;
            pose.TurnInPlace = _turnInPlace.Value;
            pose.TurningStarts = _turningStarts.Value;
            pose.Variations = _variations.Value;
            pose.PelvisTiltFromClip = _pelvisTilt.Value;
            pose.ClipDrivenStarts = _clipDrivenStarts.Value;
            pose.PreferGrunt = _preferGrunt.Value;
            pose.WalkFamily = _walkFamily.Value;
            pose.Inertialize = _inertialize.Value;
        }

        // the layer on every live bot, streamed to one file; runs beside nothing else (no puppet, no capture)
        internal string StartFleet(float seconds, string poseClip, int maxBots) => StartFleet(seconds, poseClip, maxBots, false);

        internal string StartFleet(float seconds, string poseClip, int maxBots, bool control)
        {
            YieldRaidToDeveloper();
            if (!_hooksReady) return "Diagnostic hooks are unavailable.";
            if (_world == null || _world.MainPlayer == null) return "No raid is running.";
            if (_capture != null || _puppet != null) return "A capture or puppet is running. Stop it first.";
            if (_fleet != null) return "Fleet is already running.";
            string error = null;
            var db = LoadPoseDatabase(ref error);
            if (db == null) return "Fleet did not start: " + error;
            if (!string.IsNullOrEmpty(poseClip)) _poseClip.Value = poseClip;
            var fleet = new Fleet(this, db, _poseClip.Value, Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput", ModInfo.Name), 2);
            fleet.MaxBots = Mathf.Clamp(maxBots, 1, 30);
            fleet.Control = control;
            string dbPath = Path.Combine(Path.GetDirectoryName(Info.Location), _poseDatabasePath.Value);
            fleet.WriteMeta(_world.LocationId, FileHash(Info.Location), FileHash(dbPath),
                "hipShift=" + _hipShift.Value + " pelvisTilt=" + _pelvisTilt.Value + " preferGrunt=" + _preferGrunt.Value + " clipDrivenStarts=" + _clipDrivenStarts.Value + " accel=" + SpeedRampPatch.LimitPerSecond + " inertia=" + BotInertiaPatch.Acceleration + " lean=" + _bodyLeanConfig.Value + " runBand=" + _runBandLegs.Value + " sprintLegs=" + _sprintLegs.Value + " walkFamily=" + _walkFamily.Value
                + " variations=" + _variations.Value + " phaseLock=" + (_phaseLockOverride ?? _animatorPhaseLock.Value) + " turningStarts=" + _turningStarts.Value + " turnInPlace=" + _turnInPlace.Value + " kneeBendFloor=" + _kneeBendFloor.Value + " sprintInertia=" + SprintInertia.Enabled);
            _fleet = fleet;
            _fleetSeconds = Mathf.Clamp(seconds, 10f, 3600f);
            _fleetStarted = Time.realtimeSinceStartup;
            LastEndReason = null;
            Report("Fleet running: every live bot " + (control ? "recorded on Tarkov's own legs (control)" : "gets the Alyx layer") + " for " + _fleetSeconds.ToString("0") + " s, streaming to " + fleet.Path);
            return _status;
        }

        internal string StopFleet(string reason)
        {
            var fleet = _fleet;
            if (fleet == null) return "No fleet is running.";
            bool automatic = _raidRecorder != null;
            _fleet = null;
            string path;
            try { path = fleet.Stop(reason); }
            catch (Exception ex) { Logger.LogError(ex); path = fleet.Path; }
            FinishRaidReport(reason);
            LastEndReason = reason;
            if (!automatic) LastCapturePath = path;
            Report("Fleet stopped: " + reason + ". " + fleet.Samples + " samples in " + path);
            return _status;
        }

        internal bool FleetRunning => _fleet != null;

        internal void SetPhaseLockForTest(bool enabled)
        {
            _phaseLockOverride = enabled;
            if (_pose != null) _pose.AnimatorPhaseLock = enabled;
            _fleet?.SetPhaseLockForTest(enabled);
        }

        private static string FileHash(string path)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    var hash = sha.ComputeHash(stream);
                    var sb = new System.Text.StringBuilder(16);
                    for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }

        // per-sample playback state; the analyzer needs the clip's own contact windows to judge sliding
        private PoseProbeSnapshot ProbePose()
        {
            var pose = _pose;
            var probe = new PoseProbeSnapshot();
            probe.SprintInertia = SprintInertia.Probe(Selected);
            probe.SprintEntry = SprintEntrySnapshot.Capture(Selected, _puppet?.SprintRequested);
            if (pose != null)
            {
                bool left, right;
                pose.TryGetContacts(out left, out right);
                probe.Active = pose.Enabled;
                probe.Phase = pose.PhaseName;
                probe.Driver = pose.DriverName;
                probe.Clip = pose.ActiveClip;
                probe.UpperOverlay = pose.UpperOverlay;
                probe.Grip = pose.ReadGripProbe();
                probe.UpperOverlayFrame = pose.UpperOverlayFrame;
                probe.Frame = pose.Frame;
                probe.Weight = pose.Weight;
                probe.Rate = pose.Rate;
                probe.ContactL = left;
                probe.ContactR = right;
                probe.PathBehind = pose.PathBehind;
                probe.Sync = pose.ReadSync();
            }
            var lean = _bodyLean;
            if (lean != null)
            {
                probe.LeanPitch = lean.PitchDegrees;
                probe.LeanRoll = lean.RollDegrees;
                probe.AimShift = lean.AimShiftDegrees;
            }
            var hands = _hands;
            if (hands != null && hands[0] && hands[1] && hands[2])
            {
                Vector3 left = hands[2].InverseTransformPoint(hands[0].position);
                Vector3 right = hands[2].InverseTransformPoint(hands[1].position);
                probe.HasHands = true;
                probe.HandL = new[] { left.x, left.y, left.z };
                probe.HandR = new[] { right.x, right.y, right.z };
            }
            var locks = _footLock;
            if (locks != null)
            {
                Vector3 anchor;
                probe.LockedL = locks.TryGetAnchor(0, out anchor);
                probe.LockedR = locks.TryGetAnchor(1, out anchor);
            }
            var placer = _placer;
            if (placer != null)
            {
                probe.LockedL |= placer.IsAnchored(0);
                probe.LockedR |= placer.IsAnchored(1);
                probe.PlacerL = placer.Probe(0);
                probe.PlacerR = placer.Probe(1);
            }
            return probe;
        }

        internal string DumpSkeleton()
        {
            if (_world == null || _world.MainPlayer == null) return "No raid is running.";
            var bot = IsUsableBot(Selected) ? Selected : NearestBot(float.MaxValue);
            if (bot == null) return "No living bot available.";
            try { return SkeletonDump.Write(bot, Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput", ModInfo.Name)); }
            catch (Exception ex) { Logger.LogError(ex); return "Skeleton dump failed: " + ex.Message; }
        }

        internal string StopFromApi()
        {
            if (_observePending != null && _capture == null) { _observePending = null; _observeSeconds = null; return "Stopped (observation was still waiting for a moving bot)."; }
            if (_capture == null && _route == null && _puppet == null) return "Nothing is running.";
            End("Stopped by test API");
            return "Stopped.";
        }

        internal object DescribeStatus()
        {
            int aliveBots = 0;
            if (_world != null)
                foreach (var p in _world.AllAlivePlayersList)
                    if (IsUsableBot(p)) aliveBots++;
            return new
            {
                ModVersion = ModInfo.Version,
                HooksReady = _hooksReady,
                Reactions = ReactionStatus(),
                InRaid = _world != null && _world.MainPlayer != null,
                AliveBots = aliveBots,
                SelectedRole = Selected?.Profile?.Info?.Settings?.Role.ToString(),
                SelectedBot = Selected == null ? null : Selected.ProfileId,
                Capturing = _capture != null,
                CaptureSeconds = _capture == null ? 0f : Time.realtimeSinceStartup - _started,
                CaptureSamples = _capture == null ? 0 : _capture.SampleCount,
                Saving = _save != null,
                Puppet = _puppet == null ? null : new { _puppet.Scenario, _puppet.StepIndex, _puppet.StepCount, _puppet.CurrentStep, _puppet.IsComplete, _puppet.Failure },
                FootLock = _footLock != null,
                FootPlacer = _placer != null,
                Pose = _pose == null ? null : new { Clip = _pose.ClipName, _pose.Enabled, _pose.Weight, _pose.Rate, Phase = _pose.PhaseName },
                LastEndReason,
                LastCapturePath,
                Status = _status
            };
        }

        // bosses carry their own speed limits (Tagilla sat at StateSpeedLimit 0.33), so they skew speed legs
        private static bool IsBossOrFollower(Player p)
        {
            var owner = p.AIData?.BotOwner;
            return owner != null && ((owner.Boss != null && owner.Boss.IamBoss) || (owner.BotFollower != null && owner.BotFollower.HaveBoss));
        }

        private static bool IsUsableBot(Player p)
        {
            // a released puppet can drop prone under its AI; the puppet refuses prone bots, so skip them here
            return p != null && p.IsAI && p.HealthController != null && p.HealthController.IsAlive && p.AIData?.BotOwner != null && p.AIData.BotOwner.BotState == EBotState.Active && !p.IsInPronePose;
        }

        // the nearest bot that is walking a path of its own (a standing bot records 45 s of nothing)
        private Player MovingBot()
        {
            Vector3 origin = _world.MainPlayer.Position;
            Player best = null;
            float bestDistance = float.MaxValue;
            foreach (var p in _world.AllAlivePlayersList)
            {
                if (!IsUsableBot(p) || IsBossOrFollower(p)) continue;
                // velocity, not the mover's flags: SAIN drives Player.Move with the stock mover paused
                if (p.Velocity.sqrMagnitude < 0.25f) continue;
                float distance = (p.Position - origin).sqrMagnitude;
                if (distance < bestDistance) { bestDistance = distance; best = p; }
            }
            return best;
        }

        private Player NearestBot(float range)
        {
            Vector3 origin = _world.MainPlayer.Position;
            Player best = null;
            float bestDistance = range == float.MaxValue ? float.MaxValue : range * range;
            foreach (var p in _world.AllAlivePlayersList)
            {
                if (!IsUsableBot(p) || IsBossOrFollower(p)) continue;
                float distance = (p.Position - origin).sqrMagnitude;
                if (distance <= bestDistance) { bestDistance = distance; best = p; }
            }
            return best;
        }

        // runs from the VisualPass postfix: record the game's pose, then correct, then record again
        // VisualPass prefix: the animator has written the pose; playback overrides legs before EFT's IK runs
        internal void BeforeVisual(Player player)
        {
            if (_fleet != null && _fleet.BeforeVisual(player)) return;
            if (player != Selected) return;
            if (_capture != null) Record(player, "before_visual");
            if (_pose != null)
            {
                try { _pose.Apply(); ResolveReactionRequest(); SpeedRampPatch.SetCeiling(Selected, _clipDrivenStarts.Value ? _pose.SpeedCeiling : null); BotInertiaPatch.SetCeiling(Selected, _clipDrivenStarts.Value ? _pose.VelocityCeiling : null); BotInertiaPatch.SetDrive(Selected, _pose.VelocityDrive); BotInertiaPatch.SetRootDrive(Selected, _pose.ReactionRootDrive); }
                catch (Exception ex) { Logger.LogError(ex); _pose = null; MovementCleanup.Detach(Selected); Report("Pose playback disabled after an error; see log."); return; }
            }
            if (_bodyLean != null)
            {
                try { _bodyLean.Apply(); }
                catch (Exception ex) { Logger.LogError(ex); _bodyLean = null; Report("Body lean disabled after an error; see log."); }
            }
            if (_pose == null) return;
            if (_capture != null) Record(player, "after_pose");
        }

        private PoseDatabase LoadPoseDatabase(ref string error)
        {
            string path = Path.Combine(Path.GetDirectoryName(Info.Location), _poseDatabasePath.Value);
            if (!File.Exists(path)) { error = "database not found at " + path; return null; }
            // reload when the file changes so a fresh export needs no game restart
            string stamp = File.GetLastWriteTimeUtc(path).Ticks.ToString();
            if (_poseDatabase != null && stamp == _poseDatabaseStamp) return _poseDatabase;
            try
            {
                _poseDatabase = PoseDatabase.Load(path);
                _poseDatabaseStamp = stamp;
                return _poseDatabase;
            }
            catch (Exception ex) { Logger.LogError(ex); error = ex.Message; return null; }
        }

        internal void AfterVisual(Player player)
        {
            if (_fleet != null && _fleet.AfterVisual(player)) return;
            if (_capture == null || player != Selected) return;
            Record(player, "after_visual");
            bool placed = false;
            if (_placer != null)
            {
                try { placed = _placer.Apply(_pose); }
                catch (Exception ex) { Logger.LogError(ex); _placer = null; Report("Foot placer disabled after an error; see log."); }
            }
            if (!placed && _footLock != null)
            {
                // the lock only ever sees Tarkov's legs now: the placer handles every frame a clip is showing
                try { _footLock.Apply(player.BodyAnimatorCommon.GetFloat(FootStepHash), _pose); }
                catch (Exception ex) { Logger.LogError(ex); _footLock = null; Report("Foot lock disabled after an error; see log."); return; }
            }
            bool upperBody = false;
            try { upperBody = _pose != null && _pose.ApplyReactionUpperBody(); }
            catch (Exception ex) { Logger.LogError(ex); _pose?.CancelReaction(); Report("Reaction upper-body error; see log."); }
            if (placed || upperBody || _footLock != null) Record(player, "after_lock");
        }

        // the first camera cull of a frame comes after every LateUpdate, so this is what actually renders
        private void OnPreCull(Camera camera)
        {
            if (Time.frameCount != _raidRenderFrame)
            {
                _raidRenderFrame = Time.frameCount;
                try { _fleet?.PreRender(); }
                catch (Exception ex) { Logger.LogError(ex); }
            }
            if (_capture == null || Selected == null || Time.frameCount == _preRenderFrame) return;
            _preRenderFrame = Time.frameCount;
            try { Record(Selected, "pre_render"); }
            catch (Exception ex) { Logger.LogError(ex); }
        }

        internal void Record(Player player, string stage)
        {
            if (_capture == null || player != Selected) return;
            try { _capture.Record(stage); }
            catch (Exception ex) { Logger.LogError(ex); End("Capture failed; bot released"); }
        }

        internal bool Owns(BotOwner owner) => _route != null && _route.Owner == owner;

        private void End(string reason)
        {
            if (_pose != null) { _lastReactionStarts = _pose.ReactionStarts; _lastUpperBodyFrames = _pose.UpperBodyReactionFrames; _lastReactionResult = _pose.ReactionResult; _lastUpperHitStarts = _pose.UpperHitStarts; _lastLandingStarts = _pose.LandingStarts; }
            // a pending observe request must not outlive the run it belonged to (review: Ctrl+F9 left it armed)
            _observePending = null;
            _observeSeconds = null;
            var puppet = _puppet;
            _puppet = null;
            _faceBot = false;
            PuppetReport? puppetReport = null;
            try { puppet?.Dispose(); puppetReport = puppet?.Report; }
            catch (Exception ex) { Logger.LogError($"Puppet cleanup: {ex}"); }
            var route = _route;
            _route = null; // Release patch ownership before restoring the mover.
            RouteSummary? summary = route?.Summary;
            try { route?.Dispose(); }
            catch (Exception ex) { Logger.LogError($"Route cleanup: {ex}"); }
            string speed = "";
            if (summary.HasValue)
            {
                var s = summary.Value;
                speed = s.HasMeasuredSpeed
                    ? $" Route speed: requested {s.RequestedMoveSpeed:F2}, player speed {s.SteadyPlayerSpeed:F2}, reached {s.SteadySpeedMetersPerSecond:F2} m/s steady ({s.MeanSpeedMetersPerSecond:F2} overall), {s.SpeedOverrides} foreign speed writes."
                    : $" Route speed: requested {s.RequestedMoveSpeed:F2}, route too short to measure.";
            }
            var capture = _capture;
            _capture = null;
            if (capture == null) { if (speed.Length > 0) Logger.LogInfo(speed.Trim()); return; }
            if (summary.HasValue) capture.SetRouteSummary(summary.Value);
            if (puppetReport.HasValue) capture.SetPuppetReport(puppetReport.Value);
            capture.SetFootLockSummary(new FootLockSummary { Enabled = _footLock != null, Locks = _footLock?.Locks ?? 0, DriftReleases = _footLock?.DriftReleases ?? 0, ReachReleases = _footLock?.ReachReleases ?? 0 });
            _footLock = null;
            capture.SetFootPlacerSummary(new FootPlacerSummary { Enabled = _placer != null, AppliedFrames = _placer?.AppliedFrames ?? 0, CycleChanges = _placer?.CycleChanges ?? 0, ReachClamps = _placer?.ReachClamps ?? 0, PeakResidual = _placer?.PeakResidual ?? 0f, PeakStepShift = _placer?.PeakStepShift ?? 0f, AnchorFailures = _placer?.AnchorFailures ?? 0, PeakHipShift = _placer?.PeakHipShift ?? 0f, ClearanceLimits = _placer?.ClearanceLimits ?? 0 });
            _placer = null;
            capture.SetPosePlaybackSummary(new PosePlaybackSummary { Enabled = _pose != null, Clip = _pose?.ClipName, AppliedFrames = _pose?.AppliedFrames ?? 0, HurriedFrames = _pose?.HurriedFrames ?? 0, MaxRate = _pose?.PeakRate ?? 0f, Events = _pose == null ? null : new System.Collections.Generic.List<string>(_pose.Events).ToArray() });
            capture.SetBodyLeanSummary(new BodyLeanSummary { Enabled = _bodyLean != null, AppliedFrames = _bodyLean?.AppliedFrames ?? 0, PeakPitch = _bodyLean?.PeakPitch ?? 0f, PeakRoll = _bodyLean?.PeakRoll ?? 0f, PeakAimShift = _bodyLean?.PeakAimShift ?? 0f });
            _pose = null;
            MovementCleanup.Detach(Selected);
            _bodyLean = null;
            LastEndReason = reason;
            _observeSeconds = null;
            _save = capture.FinishAsync(reason);
            Report(reason + "." + speed + " Saving capture automatically.");
        }

        private void PollSave()
        {
            if (_save == null || !_save.IsCompleted) return;
            var save = _save;
            _save = null;
            if (save.IsFaulted) { Logger.LogError(save.Exception); Report("Capture save failed. See BepInEx log."); }
            else if (save.IsCanceled) Report("Capture save was canceled.");
            else { LastCapturePath = save.Result; Report("Capture saved: " + save.Result); }
        }

        private void Report(string text) { _status = text; Logger.LogInfo(text); }

        private void OnDisable() { StopFidget(); if (_fleet != null) StopFleet("Plugin disabled"); End("Plugin disabled"); }
        private void OnDestroy()
        {
            StopFidget();
            _fidgetPatches.Disable();
            if (_fleet != null) StopFleet("Plugin unloaded");
            End("Plugin unloaded");
            _patches.Disable();
            _puppetPatches.Disable();
            Camera.onPreCull -= OnPreCull;
            SprintInertia.DetachAll();
            DebugDraw.Uninstall();
            Instance = null;
        }

        private void OnGUI()
        {
            if (_overlay == null || !_overlay.Value || !_enabled.Value || _world == null) return;
            GUI.Box(new Rect(18, 18, 610, 150), GUIContent.none);
            GUI.Label(new Rect(30, 24, 585, 138), ModInfo.Name + " â€” diagnostics\n" +
                $"{_select.Value}: select  |  {_observe.Value}: observe  |  {_walk.Value}: walk  |  {_stop.Value}: stop\n" +
                (_puppet != null ? $"Puppet step {_puppet.StepIndex + 1}/{_puppet.StepCount}: {_puppet.CurrentStep}  ({Time.realtimeSinceStartup - _started:F1}s)\n"
                    : _capture != null ? $"Recording {Time.realtimeSinceStartup - _started:F1}s / {_seconds.Value:F0}s\n" : "") + _status);
            if (Selected == null) return;
            if (_reactionsEnabled)
                GUI.Label(new Rect(30, 170, 850, 48), $"HIT REACTIONS: {_pose?.ReactionStarts ?? _lastReactionStarts} played / {_reactionPolicy.Accepted} triggers   {_pose?.ReactionResult ?? _lastReactionResult}\n{_reactionNote}");
            var camera = Camera.main;
            if (camera == null) return;
            DrawMarker(camera, Selected.Position + Vector3.up * 2f, _footLock == null ? "TEST BOT" : "TEST BOT  lock " + (_footLock.Active ? "ON" : "OFF"), Color.yellow);
            for (int leg = 0; _footLock != null && leg < 2; leg++)
                if (_footLock.TryGetAnchor(leg, out Vector3 anchor)) DrawMarker(camera, anchor, "PIN", Color.green);
            var grounder = Selected.Grounder;
            if (grounder?.ik?.solver != null)
            {
                var left = grounder.ik.solver.leftFootEffector.bone;
                var right = grounder.ik.solver.rightFootEffector.bone;
                if (left != null) DrawMarker(camera, left.position, "L", Color.cyan);
                if (right != null) DrawMarker(camera, right.position, "R", Color.magenta);
            }
            if (_route == null) return;
            for (int i = 0; i < _route.Points.Length; i++)
            {
                DrawMarker(camera, _route.Points[i], (i + 1).ToString(), Color.green);
                if (i > 0) DrawSegment(camera, _route.Points[i - 1], _route.Points[i]);
            }
        }

        private static void DrawSegment(Camera camera, Vector3 from, Vector3 to)
        {
            Vector3 a = camera.WorldToScreenPoint(from), b = camera.WorldToScreenPoint(to);
            if (a.z <= 0f || b.z <= 0f) return;
            Vector2 start = new Vector2(a.x, Screen.height - a.y);
            Vector2 delta = new Vector2(b.x - a.x, a.y - b.y);
            Matrix4x4 savedMatrix = GUI.matrix;
            Color savedColor = GUI.color;
            try
            {
                GUI.color = Color.green;
                GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, start);
                GUI.DrawTexture(new Rect(start.x, start.y, delta.magnitude, 2f), Texture2D.whiteTexture);
            }
            finally { GUI.matrix = savedMatrix; GUI.color = savedColor; }
        }

        private static void DrawMarker(Camera camera, Vector3 world, string label, Color color)
        {
            Vector3 screen = camera.WorldToScreenPoint(world);
            if (screen.z <= 0f) return;
            Color saved = GUI.color;
            GUI.color = color;
            GUI.Label(new Rect(screen.x - 10, Screen.height - screen.y - 10, 90, 24), "+ " + label);
            GUI.color = saved;
        }
    }
}
