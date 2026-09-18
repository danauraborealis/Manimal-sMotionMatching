using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using Newtonsoft.Json;
using UnityEngine;

namespace Manimal.MotionMatching
{
    /// <summary>
    /// Captures a bounded, read-only observation of one EFT player.
    ///
    /// All Unity and EFT reads happen in <see cref="Record"/> on the caller's
    /// thread. <see cref="FinishAsync"/> only sees the detached value structs
    /// produced by Record, so serialization can run away from the Unity thread.
    /// </summary>
    public sealed class DiagnosticCapture
    {
        private const int MaximumStageNameLength = 96;
        private const int MaximumReasonLength = 512;
        private const int MaximumStageCountEntries = 64;
        private const int MaximumAnimatorLayers = 16;
        private const string FinalStage = "after_visual";

        private static int _captureSequence;

        private readonly object _gate = new object();
        private readonly Player _player;
        private readonly string _outputDirectory;
        private readonly string _localBotId;
        private readonly string _unityVersion;
        private readonly string _gameVersion;
        private readonly string _assemblyCSharpMvid;
        private readonly int _maximumSamples;
        private readonly SampleSnapshot[] _samples;
        private readonly Dictionary<string, int> _stageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly PlayerAccessors _accessors;

        private RouteSummary? _route;
        private PuppetReport? _puppet;
        private FootLockSummary? _footLock;
        private FootPlacerSummary? _footPlacer;
        private PosePlaybackSummary? _posePlayback;
        private BodyLeanSummary? _bodyLean;
        private int _sampleCount;
        private int _droppedSampleCount;
        private int _finished;
        private Task<string> _finishTask;

        /// <summary>
        /// Creates a bounded capture for one player. A maximum of zero is
        /// allowed and produces a metadata-only capture.
        /// </summary>
        public DiagnosticCapture(Player player, string outputDirectory, int maximumSamples)
        {
            _player = player ?? throw new ArgumentNullException(nameof(player));
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("An output directory is required.", nameof(outputDirectory));
            }

            _outputDirectory = outputDirectory;
            _maximumSamples = Math.Max(0, maximumSamples);
            _samples = new SampleSnapshot[_maximumSamples];
            _localBotId = ReadLocalBotId(player);
            _unityVersion = Application.unityVersion;
            _gameVersion = Application.version;
            _assemblyCSharpMvid = typeof(Player).Assembly.ManifestModule.ModuleVersionId.ToString();
            _accessors = PlayerAccessors.Create(player);
        }

        /// <summary>
        /// Gets whether the sample buffer reached its configured cap.
        /// </summary>
        public bool IsFull
        {
            get
            {
                return Volatile.Read(ref _sampleCount) >= _maximumSamples;
            }
        }

        public int SampleCount
        {
            get
            {
                return Volatile.Read(ref _sampleCount);
            }
        }

        // which clip frame is showing and which feet it calls planted, so slide can be measured over the windows
        // the animation itself defines rather than EFT's out-of-phase FootStep curve
        internal Func<PoseProbeSnapshot> PoseProbe
        {
            get { return _accessors == null ? null : _accessors.PoseProbe; }
            set { if (_accessors != null) _accessors.PoseProbe = value; }
        }

        /// <summary>
        /// Records one observation at the supplied pipeline stage.
        /// </summary>
        public void Record(string stage)
        {
            if (Volatile.Read(ref _finished) != 0)
            {
                return;
            }

            string normalizedStage = NormalizeStage(stage);
            lock (_gate)
            {
                if (_finished != 0)
                {
                    return;
                }

                if (_sampleCount >= _maximumSamples)
                {
                    _droppedSampleCount++;
                    return;
                }

                int sampleIndex = _sampleCount;
                SampleSnapshot sample;
                try
                {
                    sample = _accessors.Capture(sampleIndex, normalizedStage);
                }
                catch (Exception exception)
                {
                    // A diagnostic must never change gameplay if an optional
                    // runtime member is unavailable or is being torn down.
                    sample = SampleSnapshot.CreateEmpty(sampleIndex, normalizedStage, exception.GetType().Name);
                }

                _samples[sampleIndex] = sample;
                _sampleCount = sampleIndex + 1;
                AddStageCount(normalizedStage);
            }
        }

        internal void SetPosePlaybackSummary(PosePlaybackSummary summary)
        {
            lock (_gate)
            {
                if (_finishTask == null)
                {
                    _posePlayback = summary;
                }
            }
        }

        internal void SetBodyLeanSummary(BodyLeanSummary summary)
        {
            lock (_gate)
            {
                if (_finishTask == null)
                {
                    _bodyLean = summary;
                }
            }
        }

        internal void SetFootLockSummary(FootLockSummary summary)
        {
            lock (_gate)
            {
                if (_finishTask == null)
                {
                    _footLock = summary;
                }
            }
        }

        internal void SetFootPlacerSummary(FootPlacerSummary summary)
        {
            lock (_gate)
            {
                if (_finishTask == null)
                {
                    _footPlacer = summary;
                }
            }
        }

        internal void SetPuppetReport(PuppetReport report)
        {
            lock (_gate)
            {
                if (_finishTask == null)
                {
                    _puppet = report;
                }
            }
        }

        internal void SetRouteSummary(RouteSummary summary)
        {
            lock (_gate)
            {
                if (_finishTask == null)
                {
                    _route = summary;
                }
            }
        }

        /// <summary>
        /// Freezes this capture and asynchronously writes its detached JSON.
        /// The returned value is the absolute path of the written file.
        /// </summary>
        public Task<string> FinishAsync(string reason)
        {
            lock (_gate)
            {
                if (_finishTask != null)
                {
                    return _finishTask;
                }

                _finished = 1;
                int sampleCount = _sampleCount;
                SampleSnapshot[] samples = new SampleSnapshot[sampleCount];
                Array.Copy(_samples, samples, sampleCount);

                Dictionary<string, int> stageCounts = new Dictionary<string, int>(_stageCounts, StringComparer.Ordinal);
                CaptureDocument document = new CaptureDocument
                {
                    Schema = "manimal.motionmatching.diagnostic.v2",
                    ClearancePolicy = LegClearance.CapturePolicy,
                    LocalBotId = _localBotId,
                    UnityVersion = _unityVersion,
                    GameVersion = _gameVersion,
                    AssemblyCSharpMvid = _assemblyCSharpMvid,
                    Reason = NormalizeReason(reason),
                    FinishedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    MaximumSamples = _maximumSamples,
                    CapturedSamples = sampleCount,
                    BufferFull = sampleCount >= _maximumSamples,
                    DroppedSampleCount = _droppedSampleCount,
                    StageCounts = stageCounts,
                    Route = _route,
                    Puppet = _puppet,
                    FootLock = _footLock,
                    FootPlacer = _footPlacer,
                    PosePlayback = _posePlayback,
                    BodyLean = _bodyLean,
                    LayerEncoding = "Animator layer arrays (LayerNormalizedTimes, LayerLengths, LayerNextNormalizedTimes) are recorded on after_visual samples only. Full Layers records appear on the first after_visual sample and whenever any layer's current state, next state, or transition flag changes; otherwise carry the last Layers forward.",
                    MotionSyncDataNote = "MotionSync contains Root_Joint-local sagittal arm and thigh segment angles at every captured stage. Each Has*Angle flag is required; unavailable angle fields are storage defaults, not measured zeros.",
                    PathDataNote = "Path context is read from AIData.BotOwner.Mover and its ActualPathController when available; samples mark Path.Available=false when the chain is unavailable.",
                    Samples = samples
                };

                string filePath = BuildOutputPath(_outputDirectory, _localBotId);
                _finishTask = Task.Run(() => WriteDocument(filePath, document));
                return _finishTask;
            }
        }

