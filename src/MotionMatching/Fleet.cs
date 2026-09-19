using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Comfort.Common;
using EFT;
using ZLinq;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // the Alyx layer on one live bot under its own AI: playback, placer, lock for the phases the clips hand back,
    // body lean, the driver adapter. the fleet keeps one per bot; nothing here is shared between bots
    internal sealed class BotRig
    {
        public Player Player;
        public int Id;
        public int AnonymousId;
        public string Role;
        public PosePlayback Pose;
        public FootPlacer Placer;
        public FootLock Lock;
        public BodyLean Lean;
        public MoveIntent Intent;
        public int EventsWritten;
        public float AttachedAt;
        public bool Failed;
        public bool Suspended;
        public float NextSample;
        public MotionSyncProbe SyncProbe;
        public MotionSyncProbeSnapshot NativeSync;
        public int NativeSyncFrame = -1;
        public HitReactionPolicy ReactionPolicy;
        public bool HasReactionDatabase;
        public int ReactionStartsWhenRequested;
        public int DamageEvents;
        public bool Detailed;
        public bool CaptureThisFrame;
        public bool ReplayTriggerThisFrame;
        public float CarryWeightKg = float.NaN;
        public float NextWeightSampleAt;
        public float LastReportedWeightKg = float.NaN;
        public string LastClip, LastPhase, LastUpperOverlay;
        public Transform[] ReplayBones;
        public Transform ReplayRoot;
        public ReplayPoseSnapshot BeforeVisual, AfterVisual, AfterLock, PreviousAfterLock;
        public int ReplayFrame = -1;
        public float ReplayTime;
        public Dictionary<string, float> LastAnomalyAt;
        public bool HasPreviousReplay;
        public CheapPoseProbe PreviousCheap;
        public bool HasPreviousCheap;
        public float LastVisibleAt;
    }

    internal struct CheapPoseProbe
    {
        public float Time;
        public bool Visible, Simplified;
        public Vector3 Root, Velocity;
        public Vector3 LeftHip, RightHip, LeftKnee, RightKnee, LeftFoot, RightFoot;
        public Quaternion Spine1, Spine2, Spine3, Chest;
        public bool HasHips, HasKnees, HasFeet, HasSpine;
    }

    internal sealed class ReplayPoseSnapshot
    {
        public int Frame;
        public float Time;
        public Vector3 Root, SkeletonRoot, Velocity;
        public Quaternion SkeletonRotation;
        public float Yaw, Speed, PoseLevel;
        public bool Visible, Simplified, Sprinting, Suspended;
        public Vector3[] BonePositions;
        public Quaternion[] BoneRotations;
        public bool[] BoneAvailable;
        public string Phase, Clip, Driver, UpperOverlay;
        public float ClipFrame, Weight, Rate, BlendWeight, OverlayFrame;
        public string BlendClip;
        public string ReactionResult;
        public Vector2? ReactionRootDrive;
        public int ReactionStarts, UpperBodyFrames, UpperHitStarts, LandingStarts;
        public MotionSyncProbeSnapshot Sync;
        public bool HasSync;
        public WeaponGripProbe Grip;
        public SprintInertiaProbe Inertia;
        public SprintEntrySnapshot SprintEntry;
        public int DamageEvents, PolicyAccepted, PolicySuppressed;
        public bool PolicyPending;
        public float PolicyChance, PolicyRoll, PolicyRecentDamage, PolicyCooldownRemaining;
        public string PolicyDecision;
    }

    // every live bot at once, recorded to one JSONL stream per raid: the puppet lane can only show what the
    // scripted legs show, and the user wants the failures that real bots produce in combat
    internal sealed class Fleet
    {
        private readonly Plugin _plugin;
        private readonly PoseDatabase _db;
        private readonly string _poseClip;
        private readonly Dictionary<int, BotRig> _rigs = new Dictionary<int, BotRig>();
        private readonly List<int> _gone = new List<int>();
        private readonly TextWriter _writer;
        private readonly RaidReportRecorder _report;
        private readonly bool _automatic;
        private readonly Dictionary<int, int> _anonymousIds = new Dictionary<int, int>();
        private sealed class CulledReaction
        {
            public Player Player;
            public HitReactionPolicy Policy;
        }
        private readonly Dictionary<int, CulledReaction> _culledReactions = new Dictionary<int, CulledReaction>();
        private readonly List<int> _activationRemovals = new List<int>();
        private float _nextActivationScan;
        private float _nextActivationReport;
        private int _nextAnonymousId;
        private readonly StringBuilder _line = new StringBuilder(1024);
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly float _started;
        private float _nextScan;
        private float _nextFlush;
        private float _nextPerf;
        private double _frameMs;
        private int _framesTimed;
        private double _peakMs;
        private int _frameOf = -1;
        private int _decimation;
        private float _nextDetailSelection;
        private float _lastPerfEvent;
        private long _hookTicks, _peakHookTicks, _hookCalls;
        private double _frameMilliseconds;
        private float _peakFrameMilliseconds;
        private long _frameSamples;
        private readonly long[] _hookTicksByStage = new long[4];
        private readonly long[] _hookCallsByStage = new long[4];
        private readonly long[] _frameTimeBuckets = new long[5];
        private static readonly string[] ReplayBoneNames = {
            "Base HumanPelvis", "Base HumanSpine1", "Base HumanSpine2", "Base HumanSpine3", "Base HumanRibcage",
            "Base HumanNeck", "Base HumanHead", "Base HumanLUpperarm", "Base HumanRUpperarm",
            "Base HumanLForearm1", "Base HumanRForearm1", "Base HumanLThigh1", "Base HumanLCalf",
            "Base HumanLFoot", "Base HumanLToe", "Base HumanRThigh1", "Base HumanRCalf", "Base HumanRFoot", "Base HumanRToe",
            "Base HumanLPalm", "Base HumanRPalm"
        };
        private static readonly string[] ReplayBoneKeys = {
            "pelvis", "spine1", "spine2", "spine3", "chest", "neck", "head", "leftUpperArm", "rightUpperArm",
            "leftForearm", "rightForearm", "leftThigh", "leftKnee", "leftFoot", "leftToe", "rightThigh", "rightKnee", "rightFoot", "rightToe",
            "leftHand", "rightHand"
        };
        public readonly string Path;
        public int MaxBots = 12;
        // control raids: the same recording with Tarkov's own legs (no playback, placer or lean), so anomaly
        // rates have a stock baseline measured the same way (review: alternate all-layer and all-stock raids)
        public bool Control;
        private const float MovingSampleSeconds = 0.033f;
        private const float StillSampleSeconds = 0.2f;
        public int Attached => _rigs.Count;
        public int Samples { get; private set; }

        public Fleet(Plugin plugin, PoseDatabase db, string poseClip, string directory, int decimation, RaidReportRecorder recorder = null)
        {
            _plugin = plugin;
            _db = db;
            _poseClip = poseClip;
            _decimation = Mathf.Max(1, decimation);
            _report = recorder;
            _automatic = recorder != null;
            if (_automatic)
            {
                // Normal raids never steer the player's camera or emit the legacy unbounded JSONL stream.
                FollowViewer = false;
                Path = null;
                _writer = TextWriter.Null;
            }
            else
            {
                Directory.CreateDirectory(directory);
                Path = System.IO.Path.Combine(directory, "fleet-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".jsonl");
                _writer = new StreamWriter(Path, false, new UTF8Encoding(false), 1 << 16);
            }
            _started = Time.realtimeSinceStartup;
            _lastPerfEvent = _started;
        }

        public void WriteMeta(string map, string dllHash, string dbHash, string configSummary)
        {
            if (_automatic) return;
            _writer.WriteLine("{\"k\":\"meta\",\"utc\":\"" + DateTime.UtcNow.ToString("o") + "\",\"map\":" + Q(map) + ",\"dll\":" + Q(dllHash) + ",\"db\":" + Q(dbHash)
                + ",\"clearancePolicy\":" + Newtonsoft.Json.JsonConvert.SerializeObject(LegClearance.CapturePolicy)
                + ",\"clipSet\":" + Q(_poseClip) + ",\"decimation\":" + _decimation + ",\"geometrySchema\":1,\"syncSchema\":1,\"sprintInertiaSchema\":1,\"config\":" + Q(configSummary) + ",\"unity\":" + Q(Application.unityVersion) + "}");
        }

        // once a second: rig new usable bots, drop dead or inactive ones
        public void Tick(GameWorld world)
        {
            long started = _automatic ? Stopwatch.GetTimestamp() : 0L;
            try { TickCore(world); }
            finally
            {
                if (_automatic)
                {
                    RecordHookCost(started, 0);
                    RecordFrameTime();
                }
            }
        }

        private void TickCore(GameWorld world)
        {
            float now = Time.realtimeSinceStartup;
            if (!_automatic && now >= _nextFlush)
            {
                _nextFlush = now + 1f;
                _writer.Flush();
            }
            if (!_automatic && now >= _nextPerf && _framesTimed > 0)
            {
                _nextPerf = now + 5f;
                _writer.WriteLine("{\"k\":\"perf\",\"t\":" + F(now - _started) + ",\"bots\":" + _rigs.Count + ",\"msPerFrame\":" + F((float)(_frameMs / _framesTimed)) + ",\"peakMs\":" + F((float)_peakMs) + "}");
                _frameMs = 0; _framesTimed = 0; _peakMs = 0;
            }
            if (world == null) return;
            if (_automatic) UpdateAutomaticActivation(world, now);
            if (now >= _nextScan) FollowBots(world, now);
            _gone.Clear();
            foreach (var kv in _rigs)
            {
                var rig = kv.Value;
                if (!Alive(rig.Player)) { _gone.Add(kv.Key); continue; }
                if (_automatic && now >= rig.NextWeightSampleAt)
                {
                    rig.NextWeightSampleAt = now + 1f;
                    float weight = ReadCarryWeight(rig.Player);
                    if (Finite(weight))
                    {
                        if (Finite(rig.LastReportedWeightKg) && Mathf.Abs(weight - rig.LastReportedWeightKg) >= 0.25f)
                        {
                            _report.Event(new { t = now - _started, b = rig.AnonymousId, type = "carry_weight_change",
                                fromKg = rig.LastReportedWeightKg, toKg = weight });
                            _report.Count("carry_weight_changes", 1);
                        }
                        rig.CarryWeightKg = weight;
                        rig.LastReportedWeightKg = weight;
                    }
                }
                // prone and crouch are not covered by the clips (standing clips on a crouched bot flagged 2100 knee
                // anomalies a minute): the layer stands down while they last and the gap is recorded
                bool prone = rig.Player.IsInPronePose;
                bool crouch = rig.Player.PoseLevel < 0.85f;
                // door kicks, breaches, loot, climbs and the like are whole-body clips of their own (user saw the
                // placer running under a door kick): the layer stands down for them too
                bool interacting = Interacting(rig.Player);
                // Keep playback running while EFT owns jump and landing. Its per-frame native-ownership gate
                // needs to see the release before it can admit a landing overlay.
                bool suspend = prone || crouch || (interacting && !NativeJumpOwnership.Owns(rig.Player));
                if (suspend != rig.Suspended)
                {
                    rig.Suspended = suspend;
                    if (rig.Pose != null) rig.Pose.Enabled = !suspend;
                    string state = suspend ? (prone ? "suspended:prone" : crouch ? "suspended:crouch" : "suspended:interact") : "active";
                    if (_automatic)
                    {
                        _report.Event(new { t = now - _started, b = rig.AnonymousId, type = "coverage", state });
                    }
                else _writer.WriteLine("{\"k\":\"cov\",\"t\":" + F(now - _started) + ",\"b\":" + rig.Id + ",\"state\":" + Q(state) + "}");
                }
            }
            foreach (int id in _gone)
                Detach(id, "dead or inactive");
            if (_automatic)
            {
                UpdateDetailedBots(world.MainPlayer, now);
                return;
            }
            if (now < _nextScan)
            {
                if (_automatic) UpdateDetailedBots(world.MainPlayer, now);
                return;
            }
            _nextScan = now + 1f;
            if (_rigs.Count >= MaxBots)
            {
                if (_automatic) UpdateDetailedBots(world.MainPlayer, now);
                return;
            }
            foreach (var p in world.AllAlivePlayersList)
            {
                if (_rigs.Count >= MaxBots) break;
                if (!Alive(p) || _rigs.ContainsKey(p.GetInstanceID())) continue;
                Attach(p);
            }
            if (_automatic) UpdateDetailedBots(world.MainPlayer, now);
        }

        private void UpdateAutomaticActivation(GameWorld world, float now)
        {
            if (now < _nextActivationScan) return;
            _nextActivationScan = now + .25f;
            Player viewer = world.MainPlayer;
            bool viewerValid = viewer != null && viewer.HealthController != null && viewer.HealthController.IsAlive;
            Vector3 origin = viewerValid ? viewer.Position : Vector3.zero;
            float range = _plugin.AnimationDistanceValue;
            bool visibility = _plugin.AnimationVisibilityValue;
            _activationRemovals.Clear();
            foreach (var pair in _culledReactions)
                if (!Alive(pair.Value.Player)) _activationRemovals.Add(pair.Key);
            foreach (int id in _activationRemovals)
            {
                _culledReactions.Remove(id);
                _anonymousIds.Remove(id);
            }
            _activationRemovals.Clear();
            foreach (var pair in _rigs)
            {
                var rig = pair.Value;
                if (!Alive(rig.Player)) continue; // ordinary teardown below retains its death reason
                float distance = (rig.Player.Position - origin).sqrMagnitude;
                if (rig.Player.IsVisible || distance <= BotActivationPolicy.NearDistance * BotActivationPolicy.NearDistance)
                    rig.LastVisibleAt = now;
                string reason = BotActivationPolicy.IneligibleReason(true, distance, range, rig.Player.IsVisible,
                    rig.Player.UsedSimplifiedSkeleton, viewerValid, now - rig.LastVisibleAt, visibility);
                if (reason != null) _activationRemovals.Add(pair.Key);
            }
            foreach (int id in _activationRemovals) Detach(id, "performance_cull", true);
            if (viewerValid && _rigs.Count < MaxBots)
            {
                var candidates = world.AllAlivePlayersList.AsValueEnumerable()
                    .Where(p => Alive(p) && !_rigs.ContainsKey(p.GetInstanceID())
                        && BotActivationPolicy.IneligibleReason(false, (p.Position - origin).sqrMagnitude, range,
                            p.IsVisible, p.UsedSimplifiedSkeleton, true, 0f, visibility) == null)
                    .OrderBy(p => (p.Position - origin).sqrMagnitude)
                    .Take(MaxBots - _rigs.Count).ToArray();
                foreach (var player in candidates) Attach(player);
            }
            if (now >= _nextActivationReport)
            {
                _nextActivationReport = now + 5f;
                int alive = world.AllAlivePlayersList.AsValueEnumerable().Count(p => Alive(p));
                _report.Event(new { t = now - _started, type = "activation_coverage", alive,
                    active = _rigs.Count, native = Math.Max(0, alive - _rigs.Count), distance = range,
                    cullUnseen = visibility });
            }
        }

        // every live bot, bosses and prone ones included (an "every bot" experiment must not select)
        // the observer stands where it spawned, and half of today's Factory batches measured nothing because every
        // bot was culled out of its view. when fewer than two rigs are visible for a while, the viewer is put a few
        // metres behind the bot that moved most and turned to face it
        public bool FollowViewer = true;
        private float _unseenSince = -1f, _nextFollow;
        private Player _followed;
        private readonly Dictionary<int, Vector3> _lastPos = new Dictionary<int, Vector3>();
        private void FollowBots(GameWorld world, float now)
        {
            if (!FollowViewer) return;
            var viewer = world.MainPlayer;
            if (viewer == null || !viewer.HealthController.IsAlive) return;
            int visible = 0; Player best = null; float bestMove = 0f;
            foreach (var kv in _rigs)
            {
                var p = kv.Value.Player;
                if (!Alive(p)) continue;
                if (p.IsVisible && !p.UsedSimplifiedSkeleton) visible++;
                Vector3 last;
                float moved = _lastPos.TryGetValue(kv.Key, out last) ? (p.Position - last).magnitude : 0f;
                _lastPos[kv.Key] = p.Position;
                if (moved > bestMove) { bestMove = moved; best = p; }
            }
            if (visible >= 2) { _unseenSince = -1f; }
            else if (_unseenSince < 0f) _unseenSince = now;
            // keep the camera on the bot being followed while it is the one in view
            if (_followed != null && Alive(_followed) && _followed.IsVisible)
            {
                Vector3 to = _followed.Position - viewer.Position; to.y = 0f;
                if (to.sqrMagnitude > 0.01f)
                {
                    float yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
                    float delta = Mathf.DeltaAngle(viewer.Rotation.x, yaw);
                    if (Mathf.Abs(delta) > 1f) viewer.Rotate(new Vector2(delta, 0f), false);
                }
            }
            if (best == null || _unseenSince < 0f || now - _unseenSince < 8f || now < _nextFollow) return;
            _nextFollow = now + 20f;
            try
            {
                Vector3 back = Quaternion.Euler(0f, best.Rotation.x, 0f) * Vector3.back * 5f;
                viewer.Teleport(PuppetSession.Grounded(best.Position + back + Vector3.up * 0.5f));
                _followed = best;
                _unseenSince = -1f;
                _writer.WriteLine("{\"k\":\"follow\",\"t\":" + F(now - _started) + ",\"b\":" + best.GetInstanceID() + "}");
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("[" + ModInfo.Name + "] fleet follow failed: " + ex.Message); }
        }

        // the movement states whose legs belong to their own clip; a bot's door opener also flags its sequence
        private static bool Interacting(Player p)
        {
            try
            {
                var state = p.MovementContext?.CurrentState;
                if (state != null)
                {
                    switch (state.Name)
                    {
                        case EPlayerState.BreachDoor:
                        case EPlayerState.DoorInteraction:
                        case EPlayerState.Open:
                        case EPlayerState.Close:
                        case EPlayerState.Unlock:
                        case EPlayerState.Loot:
                        case EPlayerState.Pickup:
                        case EPlayerState.Plant:
                        case EPlayerState.Jump:
                        case EPlayerState.JumpLanding:
                        case EPlayerState.FallDown:
                        case EPlayerState.ClimbOver:
                        case EPlayerState.ClimbUp:
                        case EPlayerState.VaultingFallDown:
                        case EPlayerState.VaultingLanding:
                        case EPlayerState.Roll:
                        case EPlayerState.Transit2Prone:
                        case EPlayerState.Prone2Stand:
                            return true;
                    }
                }
                var opener = p.AIData?.BotOwner?.DoorOpener;
                // the opener only clears its flag inside its own update, which SAIN skips in combat: the flag stayed
                // up and a PMC walked 10 s on native legs (user). past its traversal deadline it is stale
                return opener != null && opener.Interacting && opener._traversingEnd >= Time.time;
            }
            catch { return false; }
        }

        private void UpdateDetailedBots(Player viewer, float now)
        {
            if (!_automatic || now < _nextDetailSelection) return;
            _nextDetailSelection = now + 1f;
            if (viewer == null)
            {
                foreach (var rig in _rigs.Values) rig.Detailed = false;
                return;
            }
            const float maxDistanceSquared = 60f * 60f;
            var selected = _rigs.Values.AsValueEnumerable()
                .Where(r => r != null && Alive(r.Player) && r.Player.IsVisible && !r.Player.UsedSimplifiedSkeleton
                    && (r.Player.Position - viewer.Position).sqrMagnitude <= maxDistanceSquared)
                .OrderBy(r => (r.Player.Position - viewer.Position).sqrMagnitude)
                .Take(8)
                .ToArray();
            foreach (var rig in _rigs.Values) rig.Detailed = false;
            foreach (var rig in selected) rig.Detailed = true;
            _report.Event(new { t = now - _started, type = "detail_coverage", tracked = selected.Length, cap = 8 });
        }

        private static bool Alive(Player p)
        {
            return p != null && p.IsAI && p.HealthController != null && p.HealthController.IsAlive && p.AIData?.BotOwner != null
                && p.AIData.BotOwner.BotState == EBotState.Active;
        }

        private static Transform[] CreateReplayBones(Player player, out Transform replayRoot)
        {
            replayRoot = null;
            var bones = new Transform[ReplayBoneNames.Length];
            try
            {
                var references = player.Grounder?.ik?.references;
                replayRoot = references?.pelvis ? references.pelvis.parent : null;
                if (!replayRoot) replayRoot = references?.root;
                if (!replayRoot) replayRoot = ((Component)player).transform;
                for (int i = 0; i < bones.Length; i++) bones[i] = FindChild(replayRoot, ReplayBoneNames[i]);
            }
            catch { }
            return bones;
        }

        private static Transform FindChild(Transform root, string name)
        {
            if (!root) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindChild(root.GetChild(i), name);
                if (found) return found;
            }
            return null;
        }

        private static float ReadCarryWeight(Player player)
        {
            try
            {
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                object profile = player?.Profile;
                object inventory = profile?.GetType().GetField("Inventory", flags)?.GetValue(profile);
                if (inventory == null) return float.NaN;
                object deferred = inventory.GetType().GetField("TotalWeight", flags)?.GetValue(inventory);
                if (deferred is float) return (float)deferred;
                object value = deferred?.GetType().GetProperty("Value", flags)?.GetValue(deferred, null)
                    ?? deferred?.GetType().GetField("Value", flags)?.GetValue(deferred);
                if (value is float) return (float)value;
                if (value is double) return (float)(double)value;
            }
            catch { }
            return float.NaN;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static ReplayPoseSnapshot NewReplayPoseSnapshot()
        {
            return new ReplayPoseSnapshot
            {
                BonePositions = new Vector3[ReplayBoneNames.Length],
                BoneRotations = new Quaternion[ReplayBoneNames.Length],
                BoneAvailable = new bool[ReplayBoneNames.Length]
            };
        }

        private void CaptureReplayPose(BotRig rig, ReplayPoseSnapshot snapshot)
        {
            var p = rig.Player;
            snapshot.Frame = rig.ReplayFrame;
            snapshot.Time = rig.ReplayTime;
            snapshot.Root = p.Position;
            snapshot.Velocity = p.Velocity;
            snapshot.Yaw = p.Rotation.x;
            snapshot.Speed = p.Speed;
            snapshot.PoseLevel = p.PoseLevel;
            snapshot.Visible = p.IsVisible;
            snapshot.Simplified = p.UsedSimplifiedSkeleton;
            snapshot.Sprinting = p.IsSprintEnabled;
            snapshot.Suspended = rig.Suspended;
            Transform root = rig.ReplayRoot;
            Vector3 rootPosition = root ? root.position : snapshot.Root;
            Quaternion rootRotation = root ? root.rotation : Quaternion.Euler(0f, snapshot.Yaw, 0f);
            Quaternion inverse = Quaternion.Inverse(rootRotation);
            snapshot.SkeletonRoot = rootPosition;
            snapshot.SkeletonRotation = rootRotation;
            for (int i = 0; i < rig.ReplayBones.Length; i++)
            {
                Transform bone = rig.ReplayBones[i];
                snapshot.BoneAvailable[i] = bone;
                if (!bone) continue;
                snapshot.BonePositions[i] = inverse * (bone.position - rootPosition);
                snapshot.BoneRotations[i] = inverse * bone.rotation;
            }
            var pose = rig.Pose;
            snapshot.Phase = pose?.PhaseName;
            snapshot.Clip = pose?.ActiveClip;
            snapshot.Driver = pose?.DriverName;
            snapshot.ClipFrame = pose?.Frame ?? 0f;
            snapshot.Weight = pose?.Weight ?? 0f;
            snapshot.Rate = pose?.Rate ?? 0f;
            snapshot.BlendClip = pose?.BlendClipName;
            snapshot.BlendWeight = pose?.BlendWeight ?? 0f;
            snapshot.UpperOverlay = pose?.UpperOverlay;
            snapshot.OverlayFrame = pose?.UpperOverlayFrame ?? 0f;
            snapshot.ReactionResult = pose?.ReactionResult;
            snapshot.ReactionRootDrive = pose?.ReactionRootDrive;
            snapshot.ReactionStarts = pose?.ReactionStarts ?? 0;
            snapshot.UpperBodyFrames = pose?.UpperBodyReactionFrames ?? 0;
            snapshot.UpperHitStarts = pose?.UpperHitStarts ?? 0;
            snapshot.LandingStarts = pose?.LandingStarts ?? 0;
            snapshot.DamageEvents = rig.DamageEvents;
            if (rig.ReactionPolicy != null)
            {
                snapshot.PolicyAccepted = rig.ReactionPolicy.Accepted;
                snapshot.PolicySuppressed = rig.ReactionPolicy.Suppressed;
                snapshot.PolicyPending = rig.ReactionPolicy.Pending;
                snapshot.PolicyChance = rig.ReactionPolicy.LastChance;
                snapshot.PolicyRoll = rig.ReactionPolicy.LastRoll;
                snapshot.PolicyRecentDamage = (float)rig.ReactionPolicy.RecentDamage;
                snapshot.PolicyCooldownRemaining = rig.ReactionPolicy.CooldownRemaining(Time.time);
                snapshot.PolicyDecision = rig.ReactionPolicy.LastDecision;
            }
            snapshot.HasSync = rig.SyncProbe != null;
            if (snapshot.HasSync) snapshot.Sync = rig.SyncProbe.Capture();
            snapshot.Grip = pose?.ReadGripProbe();
            if (snapshot.Grip != null)
            {
                snapshot.Grip = new WeaponGripProbe
                {
                    LeftPosition = Clone(snapshot.Grip.LeftPosition), RightPosition = Clone(snapshot.Grip.RightPosition),
                    LeftRotation = Clone(snapshot.Grip.LeftRotation), RightRotation = Clone(snapshot.Grip.RightRotation),
                    SupportError = snapshot.Grip.SupportError, TriggerError = snapshot.Grip.TriggerError,
                    ArmWeight = snapshot.Grip.ArmWeight, AppliedFrame = snapshot.Grip.AppliedFrame
                };
            }
            snapshot.Inertia = SprintInertia.Probe(p);
            snapshot.SprintEntry = SprintEntrySnapshot.Capture(p);
        }

        private Dictionary<string, object> BuildReplaySnapshot(BotRig rig, ReplayPoseSnapshot frame, string stage)
        {
            var snapshot = new Dictionary<string, object>(18)
            {
                ["frame"] = frame.Frame,
                ["stage"] = stage,
                ["root"] = V(frame.Root),
                ["velocity"] = V(frame.Velocity),
                ["yaw"] = frame.Yaw,
                ["speed"] = frame.Speed,
                ["sprint"] = frame.Sprinting,
                ["poseLevel"] = frame.PoseLevel,
                ["visible"] = frame.Visible,
                ["simplified"] = frame.Simplified,
                ["suspended"] = frame.Suspended,
                ["role"] = rig.Role,
                ["carryWeightKg"] = Finite(rig.CarryWeightKg) ? (object)rig.CarryWeightKg : null
            };
            var body = new Dictionary<string, object>(ReplayBoneNames.Length);
            var worldBones = new Vector3[ReplayBoneNames.Length];
            for (int i = 0; i < ReplayBoneNames.Length; i++)
            {
                if (!frame.BoneAvailable[i]) continue;
                Vector3 localPosition = frame.BonePositions[i];
                Quaternion localRotation = frame.BoneRotations[i];
                worldBones[i] = frame.SkeletonRoot + frame.SkeletonRotation * localPosition;
                body[ReplayBoneKeys[i]] = new Dictionary<string, object>(2)
                {
                    ["p"] = V(localPosition),
                    ["q"] = Q4(localRotation)
                };
            }
            snapshot["body"] = body;
            snapshot["skeletonOrigin"] = V(frame.SkeletonRoot);
            snapshot["skeletonRotation"] = Q4(frame.SkeletonRotation);
            snapshot["legs"] = new Dictionary<string, object>
            {
                ["left"] = BuildReplayLeg(rig, 0, frame, worldBones, stage),
                ["right"] = BuildReplayLeg(rig, 1, frame, worldBones, stage)
            };
            if (rig.Pose != null)
            {
                var pose = rig.Pose;
                snapshot["reaction"] = new Dictionary<string, object>
                {
                    ["phase"] = frame.Phase,
                    ["clip"] = frame.Clip,
                    ["frame"] = frame.ClipFrame,
                    ["weight"] = frame.Weight,
                    ["rate"] = frame.Rate,
                    ["driver"] = frame.Driver,
                    ["blendClip"] = frame.BlendClip,
                    ["blendWeight"] = frame.BlendWeight,
                    ["upperOverlay"] = frame.UpperOverlay,
                    ["upperOverlayFrame"] = frame.OverlayFrame,
                    ["result"] = frame.ReactionResult,
                    ["rootDrive"] = frame.ReactionRootDrive.HasValue
                        ? (object)new[] { frame.ReactionRootDrive.Value.x, frame.ReactionRootDrive.Value.y } : null,
                    ["starts"] = frame.ReactionStarts,
                    ["upperBodyFrames"] = frame.UpperBodyFrames,
                    ["upperHitStarts"] = frame.UpperHitStarts,
                    ["landingStarts"] = frame.LandingStarts
                };
                var grip = frame.Grip;
                if (grip != null) snapshot["grip"] = new Dictionary<string, object>
                {
                    ["leftPosition"] = Clone(grip.LeftPosition), ["rightPosition"] = Clone(grip.RightPosition),
                    ["leftRotation"] = Clone(grip.LeftRotation), ["rightRotation"] = Clone(grip.RightRotation),
                    ["supportError"] = grip.SupportError, ["triggerError"] = grip.TriggerError,
                    ["armWeight"] = grip.ArmWeight, ["appliedFrame"] = grip.AppliedFrame
                };
            }
            if (rig.ReactionPolicy != null)
                snapshot["hitPolicy"] = new Dictionary<string, object>
                {
                    ["damageEvents"] = frame.DamageEvents, ["accepted"] = frame.PolicyAccepted,
                    ["suppressed"] = frame.PolicySuppressed, ["pending"] = frame.PolicyPending,
                    ["chance"] = frame.PolicyChance, ["roll"] = frame.PolicyRoll,
                    ["recentDamage"] = frame.PolicyRecentDamage, ["cooldownRemaining"] = frame.PolicyCooldownRemaining,
                    ["decision"] = frame.PolicyDecision
                };
            if (frame.HasSync)
            {
                var sync = frame.Sync;
                snapshot["armLegSync"] = new Dictionary<string, object>
                {
                    ["leftArmAngle"] = sync.HasLeftArmAngle ? (object)sync.LeftArmAngle : null,
                    ["rightArmAngle"] = sync.HasRightArmAngle ? (object)sync.RightArmAngle : null,
                    ["leftThighAngle"] = sync.HasLeftThighAngle ? (object)sync.LeftThighAngle : null,
                    ["rightThighAngle"] = sync.HasRightThighAngle ? (object)sync.RightThighAngle : null
                };
            }
            snapshot["inertia"] = frame.Inertia;
            snapshot["sprintEntry"] = frame.SprintEntry;
            return snapshot;
        }

        private object BuildReplayLeg(BotRig rig, int side, ReplayPoseSnapshot frame, Vector3[] world, string stage)
        {
            int hip = side == 0 ? 11 : 15, knee = side == 0 ? 12 : 16, foot = side == 0 ? 13 : 17, toe = side == 0 ? 14 : 18;
            var result = new Dictionary<string, object>(6);
            if (frame.BoneAvailable[hip]) result["hip"] = V(world[hip]);
            if (frame.BoneAvailable[knee]) result["knee"] = V(world[knee]);
            if (frame.BoneAvailable[foot]) result["ankle"] = V(world[foot]);
            if (frame.BoneAvailable[toe]) result["toe"] = V(world[toe]);
            if (stage == "after_lock" && rig.Placer != null && !Control)
            {
                FootPlacerProbe p = rig.Placer.Probe(side);
                result["placement"] = new Dictionary<string, object>
                {
                    ["active"] = p.Active, ["cycle"] = p.Cycle, ["progression"] = p.Progression,
                    ["cycleTime"] = p.CycleTime, ["swing"] = p.InAuthoredSwing, ["grounded"] = p.AuthoredGrounded,
                    ["locked"] = p.Locked, ["correction"] = p.Correction, ["failure"] = p.Failure,
                    ["authority"] = p.Authority, ["target"] = Clone(p.Target), ["placed"] = Clone(p.Placed),
                    ["clearanceBefore"] = p.ClearanceBefore, ["clearanceWanted"] = p.ClearanceRequested,
                    ["clearanceAfter"] = p.ClearanceAfter, ["clearanceFraction"] = p.ClearanceFraction,
                    ["midpointCorrection"] = p.MidpointCorrection, ["swingAvoidance"] = p.SwingAvoidance,
                    ["swingPlanned"] = p.SwingPathPlanned, ["swingResolved"] = p.SwingPathResolved,
                    ["stopStep"] = p.StopCorrectionStep, ["stopReady"] = p.StopSettlementReady,
                    ["stopFailed"] = p.StopSettlementFailed, ["placedHeel"] = Clone(p.PlacedHeel),
                    ["placedToe"] = Clone(p.PlacedToe), ["straightness"] = p.Straightness,
                    ["groundNormalAvailable"] = p.GroundNormalAvailable,
                    ["ankleLimitDegrees"] = p.AnkleLimitDegrees, ["soleTargetError"] = p.SoleTargetError
                };
            }
            return result;
        }

        private static float[] V(Vector3 value) => new[] { value.x, value.y, value.z };
        private static float[] Q4(Quaternion value) => new[] { value.x, value.y, value.z, value.w };
        private static float[] Clone(float[] value) => value == null ? null : (float[])value.Clone();

        private void Attach(Player p)
        {
            int id = p.GetInstanceID();
            int anonymousId;
            if (!_anonymousIds.TryGetValue(id, out anonymousId))
            {
                anonymousId = ++_nextAnonymousId;
                _anonymousIds[id] = anonymousId;
                if (_automatic) _report.Count("unique_bots_activated", 1);
            }
            var rig = new BotRig { Player = p, Id = id, AnonymousId = anonymousId,
                Role = p.Profile?.Info?.Settings?.Role.ToString(), AttachedAt = Time.realtimeSinceStartup,
                LastVisibleAt = Time.realtimeSinceStartup };
            string error = null;
            try
            {
                rig.Intent = MoveIntent.ForBot(p);
                if (Control)
                {
                    // Tarkov's own legs; the placer object only lends its bone references to the recorder
                    if (_plugin.FootPlacerWanted)
                    {
                        string placerError;
                        rig.Placer = FootPlacer.Create(p, _db, out placerError);
                        if (rig.Placer != null && p.PlayerBones != null) { rig.Placer.WeaponMount = p.PlayerBones.Weapon_Root_Third; rig.Placer.WeaponFollower = p.PlayerBones.Weapon_Root_Anim; }
                        if (rig.Placer == null) error = placerError;
                    }
                }
                else
                {
                    rig.Pose = PosePlayback.Create(p, _db, _poseClip, rig.Intent, out error);
                    if (rig.Pose != null)
                    {
                        var owner = p.AIData?.BotOwner;
                        rig.Pose.InCombat = () => owner != null && owner.Memory?.GoalEnemy != null;
                    }
                }
                if (rig.Pose != null)
                {
                    _plugin.ConfigurePlayback(rig.Pose);
                    rig.HasReactionDatabase = _plugin.ConfigureRaidReactions(rig.Pose, _db);
                    if (rig.HasReactionDatabase)
                    {
                        CulledReaction retained;
                        rig.ReactionPolicy = _culledReactions.TryGetValue(id, out retained) && ReferenceEquals(retained.Player, p)
                            ? retained.Policy ?? new HitReactionPolicy() : new HitReactionPolicy();
                    }
                    _culledReactions.Remove(id);
                    SpeedRampPatch.Attach(p); BotInertiaPatch.Attach(p);
                    SprintInertia.Attach(p, () => rig.Pose != null && rig.Pose.Enabled && !rig.Failed && !rig.Suspended);
                    string placerError = null;
                    if (_plugin.FootPlacerWanted)
                    {
                        rig.Placer = FootPlacer.Create(p, _db, out placerError);
                        if (rig.Placer != null && p.PlayerBones != null) { rig.Placer.WeaponMount = p.PlayerBones.Weapon_Root_Third; rig.Placer.WeaponFollower = p.PlayerBones.Weapon_Root_Anim; }
                        if (rig.Placer != null)
                        {
                            rig.Pose.StrideWarp = false;
                            rig.Pose.LeadSeconds = 0f;
                            var intent = rig.Intent;
                            var placer = rig.Placer;
                            rig.Placer.PathProvider = () => intent.Corners?.Invoke();
                            rig.Placer.HipShift = _plugin.HipShiftValue;
                            rig.Placer.KneeBendFloor = _plugin.KneeBendFloorValue;
                            rig.Pose.DrawnAnkle = side => placer.LastAnkle(side);
                            rig.Pose.DrawnPlanted = side => placer.LastPlanted(side);
                            rig.Pose.DrawnFootbase = side => placer.LastFootbase(side);
                            rig.Pose.DrawnProgression = side => placer.LastProgression(side);
                            rig.Pose.CurrentPlacementCost = placer.CurrentPlacementCost;
                            rig.Pose.StopSettled = placer.MatchesNativeIdle;
                        }
                        else error = placerError;
                    }
                    if (_plugin.FootLockWanted) rig.Lock = FootLock.Create(p);
                    rig.Lean = _plugin.BodyLeanWanted ? BodyLean.Create(p) : null;
                    if (rig.Lean != null)
                    {
                        rig.Lean.Strength = _plugin.BodyLeanStrengthValue;
                        rig.Lean.KeepWeaponLevel = _plugin.BodyLeanWeaponLevelValue;
                    }
                }
                rig.CarryWeightKg = ReadCarryWeight(p);
                rig.LastReportedWeightKg = rig.CarryWeightKg;
                rig.NextWeightSampleAt = Time.realtimeSinceStartup + 1f;
                rig.ReplayBones = CreateReplayBones(p, out rig.ReplayRoot);
                rig.BeforeVisual = NewReplayPoseSnapshot();
                rig.AfterVisual = NewReplayPoseSnapshot();
                rig.AfterLock = NewReplayPoseSnapshot();
                rig.PreviousAfterLock = NewReplayPoseSnapshot();
                rig.LastAnomalyAt = new Dictionary<string, float>();
            }
            catch (Exception ex) { rig.Failed = true; MovementCleanup.Detach(p); error = ex.Message; UnityEngine.Debug.LogError("[" + ModInfo.Name + "] fleet attach: " + ex); }
            _rigs[rig.Id] = rig;
            rig.SyncProbe = MotionSyncProbe.Create(p);
            bool boss = false;
            try { var owner = p.AIData?.BotOwner; boss = owner != null && ((owner.Boss != null && owner.Boss.IamBoss) || (owner.BotFollower != null && owner.BotFollower.HaveBoss)); } catch { }
            if (_automatic)
            {
            _report.Event(new { t = Time.realtimeSinceStartup - _started, b = rig.AnonymousId, type = "bot_attached",
                    role = rig.Role, boss, control = Control, pose = rig.Pose != null, placer = rig.Placer != null && !Control,
                    reactions = rig.HasReactionDatabase, carryWeightKg = Finite(rig.CarryWeightKg) ? (float?)rig.CarryWeightKg : null,
                    failed = rig.Failed, error = rig.Failed ? "attach_failed" : null });
                _report.Count("bots_attached", 1);
            }
            else _writer.WriteLine("{\"k\":\"bot\",\"t\":" + F(Time.realtimeSinceStartup - _started) + ",\"b\":" + rig.Id + ",\"role\":" + Q(rig.Role) + ",\"name\":" + Q(p.Profile?.Nickname)
                + ",\"boss\":" + (boss ? "true" : "false") + ",\"control\":" + (Control ? "true" : "false")
                + ",\"pose\":" + (rig.Pose != null ? "true" : "false") + ",\"placer\":" + (rig.Placer != null && !Control ? "true" : "false") + ",\"error\":" + Q(error) + "}");
        }

        private void Detach(int id, string why, bool performanceCull = false)
        {
            BotRig rig;
            if (!_rigs.TryGetValue(id, out rig)) return;
            _rigs.Remove(id);
            MovementCleanup.Detach(rig.Player);
            if (performanceCull)
            {
                if (rig.ReactionPolicy != null && rig.ReactionPolicy.Pending)
                {
                    bool started = rig.Pose != null && rig.Pose.ReactionStarts > rig.ReactionStartsWhenRequested;
                    rig.ReactionPolicy.Resolve(Time.time, started);
                    _report.Count(started ? "hit_reaction_started" : "hit_reaction_rejected", 1);
                }
                _culledReactions[id] = new CulledReaction { Player = rig.Player, Policy = rig.ReactionPolicy };
                _report.Count("performance_culls", 1);
            }
            else _culledReactions.Remove(id);
            WriteEvents(rig);
            if (_automatic)
            {
                string reason = performanceCull ? "performance_cull" : why == "dead or inactive" ? "dead_or_inactive" : "raid_end";
                _report.Event(new { t = Time.realtimeSinceStartup - _started, b = rig.AnonymousId, type = "bot_detached", reason,
                    appliedFrames = rig.Pose?.AppliedFrames ?? 0, hurried = rig.Pose?.HurriedFrames ?? 0,
                    reachClamps = rig.Placer?.ReachClamps ?? 0, anchorFailures = rig.Placer?.AnchorFailures ?? 0,
                    clearanceLimits = rig.Placer?.ClearanceLimits ?? 0, peakHipShift = rig.Placer?.PeakHipShift ?? 0f,
                    damageEvents = rig.DamageEvents, reactionsAccepted = rig.ReactionPolicy?.Accepted ?? 0,
                    reactionsSuppressed = rig.ReactionPolicy?.Suppressed ?? 0 });
                _report.Count("bots_detached", 1);
                if (!performanceCull) _anonymousIds.Remove(id);
                return;
            }
            _writer.WriteLine("{\"k\":\"gone\",\"t\":" + F(Time.realtimeSinceStartup - _started) + ",\"b\":" + id + ",\"why\":" + Q(why)
                + ",\"appliedFrames\":" + (rig.Pose?.AppliedFrames ?? 0) + ",\"hurried\":" + (rig.Pose?.HurriedFrames ?? 0) + ",\"reachClamps\":" + (rig.Placer?.ReachClamps ?? 0)
                + ",\"anchorFailures\":" + (rig.Placer?.AnchorFailures ?? 0) + ",\"clearanceLimits\":" + (rig.Placer?.ClearanceLimits ?? 0) + ",\"peakHipShift\":" + F(rig.Placer?.PeakHipShift ?? 0f) + "}");
        }

        // VisualPass prefix for one bot: the clip writes the legs before EFT's IK. true when this bot is rigged
        public bool BeforeVisual(Player player)
        {
            BotRig rig;
            if (!_rigs.TryGetValue(player.GetInstanceID(), out rig)) return false;
            if (rig.Failed) return true;
            long hookStarted = _automatic ? Stopwatch.GetTimestamp() : 0L;
            if (Time.frameCount != _frameOf) { _frameOf = Time.frameCount; _clock.Restart(); }
            try
            {
                float now = Time.realtimeSinceStartup;
                bool sampleDue = now >= rig.NextSample;
                rig.CaptureThisFrame = _automatic && rig.Detailed && sampleDue;
                rig.ReplayTriggerThisFrame = false;
                if (rig.CaptureThisFrame)
                {
                    Vector3 velocity = player.Velocity;
                    rig.NextSample = now + (velocity.x * velocity.x + velocity.z * velocity.z > 0.09f
                        ? MovingSampleSeconds : StillSampleSeconds);
                }
                if ((_automatic ? rig.CaptureThisFrame : sampleDue) && rig.SyncProbe != null)
                {
                    rig.NativeSync = rig.SyncProbe.Capture();
                    rig.NativeSyncFrame = Time.frameCount;
                }
                if (_automatic && rig.Detailed)
                {
                    rig.ReplayFrame = Time.frameCount;
                    rig.ReplayTime = now - _started;
                    CaptureReplayPose(rig, rig.BeforeVisual);
                }
                if (rig.Pose != null)
                {
                    rig.Pose.Apply();
                    SpeedRampPatch.SetCeiling(player, _plugin.ClipDrivenStartsWanted ? rig.Pose.SpeedCeiling : null);
                    BotInertiaPatch.SetCeiling(player, _plugin.ClipDrivenStartsWanted ? rig.Pose.VelocityCeiling : null);
                    BotInertiaPatch.SetDrive(player, rig.Pose.VelocityDrive);
                    BotInertiaPatch.SetRootDrive(player, rig.Pose.ReactionRootDrive);
                    if (rig.ReactionPolicy != null && rig.ReactionPolicy.Pending && !rig.Pose.ReactionPending)
                    {
                        bool started = rig.Pose.ReactionStarts > rig.ReactionStartsWhenRequested;
                        rig.ReactionPolicy.Resolve(Time.time, started);
                        _report?.Count(started ? "hit_reaction_started" : "hit_reaction_rejected", 1);
                        if (_automatic) _report.Event(new { t = now - _started, b = rig.AnonymousId,
                            type = "hit_reaction", outcome = started ? "started" : "rejected",
                            decision = rig.ReactionPolicy.LastDecision, chance = rig.ReactionPolicy.LastChance,
                            roll = rig.ReactionPolicy.LastRoll, accepted = rig.ReactionPolicy.Accepted,
                            suppressed = rig.ReactionPolicy.Suppressed });
                    }
                }
                rig.Lean?.Apply();
            }
            catch (Exception ex)
            {
                rig.Failed = true;
                MovementCleanup.Detach(rig.Player);
                UnityEngine.Debug.LogError("[" + ModInfo.Name + "] fleet before-visual on bot " + rig.Id + ": " + ex);
                if (_automatic) _report.Event(new { t = Time.realtimeSinceStartup - _started, b = rig.AnonymousId,
                    type = "rig_error", stage = "before_visual", reason = ex.GetType().Name });
                else _writer.WriteLine("{\"k\":\"e\",\"t\":" + F(Time.realtimeSinceStartup - _started) + ",\"b\":" + rig.Id + ",\"text\":" + Q("disabled after an error: " + ex.Message) + "}");
            }
            if (_automatic) RecordHookCost(hookStarted, 1);
            return true;
        }

        // VisualPass postfix: the placer moves the feet, then the frame is recorded
        public bool AfterVisual(Player player)
        {
            BotRig rig;
            if (!_rigs.TryGetValue(player.GetInstanceID(), out rig)) return false;
            if (rig.Failed) return true;
            long hookStarted = _automatic ? Stopwatch.GetTimestamp() : 0L;
            float now = Time.realtimeSinceStartup;
            bool sampleDue = _automatic ? rig.CaptureThisFrame : now >= rig.NextSample;
            GeometrySnapshot pre = sampleDue && !_automatic ? CaptureGeometry(rig, true) : default(GeometrySnapshot);
            string anomaly = null;
            string anomalyValue = null;
            if (_automatic && rig.Detailed && rig.ReplayFrame == Time.frameCount)
                CaptureReplayPose(rig, rig.AfterVisual);
            bool placed = false;
            try
            {
                if (!Control && rig.Placer != null) placed = rig.Placer.Apply(rig.Pose);
                if (!placed && rig.Lock != null)
                    rig.Lock.Apply(player.BodyAnimatorCommon.GetFloat(Plugin.FootStepHashValue), rig.Pose);
                if (rig.Pose != null) rig.Pose.ApplyReactionUpperBody();
                BotInertiaPatch.SetRootDrive(player, rig.Pose?.ReactionRootDrive);
            }
            catch (Exception ex)
            {
                rig.Failed = true;
                MovementCleanup.Detach(rig.Player);
                UnityEngine.Debug.LogError("[" + ModInfo.Name + "] fleet after-visual on bot " + rig.Id + ": " + ex);
                if (_automatic) _report.Event(new { t = now - _started, b = rig.AnonymousId,
                    type = "rig_error", stage = "after_lock", reason = ex.GetType().Name });
                if (_automatic) RecordHookCost(hookStarted, 2);
                return true;
            }
            if (_automatic) anomaly = DetectCheapAnomaly(rig, CaptureCheapPose(rig, Time.realtimeSinceStartup), out anomalyValue);
            if (_automatic && rig.Detailed && rig.ReplayFrame == Time.frameCount)
                CaptureReplayPose(rig, rig.AfterLock);

            if (_automatic && rig.Detailed && rig.ReplayFrame == Time.frameCount)
            {
                if (anomaly == null) anomaly = CheckReplayAnomalies(rig, rig.AfterLock, out anomalyValue);
                string placedAnomaly = CheckPlacedAnomalies(rig.AfterVisual, rig.AfterLock, out string placedValue);
                if (anomaly == null && placedAnomaly != null)
                {
                    anomaly = placedAnomaly;
                    anomalyValue = placedValue;
                }
                if (anomaly == null)
                {
                    WeaponGripProbe grip = rig.AfterLock.Grip;
                    if (grip != null && (grip.SupportError > 0.04f || grip.TriggerError > 0.04f))
                    {
                        anomaly = "grip_error";
                        anomalyValue = Math.Max(grip.SupportError, grip.TriggerError).ToString("0.###", CultureInfo.InvariantCulture);
                    }
                }
            }

            string routine = _automatic ? TrackRoutineTransition(rig) : null;
            string trigger = anomaly ?? routine;
            if (anomaly != null) RecordAnomaly(rig, anomaly, anomalyValue);
            if (_automatic && rig.Detailed && rig.ReplayFrame == Time.frameCount && (sampleDue || trigger != null))
                RecordReplayStages(rig, trigger);
            if (_automatic && rig.Detailed && rig.ReplayFrame == Time.frameCount)
            {
                if (!rig.HasPreviousReplay) rig.HasPreviousReplay = true;
                CopyReplayPose(rig.AfterLock, rig.PreviousAfterLock);
            }

            double ms = _clock.Elapsed.TotalMilliseconds;
            if (!_automatic) { _frameMs += ms; _framesTimed++; if (ms > _peakMs) _peakMs = ms; }
            // moving bots at ~30 Hz, still ones at 5 Hz (review): elapsed time, not frame counts
            if (sampleDue && !_automatic)
            {
                Vector3 v = player.Velocity;
                bool moving = v.x * v.x + v.z * v.z > 0.09f;
                rig.NextSample = now + (moving ? MovingSampleSeconds : StillSampleSeconds);
                try { Record(rig, placed, pre, CaptureGeometry(rig, true)); }
                catch (Exception ex) { UnityEngine.Debug.LogError("[" + ModInfo.Name + "] fleet record: " + ex); }
            }
            WriteEvents(rig);
            if (_automatic) RecordHookCost(hookStarted, 2);
            return true;
        }

        private int _preRenderFrame = -1;
        public void PreRender()
        {
            if (!_automatic || Time.frameCount == _preRenderFrame) return;
            long hookStarted = Stopwatch.GetTimestamp();
            _preRenderFrame = Time.frameCount;
            foreach (var rig in _rigs.Values)
            {
                if (rig == null || rig.Failed || !rig.Detailed || rig.ReplayFrame != Time.frameCount
                    || (!rig.CaptureThisFrame && !rig.ReplayTriggerThisFrame)) continue;
                try
                {
                    CaptureReplayPose(rig, rig.AfterLock);
                    _report.Record(rig.AnonymousId, rig.ReplayTime, "pre_render",
                        BuildReplaySnapshot(rig, rig.AfterLock, "pre_render"));
                }
                catch (Exception ex)
                {
                    _report.Event(new { t = rig.ReplayTime, b = rig.AnonymousId, type = "snapshot_error", stage = "pre_render", reason = ex.GetType().Name });
                }
            }
            RecordHookCost(hookStarted, 3);
        }

        private void WriteEvents(BotRig rig)
        {
            if (rig.Pose == null) return;
            var events = rig.Pose.Events;
            int start = rig.EventsWritten;
            if (_automatic && events.Count - start > 32) start = events.Count - 32;
            for (int i = start; i < events.Count; i++)
            {
                if (_automatic)
                {
                    string category = ClassifyPoseEvent(events[i]);
                    if (category != null) _report.Event(new { t = Time.realtimeSinceStartup - _started, f = Time.frameCount,
                        b = rig.AnonymousId, type = "pose_event", eventCode = category });
                }
                else _writer.WriteLine("{\"k\":\"e\",\"t\":" + F(Time.realtimeSinceStartup - _started) + ",\"f\":" + Time.frameCount + ",\"b\":" + rig.Id + ",\"text\":" + Q(events[i]) + "}");
            }
            rig.EventsWritten = events.Count;
            // PosePlayback keeps diagnostic events for its lifetime. Keep this per-rig queue bounded on long raids.
            if (_automatic && events.Count > 512)
            {
                events.Clear();
                rig.EventsWritten = 0;
            }
        }

        private static string ClassifyPoseEvent(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (value.IndexOf("Reaction", StringComparison.OrdinalIgnoreCase) >= 0) return "reaction";
            if (value.IndexOf("UpperOverlay", StringComparison.OrdinalIgnoreCase) >= 0) return "upper_overlay";
            if (value.IndexOf("Native jump", StringComparison.OrdinalIgnoreCase) >= 0) return "native_jump";
            if (value.IndexOf("sprint", StringComparison.OrdinalIgnoreCase) >= 0) return "sprint";
            if (value.IndexOf("stop", StringComparison.OrdinalIgnoreCase) >= 0) return "stop";
            if (value.IndexOf("turn", StringComparison.OrdinalIgnoreCase) >= 0) return "turn";
            if (value.IndexOf("Variation", StringComparison.OrdinalIgnoreCase) >= 0) return "variation";
            if (value.IndexOf("cycle", StringComparison.OrdinalIgnoreCase) >= 0) return "cycle";
            if (value.IndexOf("start", StringComparison.OrdinalIgnoreCase) >= 0) return "start";
            return null;
        }

        public bool WatchesDamage(Player player)
        {
            if (!_automatic || player == null || !player.IsAI || player.HealthController == null || !player.HealthController.IsAlive)
                return false;
            BotRig rig;
            return _rigs.TryGetValue(player.GetInstanceID(), out rig) && !rig.Failed && rig.Pose != null
                && rig.ReactionPolicy != null && rig.HasReactionDatabase;
        }

        public void ReceiveReactionDamage(Player player, float damage, string source)
        {
            if (!WatchesDamage(player)) return;
            BotRig rig = _rigs[player.GetInstanceID()];
            bool requested = rig.ReactionPolicy.AddDamage(Time.time, damage, UnityEngine.Random.value);
            rig.DamageEvents++;
            _report.Count("hit_damage", 1);
            _report.Event(new
            {
                t = Time.realtimeSinceStartup - _started,
                b = rig.AnonymousId,
                type = "hit_damage",
                source = source == "bullet" ? "bullet" : "other",
                damage,
                recentDamage = rig.ReactionPolicy.RecentDamage,
                chance = rig.ReactionPolicy.LastChance,
                roll = rig.ReactionPolicy.LastRoll,
                decision = rig.ReactionPolicy.LastDecision,
                accepted = rig.ReactionPolicy.Accepted,
                suppressed = rig.ReactionPolicy.Suppressed,
                cooldownRemaining = rig.ReactionPolicy.CooldownRemaining(Time.time)
            });
            if (!requested) return;
            _report.Count("hit_reaction_requested", 1);
            rig.ReactionStartsWhenRequested = rig.Pose.ReactionStarts;
            rig.Pose.RequestReaction(rig.ReactionPolicy.LastChance);
        }

        private void RecordReplayStages(BotRig rig, string trigger)
        {
            bool triggered = trigger != null;
            rig.ReplayTriggerThisFrame = triggered;
            _report.Record(rig.AnonymousId, rig.ReplayTime, "after_lock",
                BuildReplaySnapshot(rig, rig.AfterLock, "after_lock"), trigger);
            _report.Record(rig.AnonymousId, rig.ReplayTime, "before_visual",
                BuildReplaySnapshot(rig, rig.BeforeVisual, "before_visual"));
            _report.Record(rig.AnonymousId, rig.ReplayTime, "after_visual",
                BuildReplaySnapshot(rig, rig.AfterVisual, "after_visual"));
            _report.Count("replay_sample", 1);
            Samples++;
            if (trigger != null && trigger.StartsWith("routine:", StringComparison.Ordinal))
                _report.Event(new { t = rig.ReplayTime, b = rig.AnonymousId, type = "movement_transition", signal = trigger.Substring("routine:".Length),
                    phase = rig.AfterLock.Phase, clip = rig.AfterLock.Clip });
            CopyReplayPose(rig.AfterLock, rig.PreviousAfterLock);
            rig.HasPreviousReplay = true;
        }

        private void RecordAnomaly(BotRig rig, string code, string value)
        {
            if (!_automatic) return;
            float now = Time.realtimeSinceStartup;
            float previous;
            if (rig.LastAnomalyAt == null) rig.LastAnomalyAt = new Dictionary<string, float>();
            _report.Count("anomaly_" + code, 1);
            if (rig.LastAnomalyAt.TryGetValue(code, out previous) && now - previous < 1.5f) return;
            rig.LastAnomalyAt[code] = now;
            _report.Count("replay_anomaly_event", 1);
            _report.Event(new { t = now - _started, b = rig.AnonymousId, type = "anomaly", signal = code, magnitude = value });
        }

        private void RecordHookCost(long started, int stage)
        {
            if (!_automatic) return;
            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed < 0) elapsed = 0;
            _hookTicks += elapsed;
            _hookCalls++;
            _report.Count("mod_hook_calls", 1);
            _report.Count("mod_hook_us", (long)(elapsed * (1000000.0 / Stopwatch.Frequency)));
            if (elapsed > _peakHookTicks) _peakHookTicks = elapsed;
            if (stage >= 0 && stage < _hookTicksByStage.Length)
            {
                _hookTicksByStage[stage] += elapsed;
                _hookCallsByStage[stage]++;
            }
        }

        private void RecordFrameTime()
        {
            float now = Time.realtimeSinceStartup;
            float milliseconds = Time.unscaledDeltaTime * 1000f;
            if (Finite(milliseconds) && milliseconds > 0f)
            {
                int bucket = milliseconds <= 16.7f ? 0 : milliseconds <= 20f ? 1 : milliseconds <= 25f ? 2 : milliseconds <= 33.3f ? 3 : 4;
                _frameTimeBuckets[bucket]++;
                _frameSamples++;
                _frameMilliseconds += milliseconds;
                if (milliseconds > _peakFrameMilliseconds) _peakFrameMilliseconds = milliseconds;
                _report.Count("frame_ms_le_16_7", bucket == 0 ? 1 : 0);
                _report.Count("frame_ms_16_7_20", bucket == 1 ? 1 : 0);
                _report.Count("frame_ms_20_25", bucket == 2 ? 1 : 0);
                _report.Count("frame_ms_25_33_3", bucket == 3 ? 1 : 0);
                _report.Count("frame_ms_over_33_3", bucket == 4 ? 1 : 0);
                _report.Count("frame_samples", 1);
            }
            if (now - _lastPerfEvent < 5f) return;
            _lastPerfEvent = now;
            double tickMs = _hookTicksByStage[0] * 1000.0 / Stopwatch.Frequency;
            double beforeMs = _hookTicksByStage[1] * 1000.0 / Stopwatch.Frequency;
            double afterMs = _hookTicksByStage[2] * 1000.0 / Stopwatch.Frequency;
            double renderMs = _hookTicksByStage[3] * 1000.0 / Stopwatch.Frequency;
            _report.Event(new
            {
                t = now - _started,
                type = "performance",
                frameSamples = _frameSamples,
                frameMeanMs = _frameSamples > 0 ? (double?)(_frameMilliseconds / _frameSamples) : null,
                framePeakMs = _peakFrameMilliseconds,
                frameMsBuckets = new[] { _frameTimeBuckets[0], _frameTimeBuckets[1], _frameTimeBuckets[2], _frameTimeBuckets[3], _frameTimeBuckets[4] },
                modHookCalls = _hookCalls,
                modHookMeanMs = _hookCalls > 0 ? (double?)(_hookTicks * 1000.0 / Stopwatch.Frequency / _hookCalls) : null,
                modHookPeakMs = _peakHookTicks * 1000.0 / Stopwatch.Frequency,
                tickMeanMs = _hookCallsByStage[0] > 0 ? (double?)(tickMs / _hookCallsByStage[0]) : null,
                beforeVisualMeanMs = _hookCallsByStage[1] > 0 ? (double?)(beforeMs / _hookCallsByStage[1]) : null,
                afterVisualMeanMs = _hookCallsByStage[2] > 0 ? (double?)(afterMs / _hookCallsByStage[2]) : null,
                preRenderMeanMs = _hookCallsByStage[3] > 0 ? (double?)(renderMs / _hookCallsByStage[3]) : null
            });
            _hookTicks = _peakHookTicks = _hookCalls = 0;
            Array.Clear(_hookTicksByStage, 0, _hookTicksByStage.Length);
            Array.Clear(_hookCallsByStage, 0, _hookCallsByStage.Length);
            _frameMilliseconds = 0; _peakFrameMilliseconds = 0; _frameSamples = 0;
            Array.Clear(_frameTimeBuckets, 0, _frameTimeBuckets.Length);
        }

        private CheapPoseProbe CaptureCheapPose(BotRig rig, float now)
        {
            var sample = new CheapPoseProbe { Time = now - _started, Visible = rig.Player.IsVisible,
                Simplified = rig.Player.UsedSimplifiedSkeleton, Root = rig.Player.Position, Velocity = rig.Player.Velocity };
            Transform[] bones = rig.ReplayBones;
            if (bones == null || bones.Length < ReplayBoneNames.Length) return sample;
            try
            {
                sample.HasHips = TryPosition(bones[11], out sample.LeftHip) && TryPosition(bones[15], out sample.RightHip);
                sample.HasKnees = TryPosition(bones[12], out sample.LeftKnee) && TryPosition(bones[16], out sample.RightKnee);
                sample.HasFeet = TryPosition(bones[13], out sample.LeftFoot) && TryPosition(bones[17], out sample.RightFoot);
                sample.HasSpine = bones[1] && bones[2] && bones[3] && bones[4];
                if (sample.HasSpine)
                {
                    sample.Spine1 = bones[1].rotation; sample.Spine2 = bones[2].rotation;
                    sample.Spine3 = bones[3].rotation; sample.Chest = bones[4].rotation;
                }
            }
            catch { }
            return sample;
        }

        private string DetectCheapAnomaly(BotRig rig, CheapPoseProbe sample, out string magnitude)
        {
            magnitude = null;
            if (!sample.Visible || sample.Simplified) { rig.PreviousCheap = sample; rig.HasPreviousCheap = true; return null; }
            if (rig.HasPreviousCheap)
            {
                float dt = sample.Time - rig.PreviousCheap.Time;
                if (dt > 0f && dt <= 0.06f && sample.HasFeet && rig.PreviousCheap.HasFeet)
                {
                    float left = Vector3.Distance(sample.LeftFoot, rig.PreviousCheap.LeftFoot);
                    float right = Vector3.Distance(sample.RightFoot, rig.PreviousCheap.RightFoot);
                    if (left > 0.28f || right > 0.28f)
                    {
                        float distance = Mathf.Max(left, right);
                        magnitude = (left > right ? "left:" : "right:") + distance.ToString("0.###", CultureInfo.InvariantCulture);
                        rig.PreviousCheap = sample; rig.HasPreviousCheap = true;
                        return "foot_snap";
                    }
                }
                if (dt > 0f && dt <= 0.06f && sample.HasSpine && rig.PreviousCheap.HasSpine)
                {
                    float max = Mathf.Max(Quaternion.Angle(rig.PreviousCheap.Spine1, sample.Spine1),
                        Quaternion.Angle(rig.PreviousCheap.Spine2, sample.Spine2),
                        Quaternion.Angle(rig.PreviousCheap.Spine3, sample.Spine3),
                        Quaternion.Angle(rig.PreviousCheap.Chest, sample.Chest));
                    if (max > 48f)
                    {
                        magnitude = max.ToString("0.#", CultureInfo.InvariantCulture);
                        rig.PreviousCheap = sample; rig.HasPreviousCheap = true;
                        return "spine_jump";
                    }
                }
            }
            if (sample.HasHips && sample.HasKnees)
            {
                Vector3 right = Quaternion.Euler(0f, rig.Player.Rotation.x, 0f) * Vector3.right;
                float hipDelta = Vector3.Dot(sample.LeftHip - sample.RightHip, right);
                float kneeDelta = Vector3.Dot(sample.LeftKnee - sample.RightKnee, right);
                if (Mathf.Abs(hipDelta) > 0.08f && Mathf.Abs(kneeDelta) < 0.18f && hipDelta * kneeDelta <= 0f)
                {
                    magnitude = kneeDelta.ToString("0.###", CultureInfo.InvariantCulture);
                    rig.PreviousCheap = sample; rig.HasPreviousCheap = true;
                    return "knee_crossing";
                }
            }
            float speed = new Vector2(sample.Velocity.x, sample.Velocity.z).magnitude;
            if (sample.HasFeet && speed > 0.8f)
            {
                Vector3 travel = new Vector3(sample.Velocity.x, 0f, sample.Velocity.z).normalized;
                float left = Vector3.Dot(sample.Root - sample.LeftFoot, travel);
                float right = Vector3.Dot(sample.Root - sample.RightFoot, travel);
                float trailing = Mathf.Max(left, right);
                if (trailing > 1.25f)
                {
                    magnitude = trailing.ToString("0.###", CultureInfo.InvariantCulture);
                    rig.PreviousCheap = sample; rig.HasPreviousCheap = true;
                    return "ankle_trailing";
                }
            }
            rig.PreviousCheap = sample;
            rig.HasPreviousCheap = true;
            return null;
        }

        private string CheckReplayAnomalies(BotRig rig, ReplayPoseSnapshot current, out string magnitude)
        {
            magnitude = null;
            if (!current.Visible || current.Simplified) return null;
            if (rig.HasPreviousReplay)
            {
                float dt = current.Time - rig.PreviousAfterLock.Time;
                if (dt > 0f && dt <= 0.06f)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        int ankle = i == 0 ? 13 : 17;
                        if (!current.BoneAvailable[ankle] || !rig.PreviousAfterLock.BoneAvailable[ankle]) continue;
                        Vector3 previous = rig.PreviousAfterLock.SkeletonRoot
                            + rig.PreviousAfterLock.SkeletonRotation * rig.PreviousAfterLock.BonePositions[ankle];
                        Vector3 position = current.SkeletonRoot + current.SkeletonRotation * current.BonePositions[ankle];
                        float delta = Vector3.Distance(previous, position);
                        if (delta > 0.28f)
                        {
                            magnitude = (i == 0 ? "left:" : "right:") + delta.ToString("0.###", CultureInfo.InvariantCulture);
                            return "foot_snap";
                        }
                    }
                    for (int i = 1; i <= 4; i++)
                    {
                        if (!current.BoneAvailable[i] || !rig.PreviousAfterLock.BoneAvailable[i]) continue;
                        float degrees = Quaternion.Angle(rig.PreviousAfterLock.BoneRotations[i], current.BoneRotations[i]);
                        if (degrees > 48f)
                        {
                            magnitude = ReplayBoneKeys[i] + ":" + degrees.ToString("0.#", CultureInfo.InvariantCulture);
                            return "spine_jump";
                        }
                    }
                }
            }
            float hipDelta = current.BonePositions[11].x - current.BonePositions[15].x;
            float kneeDelta = current.BonePositions[12].x - current.BonePositions[16].x;
            if (current.BoneAvailable[11] && current.BoneAvailable[15] && current.BoneAvailable[12] && current.BoneAvailable[16]
                && Mathf.Abs(hipDelta) > 0.08f && Mathf.Abs(kneeDelta) < 0.18f && hipDelta * kneeDelta <= 0f)
            {
                magnitude = kneeDelta.ToString("0.###", CultureInfo.InvariantCulture);
                return "knee_crossing";
            }
            float speed = new Vector2(current.Velocity.x, current.Velocity.z).magnitude;
            if (speed > 0.8f)
            {
                Vector3 travel = new Vector3(current.Velocity.x, 0f, current.Velocity.z).normalized;
                for (int i = 0; i < 2; i++)
                {
                    int ankle = i == 0 ? 13 : 17;
                    if (!current.BoneAvailable[ankle]) continue;
                    Vector3 foot = current.SkeletonRoot + current.SkeletonRotation * current.BonePositions[ankle];
                    float trailing = Vector3.Dot(current.Root - foot, travel);
                    if (trailing > 1.25f)
                    {
                        magnitude = (i == 0 ? "left:" : "right:") + trailing.ToString("0.###", CultureInfo.InvariantCulture);
                        return "ankle_trailing";
                    }
                }
            }
            return null;
        }

        private static string CheckPlacedAnomalies(ReplayPoseSnapshot before, ReplayPoseSnapshot after, out string magnitude)
        {
            magnitude = null;
            for (int i = 0; i < 2; i++)
            {
                int ankle = i == 0 ? 13 : 17;
                if (!before.BoneAvailable[ankle] || !after.BoneAvailable[ankle]) continue;
                Vector3 a = before.SkeletonRoot + before.SkeletonRotation * before.BonePositions[ankle];
                Vector3 b = after.SkeletonRoot + after.SkeletonRotation * after.BonePositions[ankle];
                float delta = Vector3.Distance(a, b);
                if (delta > 0.2f)
                {
                    magnitude = (i == 0 ? "left:" : "right:") + delta.ToString("0.###", CultureInfo.InvariantCulture);
                    return "placement_snap";
                }
            }
            for (int i = 1; i <= 4; i++)
            {
                if (!before.BoneAvailable[i] || !after.BoneAvailable[i]) continue;
                float degrees = Quaternion.Angle(before.BoneRotations[i], after.BoneRotations[i]);
                if (degrees > 42f)
                {
                    magnitude = ReplayBoneKeys[i] + ":" + degrees.ToString("0.#", CultureInfo.InvariantCulture);
                    return "torso_stage_jump";
                }
            }
            return null;
        }

        private string TrackRoutineTransition(BotRig rig)
        {
            var pose = rig.Pose;
            if (pose == null) return null;
            string phase = pose.PhaseName, clip = pose.ActiveClip, overlay = pose.UpperOverlay;
            string trigger = null;
            if (rig.LastPhase != null && phase != rig.LastPhase) trigger = "routine:phase";
            else if (rig.LastClip != null && clip != rig.LastClip) trigger = "routine:clip";
            else if (rig.LastUpperOverlay != overlay) trigger = "routine:overlay";
            rig.LastPhase = phase;
            rig.LastClip = clip;
            rig.LastUpperOverlay = overlay;
            return rig.Detailed ? trigger : null;
        }

        private static void CopyReplayPose(ReplayPoseSnapshot source, ReplayPoseSnapshot destination)
        {
            destination.Frame = source.Frame; destination.Time = source.Time; destination.Root = source.Root;
            destination.SkeletonRoot = source.SkeletonRoot; destination.SkeletonRotation = source.SkeletonRotation;
            destination.Velocity = source.Velocity; destination.Yaw = source.Yaw; destination.Speed = source.Speed;
            destination.PoseLevel = source.PoseLevel; destination.Visible = source.Visible; destination.Simplified = source.Simplified;
            destination.Sprinting = source.Sprinting; destination.Suspended = source.Suspended;
            Array.Copy(source.BonePositions, destination.BonePositions, source.BonePositions.Length);
            Array.Copy(source.BoneRotations, destination.BoneRotations, source.BoneRotations.Length);
            Array.Copy(source.BoneAvailable, destination.BoneAvailable, source.BoneAvailable.Length);
            destination.Phase = source.Phase; destination.Clip = source.Clip; destination.Driver = source.Driver;
            destination.ClipFrame = source.ClipFrame; destination.Weight = source.Weight; destination.Rate = source.Rate;
            destination.BlendClip = source.BlendClip; destination.BlendWeight = source.BlendWeight;
            destination.UpperOverlay = source.UpperOverlay; destination.OverlayFrame = source.OverlayFrame;
            destination.HasSync = source.HasSync; destination.Sync = source.Sync;
            destination.Grip = source.Grip == null ? null : new WeaponGripProbe
            {
                LeftPosition = Clone(source.Grip.LeftPosition), RightPosition = Clone(source.Grip.RightPosition),
                LeftRotation = Clone(source.Grip.LeftRotation), RightRotation = Clone(source.Grip.RightRotation),
                SupportError = source.Grip.SupportError, TriggerError = source.Grip.TriggerError,
                ArmWeight = source.Grip.ArmWeight, AppliedFrame = source.Grip.AppliedFrame
            };
            destination.Inertia = source.Inertia; destination.SprintEntry = source.SprintEntry;
        }

        internal void SetPhaseLockForTest(bool enabled)
        {
            foreach (var rig in _rigs.Values)
                if (rig.Pose != null) rig.Pose.AnimatorPhaseLock = enabled;
        }

        private struct GeometrySnapshot
        {
            public bool HasRootJoint;
            public Vector3 RootJoint;
            public bool HasPelvis;
            public Vector3 Pelvis;
            public LegGeometry Left;
            public LegGeometry Right;
        }

        private struct LegGeometry
        {
            public bool HasHip;
            public Vector3 Hip;
            public bool HasKnee;
            public Vector3 Knee;
            public bool HasAnkle;
            public Vector3 Ankle;
            public bool HasHeel;
            public Vector3 Heel;
            public bool HasToe;
            public Vector3 Toe;

            public bool Any => HasHip || HasKnee || HasAnkle || HasHeel || HasToe;
        }

        // Reads only when Record is due. The placer already owns the same references, but reading them directly here
        // also covers control rigs and frames after its Reset() without exposing its private state.
        private GeometrySnapshot CaptureGeometry(BotRig rig, bool includeSoles)
        {
            GeometrySnapshot snapshot = default(GeometrySnapshot);
            if (rig == null || rig.Player == null) return snapshot;

            try
            {
                var references = rig.Player.Grounder?.ik?.references;
                if (references == null) return snapshot;

                Transform pelvis = references.pelvis;
                if (TryPosition(pelvis, out snapshot.Pelvis)) snapshot.HasPelvis = true;

                Transform root = null;
                try { root = pelvis ? pelvis.parent : null; } catch { }
                if (!root)
                {
                    try { root = references.root; } catch { }
                }
                if (TryPosition(root, out snapshot.RootJoint)) snapshot.HasRootJoint = true;

                CaptureLeg(ref snapshot.Left, references.leftThigh, references.leftCalf, references.leftFoot, 0, includeSoles);
                CaptureLeg(ref snapshot.Right, references.rightThigh, references.rightCalf, references.rightFoot, 1, includeSoles);
            }
            catch
            {
                // Individual fields already carry availability. A partially initialized skeleton should not turn
                // unavailable positions into zero-valued geometry in the stream.
            }
            return snapshot;
        }

        private void CaptureLeg(ref LegGeometry leg, Transform thigh, Transform calf, Transform foot, int side, bool includeSoles)
        {
            leg.HasHip = TryPosition(thigh, out leg.Hip);
            leg.HasKnee = TryPosition(calf, out leg.Knee);
            leg.HasAnkle = TryPosition(foot, out leg.Ankle);
            if (!includeSoles || _db == null || !_db.HasSolePoints || _db.SoleHeel == null || _db.SoleToe == null
                || side < 0 || side >= _db.SoleHeel.Length || side >= _db.SoleToe.Length)
                return;
            leg.HasHeel = TrySole(foot, _db.SoleHeel[side], out leg.Heel, _db.SoleToe[side], out leg.Toe);
            leg.HasToe = leg.HasHeel;
        }

        private static bool TryPosition(Transform transform, out Vector3 position)
        {
            position = Vector3.zero;
            if (!transform) return false;
            try
            {
                position = transform.position;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySole(Transform foot, Vector3 heelOffset, out Vector3 heel, Vector3 toeOffset, out Vector3 toe)
        {
            heel = toe = Vector3.zero;
            if (!foot) return false;
            try
            {
                Vector3 position = foot.position;
                Quaternion rotation = foot.rotation;
                heel = position + rotation * heelOffset;
                toe = position + rotation * toeOffset;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void AppendSole(StringBuilder sb, FootPlacerProbe probe, LegGeometry before, LegGeometry after)
        {
            bool hasHeel = false, hasToe = false;
            if (probe.Active && after.HasAnkle && HasVector(probe.PlacedHeel)) { V3(sb, ",\"hl\":", probe.PlacedHeel); hasHeel = true; }
            if (probe.Active && after.HasAnkle && HasVector(probe.PlacedToe)) { V3(sb, ",\"to\":", probe.PlacedToe); hasToe = true; }
            if (!hasHeel && after.HasHeel) V3(sb, ",\"hl\":", after.Heel);
            if (!hasToe && after.HasToe) V3(sb, ",\"to\":", after.Toe);
            if (before.HasHeel) V3(sb, ",\"hl0\":", before.Heel);
            if (before.HasToe) V3(sb, ",\"to0\":", before.Toe);
        }

        private static bool HasVector(float[] value)
        {
            return value != null && value.Length >= 3;
        }

        // one compact line: playback state, body and driver context, both feet from the placer, both legs' bend
        private void Record(BotRig rig, bool placed, GeometrySnapshot pre, GeometrySnapshot post)
        {
            var p = rig.Player;
            var sb = _line;
            sb.Length = 0;
            float t = Time.realtimeSinceStartup - _started;
            Vector3 pos = p.Position;
            Vector3 vel = p.Velocity;
            sb.Append("{\"k\":\"s\",\"t\":").Append(F(t)).Append(",\"f\":").Append(Time.frameCount).Append(",\"b\":").Append(rig.Id);
            sb.Append(",\"x\":").Append(F(pos.x)).Append(",\"y\":").Append(F(pos.y)).Append(",\"z\":").Append(F(pos.z));
            sb.Append(",\"vx\":").Append(F(vel.x)).Append(",\"vz\":").Append(F(vel.z)).Append(",\"yaw\":").Append(F(p.Rotation.x));
            sb.Append(",\"spd\":").Append(F(p.Speed)).Append(",\"spr\":").Append(p.IsSprintEnabled ? 1 : 0).Append(",\"pose\":").Append(F(p.PoseLevel));
            // culled or simplified skeletons do not animate: detectors must know (review: coverage, not silence)
            sb.Append(",\"vis\":").Append(p.IsVisible ? 1 : 0).Append(",\"simp\":").Append(p.UsedSimplifiedSkeleton ? 1 : 0).Append(",\"sus\":").Append(rig.Suspended ? 1 : 0);
            // combat context, best effort: the bot's enemy, whether it is shooting, aiming
            int enemy = 0, shooting = 0, aiming = 0;
            bool aimingKnown = false, shootingKnown = false;
            try
            {
                var owner = p.AIData?.BotOwner;
                if (owner != null)
                {
                    enemy = owner.Memory?.GoalEnemy != null ? 1 : 0;
                    if (owner.ShootData != null)
                    {
                        shooting = owner.ShootData.Shooting ? 1 : 0;
                        shootingKnown = true;
                    }
                }
                if (p.ProceduralWeaponAnimation != null)
                {
                    aiming = p.ProceduralWeaponAnimation.IsAiming ? 1 : 0;
                    aimingKnown = true;
                }
            }
            catch { }
            sb.Append(",\"enemy\":").Append(enemy).Append(",\"shoot\":").Append(shooting).Append(",\"aim\":").Append(aiming);
            sb.Append(",\"shootKnown\":").Append(shootingKnown ? 1 : 0).Append(",\"aimKnown\":").Append(aimingKnown ? 1 : 0);
            var pose = rig.Pose;
            if (pose != null)
            {
                sb.Append(",\"ph\":").Append(Q(pose.PhaseName)).Append(",\"clip\":").Append(Q(pose.ActiveClip)).Append(",\"cf\":").Append(F(pose.Frame));
                sb.Append(",\"rt\":").Append(F(pose.Rate)).Append(",\"w\":").Append(F(pose.Weight)).Append(",\"drv\":").Append(Q(pose.DriverName));
                if (pose.BlendClipName != null) sb.Append(",\"bl\":").Append(Q(pose.BlendClipName)).Append(",\"bw\":").Append(F(pose.BlendWeight));
                sb.Append(",\"pl\":").Append(pose.PhaseLocked ? 1 : 0);
                sb.Append(",\"sync\":").Append(Newtonsoft.Json.JsonConvert.SerializeObject(pose.ReadSync()));
            }
            if (rig.NativeSyncFrame == Time.frameCount)
                sb.Append(",\"syncBefore\":").Append(Newtonsoft.Json.JsonConvert.SerializeObject(rig.NativeSync));
            if (rig.SyncProbe != null)
                sb.Append(",\"syncAfter\":").Append(Newtonsoft.Json.JsonConvert.SerializeObject(rig.SyncProbe.Capture()));
            sb.Append(",\"si\":").Append(Newtonsoft.Json.JsonConvert.SerializeObject(SprintInertia.Probe(p)));
            sb.Append(",\"se\":").Append(Newtonsoft.Json.JsonConvert.SerializeObject(SprintEntrySnapshot.Capture(p)));
            sb.Append(",\"placed\":").Append(placed ? 1 : 0);
            sb.Append(",\"refinement\":").Append(LocomotionRefinement.Enabled ? 1 : 0);
            if (post.HasRootJoint) V3(sb, ",\"rj\":", post.RootJoint);
            if (post.HasPelvis) V3(sb, ",\"pe\":", post.Pelvis);
            if (pre.HasPelvis) V3(sb, ",\"pe0\":", pre.Pelvis);
            if (rig.Placer != null || post.Left.Any || post.Right.Any || pre.Left.Any || pre.Right.Any)
            {
                for (int side = 0; side < 2; side++)
                {
                    var pr = Control || rig.Placer == null ? default(FootPlacerProbe) : rig.Placer.Probe(side);
                    LegGeometry before = side == 0 ? pre.Left : pre.Right;
                    LegGeometry after = side == 0 ? post.Left : post.Right;
                    sb.Append(side == 0 ? ",\"L\":{" : ",\"R\":{");
                    sb.Append("\"on\":").Append(pr.Active ? 1 : 0).Append(",\"cy\":").Append(pr.Cycle).Append(",\"pg\":").Append(F(pr.Progression));
                    if (pr.Active) sb.Append(",\"ct\":").Append(F(pr.CycleTime)).Append(",\"sw\":").Append(pr.InAuthoredSwing ? 1 : 0).Append(",\"gr\":").Append(pr.AuthoredGrounded ? 1 : 0);
                    sb.Append(",\"lk\":").Append(pr.Locked ? 1 : 0).Append(",\"corr\":").Append(F(pr.Correction)).Append(",\"fail\":").Append(pr.Failure).Append(",\"auth\":").Append(F(pr.Authority));
                    sb.Append(",\"hd\":").Append(F(pr.PlacedHeading));
                    if (placed) sb.Append(",\"clearance0\":").Append(F(pr.ClearanceBefore)).Append(",\"clearanceWanted\":").Append(F(pr.ClearanceRequested)).Append(",\"clearance\":").Append(F(pr.ClearanceAfter)).Append(",\"clearanceFraction\":").Append(F(pr.ClearanceFraction));
                    if (placed) sb.Append(",\"fade\":").Append(pr.Fading ? 1 : 0).Append(",\"normal\":").Append(pr.GroundNormalAvailable ? 1 : 0).Append(",\"ankleLimit\":").Append(F(pr.AnkleLimitDegrees)).Append(",\"soleError\":").Append(F(pr.SoleTargetError)).Append(",\"midCorrection\":").Append(F(pr.MidpointCorrection));
                    if (placed) sb.Append(",\"swingAvoidance\":").Append(F(pr.SwingAvoidance)).Append(",\"swingPlanned\":").Append(pr.SwingPathPlanned ? 1 : 0).Append(",\"swingResolved\":").Append(pr.SwingPathResolved ? 1 : 0).Append(",\"swingEndpointsBlocked\":").Append(pr.SwingEndpointsBlocked ? 1 : 0);
                    if (placed) sb.Append(",\"stopStep\":").Append(pr.StopCorrectionStep ? 1 : 0);
                    if (placed) sb.Append(",\"stopFailed\":").Append(pr.StopSettlementFailed ? 1 : 0).Append(",\"stopReady\":").Append(pr.StopSettlementReady ? 1 : 0);
                    if (placed) sb.Append(",\"strideSource\":").Append(Newtonsoft.Json.JsonConvert.SerializeObject(pr.StrideSource)).Append(",\"stridePartner\":").Append(Newtonsoft.Json.JsonConvert.SerializeObject(pr.StridePartner)).Append(",\"strideBlend\":").Append(F(pr.StrideBlend));
                    if (pr.Active && after.HasAnkle) { V3(sb, ",\"p\":", pr.Placed); V3(sb, ",\"tg\":", pr.Target); V3(sb, ",\"nx\":", pr.Next); V3(sb, ",\"pv\":", pr.Prev); }
                    // The ankle remains the compatibility value for control and inactive frames. Probe vectors are
                    // emitted only while the placer is active, so Reset() cannot leave stale placement positions.
                    if (!pr.Active && after.HasAnkle) { V3(sb, ",\"p\":", after.Ankle); sb.Append(",\"src\":\"bone\""); }
                    AppendSole(sb, pr, before, after);
                    if (after.HasHip && after.HasKnee && after.HasAnkle)
                    {
                        Vector3 axis = after.Ankle - after.Hip;
                        float la = Mathf.Max(axis.magnitude, 1e-6f);
                        Vector3 kv = after.Knee - after.Hip;
                        Vector3 perp = kv - axis * (Vector3.Dot(kv, axis) / (la * la));
                        float chain = (after.Knee - after.Hip).magnitude + (after.Ankle - after.Knee).magnitude;
                        Quaternion inv = Quaternion.Euler(0f, -p.Rotation.x, 0f);
                        Vector3 local = inv * perp;
                        sb.Append(",\"st\":").Append(F(la / Mathf.Max(chain, 1e-6f))).Append(",\"kf\":").Append(F(local.z)).Append(",\"kl\":").Append(F(local.x));
                    }
                    if (after.HasHip) V3(sb, ",\"h\":", after.Hip);
                    if (after.HasKnee) V3(sb, ",\"k\":", after.Knee);
                    if (after.HasAnkle) V3(sb, ",\"a\":", after.Ankle);
                    if (before.HasHip) V3(sb, ",\"h0\":", before.Hip);
                    if (before.HasKnee) V3(sb, ",\"k0\":", before.Knee);
                    if (before.HasAnkle) V3(sb, ",\"a0\":", before.Ankle);
                    sb.Append('}');
                }
            }
            sb.Append('}');
            _writer.WriteLine(sb.ToString());
            Samples++;
        }

        public string Stop(string reason)
        {
            foreach (var id in new List<int>(_rigs.Keys))
                Detach(id, reason);
            _culledReactions.Clear();
            _anonymousIds.Clear();
            _writer.WriteLine("{\"k\":\"end\",\"t\":" + F(Time.realtimeSinceStartup - _started) + ",\"why\":" + Q(reason) + ",\"samples\":" + Samples + "}");
            _writer.Flush();
            _writer.Dispose();
            return Path;
        }

        private static void V3(StringBuilder sb, string key, float[] v)
        {
            if (v == null || v.Length < 3) return;
            sb.Append(key).Append('[').Append(F(v[0])).Append(',').Append(F(v[1])).Append(',').Append(F(v[2])).Append(']');
        }

        private static void V3(StringBuilder sb, string key, Vector3 v)
        {
            sb.Append(key).Append('[').Append(F(v.x)).Append(',').Append(F(v.y)).Append(',').Append(F(v.z)).Append(']');
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        private static string Q(string s) => s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