        private void AddStageCount(string stage)
        {
            int count;
            if (_stageCounts.TryGetValue(stage, out count))
            {
                _stageCounts[stage] = count + 1;
                return;
            }

            if (_stageCounts.Count >= MaximumStageCountEntries - 1)
            {
                const string otherStage = "(other stages)";
                if (_stageCounts.TryGetValue(otherStage, out count))
                {
                    _stageCounts[otherStage] = count + 1;
                }
                else
                {
                    _stageCounts[otherStage] = 1;
                }
                return;
            }

            _stageCounts.Add(stage, 1);
        }

        private static string WriteDocument(string filePath, CaptureDocument document)
        {
            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            JsonSerializer serializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                Culture = CultureInfo.InvariantCulture,
                NullValueHandling = NullValueHandling.Ignore
            });
            using (StreamWriter writer = new StreamWriter(filePath, false, new UTF8Encoding(false)))
            using (JsonTextWriter jsonWriter = new JsonTextWriter(writer)
            {
                Formatting = Formatting.None,
                Culture = CultureInfo.InvariantCulture
            })
            {
                serializer.Serialize(jsonWriter, document);
            }
            return filePath;
        }

        private static string BuildOutputPath(string outputDirectory, string localBotId)
        {
            int sequence = Interlocked.Increment(ref _captureSequence);
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string safeBotId = SanitizeFilePart(localBotId);
            string fileName = string.Concat("motion-capture-", safeBotId, "-", timestamp, "-", sequence.ToString("D4", CultureInfo.InvariantCulture), ".json");
            return Path.GetFullPath(Path.Combine(outputDirectory, fileName));
        }

        private static string ReadLocalBotId(Player player)
        {
            try
            {
                return player.PlayerId.ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
                return "unknown";
            }
        }

        private static string NormalizeStage(string stage)
        {
            if (string.IsNullOrEmpty(stage))
            {
                return "(unspecified)";
            }

            string normalized = stage.Trim();
            if (normalized.Length == 0)
            {
                return "(unspecified)";
            }

            return normalized.Length <= MaximumStageNameLength
                ? normalized
                : normalized.Substring(0, MaximumStageNameLength);
        }

        private static string NormalizeReason(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return "(unspecified)";
            }

            string normalized = reason.Trim();
            if (normalized.Length == 0)
            {
                return "(unspecified)";
            }

            return normalized.Length <= MaximumReasonLength
                ? normalized
                : normalized.Substring(0, MaximumReasonLength);
        }

        private static string SanitizeFilePart(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "unknown";
            }

            StringBuilder builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if ((character >= 'a' && character <= 'z') ||
                    (character >= 'A' && character <= 'Z') ||
                    (character >= '0' && character <= '9') ||
                    character == '-' || character == '_')
                {
                    builder.Append(character);
                }
                else
                {
                    builder.Append('_');
                }
            }

            return builder.Length == 0 ? "unknown" : builder.ToString();
        }

        private sealed class PlayerAccessors
        {
            private readonly CachedTransform _root;
            private readonly CachedTransform _pelvis;
            private readonly GrounderAccessors _grounder;
            private readonly AnimatorAccessors _animator;
            private readonly PathAccessors _path;
            private readonly MemberAccessor _velocity;
            private readonly MemberAccessor _motion;
            private readonly MemberAccessor _deltaTime;
            private readonly MemberAccessor _updateQueue;
            private readonly MemberAccessor _isVisible;
            private readonly MemberAccessor _isVisibleToCamera;
            private readonly MemberAccessor _usedSimplifiedSkeleton;
            private readonly MemberAccessor _playerSpeed;
            private readonly MemberAccessor _sprintEnabled;
            private readonly MemberAccessor _movementContext;
            private readonly MemberAccessor _maxSpeed;
            private readonly MemberAccessor _stateSpeedLimit;
            private readonly CachedTransform _leftFoot;
            private readonly CachedTransform _rightFoot;
            private readonly CachedTransform _leftKnee;
            private readonly CachedTransform _rightKnee;
            private readonly CachedTransform _leftHip;
            private readonly CachedTransform _rightHip;
            private readonly CachedTransform _leftToe;
            private readonly CachedTransform _rightToe;
            // foot orientation lets stop retargeting match Tarkov's idle foot rotation, not just position
            private readonly Transform _leftFootBone;
            // full-body kinematics for the movement analysis: Root_Joint-space position and rotation per bone,
            // written on the after_visual and after_lock stages (the animator's pose and the final pose)
            private static readonly string[] BoneNames = { "Base HumanPelvis", "Base HumanSpine1", "Base HumanSpine2", "Base HumanSpine3", "Base HumanRibcage", "Base HumanNeck", "Base HumanHead", "Base HumanLUpperarm", "Base HumanRUpperarm", "Base HumanLForearm1", "Base HumanRForearm1",
                "Base HumanLThigh1", "Base HumanLCalf", "Base HumanLFoot", "Base HumanLToe", "Base HumanRThigh1", "Base HumanRCalf", "Base HumanRFoot", "Base HumanRToe" };
            private readonly Transform[] _bones = new Transform[BoneNames.Length];
            private readonly Transform _rootJoint;
            private readonly Player _typedPlayer;
            private readonly Transform _rightFootBone;
            private readonly MotionSyncProbe _motionSync;

            private PlayerAccessors(
                Player player,
                CachedTransform root,
                CachedTransform pelvis,
                GrounderAccessors grounder,
                AnimatorAccessors animator,
                PathAccessors path,
                MemberAccessor velocity,
                MemberAccessor motion,
                MemberAccessor deltaTime,
                MemberAccessor updateQueue,
                MemberAccessor isVisible,
                MemberAccessor isVisibleToCamera,
                MemberAccessor usedSimplifiedSkeleton)
            {
                _playerForCapture = player;
                _root = root;
                _pelvis = pelvis;
                _grounder = grounder;
                _animator = animator;
                _path = path;
                _velocity = velocity;
                _motion = motion;
                _deltaTime = deltaTime;
                _updateQueue = updateQueue;
                _isVisible = isVisible;
                _isVisibleToCamera = isVisibleToCamera;
                _usedSimplifiedSkeleton = usedSimplifiedSkeleton;
                Type playerType = player.GetType();
                _playerSpeed = MemberAccessor.Find(playerType, "Speed");
                _sprintEnabled = MemberAccessor.Find(playerType, "IsSprintEnabled");
                _movementContext = MemberAccessor.Find(playerType, "MovementContext");
                object movementContext = _movementContext.GetValue(player);
                Type movementType = movementContext == null ? null : movementContext.GetType();
                _maxSpeed = MemberAccessor.Find(movementType, "MaxSpeed");
                _stateSpeedLimit = MemberAccessor.Find(movementType, "StateSpeedLimit");
                // Read actual bones even if the terrain grounder has never initialized.
                // Grounder metadata remains independently unavailable in that case.
                var ikSolver = player.Grounder?.ik?.solver;
                _leftFoot = CachedTransform.Create(ikSolver?.leftFootEffector?.bone, null);
                _rightFoot = CachedTransform.Create(ikSolver?.rightFootEffector?.bone, null);
                // knee/hip let the analysis catch knee pops and legs the foot lock straightened
                var references = player.Grounder?.ik?.references;
                _leftKnee = CachedTransform.Create(references?.leftCalf, null);
                _rightKnee = CachedTransform.Create(references?.rightCalf, null);
                _leftHip = CachedTransform.Create(references?.leftThigh, null);
                _rightHip = CachedTransform.Create(references?.rightThigh, null);
                _leftFootBone = references?.leftFoot;
                _typedPlayer = player as Player;
                _rootJoint = references?.pelvis != null ? references.pelvis.parent : null;
                if (_rootJoint != null)
                    for (int i = 0; i < BoneNames.Length; i++)
                        _bones[i] = FindChild(_rootJoint, BoneNames[i]);
                _rightFootBone = references?.rightFoot;
                _leftToe = CachedTransform.Create(_leftFootBone && _leftFootBone.childCount > 0 ? _leftFootBone.GetChild(0) : null, null);
                _rightToe = CachedTransform.Create(_rightFootBone && _rightFootBone.childCount > 0 ? _rightFootBone.GetChild(0) : null, null);
                _motionSync = MotionSyncProbe.Create(player);
            }

            public static PlayerAccessors Create(Player player)
            {
                Type playerType = player.GetType();
                Transform fallbackRoot = null;
                try
                {
                    Component component = player as Component;
                    fallbackRoot = component == null ? null : component.transform;
                }
                catch
                {
                }

                MemberAccessor transformMember = MemberAccessor.Find(playerType, "Transform");
                object rootFacade = transformMember.GetValue(player);
                CachedTransform root = CachedTransform.Create(rootFacade, fallbackRoot);

                MemberAccessor playerBonesMember = MemberAccessor.Find(playerType, "PlayerBones");
                object playerBones = playerBonesMember.GetValue(player);
                MemberAccessor pelvisMember = MemberAccessor.Find(playerBones == null ? null : playerBones.GetType(), "Pelvis");
                CachedTransform pelvis = CachedTransform.Create(pelvisMember.GetValue(playerBones), null);

                MemberAccessor grounderMember = MemberAccessor.Find(playerType, "Grounder");
                GrounderAccessors grounder = GrounderAccessors.Create(grounderMember.GetValue(player));

                MemberAccessor animatorMember = MemberAccessor.Find(playerType, "BodyAnimatorCommon");
                AnimatorAccessors animator = AnimatorAccessors.Create(animatorMember.GetValue(player));
                PathAccessors path = PathAccessors.Create(player, playerType);

                return new PlayerAccessors(
                    player,
                    root,
                    pelvis,
                    grounder,
                    animator,
                    path,
                    MemberAccessor.Find(playerType, "Velocity"),
                    MemberAccessor.Find(playerType, "Motion"),
                    MemberAccessor.Find(playerType, "DeltaTime"),
                    MemberAccessor.Find(playerType, "UpdateQueue"),
                    MemberAccessor.Find(playerType, "IsVisible"),
                    MemberAccessor.Find(playerType, "IsVisibleToCamera"),
                    MemberAccessor.Find(playerType, "UsedSimplifiedSkeleton"));
            }

            // set by the owning capture; see DiagnosticCapture.PoseProbe
            public Func<PoseProbeSnapshot> PoseProbe;

            public SampleSnapshot Capture(int index, string stage)
            {
                Vector3Snapshot velocity;
                bool velocityAvailable = TryReadVector3(_velocity.GetValue(_playerForCapture), out velocity);
                Vector3Snapshot motion;
                bool motionAvailable = TryReadVector3(_motion.GetValue(_playerForCapture), out motion);
                bool isVisible;
                bool hasIsVisible = TryReadBool(_isVisible.GetValue(_playerForCapture), out isVisible);
                bool isVisibleToCamera;
                bool hasIsVisibleToCamera = TryReadBool(_isVisibleToCamera.GetValue(_playerForCapture), out isVisibleToCamera);
                bool usedSimplifiedSkeleton;
                bool hasUsedSimplifiedSkeleton = TryReadBool(_usedSimplifiedSkeleton.GetValue(_playerForCapture), out usedSimplifiedSkeleton);
                SampleSnapshot sample = new SampleSnapshot
                {
                    Index = index,
                    Stage = stage,
                    Frame = Time.frameCount,
                    Time = Time.time,
                    UnscaledTime = Time.unscaledTime,
                    UnityDeltaTime = Time.deltaTime,
                    HasDeltaTime = false,
                    UpdateQueue = ValueToString(_updateQueue.GetValue(_playerForCapture), "unknown"),
                    Root = _root.Read(),
                    Pelvis = _pelvis.Read(),
                    Animator = _animator.Capture(string.Equals(stage, FinalStage, StringComparison.Ordinal)),
                    MotionSync = _motionSync == null ? default(MotionSyncProbeSnapshot) : _motionSync.Capture(),
                    Movement = CaptureMovement(),
                    Grounder = _grounder.Capture(),
                    Pose = PoseProbe == null ? default(PoseProbeSnapshot) : PoseProbe(),
                    Path = _path.Capture(_playerForCapture),
                    Culling = _animator.CaptureCulling(),
                    Velocity = velocity,
                    VelocityAvailable = velocityAvailable,
                    Motion = motion,
                    MotionAvailable = motionAvailable,
                    HasIsVisible = hasIsVisible,
                    IsVisible = isVisible,
                    HasIsVisibleToCamera = hasIsVisibleToCamera,
                    IsVisibleToCamera = isVisibleToCamera,
                    HasUsedSimplifiedSkeleton = hasUsedSimplifiedSkeleton,
                    UsedSimplifiedSkeleton = usedSimplifiedSkeleton
                };

                PositionSnapshot leftFoot = _leftFoot.Read();
                PositionSnapshot rightFoot = _rightFoot.Read();
                if (leftFoot.Available)
                {
                    sample.Grounder.LeftLeg.HasFootPosition = true;
                    sample.Grounder.LeftLeg.FootPosition = leftFoot.Value;
                    sample.Grounder.LeftLeg.FootPositionSource = "FBBIK left foot bone";
                }
                if (rightFoot.Available)
                {
                    sample.Grounder.RightLeg.HasFootPosition = true;
                    sample.Grounder.RightLeg.FootPosition = rightFoot.Value;
                    sample.Grounder.RightLeg.FootPositionSource = "FBBIK right foot bone";
                }
                ReadJoint(_leftKnee, ref sample.Grounder.LeftLeg.HasKneePosition, ref sample.Grounder.LeftLeg.KneePosition);
                ReadJoint(_rightKnee, ref sample.Grounder.RightLeg.HasKneePosition, ref sample.Grounder.RightLeg.KneePosition);
                ReadJoint(_leftHip, ref sample.Grounder.LeftLeg.HasHipPosition, ref sample.Grounder.LeftLeg.HipPosition);
                ReadJoint(_rightHip, ref sample.Grounder.RightLeg.HasHipPosition, ref sample.Grounder.RightLeg.HipPosition);
                ReadJoint(_leftToe, ref sample.Grounder.LeftLeg.HasToePosition, ref sample.Grounder.LeftLeg.ToePosition);
                ReadJoint(_rightToe, ref sample.Grounder.RightLeg.HasToePosition, ref sample.Grounder.RightLeg.ToePosition);
                ReadRotation(_leftFootBone, ref sample.Grounder.LeftLeg.HasFootRotation, ref sample.Grounder.LeftLeg.FootRotation);
                ReadRotation(_rightFootBone, ref sample.Grounder.RightLeg.HasFootRotation, ref sample.Grounder.RightLeg.FootRotation);
                if (_typedPlayer != null)
                    sample.BodyYaw = _typedPlayer.Rotation.x;
                CaptureWeaponContext(ref sample);
                if (_rootJoint != null)
                {
                    sample.RootJoint = Vector3Snapshot.From(_rootJoint.position);
                    sample.RootJointYaw = _rootJoint.eulerAngles.y;
                    if (string.Equals(stage, FinalStage, StringComparison.Ordinal) || string.Equals(stage, "after_lock", StringComparison.Ordinal)
                        || string.Equals(stage, "before_visual", StringComparison.Ordinal))
                        sample.Bones = ReadBones();
                }

                object deltaTimeValue = _deltaTime.GetValue(_playerForCapture);
                float deltaTime;
                if (TryReadFloat(deltaTimeValue, out deltaTime))
                {
                    sample.DeltaTime = deltaTime;
                    sample.HasDeltaTime = true;
                }

                return sample;
            }

            private void CaptureWeaponContext(ref SampleSnapshot sample)
            {
                if (_typedPlayer == null)
                    return;

                try
                {
                    if (_typedPlayer.ProceduralWeaponAnimation != null)
                    {
                        sample.HasAiming = true;
                        sample.IsAiming = _typedPlayer.ProceduralWeaponAnimation.IsAiming;
                    }
                }
                catch
                {
                }

                try
                {
                    var owner = _typedPlayer.AIData?.BotOwner;
                    if (owner != null && owner.ShootData != null)
                    {
                        sample.HasShooting = true;
                        sample.IsShooting = owner.ShootData.Shooting;
                    }
                }
                catch
                {
                }
            }

            private static void ReadRotation(Transform bone, ref bool has, ref float[] value)
            {
                has = bone;
                if (!has) return;
                Quaternion q = bone.rotation;
                value = new[] { q.x, q.y, q.z, q.w };
            }

            private BoneSnapshot[] ReadBones()
            {
                var list = new BoneSnapshot[_bones.Length];
                Quaternion inverse = Quaternion.Inverse(_rootJoint.rotation);
                for (int i = 0; i < _bones.Length; i++)
                {
                    var bone = _bones[i];
                    if (!bone) continue;
                    Vector3 p = _rootJoint.InverseTransformPoint(bone.position);
                    Quaternion r = inverse * bone.rotation;
                    list[i] = new BoneSnapshot { N = BoneNames[i], P = new[] { p.x, p.y, p.z }, R = new[] { r.x, r.y, r.z, r.w } };
                }
                return list;
            }

            private static Transform FindChild(Transform node, string name)
            {
                if (!node) return null;
                if (node.name == name) return node;
                for (int i = 0; i < node.childCount; i++)
                {
                    var found = FindChild(node.GetChild(i), name);
                    if (found) return found;
                }
                return null;
            }

            private static void ReadJoint(CachedTransform joint, ref bool has, ref Vector3Snapshot value)
            {
                PositionSnapshot read = joint.Read();
                has = read.Available;
                value = read.Value;
            }

            private MovementSnapshot CaptureMovement()
            {
                object movementContext = _movementContext.GetValue(_playerForCapture);
                MovementSnapshot movement = new MovementSnapshot();
                movement.HasPlayerSpeed = TryReadFloat(_playerSpeed.GetValue(_playerForCapture), out movement.PlayerSpeed);
                movement.HasSprintEnabled = TryReadBool(_sprintEnabled.GetValue(_playerForCapture), out movement.SprintEnabled);
                movement.HasMaxSpeed = TryReadFloat(_maxSpeed.GetValue(movementContext), out movement.MaxSpeed);
                movement.HasStateSpeedLimit = TryReadFloat(_stateSpeedLimit.GetValue(movementContext), out movement.StateSpeedLimit);
                return movement;
            }

            // Assigned once by Create. Keeping the Player only in this
            // game-thread accessor object prevents it from entering the
            // detached document passed to Task.Run.
            private Player _playerForCapture;
        }

        private sealed class PathAccessors
        {
            private readonly MemberAccessor _aiData;
            private readonly MemberAccessor _botOwner;
            private readonly MemberAccessor _mover;
            private readonly MemberAccessor _pathController;
            private readonly MemberAccessor _havePath;
            private readonly MemberAccessor _remainingDistance;
            private readonly MemberAccessor _currentCorner;
            private readonly MemberAccessor _previousCorner;
            private readonly MemberAccessor _targetPoint;
            private readonly MemberAccessor _desiredDirection;
            private readonly MemberAccessor _isMoving;
            private readonly MemberAccessor _destinationSpeed;

            private PathAccessors(
                MemberAccessor aiData,
                MemberAccessor botOwner,
                MemberAccessor mover,
                MemberAccessor pathController,
                MemberAccessor havePath,
                MemberAccessor remainingDistance,
                MemberAccessor currentCorner,
                MemberAccessor previousCorner,
                MemberAccessor targetPoint,
                MemberAccessor desiredDirection,
                MemberAccessor isMoving,
                MemberAccessor destinationSpeed)
            {
                _aiData = aiData;
                _botOwner = botOwner;
                _mover = mover;
                _pathController = pathController;
                _havePath = havePath;
                _remainingDistance = remainingDistance;
                _currentCorner = currentCorner;
                _previousCorner = previousCorner;
                _targetPoint = targetPoint;
                _desiredDirection = desiredDirection;
                _isMoving = isMoving;
                _destinationSpeed = destinationSpeed;
            }

            public static PathAccessors Create(Player player, Type playerType)
            {
                MemberAccessor aiData = MemberAccessor.Find(playerType, "AIData");
                object aiDataValue = aiData.GetValue(player);
                MemberAccessor botOwner = MemberAccessor.Find(aiDataValue == null ? null : aiDataValue.GetType(), "BotOwner");
                object botOwnerValue = botOwner.GetValue(aiDataValue);
                MemberAccessor mover = MemberAccessor.Find(botOwnerValue == null ? null : botOwnerValue.GetType(), "Mover");
                object moverValue = mover.GetValue(botOwnerValue);
                MemberAccessor pathController = MemberAccessor.Find(moverValue == null ? null : moverValue.GetType(), "ActualPathController");
                object pathControllerValue = pathController.GetValue(moverValue);
                Type pathType = pathControllerValue == null ? null : pathControllerValue.GetType();
                Type moverType = moverValue == null ? null : moverValue.GetType();

                return new PathAccessors(
                    aiData,
                    botOwner,
                    mover,
                    pathController,
                    MemberAccessor.Find(pathType, "HavePath"),
                    MemberAccessor.Find(pathType, "PlayerRemainingDist"),
                    MemberAccessor.Find(pathType, "CurrentCornerPoint"),
                    MemberAccessor.Find(pathType, "PrevCorner"),
                    MemberAccessor.Find(pathType, "TargetPoint"),
                    MemberAccessor.Find(moverType, "DirCurPoint"),
                    MemberAccessor.Find(moverType, "IsMoving"),
                    MemberAccessor.Find(moverType, "DestMoveSpeed"));
            }

            public PathSnapshot Capture(Player player)
            {
                object aiData = _aiData.GetValue(player);
                object botOwner = _botOwner.GetValue(aiData);
                object mover = _mover.GetValue(botOwner);
                object pathController = _pathController.GetValue(mover);
                if (mover == null && pathController == null)
                {
                    return default(PathSnapshot);
                }

                Vector3Snapshot currentCorner;
                bool hasCurrentCorner = TryReadVector3(_currentCorner.GetValue(pathController), out currentCorner);
                Vector3Snapshot previousCorner;
                bool hasPreviousCorner = TryReadVector3(_previousCorner.GetValue(pathController), out previousCorner);
                Vector3Snapshot targetPoint;
                bool hasTargetPoint = TryReadVector3(_targetPoint.GetValue(pathController), out targetPoint);
                Vector3Snapshot desiredDirection;
                bool hasDesiredDirection = TryReadVector3(_desiredDirection.GetValue(mover), out desiredDirection);

                return new PathSnapshot
                {
                    Available = true,
                    MoverType = mover == null ? null : mover.GetType().FullName,
                    PathControllerType = pathController == null ? null : pathController.GetType().FullName,
                    HasPath = ReadBool(_havePath.GetValue(pathController)),
                    HasRemainingDistance = TryReadFloat(_remainingDistance.GetValue(pathController), out float remainingDistance),
                    RemainingDistance = remainingDistance,
                    HasCurrentCorner = hasCurrentCorner,
                    CurrentCorner = currentCorner,
                    HasPreviousCorner = hasPreviousCorner,
                    PreviousCorner = previousCorner,
                    HasTargetPoint = hasTargetPoint,
                    TargetPoint = targetPoint,
                    HasDesiredDirection = hasDesiredDirection,
                    DesiredDirection = desiredDirection,
                    HasIsMoving = _isMoving.HasValue,
                    IsMoving = ReadBool(_isMoving.GetValue(mover)),
                    HasDestinationSpeed = TryReadFloat(_destinationSpeed.GetValue(mover), out float destinationSpeed),
                    DestinationSpeed = destinationSpeed
                };
            }
        }

        private sealed class CachedTransform
        {
            private readonly object _owner;
            private readonly MemberAccessor _position;
            private readonly MemberAccessor _original;
            private readonly Transform _transform;

            private CachedTransform(object owner, MemberAccessor position, MemberAccessor original, Transform transform)
            {
                _owner = owner;
                _position = position;
                _original = original;
                _transform = transform;
            }

            public static CachedTransform Create(object facade, Transform fallback)
            {
                if (facade == null)
                {
                    return new CachedTransform(null, MemberAccessor.Empty, MemberAccessor.Empty, fallback);
                }

                Transform directTransform = facade as Transform;
                if (directTransform != null)
                {
                    return new CachedTransform(facade, MemberAccessor.Empty, MemberAccessor.Empty, directTransform);
                }

                Type type = facade.GetType();
                return new CachedTransform(
                    facade,
                    MemberAccessor.Find(type, "position"),
                    MemberAccessor.Find(type, "Original"),
                    fallback);
            }

            public PositionSnapshot Read()
            {
                Vector3 position;
                if (_transform != null)
                {
                    try
                    {
                        position = _transform.position;
                        return new PositionSnapshot { Available = true, Value = Vector3Snapshot.From(position) };
                    }
                    catch
                    {
                        return default(PositionSnapshot);
                    }
                }

                object value = _position.GetValue(_owner);
                if (value is Vector3)
                {
                    return new PositionSnapshot { Available = true, Value = Vector3Snapshot.From((Vector3)value) };
                }

                Transform original = _original.GetValue(_owner) as Transform;
                if (original != null)
                {
                    try
                    {
                        return new PositionSnapshot { Available = true, Value = Vector3Snapshot.From(original.position) };
                    }
                    catch
                    {
                    }
                }

                return default(PositionSnapshot);
            }
        }

        private sealed class GrounderAccessors
        {
            private readonly object _grounder;
            private readonly object _solver;
            private readonly MemberAccessor _weight;
            private readonly MemberAccessor _solverGrounded;
            private readonly MemberAccessor _rootGrounded;
            private readonly GrounderLegAccessors[] _legs;

            private GrounderAccessors(
                object grounder,
                object solver,
                MemberAccessor weight,
                MemberAccessor solverGrounded,
                MemberAccessor rootGrounded,
                GrounderLegAccessors[] legs)
            {
                _grounder = grounder;
                _solver = solver;
                _weight = weight;
                _solverGrounded = solverGrounded;
                _rootGrounded = rootGrounded;
                _legs = legs;
            }

            public static GrounderAccessors Create(object grounder)
            {
                if (grounder == null)
                {
                    return new GrounderAccessors(null, null, MemberAccessor.Empty, MemberAccessor.Empty, MemberAccessor.Empty, new GrounderLegAccessors[0]);
                }

                MemberAccessor grounderTypeSolver = MemberAccessor.Find(grounder.GetType(), "solver");
                object solver = grounderTypeSolver.GetValue(grounder);
                if (solver == null)
                {
                    return new GrounderAccessors(grounder, null, MemberAccessor.Find(grounder.GetType(), "weight"), MemberAccessor.Empty, MemberAccessor.Empty, new GrounderLegAccessors[0]);
                }

                MemberAccessor legsMember = MemberAccessor.Find(solver.GetType(), "legs");
                Array legs = legsMember.GetValue(solver) as Array;
                int legCount = legs == null ? 0 : Math.Min(legs.Length, 2);
                GrounderLegAccessors[] legAccessors = new GrounderLegAccessors[legCount];
                for (int i = 0; i < legCount; i++)
                {
                    legAccessors[i] = GrounderLegAccessors.Create(legs.GetValue(i));
                }

                return new GrounderAccessors(
                    grounder,
                    solver,
                    MemberAccessor.Find(grounder.GetType(), "weight"),
                    MemberAccessor.Find(solver.GetType(), "isGrounded"),
                    MemberAccessor.Find(solver.GetType(), "rootGrounded"),
                    legAccessors);
            }

            public GrounderSnapshot Capture()
            {
                if (_grounder == null || _solver == null)
                {
                    return default(GrounderSnapshot);
                }

                bool isGrounded;
                bool hasIsGrounded = TryReadBool(_solverGrounded.GetValue(_solver), out isGrounded);
                bool rootGrounded;
                bool hasRootGrounded = TryReadBool(_rootGrounded.GetValue(_solver), out rootGrounded);
                float weight;
                bool hasWeight = TryReadFloat(_weight.GetValue(_grounder), out weight);
                GrounderSnapshot snapshot = new GrounderSnapshot
                {
                    Available = true,
                    HasIsGrounded = hasIsGrounded,
                    IsGrounded = isGrounded,
                    HasRootGrounded = hasRootGrounded,
                    RootGrounded = rootGrounded,
                    HasWeight = hasWeight,
                    Weight = weight,
                    LegCount = _legs.Length
                };

                if (_legs.Length > 0)
                {
                    snapshot.LeftLeg = _legs[0].Capture();
                }
                if (_legs.Length > 1)
                {
                    snapshot.RightLeg = _legs[1].Capture();
                }
                return snapshot;
            }
        }

        private sealed class GrounderLegAccessors
        {
            private readonly object _leg;
            private readonly MemberAccessor _isGrounded;
            private readonly MemberAccessor _ikPosition;
            private readonly MemberAccessor _ikOffset;
            private readonly MemberAccessor _heightFromGround;
            private readonly MemberAccessor _velocity;
            private readonly MemberAccessor _initiated;
            private readonly CachedTransform _transform;

            private GrounderLegAccessors(
                object leg,
                MemberAccessor isGrounded,
                MemberAccessor ikPosition,
                MemberAccessor ikOffset,
                MemberAccessor heightFromGround,
                MemberAccessor velocity,
                MemberAccessor initiated,
                CachedTransform transform)
            {
                _leg = leg;
                _isGrounded = isGrounded;
                _ikPosition = ikPosition;
                _ikOffset = ikOffset;
                _heightFromGround = heightFromGround;
                _velocity = velocity;
                _initiated = initiated;
                _transform = transform;
            }

            public static GrounderLegAccessors Create(object leg)
            {
                if (leg == null)
                {
                    return new GrounderLegAccessors(null, MemberAccessor.Empty, MemberAccessor.Empty, MemberAccessor.Empty, MemberAccessor.Empty, MemberAccessor.Empty, MemberAccessor.Empty, CachedTransform.Create(null, null));
                }

                Type type = leg.GetType();
                MemberAccessor transformMember = MemberAccessor.Find(type, "transform");
                return new GrounderLegAccessors(
                    leg,
                    MemberAccessor.Find(type, "isGrounded"),
                    MemberAccessor.Find(type, "IKPosition"),
                    MemberAccessor.Find(type, "IKOffset"),
                    MemberAccessor.Find(type, "heightFromGround"),
                    MemberAccessor.Find(type, "velocity"),
                    MemberAccessor.Find(type, "initiated"),
                    CachedTransform.Create(transformMember.GetValue(leg), null));
            }

            public GrounderLegSnapshot Capture()
            {
                if (_leg == null)
                {
                    return default(GrounderLegSnapshot);
                }

                PositionSnapshot foot = _transform.Read();
                Vector3Snapshot ikPosition;
                bool hasIkPosition = TryReadVector3(_ikPosition.GetValue(_leg), out ikPosition);
                Vector3Snapshot legVelocity;
                bool hasVelocity = TryReadVector3(_velocity.GetValue(_leg), out legVelocity);
                bool isGrounded;
                bool hasIsGrounded = TryReadBool(_isGrounded.GetValue(_leg), out isGrounded);
                bool initiated;
                bool hasInitiated = TryReadBool(_initiated.GetValue(_leg), out initiated);
                float ikOffset;
                bool hasIkOffset = TryReadFloat(_ikOffset.GetValue(_leg), out ikOffset);
                float heightFromGround;
                bool hasHeightFromGround = TryReadFloat(_heightFromGround.GetValue(_leg), out heightFromGround);
                GrounderLegSnapshot snapshot = new GrounderLegSnapshot
                {
                    Available = true,
                    HasIsGrounded = hasIsGrounded,
                    IsGrounded = isGrounded,
                    HasInitiated = hasInitiated,
                    Initiated = initiated,
                    HasFootPosition = foot.Available,
                    FootPosition = foot.Value,
                    HasIKPosition = hasIkPosition,
                    IKPosition = ikPosition,
                    HasIKOffset = hasIkOffset,
                    IKOffset = ikOffset,
                    HasHeightFromGround = hasHeightFromGround,
                    HeightFromGround = heightFromGround,
                    Velocity = legVelocity,
                    HasVelocity = hasVelocity
                };
                return snapshot;
            }
        }

        private sealed class AnimatorAccessors
        {
            private readonly object _animator;
            private readonly Type _animatorType;
            private readonly MemberAccessor _runtimeAnimatorController;
            private readonly MemberAccessor _layerCount;
            private readonly MemberAccessor _enabled;
            private readonly MemberAccessor _isActiveAndEnabled;
            private readonly MemberAccessor _cullingMode;
            private readonly MethodInfo _getCurrentState;
            private readonly MethodInfo _getNextState;
            private readonly MethodInfo _isInTransition;
            private readonly MethodInfo _getFloat;
            private readonly MethodInfo _hasParameter;
            private readonly MethodInfo _getStateName;
            private readonly StateInfoAccessors _stateInfo;
            private readonly int _footStepHash = Animator.StringToHash("FootStep");
            private readonly bool _footStepParameterAvailable;
            private readonly AnimatorLayerSnapshot[] _layerScratch = new AnimatorLayerSnapshot[MaximumAnimatorLayers];
            private AnimatorLayerSnapshot[] _lastWrittenLayers;

            private AnimatorAccessors(
                object animator,
                Type animatorType,
                MemberAccessor runtimeAnimatorController,
                MemberAccessor layerCount,
                MemberAccessor enabled,
                MemberAccessor isActiveAndEnabled,
                MemberAccessor cullingMode,
                MethodInfo getCurrentState,
                MethodInfo getNextState,
                MethodInfo isInTransition,
                MethodInfo getFloat,
                MethodInfo hasParameter,
                MethodInfo getStateName,
                StateInfoAccessors stateInfo,
                bool footStepParameterAvailable)
            {
                _animator = animator;
                _animatorType = animatorType;
                _runtimeAnimatorController = runtimeAnimatorController;
                _layerCount = layerCount;
                _enabled = enabled;
                _isActiveAndEnabled = isActiveAndEnabled;
                _cullingMode = cullingMode;
                _getCurrentState = getCurrentState;
                _getNextState = getNextState;
                _isInTransition = isInTransition;
                _getFloat = getFloat;
                _hasParameter = hasParameter;
                _getStateName = getStateName;
                _stateInfo = stateInfo;
                _footStepParameterAvailable = footStepParameterAvailable;
            }

            public static AnimatorAccessors Create(object animator)
            {
                if (animator == null)
                {
                    return new AnimatorAccessors(null, null, MemberAccessor.Empty, MemberAccessor.Empty, MemberAccessor.Empty, MemberAccessor.Empty, MemberAccessor.Empty, null, null, null, null, null, null, null, false);
                }

                Type type = animator.GetType();
                MethodInfo current = FindMethod(type, "GetCurrentAnimatorStateInfo", typeof(int));
                MethodInfo next = FindMethod(type, "GetNextAnimatorStateInfo", typeof(int));
                MethodInfo transition = FindMethod(type, "IsInTransition", typeof(int));
                MethodInfo getFloat = FindMethod(type, "GetFloat", typeof(int));
                MethodInfo hasParameter = FindMethod(type, "HasParameter", typeof(int));
                Type stateType = current == null ? null : current.ReturnType;
                if (stateType != null && stateType.IsByRef)
                {
                    stateType = stateType.GetElementType();
                }

                bool footStepParameterAvailable = false;
                if (hasParameter != null)
                {
                    bool hasParameterValue;
                    if (TryReadBool(Invoke(hasParameter, animator, Animator.StringToHash("FootStep")), out hasParameterValue))
                    {
                        footStepParameterAvailable = hasParameterValue;
                    }
                }

                return new AnimatorAccessors(
                    animator,
                    type,
                    MemberAccessor.Find(type, "runtimeAnimatorController"),
                    MemberAccessor.Find(type, "layerCount"),
                    MemberAccessor.Find(type, "enabled"),
                    MemberAccessor.Find(type, "isActiveAndEnabled"),
                    MemberAccessor.Find(type, "cullingMode"),
                    current,
                    next,
                    transition,
                    getFloat,
                    hasParameter,
                    FindStateNameMethod(type),
                    StateInfoAccessors.Create(stateType),
                    footStepParameterAvailable);
            }

            public AnimatorSnapshot Capture(bool includeLayers)
            {
                if (_animator == null)
                {
                    return default(AnimatorSnapshot);
                }

                AnimatorSnapshot snapshot = new AnimatorSnapshot
                {
                    Available = true,
                    RuntimeType = _animatorType == null ? null : _animatorType.FullName,
                    ControllerType = GetRuntimeControllerType(),
                    FootStepCurveIsHeuristic = true,
                    HasFootStepCurve = false,
                    HasFootStepParameter = _footStepParameterAvailable,
                    HasLayerCount = false,
                    ReportedLayerCount = 0
                };

                int reportedLayerCount;
                if (TryReadInt(_layerCount.GetValue(_animator), out reportedLayerCount))
                {
                    snapshot.HasLayerCount = true;
                    snapshot.ReportedLayerCount = reportedLayerCount;
                }

                if (_getFloat != null && _footStepParameterAvailable)
                {
                    object footStep = Invoke(_getFloat, _animator, _footStepHash);
                    float value;
                    if (TryReadFloat(footStep, out value))
                    {
                        snapshot.HasFootStepCurve = true;
                        snapshot.FootStepCurve = value;
                    }
                }

                // full layer records were ~80% of v1 capture size while barely changing; per-frame
                // times/lengths keep stride phase, full records only on state changes
                int layerCount = Math.Max(0, Math.Min(snapshot.ReportedLayerCount, MaximumAnimatorLayers));
                if (includeLayers && _getCurrentState != null && _stateInfo != null && layerCount > 0)
                {
                    snapshot.CapturedLayerCount = layerCount;
                    snapshot.LayerNormalizedTimes = new float[layerCount];
                    snapshot.LayerLengths = new float[layerCount];
                    snapshot.LayerNextNormalizedTimes = new float[layerCount];
                    bool changed = _lastWrittenLayers == null || _lastWrittenLayers.Length != layerCount;
                    for (int layer = 0; layer < layerCount; layer++)
                    {
                        object current = Invoke(_getCurrentState, _animator, layer);
                        object next = _getNextState == null ? null : Invoke(_getNextState, _animator, layer);
                        bool inTransition;
                        bool hasInTransition = TryReadBool(Invoke(_isInTransition, _animator, layer), out inTransition);
                        AnimatorLayerSnapshot read = new AnimatorLayerSnapshot
                        {
                            Layer = layer,
                            HasInTransition = hasInTransition,
                            InTransition = inTransition,
                            Current = _stateInfo.Read(current, _getStateName, _animator),
                            Next = _stateInfo.Read(next, _getStateName, _animator)
                        };
                        _layerScratch[layer] = read;
                        snapshot.LayerNormalizedTimes[layer] = read.Current.NormalizedTime;
                        snapshot.LayerLengths[layer] = read.Current.Length;
                        snapshot.LayerNextNormalizedTimes[layer] = read.InTransition ? read.Next.NormalizedTime : 0f;
                        if (!changed && !SameState(read, _lastWrittenLayers[layer]))
                        {
                            changed = true;
                        }
                    }

                    if (changed)
                    {
                        AnimatorLayerSnapshot[] layers = new AnimatorLayerSnapshot[layerCount];
                        Array.Copy(_layerScratch, layers, layerCount);
                        snapshot.Layers = layers;
                        _lastWrittenLayers = layers;
                    }
                }
                return snapshot;
            }

            private static bool SameState(AnimatorLayerSnapshot a, AnimatorLayerSnapshot b)
            {
                return a.InTransition == b.InTransition &&
                    a.Current.FullPathHash == b.Current.FullPathHash &&
                    a.Next.FullPathHash == b.Next.FullPathHash;
            }

            public CullingSnapshot CaptureCulling()
            {
                bool enabled;
                bool hasEnabled = TryReadBool(_enabled.GetValue(_animator), out enabled);
                bool isActiveAndEnabled;
                bool hasIsActiveAndEnabled = TryReadBool(_isActiveAndEnabled.GetValue(_animator), out isActiveAndEnabled);
                return new CullingSnapshot
                {
                    Available = _animator != null,
                    Mode = ValueToString(_cullingMode.GetValue(_animator), "unknown"),
                    HasEnabled = hasEnabled,
                    Enabled = enabled,
                    HasIsActiveAndEnabled = hasIsActiveAndEnabled,
                    IsActiveAndEnabled = isActiveAndEnabled
                };
            }

            private string GetRuntimeControllerType()
            {
                // FastAnimatorProcessor's runtimeAnimatorController getter
                // intentionally calls Reset() before returning null. Do not
                // invoke a potentially mutating controller getter from a
                // read-only diagnostic. RuntimeType and state hashes remain
                // available below; a controller type is optional metadata.
                return null;
            }

            private static object Invoke(MethodInfo method, object target, int argument)
            {
                if (method == null || target == null)
                {
                    return null;
                }

                try
                {
                    return method.Invoke(target, new object[] { argument });
                }
                catch
                {
                    return null;
                }
            }

            private static MethodInfo FindMethod(Type type, string name, Type argumentType)
            {
                if (type == null)
                {
                    return null;
                }

                MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    ParameterInfo[] parameters = methods[i].GetParameters();
                    if (methods[i].Name == name && parameters.Length == 1 && parameters[0].ParameterType == argumentType)
                    {
                        return methods[i];
                    }
                }
                return null;
            }

            private static MethodInfo FindStateNameMethod(Type type)
            {
                if (type == null)
                {
                    return null;
                }

                MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    if (!methods[i].Name.EndsWith("GetStateName", StringComparison.Ordinal) || methods[i].ReturnType != typeof(string))
                    {
                        continue;
                    }

                    ParameterInfo[] parameters = methods[i].GetParameters();
                    if (parameters.Length == 1)
                    {
                        return methods[i];
                    }
                }
                return null;
            }
        }

        private sealed class StateInfoAccessors
        {
            private readonly MemberAccessor _fullPathHash;
            private readonly MemberAccessor _shortNameHash;
            private readonly MemberAccessor _normalizedTime;
            private readonly MemberAccessor _length;
            private readonly MemberAccessor _speed;
            private readonly MemberAccessor _loop;

            private StateInfoAccessors(Type stateType)
            {
                _fullPathHash = MemberAccessor.Find(stateType, "fullPathHash");
                _shortNameHash = MemberAccessor.Find(stateType, "shortNameHash");
                _normalizedTime = MemberAccessor.Find(stateType, "normalizedTime");
                _length = MemberAccessor.Find(stateType, "length");
                _speed = MemberAccessor.Find(stateType, "speed");
                _loop = MemberAccessor.Find(stateType, "loop");
            }

            public static StateInfoAccessors Create(Type stateType)
            {
                return stateType == null ? null : new StateInfoAccessors(stateType);
            }

            public AnimatorStateSnapshot Read(object state, MethodInfo stateNameMethod, object animator)
            {
                if (state == null)
                {
                    return default(AnimatorStateSnapshot);
                }

                int fullPathHash;
                bool hasFullPathHash = TryReadInt(_fullPathHash.GetValue(state), out fullPathHash);
                int shortNameHash;
                bool hasShortNameHash = TryReadInt(_shortNameHash.GetValue(state), out shortNameHash);
                float normalizedTime;
                bool hasNormalizedTime = TryReadFloat(_normalizedTime.GetValue(state), out normalizedTime);
                float length;
                bool hasLength = TryReadFloat(_length.GetValue(state), out length);
                float speed;
                bool hasSpeed = TryReadFloat(_speed.GetValue(state), out speed);
                bool loop;
                bool hasLoop = TryReadBool(_loop.GetValue(state), out loop);
                return new AnimatorStateSnapshot
                {
                    Available = true,
                    HasFullPathHash = hasFullPathHash,
                    FullPathHash = fullPathHash,
                    HasShortNameHash = hasShortNameHash,
                    ShortNameHash = shortNameHash,
                    HasNormalizedTime = hasNormalizedTime,
                    NormalizedTime = normalizedTime,
                    HasLength = hasLength,
                    Length = length,
                    HasSpeed = hasSpeed,
                    Speed = speed,
                    HasLoop = hasLoop,
                    Loop = loop,
                    Name = ReadStateName(stateNameMethod, animator, state)
                };
            }

            private static string ReadStateName(MethodInfo method, object animator, object state)
            {
                if (method == null || animator == null)
                {
                    return null;
                }

                try
                {
                    object value = method.Invoke(animator, new[] { state });
                    return value as string;
                }
                catch
                {
                    return null;
                }
            }
        }

        private sealed class MemberAccessor
        {
            public static readonly MemberAccessor Empty = new MemberAccessor(null, null);

            private readonly PropertyInfo _property;
            private readonly FieldInfo _field;

            private MemberAccessor(PropertyInfo property, FieldInfo field)
            {
                _property = property;
                _field = field;
            }

            public bool HasValue
            {
                get
                {
                    return _property != null || _field != null;
                }
            }

            public object GetValue(object instance)
            {
                if (instance == null)
                {
                    return null;
                }

                try
                {
                    if (_property != null)
                    {
                        return _property.GetValue(instance, null);
                    }
                    if (_field != null)
                    {
                        return _field.GetValue(instance);
                    }
                }
                catch
                {
                }
                return null;
            }

            public static MemberAccessor Find(Type type, string name)
            {
                if (type == null || string.IsNullOrEmpty(name))
                {
                    return Empty;
                }

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo[] properties = type.GetProperties(flags);
                for (int i = 0; i < properties.Length; i++)
                {
                    if (properties[i].GetIndexParameters().Length == 0 && string.Equals(properties[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return new MemberAccessor(properties[i], null);
                    }
                }

                FieldInfo[] fields = type.GetFields(flags);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (string.Equals(fields[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return new MemberAccessor(null, fields[i]);
                    }
                }
                return Empty;
            }
        }

        private struct CaptureDocument
        {
            public string Schema;
            [JsonProperty("clearancePolicy")]
            public object ClearancePolicy;
            public string LocalBotId;
            public string UnityVersion;
            public string GameVersion;
            public string AssemblyCSharpMvid;
            public string Reason;
            public string FinishedAtUtc;
            public int MaximumSamples;
            public int CapturedSamples;
            public bool BufferFull;
            public int DroppedSampleCount;
            public Dictionary<string, int> StageCounts;
            public RouteSummary? Route;
            public PuppetReport? Puppet;
            public FootLockSummary? FootLock;
            public FootPlacerSummary? FootPlacer;
            public PosePlaybackSummary? PosePlayback;
            public BodyLeanSummary? BodyLean;
            public string LayerEncoding;
            public string PathDataNote;
            public string MotionSyncDataNote;
            public SampleSnapshot[] Samples;
        }

        private struct MovementSnapshot
        {
            public bool HasPlayerSpeed;
            public float PlayerSpeed;
            public bool HasMaxSpeed;
            public float MaxSpeed;
            public bool HasStateSpeedLimit;
            public float StateSpeedLimit;
            public bool HasSprintEnabled;
            public bool SprintEnabled;
        }

        private struct SampleSnapshot
        {
            public int Index;
            public string Stage;
            public int Frame;
            public float Time;
            public float UnscaledTime;
            public float UnityDeltaTime;
            public float DeltaTime;
            public bool HasDeltaTime;
            public string UpdateQueue;
            public Vector3Snapshot Velocity;
            public bool VelocityAvailable;
            public Vector3Snapshot Motion;
            public bool MotionAvailable;
            public PositionSnapshot Root;
            public PositionSnapshot Pelvis;
            public AnimatorSnapshot Animator;
            public MotionSyncProbeSnapshot MotionSync;
            public MovementSnapshot Movement;
            public GrounderSnapshot Grounder;
            public PoseProbeSnapshot Pose;
            public PathSnapshot Path;
            public CullingSnapshot Culling;
            public bool HasIsVisible;
            public bool IsVisible;
            public bool HasIsVisibleToCamera;
            public bool IsVisibleToCamera;
            public bool HasUsedSimplifiedSkeleton;
            public bool UsedSimplifiedSkeleton;
            public float BodyYaw;
            public bool HasAiming;
            public bool IsAiming;
            public bool HasShooting;
            public bool IsShooting;
            public Vector3Snapshot RootJoint;
            public float RootJointYaw;
            public BoneSnapshot[] Bones;

            public static SampleSnapshot CreateEmpty(int index, string stage, string error)
            {
                return new SampleSnapshot
                {
                    Index = index,
                    Stage = stage,
                    Frame = UnityEngine.Time.frameCount,
                    Time = UnityEngine.Time.time,
                    UnscaledTime = UnityEngine.Time.unscaledTime,
                    UnityDeltaTime = UnityEngine.Time.deltaTime,
                    UpdateQueue = "capture-error",
                    CaptureError = error
                };
            }

            public string CaptureError { get; set; }
        }

        private struct PositionSnapshot
        {
            public bool Available;
            public Vector3Snapshot Value;
        }

        private struct BoneSnapshot
        {
            public string N;
            public float[] P;
            public float[] R;
        }

        private struct Vector3Snapshot
        {
            public float X;
            public float Y;
            public float Z;

            public static Vector3Snapshot From(Vector3 value)
            {
                return new Vector3Snapshot { X = value.x, Y = value.y, Z = value.z };
            }
        }

        private struct CullingSnapshot
        {
            public bool Available;
            public string Mode;
            public bool HasEnabled;
            public bool Enabled;
            public bool HasIsActiveAndEnabled;
            public bool IsActiveAndEnabled;
        }

        private struct AnimatorSnapshot
        {
            public bool Available;
            public string RuntimeType;
            public string ControllerType;
            public bool HasLayerCount;
            public int ReportedLayerCount;
            public int CapturedLayerCount;
            public bool HasFootStepParameter;
            public bool HasFootStepCurve;
            public float FootStepCurve;
            public bool FootStepCurveIsHeuristic;
            public AnimatorLayerSnapshot[] Layers;
            public float[] LayerNormalizedTimes;
            public float[] LayerLengths;
            public float[] LayerNextNormalizedTimes;
        }

        private struct AnimatorLayerSnapshot
        {
            public int Layer;
            public bool HasInTransition;
            public bool InTransition;
            public AnimatorStateSnapshot Current;
            public AnimatorStateSnapshot Next;
        }

        private struct AnimatorStateSnapshot
        {
            public bool Available;
            public bool HasFullPathHash;
            public int FullPathHash;
            public bool HasShortNameHash;
            public int ShortNameHash;
            public string Name;
            public bool HasNormalizedTime;
            public float NormalizedTime;
            public bool HasLength;
            public float Length;
            public bool HasSpeed;
            public float Speed;
            public bool HasLoop;
            public bool Loop;
        }

        private struct GrounderSnapshot
        {
            public bool Available;
            public bool HasIsGrounded;
            public bool IsGrounded;
            public bool HasRootGrounded;
            public bool RootGrounded;
            public bool HasWeight;
            public float Weight;
            public int LegCount;
            public GrounderLegSnapshot LeftLeg;
            public GrounderLegSnapshot RightLeg;
        }

        private struct PathSnapshot
        {
            public bool Available;
            public string MoverType;
            public string PathControllerType;
            public bool HasPath;
            public bool HasRemainingDistance;
            public float RemainingDistance;
            public bool HasCurrentCorner;
            public Vector3Snapshot CurrentCorner;
            public bool HasPreviousCorner;
            public Vector3Snapshot PreviousCorner;
            public bool HasTargetPoint;
            public Vector3Snapshot TargetPoint;
            public bool HasDesiredDirection;
            public Vector3Snapshot DesiredDirection;
            public bool HasIsMoving;
            public bool IsMoving;
            public bool HasDestinationSpeed;
            public float DestinationSpeed;
        }

        private struct GrounderLegSnapshot
        {
            public string FootPositionSource;
            public bool Available;
            public bool HasIsGrounded;
            public bool IsGrounded;
            public bool HasInitiated;
            public bool Initiated;
            public bool HasFootPosition;
            public Vector3Snapshot FootPosition;
            public bool HasKneePosition;
            public Vector3Snapshot KneePosition;
            public bool HasHipPosition;
            public Vector3Snapshot HipPosition;
            public bool HasToePosition;
            public Vector3Snapshot ToePosition;
            public bool HasFootRotation;
            public float[] FootRotation;
            public bool HasIKPosition;
            public Vector3Snapshot IKPosition;
            public bool HasIKOffset;
            public float IKOffset;
            public bool HasHeightFromGround;
            public float HeightFromGround;
            public Vector3Snapshot Velocity;
            public bool HasVelocity;
        }

        private static bool TryReadInt(object value, out int result)
        {
            if (value == null)
            {
                result = 0;
                return false;
            }

            try
            {
                result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                result = 0;
                return false;
            }
        }

        private static bool TryReadFloat(object value, out float result)
        {
            if (value is float)
            {
                result = (float)value;
                return true;
            }

            if (value == null)
            {
                result = 0f;
                return false;
            }

            try
            {
                result = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                result = 0f;
                return false;
            }
        }

        private static bool ReadBool(object value)
        {
            bool result;
            return TryReadBool(value, out result) && result;
        }

        private static bool TryReadBool(object value, out bool result)
        {
            if (value is bool)
            {
                result = (bool)value;
                return true;
            }
            if (value == null)
            {
                result = false;
                return false;
            }

            try
            {
                result = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                result = false;
                return false;
            }
        }

        private static bool TryReadVector3(object value, out Vector3Snapshot result)
        {
            if (value is Vector3)
            {
                result = Vector3Snapshot.From((Vector3)value);
                return true;
            }

            result = default(Vector3Snapshot);
            return false;
        }

        private static string ValueToString(object value, string fallback)
        {
            if (value == null)
            {
                return fallback;
            }
            string text = value as string;
            return text ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback;
        }
    }
}
