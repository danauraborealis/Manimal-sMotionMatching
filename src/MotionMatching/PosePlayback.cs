using System;
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using EFT;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ZLinq;

namespace Manimal.MotionMatching
{
    // pose database written by tools/export_posedb.py: per-frame local rotations for EFT's pelvis/leg bones
    internal sealed class PoseDatabase
    {
        public string[] Bones;
        public Dictionary<string, Quaternion> RestLocal = new Dictionary<string, Quaternion>();
        public List<PoseClip> Clips = new List<PoseClip>();
        // heel and toe-tip points in each Foot bone's frame (export_posedb.py solePoints); the placer's FootBase
        public bool HasSolePoints;
        public Vector3[] SoleHeel = new Vector3[2];
        public Vector3[] SoleToe = new Vector3[2];

        public static PoseDatabase Load(string path)
        {
            JObject root = JObject.Parse(File.ReadAllText(path));
            if ((string)root["schema"] != "manimal.motionmatching.posedb.v1")
                throw new InvalidDataException("Unsupported pose database schema: " + root["schema"]);
            var db = new PoseDatabase { Bones = root["bones"].ToObject<string[]>() };
            foreach (JProperty rest in ((JObject)root["restLocal"]).Properties())
                db.RestLocal[rest.Name] = ToQuaternion((JArray)rest.Value);
            if (root["solePoints"] is JObject sole && sole["L"] is JObject && sole["R"] is JObject)
            {
                string[] sides = { "L", "R" };
                for (int side = 0; side < 2; side++)
                {
                    db.SoleHeel[side] = ToVector((JArray)sole[sides[side]]["heel"]);
                    db.SoleToe[side] = ToVector((JArray)sole[sides[side]]["toe"]);
                }
                db.HasSolePoints = true;
            }
            foreach (JObject c in root["clips"])
            {
                int frames = (int)c["frames"];
                var clip = new PoseClip
                {
                    Name = (string)c["name"],
                    Fps = (float)c["fps"],
                    Frames = frames,
                    Loop = (bool)c["loop"],
                    SpeedMetersPerSecond = (float)c["speedMetersPerSecond"],
                    Rotations = new Quaternion[db.Bones.Length][],
                    PelvisPosition = new Vector3[frames],
                    RootSpeed = c["rootSpeed"] == null ? null : c["rootSpeed"].ToObject<float[]>(),
                    EndsInTarkovIdle = c["endsInTarkovIdle"] != null && (bool)c["endsInTarkovIdle"],
                    Roles = c["roles"] == null ? new string[0] : c["roles"].ToObject<string[]>(),
                    Gait = (string)c["gait"] ?? "run",
                    MoveYaw = c["moveYaw"] == null ? 0f : (float)c["moveYaw"],
                    FromYaw = c["fromYaw"] == null ? 0f : (float)c["fromYaw"],
                    ToYaw = c["toYaw"] == null ? 0f : (float)c["toYaw"],
                    TurnFrame = c["turnFrame"] == null ? 0 : (int)c["turnFrame"],
                    YawChange = c["yawChange"] == null ? 0f : (float)c["yawChange"]
                };
                for (int b = 0; b < db.Bones.Length; b++)
                {
                    var rotations = (JArray)c["rotations"][db.Bones[b]];
                    clip.Rotations[b] = new Quaternion[frames];
                    for (int f = 0; f < frames; f++)
                        clip.Rotations[b][f] = ToQuaternion((JArray)rotations[f]);
                }
                if (c["upperBodyRotations"] is JObject upper)
                {
                    clip.UpperBodyRotations = new Dictionary<string, Quaternion[]>();
                    foreach (JProperty property in upper.Properties())
                    {
                        var values = (JArray)property.Value;
                        if (values.Count != frames) throw new InvalidDataException("Upper-body frame count differs: " + property.Name);
                        var rotations = new Quaternion[frames];
                        for (int f = 0; f < frames; f++) rotations[f] = ToQuaternion((JArray)values[f]);
                        clip.UpperBodyRotations.Add(property.Name, rotations);
                    }
                }
                var pelvis = (JArray)c["pelvisPosition"];
                for (int f = 0; f < frames; f++)
                    clip.PelvisPosition[f] = new Vector3((float)pelvis[f][0], (float)pelvis[f][1], (float)pelvis[f][2]);
                if (c["rootVelocity"] is JArray velocity)
                {
                    clip.RootVelocity = new Vector2[frames];
                    for (int f = 0; f < frames; f++)
                        clip.RootVelocity[f] = new Vector2((float)velocity[f][0], (float)velocity[f][1]);
                }
                if (c["yawProgress"] is JArray yawProgress)
                {
                    clip.YawProgress = new float[frames];
                    for (int f = 0; f < frames; f++)
                    {
                        clip.YawProgress[f] = (float)yawProgress[f];
                        clip.YawExcursion = Mathf.Max(clip.YawExcursion, Mathf.Abs(clip.YawProgress[f] - clip.YawProgress[0]));
                    }
                    // the export writes yawChange for transitions only; every other clip loaded as "no turn"
                    if (c["yawChange"] == null && frames > 1)
                        clip.YawChange = clip.YawProgress[frames - 1] - clip.YawProgress[0];
                }
                if (c["contacts"] is JObject contacts)
                {
                    clip.Contact = new bool[2][];
                    string[] sides = { "L", "R" };
                    for (int side = 0; side < 2; side++)
                    {
                        var values = (JArray)contacts[sides[side]];
                        clip.Contact[side] = new bool[frames];
                        for (int f = 0; f < frames; f++)
                            clip.Contact[side][f] = (int)values[f] != 0;
                    }
                }
                if (c["stride"] is JObject stride && stride["L"] is JObject && stride["R"] is JObject)
                {
                    clip.Stride = new[] { StrideFoot.Parse((JObject)stride["L"], frames), StrideFoot.Parse((JObject)stride["R"], frames) };
                    if (stride["stepsRemaining"] is JArray remaining)
                        clip.StepsRemaining = remaining.ToObject<int[]>();
                }
                if (c["feet"] is JArray feet)
                {
                    clip.FootL = new Vector3[frames];
                    clip.FootR = new Vector3[frames];
                    for (int f = 0; f < frames; f++)
                    {
                        var row = (JArray)feet[f];
                        clip.FootL[f] = new Vector3((float)row[0], (float)row[1], (float)row[2]);
                        clip.FootR[f] = new Vector3((float)row[3], (float)row[4], (float)row[5]);
                    }
                }
                db.Clips.Add(clip);
            }
            return db;
        }

        public PoseClip Find(string name)
        {
            foreach (var clip in Clips)
                if (string.IsNullOrEmpty(name) || clip.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    return clip;
            return null;
        }

        private static Quaternion ToQuaternion(JArray q) => new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
        private static Vector3 ToVector(JArray v) => new Vector3((float)v[0], (float)v[1], (float)v[2]);
    }

    internal sealed class PoseClip
    {
        public string Name;
        public float Fps;
        public int Frames;
        public bool Loop;
        public float SpeedMetersPerSecond;
        public Quaternion[][] Rotations;
        public Dictionary<string, Quaternion[]> UpperBodyRotations;
        public Vector3[] PelvisPosition;
        public float[] RootSpeed;
        public bool EndsInTarkovIdle;
        // directional playback metadata (export_posedb.py --role); yaws are degrees relative to facing, measured from root motion
        public string[] Roles;
        public string Gait;
        public float MoveYaw;
        // a phase-aligned directional set (Tarkov's 8-way cycles): members blend by relative yaw at one phase.
        // PhaseOffset is the member's frame at the family's reference phase
        public string Family;
        public int PhaseOffset;
        public PoseClip FamilyReference;
        public float FromYaw;
        public float ToYaw;
        public int TurnFrame;
        // sprint transitions rotate the character; playback follows the bot's own turn through this curve
        public float YawChange;
        public float YawExcursion;
        public float[] YawProgress;
        public Vector2[] RootVelocity;
        // per-frame planted flags from the clip itself, so a lock triggers in phase with these legs
        public bool[][] Contact;
        // Root_Joint-space ankles
        public Vector3[] FootL;
        public Vector3[] FootR;
        // Valve-style foot motion data per foot (tools/stride_data.py); null on databases exported before it
        public StrideFoot[] Stride;
        public int[] StepsRemaining;

        public bool HasRole(string role) => Array.IndexOf(Roles, role) >= 0;

        // distance the clip's root has travelled by each frame; planted feet only stay put when the clip and the
        // body cover the same ground, so starts and cuts match on this rather than on speed
        public float[] PathLength;

        // EFT's body moves the instant a bot starts, while an Alyx start winds up first with both feet planted;
        // entering at frame 0 dragged those planted feet (0.7-0.8 m per window), so playback starts where it moves
        public int MotionStart;

        public void BuildPathLength()
        {
            if (RootSpeed == null || PathLength != null)
                return;
            MotionStart = 0;
            for (int f = 0; f < Frames; f++)
                if (RootSpeed[f] > 0.25f) { MotionStart = f; break; }
            PathLength = new float[Frames];
            float travelled = 0f;
            for (int f = 0; f < Frames; f++)
            {
                PathLength[f] = travelled;
                travelled += RootSpeed[f] / Fps;
            }
        }

        public int FrameAtDistance(float distance, int from)
        {
            for (int f = Mathf.Max(0, from); f < Frames; f++)
                if (PathLength[f] >= distance)
                    return f;
            return Frames - 1;
        }

        public int FrameAtYaw(float turned, int from)
        {
            if (YawProgress == null || Mathf.Abs(YawChange) < 1f)
                return from;
            float sign = Mathf.Sign(YawChange);
            for (int f = Mathf.Max(0, from); f < Frames; f++)
                if (YawProgress[f] * sign >= turned * sign)
                    return f;
            return Frames - 1;
        }
    }

    // plays retargeted Alyx legs between the animator and VisualPass, so EFT's own IK (grounder, hands on weapon)
    // still runs on top; the torso keeps its animated world rotation. two modes: a steady loop, or directional
    // Alyx starts/stops/cuts layered over Tarkov's own movement cycles (the user wants Tarkov's run cycle kept for now)
    internal sealed partial class PosePlayback
    {
        public const string StartStopMode = "startstop";
        // farther than this from every clip's direction and Tarkov's own legs play instead of a sliding clip
        private const float MaxDirectionError = 25f;
        // clips are picked by how close their own speed is to the bot's, so slow/walk/run sets can coexist.
        // a clip slower than the bot is much worse than a faster one: it cannot cover the ground, and a 2.4 m/s stop
        // that picked the 1.7 m/s walk clip ran out of clip mid-slide (707 mm of planted-foot slide)
        private const float FasterClipPenaltyDegreesPerMps = 12f;
        // a slower clip plays faster (rate up to 1.5 in stops, 2.5 in starts); the old 45 made a 1.5 m/s walk take
        // the run stop over a 1.1 m/s walk stop
        private const float SlowerClipPenaltyDegreesPerMps = 25f;
        // Player.Speed to m/s, measured in the first clean sweep; a start fires before the body has accelerated
        private static readonly float[] CommandSpeeds = { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.625f };
        private static readonly float[] CommandMetersPerSecond = { 0.99f, 1.23f, 1.53f, 1.93f, 2.5f, 2.83f };
        private const float StartBlendIn = 0.1f;
        private const float StopBlendIn = 0.15f;
        // entering a cut near its reversal leaves a bigger pose gap to absorb than entering early did
        private const float CutBlendIn = 0.18f;
        private const float HandOffBlend = 0.5f;
        // a transition clip tops out at 3.4 m/s against EFT's 4.76 sprint, so it always hands back mid-stride;
        // a longer blend spreads that mismatch instead of snapping the legs onto Tarkov's cadence
        private const float TransitionHandOffBlend = 0.55f;
        private const float StandingSpeed = 0.2f;
        private const float TransitionOutrunSpeed = 4f, TransitionOutrunRate = 1.6f;
        private const float StandingSpeedFloor = 0f, StandingSpeedResetSeconds = 0.25f;
        private const float StandingSeconds = 0.25f;
        // starting while the body still swings onto its facing read the travel direction stale and churned through
        // three or four clips in a row; wait for the turn to settle, then commit
        private const float SteadyYawRate = 90f;
        // strafe clips stop from ~1.5 m/s, so the trigger sits below walking strafe speed
        private const float StopTriggerSpeed = 0.8f;
        // a start is done once the bot quits accelerating; EFT reaches its target speed well inside the clip
        private const float SettledAcceleration = 0.6f;
        // matching the ground already covered leaves the legs trailing the body (the user saw feet fall behind on
        // starts and cuts, then catch up). a real push-off plants ahead of the body, so match where the bot will be
        private const float DefaultLeadSeconds = 0.15f;
        private const float MinMatchRate = 0.5f;
        // a start whose peak speed the body beats by this much, after this long, hands to the loop early
        private const float OutrunStartFactor = 1.3f;
        private const float OutrunStartSeconds = 0.4f;
        // EFT is at full speed in ~0.3 s while an Alyx start takes ~1 s, so the clip fell up to 1.6 m behind the body.
        // the push itself may catch up fast (the legs are already swinging hard there); after it, normal pace
        private const float CatchUpSeconds = 0.4f;
        private const float DefaultCatchUpRate = 2.5f;
        private const float DefaultMaxMatchRate = 1.25f;
        // stops slid 0.25-0.33 (factory) and 0.26-0.29 (woods) m per m/s; biased short on purpose, see Phase.Stop
        private const float StopDistancePerSpeed = 0.26f;
        private const float MinStopDistance = 0.2f;
        private const float MaxStopDistance = 1.5f;
        // how far ahead of a stand the stop clip may start. run_to_stand eases off over 3.5 m from full speed while
        // EFT holds speed and brakes in 0.7 m, so entering at the clip's true braking distance left it finished with
        // the body still sliding a metre (legs split until the snap to idle, user screenshot); 1.5 m keeps the clip's
        // speeds within reach of the body's
        private const float MaxAnticipatedStopDistance = 1.5f;
        // a walk stop from the takes brakes over 1.5-1.9 m while EFT walks at full speed until its 0.4 m brake:
        // entered whole, the stop clip played for a second under a body still walking (user: strafe forward and
        // back "looked bad"). walking speeds enter only the clip's last stretch, as a run stop already does
        private const float MaxAnticipatedWalkStopDistance = 0.7f;
        private float MaxStopDistanceFor(float speed) => speed < RunTierSpeed ? MaxAnticipatedWalkStopDistance : MaxAnticipatedStopDistance;
        // stop entries may shift this far along the clip to land on a frame whose feet match the pose on screen
        private const float StopPoseWindowMeters = 0.6f;
        private const float StopFeetDeferCost = 0.12f;
        // a stop entered early has less clip distance than the body will travel; the frame then plays on at this
        // rate instead of stalling with the feet frozen while the body walks
        private const float MinStopRate = 0.6f;
        private const float StoppedSpeed = 0.15f;
        private const float StopMotionSpeed = 0.1f;
        // same reason as MaxCutRate: a hurried stop reads as scrambling
        private const float MaxStopRate = 1.5f;
        // a one-frame intent swing this large while moving is a direction change, not steering drift
        private const float CutIntentChange = 45f;
        private const float CutDirectionError = 35f;
        // EFT goes 1.45 -> 4.76 m/s in ~0.6 s entering a sprint and drops back in ~0.35 s leaving one (measured)
        private const float SprintTransitionError = 30f;
        private const float SprintExitSpeed = 1.5f;
        // below this there is no turn to animate, so Tarkov's own straight-ahead sprint start is left alone
        private const float SprintTurnMinimum = 20f;
        // above this the bot is sprinting: EFT's own power slide handles the stop, and no Alyx clip brakes this fast
        private const float SprintStopSpeed = 3.8f;
        private const float SprintStopGrace = 0.8f;
        private const float SprintTurnError = 45f;
        // these clips turn over ~1.5 s where EFT takes ~0.6, so they may run faster for longer than a cut does
        private const float MaxTransitionRate = 2f;
        // the Alyx sprint loop bridges from a transition to Tarkov's own sprint, so the clip never runs dry mid-stride.
        // it is a seam filler, not a replacement: playback hands back as soon as the feet line up, and gives up after this
        private const float MaxSprintCycleSeconds = 2f;
        private const float MinCycleRate = 0.6f;
        // matching on distance alone keeps the clip's stride length and slows the cadence instead, which reads as
        // slow-motion striding at walking speed (user). scaling the stride to the bot's speed keeps the cadence
        private const float MinStrideScale = 0.5f;
        // never lengthen an authored start stride: the bot reaching speed faster than the clip is caught up through the
        // rate, not by lunging (the heavy walk start's 1.61 m first stride was placed at 1.95 m, user: "spreads his leg
        // pretty far forward" at every start)
        private const float MaxStrideScale = 1f;
        private const float StrideSmoothing = 0.25f;
        // the arms and weapon hang off the pelvis, so Alyx hip motion dragged the hands up to 184 mm and warped the
        // gun (user). the torso keeps Tarkov's world position as well as its rotation, capped so the spine cant stretch
        // horizontal only: the loops bob the pelvis several centimetres a stride, and holding the torso against that
        // stretched the spine visibly (user); the whole body rides the bob now, the hands still keep their place
        private const float MaxInertiaPelvisOffset = 0.3f;
        private const float MaxReach = 0.97f;
        private const float MaxCycleRate = 1.6f;
        // EFT reverses in ~0.35-0.45 s while the grunt's cuts take about a second. rushing the clip to match read as
        // scrambling feet (user, 2026-09-15), so a cut is entered close to its reversal and played near normal speed
        private const float CutReversalSeconds = 0.4f;
        private const float MinCutRate = 0.7f;
        private const float MaxCutRate = 1.3f;
        private const float PoseCostWeight = 20f;
        // handing back on whatever frame the bot settled on made the blend drag planted feet a metre; wait (up to this
        // long) for a frame whose feet line up with the pose Tarkov's animator is holding underneath
        private const float HandOffMatchSeconds = 0.5f;
        private const float HandOffFeetCost = 0.02f;
        private const float HoldTurnRate = 30f;
        // the held Alyx stance used to linger 0.6 s with the feet pinned wherever they landed (user: "standing there
        // with weird magic bugged feet"); now it only bridges the frame or two until the idle blend starts
        private const float HoldSettleSeconds = 0.15f;
        private const float HoldDriftSpeed = 0.3f;
        private const float HoldMaxSeconds = 15f;
        private const float HoldMaxPivot = 120f;
        private const float HoldPivotRelease = 20f;
        private const float HoldPivotSettled = 10f;
        private const float HoldReleaseBlend = 3f;
        private bool _holdRelease;
        private float _holdYawStart;
        private const float StopOverrunSeconds = 0.25f;
        private float _stopOverrunFor;
        // how much of the clip's pelvis tilt (pitch and roll) is written; the yaw is always the clip's. the Alyx
        // soldiers lean the pelvis well forward and Tarkov's upright torso above it read as a stuck-out backside
        // (user screenshot), so most of the tilt stays Tarkov's
        public float PelvisTiltFromClip = 1f;
        private int _pelvisIndex = -1;
        private readonly int[] _thighIndex = { -1, -1 };
        private Quaternion _thighCompensation = Quaternion.identity;
        private bool _hasThighCompensation;
        private const float CrossfadeSeconds = 0.22f;

        private enum Phase { Idle, Start, Stop, Hold, HandOff, Cut, Transition, SprintCycle, Turn, Reaction }

        private sealed class MoveSet
        {
            public float Yaw;
            public string Gait;
            // the body turn the start clip carries (0 for a straight or strafing start)
            public float YawChange;
            public PoseClip Start;
            public PoseClip Stop;
            public float StartSpeed;
            public float StopSpeed;
            public float[] StartPeak;
            // stop clip: root distance still to travel from each frame, and where its braking footwork ends
            public float[] StopRemaining;
            public int StopMotionEnd;
        }

        private readonly List<MoveSet> _sets = new List<MoveSet>();
        private readonly List<PoseClip> _cuts = new List<PoseClip>();
        private readonly List<PoseClip> _sprintEnters = new List<PoseClip>();
        // stage F: turn-in-place clips (Valve's turn_left/right_22/90/180), matched to the controller's own turn
        private readonly List<PoseClip> _turns = new List<PoseClip>();
        private float _turnPrevYaw, _turnStartYaw, _turnTurned, _turnGoal, _turnStalledFor, _turnReversedFor;
        private const float TurnStartRate = 25f;
        private const float TurnMinGoal = 15f;
        private const float TurnFinishMargin = 6f;
        private const float TurnStallSeconds = 0.5f;
        private const float TurnReverseSeconds = 0.2f;
        private const float MaxTurnRate = 2.5f;
        private const float MinTurnRate = 1f;
        public bool TurnInPlace { get; set; } = false;
        // stage F: the grunt's turning starts (turn, then run out), following the controller's turn
        public bool TurningStarts { get; set; } = true;
        private const float TurnGoalWeight = 0.5f;
        private const float TurnLeftMinimum = 25f;
        private const float CycleIntentSpeed = 1.2f;
        private const float SprintRunLoopBelow = 4.1f;
        private const float SpinFadeFrom = 90f, SpinFadeTo = 200f;
        private float _startPrevYaw, _startTurned, _orderYaw;

        // a standing body the controller is turning: the clip whose authored turn is nearest the goal, same hand
        private bool TryBeginTurn(MoveIntent intent, float bodyYaw)
        {
            if (!TurnInPlace || _turns.Count == 0 || Mathf.Abs(_yawRate) < TurnStartRate)
                return false;
            float? goal = intent?.GoalYaw != null ? intent.GoalYaw() : null;
            // no steering target (SAIN, or a look-around): a 90 in the turning direction, cut short by the stall rule
            float wanted = goal ?? Mathf.Sign(_yawRate) * 90f;
            if (Mathf.Abs(wanted) < TurnMinGoal || Mathf.Sign(wanted) != Mathf.Sign(_yawRate))
                return false;
            PoseClip best = null; float bestError = float.MaxValue;
            foreach (var c in _turns)
            {
                if (Mathf.Sign(c.YawChange) != Mathf.Sign(wanted)) continue;
                float e = Mathf.Abs(Mathf.Abs(c.YawChange) - Mathf.Abs(wanted));
                if (e < bestError) { bestError = e; best = c; }
            }
            if (best == null) return false;
            _turnPrevYaw = bodyYaw; _turnStartYaw = bodyYaw; _turnTurned = 0f; _turnGoal = wanted; _turnStalledFor = 0f; _turnReversedFor = 0f;
            Begin(Phase.Turn, best, 0f);
            _events.Add(string.Format("{0:F2}s Turn {1} goal {2:F0} rate {3:F0}", Time.time, best.Name, wanted, _yawRate));
            return true;
        }

        // frame at which the clip has turned as far as the body has (yaw progress is monotone over a turn clip)
        private static int FrameAtYaw(PoseClip clip, float turned)
        {
            if (clip.YawProgress == null) return 0;
            float want = Mathf.Abs(turned);
            for (int f = 0; f < clip.YawProgress.Length; f++)
                if (Mathf.Abs(clip.YawProgress[f]) >= want) return f;
            return clip.Frames - 1;
        }
        private readonly List<PoseClip> _sprintExits = new List<PoseClip>();
        private readonly List<PoseClip> _sprintCycles = new List<PoseClip>();
        private PoseClip _transition;
        private bool _handOffFromTransition;
        private bool _wasSprinting;
        private float _yawTurned;
        private float _sprintEndedAt = -10f;
        private bool _sprintSeen;
        private float _sprintSeenAt = -10f;
        private const float SprintStateHold = 0.25f;
        private float _strideScale = 1f;
        private bool _startRepicked;

        // variations (user: bots strafe in long single-direction paths in combat and should feel light on their
        // feet): a directional hop played from inside a loop and returned to it on its landing, the way a cut is.
        // 0 off, 1 on (combat on the lateral and backward loops every few seconds, patrol on any straight stretch
        // at long intervals), 2 test mode (every few seconds anywhere)
        public int Variations { get; set; } = 1;
        public Func<bool> InCombat;
        private float _nextVariationAt;
        private bool _variation, _variationForCombat;
        private const float VariationCombatMin = 4f, VariationCombatMax = 10f;
        private const float VariationPatrolMin = 15f, VariationPatrolMax = 40f;
        private const float VariationTestMin = 1f, VariationTestMax = 2.5f;
        private const float VariationLoopSeconds = 2.5f;
        private const float VariationDirectionError = 22f;

        private bool TryBeginVariation()
        {
            bool combat = InCombat != null && InCombat();
            bool lateral = Mathf.Abs(TravelYaw()) > 45f;
            if (Variations == 1 && !combat && !(_phaseFor > 6f))
                return false;
            if (Variations == 1 && combat && !lateral)
                return false;
            float travel = TravelYaw();
            // Reject unusable entries before ranking, so a too-short or too-slow hop cannot hide a usable one.
            var candidate = _sets.AsValueEnumerable()
                .Where(set => set.Start != null && IsHop(set.Start) && set.YawChange == 0f)
                .Select(set => new
                {
                    Set = set,
                    Direction = Mathf.Abs(Mathf.DeltaAngle(set.Yaw, travel)),
                    Ratio = _speed / Mathf.Max(set.StartSpeed, 0.1f),
                    From = FirstPeakAtOrAbove(set, _speed, 0)
                })
                .Where(item => item.Direction <= VariationDirectionError && item.Ratio >= 0.6f && item.Ratio <= 1.7f
                    && item.From < item.Set.Start.Frames - 8 && item.Set.StartPeak[item.From] >= _speed)
                .OrderBy(item => item.Direction + 20f * Mathf.Abs(item.Ratio - 1f))
                .FirstOrDefault();
            if (candidate == null)
            {
                _events.Add(string.Format("{0:F2}s Variation: no hop for travel {1:F0} at {2:F2} m/s", Time.time, travel, _speed));
                return false;
            }
            MoveSet best = candidate.Set;
            // enter where the hop is already at the body's speed, on the frame whose feet match the loop
            // The candidate may be a different directional/speed set from the current loop's last start.
            // Match its own speed curve; an index from the old set can even lie past this hop's end.
            int from = candidate.From;
            int entry = from; float cost = float.MaxValue;
            for (int f = Mathf.Max(0, from - 4); f < Mathf.Min(best.Start.Frames - 8, from + 12); f++)
            {
                float c = FeetCost(best.Start, f);
                if (c < cost) { cost = c; entry = f; }
            }
            _set = best;
            _variation = true;
            _startRepicked = true;
            Begin(Phase.Start, best.Start, entry);
            _events.Add(string.Format("{0:F2}s Variation {1} entry {2} feet {3:F3} {4}", Time.time, best.Start.Name, entry, cost, combat ? "combat" : "patrol"));
            return true;
        }

        private void ScheduleVariation(bool combat)
        {
            float lo = Variations == 2 ? VariationTestMin : combat ? VariationCombatMin : VariationPatrolMin;
            float hi = Variations == 2 ? VariationTestMax : combat ? VariationCombatMax : VariationPatrolMax;
            _nextVariationAt = Time.time + UnityEngine.Random.Range(lo, hi);
            _variationForCombat = combat;
        }
        private MoveSet _set;
        private PoseClip _cut;
        private float _cutScaleIn;
        private float _cutScaleOut;
        private Vector3 _stopOrigin;
        // ground actually covered, not displacement: a cut doubles back, so displacement shrinks and stalled the clip
        private float _travelled;
        private float _travelledAtBegin;
        // how far the clip is behind the ground covered, in metres; trailing legs show up here
        private float _pathBehind;
        private float _moveOriginPath;
        private float _stopPredicted;
        // near-instant speed for stops: the 0.2 s smoothed speed noticed a halt ~0.3 s late, and the clip then played
        // its last 15-25 cm of root travel with the body already still, sliding both planted feet
        private float _quickSpeed;
        private const float QuickSpeedSmoothing = 0.05f;
        private Vector2 _velocity;
        private Vector2 _localVelocity;
        private Func<float?> _moveIntent;
        private Func<float?> _goal = () => null;
        private MoveIntent _intent;
        // a stop begun from the driver's goal distance while it still wants to move; it survives wantsMove until the
        // goal moves away (replan) or the direction swings (a new order), then hands back
        private bool _stopAnticipated;
        private float _stopGoalLast;
        // the clip and frame a hand-off left from, kept while the inertializer fades it so the placer can follow
        private PoseClip _fadeClip;
        private float _fadeFrame;
        private float _fadeStarted;
        private const float FadeMaxSeconds = 1.5f;
        public bool AnticipateStops = true;
        private float? _lastIntent;
        private float _previousIntent, _previousIntentWorld;
        private string _lastDriver;
        private float _intentFor;
        private bool _intentFromStanding;
        private const float StartMotionSpeed = 0.25f;
        private const float StartIntentSeconds = 0.2f;
        private bool _hadIntent;
        private Phase _phase = Phase.Idle;
        private PoseClip _active;
        private float _frame;
        private float _standingFor;
        private float _phaseFor;
        // the push out of a cut's reversal accelerates slower than EFT does, same as a start does
        private float _sinceTurn;
        private float _previousSpeed;
        private readonly List<string> _events = new List<string>();

        // blending a finished stop back to Tarkov's idle slid each foot ~14 cm (280-291 mm of the 330 mm left after
        // a halt), so the planted end stance holds until the bot moves or turns
        private Quaternion[] _lastSampled;
        private Vector3 _lastPelvis;
        private Quaternion[] _previousSampled;
        private Vector3 _previousSampledPelvis;
        private float _lastSampleTime = -1f, _previousSampleTime = -1f;
        private readonly Inertializer _inertia = new Inertializer();
        // the animator's own leg pose this frame, read before we overwrite it
        private Quaternion[] _animated;
        private Quaternion[] _previousAnimated;
        private Vector3 _previousAnimatedPelvis;
        private float _animatedTime = -1f, _previousAnimatedTime = -1f;
        private Vector3 _animatedPelvis;
        private bool _handingOff;
        private float _previousYaw;
        private float _yawRate;
        private bool _hasYaw;

        public string PhaseName => _phase.ToString();
        public string DriverName => _lastDriver;
        public string ActiveClip => _active?.Name;
        public float Frame => _frame;
        public IList<string> Events => _events;

        private const float BlendSeconds = 0.25f;
        private const float MovingSpeed = 0.3f;
        private const float SpeedSmoothing = 0.2f;
        // stride rate follows real ground speed so feet don't moonwalk when the puppet is faster or slower than the clip
        private const float MinRate = 0.6f;
        private const float MaxRate = 1.6f;

        private readonly Player _player;
        private readonly PoseClip _clip;
        private readonly Transform _pelvis;
        private readonly Transform _spine;
        private readonly Transform[] _bones;
        private readonly Transform _footL;
        private readonly Transform _footR;
        private readonly Transform[] _thigh = new Transform[2];
        private readonly Transform[] _calf = new Transform[2];
        private readonly Transform[] _thigh2 = new Transform[2];
        private readonly Quaternion[] _thigh2Rest = new Quaternion[2];
        private Vector3 _shownFootL;
        private Vector3 _shownFootR;
        // Character-local sole FootBase values used by selection. `_shownFoot*` remains the
        // ankle reader used by hand-off matching and existing diagnostics.
        private Vector3 _shownFootbaseL;
        private Vector3 _shownFootbaseR;
        private bool _hasShownFootbaseL, _hasShownFootbaseR;
        private float? _shownProgressionL, _shownProgressionR;
        // Tarkov's own animated feet this frame, read before this playback overwrites the legs
        private Vector3 _animatedFootL;
        private Vector3 _animatedFootR;
        private readonly Vector3[] _soleHeel = new Vector3[2];
        private readonly Vector3[] _soleToe = new Vector3[2];
        private bool _hasSolePoints;
        // Native sole pose captured before this playback writes the legs. The placer
        // uses this fresh world-space pose for the idle hand-off target; it is valid
        // only for the frame in which the animator was sampled.
        private Vector3 _nativeIdleCenterL;
        private Vector3 _nativeIdleCenterR;
        private float _nativeIdleHeadingL;
        private float _nativeIdleHeadingR;
        private int _nativeIdlePoseFrame = -1;
        private bool _hasNativeIdlePose;
        private bool _handOffPending;
        private float _handOffWaited;
        private float _time;
        private float _weight;
        private float _speed;
        private Vector3 _previousPosition;
        private bool _hasPrevious;

        private PosePlayback(Player player, PoseClip clip, Transform pelvis, Transform spine, Transform[] bones, Transform footL, Transform footR)
        {
            _player = player;
            _clip = clip;
            _pelvis = pelvis;
            _spine = spine;
            _bones = bones;
            _footL = footL;
            _footR = footR;
        }

        public bool Enabled { get; set; } = true;
        // each recent addition can be switched off in game so a bad look can be isolated without a rebuild
        public bool StrideWarp { get; set; } = true;
        public bool SprintTransitions { get; set; } = true;
        public bool SprintCycleBridge { get; set; } = true;
        // the user wants Alyx legs for all movement: after a start, cut or sprint transition the playback hands
        // into the closest Alyx cycle loop and stays in Alyx until the next stop or cut, instead of returning to
        // Tarkov's own run. loops are re-picked when travel direction or speed drift away from the current one
        public bool AlyxCycles { get; set; } = true;
        // Native mode prefers Tarkov's forward walking cycles, including acceleration,
        // while Alyx supplies starts, stops and directional actions. Refinement-off
        // retains the original 2.3 m/s native run band for comparisons.
        // 0 brisk Alyx walk; 1 Tarkov walk; 2 Alyx run/sprint loops.
        public int RunBandLegs { get; set; } = 1;
        private bool TarkovRunLegs => RunBandLegs == 1;
        private bool BriskWalkLegs => RunBandLegs == 0;
        private const float TarkovRunSpeed = 2.3f;
        private float NativeWalkMinimumSpeed => LocomotionRefinement.Enabled ? StartMotionSpeed : TarkovRunSpeed;
        private const float TarkovRunYaw = 45f;
        private const float BriskWalkTop = 2.85f;
        // 0 Tarkov's animator owns the sprint (bridged by the Alyx transitions); 1 Tarkov's own sprint cycles run
        // on the placer (the sprint pass the user asked for): the sprint family keeps playing under a sprinting
        // body, entered by Tarkov's stand-to-sprint transition or picked mid-sprint, left through the Alyx
        // sprint exits or Tarkov's own power slide on a stop
        public int SprintLegs { get; set; } = 1;
        private bool SprintOnPlacer => SprintLegs == 1 && HasTarkovSprint;
        private static bool IsTarkovSprint(PoseClip clip) => IsTarkov(clip) && clip.Loop && clip.Gait == "sprint";
        private bool _hasTarkovSprint, _tarkovSprintChecked;
        private bool HasTarkovSprint
        {
            get
            {
                if (!_tarkovSprintChecked)
                {
                    _tarkovSprintChecked = true;
                    foreach (var c in _sprintCycles)
                        if (IsTarkovSprint(c)) { _hasTarkovSprint = true; break; }
                }
                return _hasTarkovSprint;
            }
        }
        private static bool IsTarkov(PoseClip clip) => clip != null && clip.Name.StartsWith("tarkov_", StringComparison.OrdinalIgnoreCase);
        private bool _hasTarkovCycles, _tarkovCyclesChecked;
        private bool HasTarkovCycles
        {
            get
            {
                if (!_tarkovCyclesChecked)
                {
                    _tarkovCyclesChecked = true;
                    foreach (var c in _sprintCycles)
                        if (IsTarkov(c)) { _hasTarkovCycles = true; break; }
                }
                return _hasTarkovCycles;
            }
        }
        private const float CycleRepickDirection = 30f;
        // inside the rate band, not at its clamps: under the inertia cap a body spends a second or more between
        // gaits, and a loop held to 0.6x or 1.6x for that long is the feet trailing the body (user, fleet 221208)
        private const float CycleRepickMinRatio = 0.78f;
        private const float CycleRepickMaxRatio = 1.3f;
        private const float CycleRepickSeconds = 0.3f;
        // an anticipated stop abandoned for a swing is not re-anticipated for this long; under this remaining
        // distance a swing does not abandon it at all
        private const float StopRefireSeconds = 0.5f;
        private const float StopGoalSettled = 0.5f;
        private float _stopRefireAt;
        private bool _orderHandedOff;
        private const float CycleSettleSeconds = 0.5f;
        private const float CycleSwitchDwell = 0.25f;
        private const float CycleSwitchDriftLimit = 70f;
        private PoseClip _pendingCycle;
        private float _pendingSince, _clipSince;
        private const float CycleRepickMargin = 15f;
        private const float FuturePathCostWeight = 10f;
        private const float MaxPlacementDemandPenalty = 5f;
        private const float SprintLoopPenalty = 30f;
        public bool PreferGrunt = true;
        // which clips walk at walking speed (about 1.2-1.9 m/s): 0 = heavy family (stand_to_walk, heavy walk
        // loop, walk_to_stand; the grunt takes only below 1.2 m/s), 1 = grunt takes everywhere, 2 = grunt hops as
        // walking starts/stops with the heavy loop between. the user could not decide; 0 is the physical fit
        public int WalkFamily = 0;
        private float HeavyPenaltyFrom => WalkFamily == 0 ? RunTierSpeed : SlowWalkSpeed;
        private const float HeavyClipPenalty = 15f;
        // from here up a grunt sprint loop slowed down is a run; below it the heavy run cycle still wins
        private const float GruntRunSpeed = 2.3f;
        private const float LateralRunSpeed = 2.0f;
        // user: the heavy and suppressor walks only when the bot actually moves slowly; from here up the grunt set
        // (its walk slice, hops as walking starts and stops) is preferred
        private const float SlowWalkSpeed = 1.2f;
        // a walk clip serving a run (the grunt walk slice played at 2x for a 2.8 m/s run once the slice penalty
        // went) or a run clip serving a slow walk is the wrong tier whatever its rate band says
        private const float RunTierSpeed = 1.9f;
        private const float GaitMismatchPenalty = 25f;

        private static float GaitMismatch(PoseClip clip, float targetSpeed)
        {
            string gait = clip?.Gait ?? "";
            bool walkClip = gait == "walk" || gait == "slow";
            bool runClip = gait == "run" || gait == "sprint";
            if (walkClip && targetSpeed >= RunTierSpeed) return GaitMismatchPenalty;
            // a run clip below the run tier (the run stop won a 1.5 m/s walk over the grunt walk stops, whose entry
            // speed reads slow because they brake from 1.1 m/s)
            if (runClip && targetSpeed < RunTierSpeed) return GaitMismatchPenalty;
            return 0f;
        }

        private static bool IsHeavy(PoseClip clip)
        {
            string n = clip?.Name ?? "";
            return n.StartsWith("com_sol_hev", StringComparison.OrdinalIgnoreCase) || n.StartsWith("stand_to_walk_combat", StringComparison.OrdinalIgnoreCase) || n.StartsWith("combat_walk_to_stand", StringComparison.OrdinalIgnoreCase);
        }
        // authored heavy loops are 0.8-1.1 s; anything longer is a slice of a take
        private const float AuthoredLoopMaxSeconds = 1.6f;
        private const float SlicedCyclePenalty = 20f;
        private const float NativeSpeedPenaltyDegreesPerMps = 4f;
        // distance matching wanted a different rate every frame (0.5x one frame, 2.5x the next), which is what
        // choppy legs are (user); the rate now slews toward what matching asks for
        private const float RateSlewPerSecond = 6f;
        private float _rateSmoothed = 1f;
        public bool RepickStart { get; set; } = true;
        // cross-fading poses a metre apart snapped; the offset is decayed instead (see Inertializer)
        public bool Inertialize { get; set; } = true;
        // how far ahead of the body the clip's root runs during starts and cuts; the foot placer anchors planted
        // feet itself, so with it on the lead must be zero or every plant sits that far behind its true spot
        public float LeadSeconds { get; set; } = DefaultLeadSeconds;
        // with anchored feet a hurried clip no longer scrambles planted feet, so the placer raises these
        public float CatchUpRate { get; set; } = DefaultCatchUpRate;
        public float MaxMatchRate { get; set; } = DefaultMaxMatchRate;
        public string ClipName => _sets.Count > 0 ? StartStopMode + " (" + _sets.Count + " clip sets, " + _cuts.Count + " cuts)" : _clip.Name;
        public float Weight => _weight;
        public float Rate { get; private set; }
        public int AppliedFrames { get; private set; }
        // playing a clip much faster than authored is what "scrambling feet" looks like
        public int HurriedFrames { get; private set; }
        public float PeakRate { get; private set; }

        // the clip and frame on screen, for the foot placer; false while Tarkov's own legs are showing
        public bool TryGetStrideState(out StrideState state)
        {
            state = default(StrideState);
            bool directional = _sets.Count > 0;
            PoseClip clip = directional ? _active : _clip;
            // a hand-off drops the blend weight at once and lets the inertializer carry the pose out; the placer
            // follows that same decay so anchored feet release over the blend instead of snapping (user)
            float weight = _weight;
            float frame = directional ? _frame : _time * clip.Fps;
            if (_phase == Phase.Reaction && weight <= .15f)
            {
                // Keep a valid release state even on the final zero-weight frame.
                weight = 1f;
                state.Fading = true;
            }
            if (directional && weight <= 0f && _fadeClip != null && Time.time - _fadeStarted < FadeMaxSeconds)
            {
                // the hand-off drops straight into Idle (the clip is gone) while the inertializer is still bleeding
                // the pose out, so the fade has to be served from the remembered clip: without it the placer stopped
                // the same frame and the anchored feet fell 5-10 cm onto Tarkov's idle feet (user: "snap back").
                // the placer releases the held feet at its own slow pace rather than following the 0.5 s pose blend
                clip = _fadeClip;
                frame = _fadeFrame;
                weight = 1f;
                state.Fading = true;
            }
            if (clip == null || clip.Stride == null || weight <= 0f)
                return false;
            float spinSuppression = 0f;
            // a start or cut under a body still spinning onto its facing (240 deg/s, puppet and SAIN alike): world
            // foot targets trail the spin and the swing foot took 0.3 m corrections, knee popping (user). the clip's
            // own feet ride the spin out; the placer comes back as the turn settles
            if ((_phase == Phase.Start || _phase == Phase.Cut) && !(_set != null && Mathf.Abs(_set.YawChange) > MaxStraightYawChange && _phase == Phase.Start))
                spinSuppression = Mathf.InverseLerp(SpinFadeFrom, SpinFadeTo, Mathf.Abs(_yawRate));
            state.Clip = clip;
            state.Frame = frame;
            state.Weight = weight * (1f - spinSuppression);
            state.SpinSuppression = spinSuppression;
            // starts and cuts are distance-matched through the stride scale; a stop scales the clip's remaining root
            // travel to the bot's own predicted braking distance (EFT brakes far harder than the clip does);
            // every other phase runs at clip distance
            state.DistanceScale = 1f;
            if (directional && (_phase == Phase.Start || _phase == Phase.Cut))
                state.DistanceScale = _strideScale;
            else if (directional && _phase == Phase.Stop && _set != null && _set.StopRemaining != null)
            {
                int f = Mathf.Clamp(Mathf.RoundToInt(_frame), 0, clip.Frames - 1);
                float travelled = new Vector2(_player.Position.x - _stopOrigin.x, _player.Position.z - _stopOrigin.z).magnitude;
                float remainingBot = _quickSpeed < StoppedSpeed ? 0f : Mathf.Max(0f, _stopPredicted - travelled);
                float remainingClip = _set.StopRemaining[f];
                state.DistanceScale = remainingClip > 0.05f ? Mathf.Clamp(remainingBot / remainingClip, 0f, 2f) : (remainingBot > 0.05f ? 1f : 0f);
            }
            state.IntentYaw = directional ? _lastIntent : 0f;
            state.TravelYaw = TravelYaw();
            state.Speed = _speed;
            state.Rate = Rate;
            state.YawRate = _yawRate;
            state.TravelYawRate = _travelYawRate;
            state.UseClipDistance = !directional || _phase == Phase.Stop || _phase == Phase.Hold || _phase == Phase.HandOff || _phase == Phase.SprintCycle || _phase == Phase.Turn;
            if (_phase == Phase.Reaction && _weight < .15f) state.Fading = true;
            state.YawFollowing = _phase == Phase.Turn || (_phase == Phase.Start && _set != null && Mathf.Abs(_set.YawChange) > MaxStraightYawChange);
            state.Stopping = _phase == Phase.Stop || _phase == Phase.Hold;
            state.HaltRemaining = -1f;
            // a turn ends in its authored stance (forcing the left-forward idle slot onto a right 180 mirrored the
            // feet); the held stance stays afterwards, so no hand-off needs the slots
            if (_phase == Phase.Stop)
            {
                float travelled = new Vector2(_player.Position.x - _stopOrigin.x, _player.Position.z - _stopOrigin.z).magnitude;
                state.HaltRemaining = _quickSpeed < StoppedSpeed ? 0f : Mathf.Max(0f, _stopPredicted - travelled);
            }
            else if (_phase == Phase.Hold)
                state.HaltRemaining = 0f;
            if (directional && _phase == Phase.SprintCycle && clip.SpeedMetersPerSecond > 0.2f && Rate > 0.05f)
            {
                // a loop's rate is clamped, so the body may cover less (or more) ground than the clip's root does
                state.DistanceScale = Mathf.Clamp(_speed / (clip.SpeedMetersPerSecond * Rate), 0.3f, 2f);
            }
            state.SourceClip = clip.Name;
            if (LocomotionRefinement.Enabled && !state.Fading && clip == _active && _phase == Phase.SprintCycle && clip.FamilyReference != null)
            {
                var reference = clip.FamilyReference;
                state.Frame = FamilyFrame(reference);
                state.Clip = _familyStride.Sample(reference, clip, _blendClip, _blendWeight, _nativeFamilyPhase);
                state.BlendClip = _blendClip?.Name;
                state.BlendWeight = _blendClip == null ? 0f : _blendWeight;
                if (state.Clip.SpeedMetersPerSecond > 0.2f && Rate > 0.05f)
                    state.DistanceScale = Mathf.Clamp(_speed / (state.Clip.SpeedMetersPerSecond * Rate), 0.3f, 2f);
            }
            if (_hasNativeIdlePose && _nativeIdlePoseFrame == Time.frameCount)
            {
                state.HasNativeIdlePose = true;
                state.NativeIdleCenterL = _nativeIdleCenterL;
                state.NativeIdleCenterR = _nativeIdleCenterR;
                state.NativeIdleHeadingL = _nativeIdleHeadingL;
                state.NativeIdleHeadingR = _nativeIdleHeadingR;
            }
            return true;
        }

        // planted flags for the frame currently on screen; false when Tarkov's own legs are showing
        public bool TryGetContacts(out bool left, out bool right)
        {
            left = right = false;
            // keep reporting contacts while the pose fades out, so the lock holds a planted foot through the blend
            if (_active?.Contact == null || _weight < 0.15f)
                return false;
            int f = Mathf.Clamp(Mathf.RoundToInt(_frame), 0, _active.Frames - 1);
            left = _active.Contact[0][f];
            right = _active.Contact[1][f];
            return true;
        }

        // intent: whichever driver moves the bot (puppet, stock mover, SAIN); its yaw is the desired travel direction
        // relative to facing or null to stand, its remaining metres let a stop start before the body brakes
        public static PosePlayback Create(Player player, PoseDatabase db, string clipName, MoveIntent intent, out string error)
        {
            error = null;
            bool startStop = string.Equals(clipName, StartStopMode, StringComparison.OrdinalIgnoreCase);
            PoseClip clip = null;
            if (startStop)
            {
                foreach (var c in db.Clips)
                    if (c.HasRole("start") && c.RootSpeed != null) { clip = c; break; }
                if (clip == null) { error = "Pose database has no clips with roles; re-export with export_posedb.py --role."; return null; }
            }
            else
            {
                clip = db.Find(clipName);
                if (clip == null) { error = "Pose clip not found: " + clipName; return null; }
            }
            var references = player?.Grounder?.ik?.references;
            if (references == null || !references.pelvis || !references.leftFoot || !references.rightFoot) { error = "Leg bone references are unavailable."; return null; }

            var bones = new Transform[db.Bones.Length];
            for (int i = 0; i < db.Bones.Length; i++)
            {
                bones[i] = FindChild(references.pelvis.parent, db.Bones[i]);
                if (!bones[i]) { error = "Bone not found on bot: " + db.Bones[i]; return null; }
            }
            Transform spine = FindChild(references.pelvis, "Base HumanSpine1");
            if (!spine) { error = "Base HumanSpine1 not found."; return null; }

            var playback = new PosePlayback(player, clip, references.pelvis, spine, bones, references.leftFoot, references.rightFoot);
            playback._thigh[0] = references.leftThigh;
            playback._thigh[1] = references.rightThigh;
            playback._calf[0] = references.leftCalf;
            playback._calf[1] = references.rightCalf;
            playback._hasSolePoints = db.HasSolePoints;
            if (db.HasSolePoints)
            {
                playback._soleHeel[0] = db.SoleHeel[0];
                playback._soleHeel[1] = db.SoleHeel[1];
                playback._soleToe[0] = db.SoleToe[0];
                playback._soleToe[1] = db.SoleToe[1];
            }
            playback._intent = intent;
            for (int i = 0; i < db.Bones.Length; i++)
            {
                if (db.Bones[i] == "Base HumanPelvis") playback._pelvisIndex = i;
                if (db.Bones[i] == "Base HumanLThigh1") playback._thighIndex[0] = i;
                if (db.Bones[i] == "Base HumanRThigh1") playback._thighIndex[1] = i;
            }
            if (startStop)
            {
                playback._moveIntent = intent?.Yaw ?? (() => 0f);
                playback._goal = intent?.Remaining ?? (() => null);
                playback.BuildSets(db);
            }
            string[] thigh2 = { "Base HumanLThigh2", "Base HumanRThigh2" };
            for (int i = 0; i < 2; i++)
            {
                playback._thigh2[i] = FindChild(references.pelvis, thigh2[i]);
                if (!playback._thigh2[i] || !db.RestLocal.TryGetValue(thigh2[i], out playback._thigh2Rest[i])) { error = "Thigh2 rest data missing: " + thigh2[i]; return null; }
            }
            return playback;
        }

        private const float MaxStraightYawChange = 30f;
        private const float MaxStraightYawExcursion = 45f;

        // one set per direction and gait; the first clip exported for a role wins
        private void BuildSets(PoseDatabase db)
        {
            foreach (var clip in db.Clips)
            {
                if (clip.RootSpeed == null)
                    continue;
                clip.BuildPathLength();
                if (clip.HasRole("cut"))
                {
                    // the grunt's run_n_to_run_* cuts turn the body 93-180 deg with travel ending forward; the cut
                    // matcher only knows travel yaw, so they would match as no-change cuts
                    if (Mathf.Abs(clip.YawChange) > MaxStraightYawChange)
                        Debug.Log("[" + ModInfo.Name + "] Pose cut " + clip.Name + " skipped: turns " + clip.YawChange.ToString("0") + " deg.");
                    else if (clip.RootVelocity != null && clip.FootL != null)
                        _cuts.Add(clip);
                    continue;
                }
                if (IsTarkov(clip))
                {
                    if (clip.Name.IndexOf("to_sprint", StringComparison.OrdinalIgnoreCase) >= 0) clip.Roles = new[] { "sprint_enter" };
                    else if (clip.Name.IndexOf("sprint_to_stand", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    else if (clip.Loop && clip.Gait == "sprint" && !clip.HasRole("sprint_cycle")) clip.Roles = new[] { "sprint_cycle", "cycle" };
                }
                if (clip.HasRole("turn")) { _turns.Add(clip); continue; }
                if (clip.HasRole("sprint_enter")) { _sprintEnters.Add(clip); continue; }
                if (clip.HasRole("sprint_exit")) { _sprintExits.Add(clip); continue; }
                if (clip.HasRole("sprint_cycle") || clip.HasRole("cycle")) { _sprintCycles.Add(clip); continue; }
                if (!clip.HasRole("start") && !clip.HasRole("stop"))
                    continue;
                // body-turning stops need the yaw following of docs/turning_clips_design.md; turning starts (the
                // grunt's directional set: turn, then run out) follow the controller's turn (stage F) and are keyed
                // by their travel relative to the initial facing plus their yaw change
                bool turning = Mathf.Abs(clip.YawChange) > MaxStraightYawChange || clip.YawExcursion > MaxStraightYawExcursion;
                if (turning && (!clip.HasRole("start") || !TurningStarts))
                {
                    Debug.Log("[" + ModInfo.Name + "] Pose clip " + clip.Name + " skipped: turns " + clip.YawChange.ToString("0") + " deg (excursion " + clip.YawExcursion.ToString("0") + ").");
                    continue;
                }
                float setYaw = turning ? Mathf.DeltaAngle(0f, clip.MoveYaw + clip.YawChange) : clip.MoveYaw;
                float setTurn = turning ? clip.YawChange : 0f;
                MoveSet set = null;
                foreach (var existing in _sets)
                    if (existing.Gait == clip.Gait && Mathf.Abs(Mathf.DeltaAngle(existing.Yaw, setYaw)) < 20f && Mathf.Abs(existing.YawChange - setTurn) < 30f) { set = existing; break; }
                if (set == null)
                {
                    set = new MoveSet { Yaw = setYaw, Gait = clip.Gait, YawChange = setTurn };
                    _sets.Add(set);
                }
                // a real start or stop for the direction replaces a hop that got there first (the hops came out of
                // the augmented database ahead of the walking starts cut from the motion_match takes)
                if (clip.HasRole("start") && set.Start != null && IsHop(set.Start) && !IsHop(clip))
                    set.Start = null;
                if (clip.HasRole("stop") && set.Stop != null && IsHop(set.Stop) && !IsHop(clip))
                    set.Stop = null;
                if (clip.HasRole("start") && set.Start == null)
                {
                    set.Start = clip;
                    set.StartSpeed = Peak(clip);
                    set.StartPeak = new float[clip.Frames];
                    float peak = 0f;
                    for (int f = 0; f < clip.Frames; f++)
                        set.StartPeak[f] = peak = Mathf.Max(peak, clip.RootSpeed[f]);
                }
                if (clip.HasRole("stop") && set.Stop == null)
                {
                    set.Stop = clip;
                    // the speed the stop is entered at, not its peak: combat_walk_to_stand opens with a 2.1 m/s
                    // transient over a 1.4 m/s walk, which made the 1.7 m/s hop look like the closer walk stop
                    set.StopSpeed = EntrySpeed(clip);
                    set.StopRemaining = new float[clip.Frames];
                    float remaining = 0f;
                    for (int f = clip.Frames - 1; f >= 0; f--)
                    {
                        set.StopRemaining[f] = remaining;
                        remaining += clip.RootSpeed[f] / clip.Fps;
                    }
                    set.StopMotionEnd = clip.Frames - 1;
                    for (int f = clip.Frames - 1; f >= 0; f--)
                        if (clip.RootSpeed[f] > StopMotionSpeed) { set.StopMotionEnd = f; break; }
                }
            }
        }

        private static float Peak(PoseClip clip)
        {
            float peak = 0f;
            for (int f = 0; f < clip.Frames; f++)
                peak = Mathf.Max(peak, clip.RootSpeed[f]);
            return peak;
        }

        // the bot's commanded speed in m/s; a start fires before the body has accelerated, so Player.Speed decides
        // Player.Speed the bot may not exceed this frame while a start clip drives it; null when nothing limits it
        public float? SpeedCeiling;
        // the same limit in m/s, for the inertia patch (the body follows Player.Speed only loosely)
        public float? VelocityCeiling;
        // m/s the clip wants the body at: a cut's push-off drives the body the way the grunt pushed, so the legs
        // are not left behind a body crawling up at the flat cap (user: leaning like michael jackson)
        public float? VelocityDrive;
        public bool ClipDrivenStarts = true;
        // a little headroom over the clip's root speed so the distance match never has to slow the clip below 1x
        private const float StartCeilingSlack = 1.1f;
        // the clip's first frames barely move; the body still needs to get going
        private const float StartCeilingFloor = 0.6f;

        // inverse of CommandedSpeed: Player.Speed (0-1) for a target in m/s, on the measured table
        private static float CommandFromMetersPerSecond(float mps)
        {
            if (mps <= CommandMetersPerSecond[0])
                return CommandSpeeds[0] * Mathf.Clamp01(mps / CommandMetersPerSecond[0]);
            for (int i = 1; i < CommandMetersPerSecond.Length; i++)
                if (mps <= CommandMetersPerSecond[i])
                    return Mathf.Lerp(CommandSpeeds[i - 1], CommandSpeeds[i], (mps - CommandMetersPerSecond[i - 1]) / (CommandMetersPerSecond[i] - CommandMetersPerSecond[i - 1]));
            return 1f;
        }

        private float CommandedSpeed()
        {
            // the driver's order when it has one: Player.Speed still holds the previous step's value in the frames a
            // start is decided, which put the run start under a walk
            float? ordered = _intent?.TargetCommand?.Invoke();
            float command = ordered ?? _player.Speed;
            if (command <= CommandSpeeds[0])
                return CommandMetersPerSecond[0];
            for (int i = 1; i < CommandSpeeds.Length; i++)
                if (command <= CommandSpeeds[i])
                    return Mathf.Lerp(CommandMetersPerSecond[i - 1], CommandMetersPerSecond[i], (command - CommandSpeeds[i - 1]) / (CommandSpeeds[i] - CommandSpeeds[i - 1]));
            return CommandMetersPerSecond[CommandMetersPerSecond.Length - 1];
        }

        // direction first, then the clip whose own speed is closest to what the bot is doing
        // median root speed over the clip's faster half: the pace the clip is travelling at before it brakes
        private static float EntrySpeed(PoseClip clip)
        {
            float peak = Peak(clip);
            var fast = new List<float>();
            for (int f = 0; f < clip.Frames; f++)
                if (clip.RootSpeed[f] >= peak * 0.5f)
                    fast.Add(clip.RootSpeed[f]);
            if (fast.Count == 0)
                return peak;
            fast.Sort();
            return fast[fast.Count / 2];
        }

        private static bool IsHop(PoseClip clip) => (clip?.Name ?? "").IndexOf("hop", StringComparison.OrdinalIgnoreCase) >= 0;

        private MoveSet NearestSet(float localYaw, float targetSpeed, bool needStart, float preTurned = 0f)
        {
            MoveSet best = null;
            float bestError = float.MaxValue;
            foreach (var set in _sets)
            {
                if ((needStart ? set.Start : set.Stop) == null)
                    continue;
                // a set's yaw is relative to the facing at the order; the body may have turned since (stage F)
                // only a turning clip: a straight one keeps the facing it is entered with, and counting the body's
                // turn against it shut every straight start out after an about-face (the turning nw start then won
                // at its last frame and the loops churned for a second)
                bool turningSet = needStart && Mathf.Abs(set.YawChange) > MaxStraightYawChange;
                float direction = Mathf.Abs(Mathf.DeltaAngle(set.Yaw - (turningSet ? preTurned : 0f), localYaw));
                if (direction > MaxDirectionError)
                    continue;
                // the body has all but finished this clip's turn already: nothing of it is left to play
                if (turningSet && Mathf.Sign(preTurned) == Mathf.Sign(set.YawChange) && Mathf.Abs(set.YawChange) - Mathf.Abs(preTurned) < TurnLeftMinimum)
                    continue;
                float clipSpeed = needStart ? set.StartSpeed : set.StopSpeed;
                float error = direction + (clipSpeed >= targetSpeed
                    ? FasterClipPenaltyDegreesPerMps * (clipSpeed - targetSpeed)
                    : SlowerClipPenaltyDegreesPerMps * (targetSpeed - clipSpeed));
                // the grunt has no walk start or stop (only hops, which are lunges), so the preference applies from
                // running speed up; a walk keeps the heavy stand_to_walk / walk_to_stand
                var candidate = needStart ? set.Start : set.Stop;
                error += GaitMismatch(candidate, targetSpeed);
                // stage F: a start whose turn matches what the controller is about to do wins over one that keeps
                // the facing; with no goal known the straight or strafing start is preferred
                if (needStart)
                {
                    float goal = _intent?.GoalYaw != null ? (_intent.GoalYaw() ?? 0f) : 0f;
                    error += TurnGoalWeight * Mathf.Abs(Mathf.DeltaAngle(turningSet ? set.YawChange - preTurned : 0f, goal));
                }
                if (PreferGrunt && targetSpeed >= (WalkFamily == 0 ? RunTierSpeed : SlowWalkSpeed) && IsHeavy(candidate))
                    error += HeavyClipPenalty;
                // a hop is a lunge: below the run tier it only serves as a walking start/stop in the hop family
                // sideways and backward legs keep the hops in every family (user: "grunt strafes look more natural")
                float hopFrom = WalkFamily == 2 ? SlowWalkSpeed : RunTierSpeed;
                if (targetSpeed < hopFrom && IsHop(candidate) && Mathf.Abs(Mathf.DeltaAngle(set.Yaw, 0f)) < 45f)
                    error += HeavyClipPenalty;
                // the grunt take starts/stops (mm_walk_*) are slow patrol walking; above a slow walk the other
                // families keep them out
                if (WalkFamily != 1 && targetSpeed >= SlowWalkSpeed && (candidate?.Name ?? "").StartsWith("mm_walk", StringComparison.OrdinalIgnoreCase))
                    error += HeavyClipPenalty;
                if (error < bestError)
                {
                    bestError = error;
                    best = set;
                }
            }
            return best;
        }

        private bool _nativeJumpOwned;

        private void YieldToNativeJump()
        {
            if (!_nativeJumpOwned) _events.Add(string.Format("{0:F2}s Native jump ownership", Time.time));
            _nativeJumpOwned = true;
            _reactionTorso.Cancel();
            _phase = Phase.Idle;
            _active = _fadeClip = _pendingCycle = _deferredCycleEntry = null;
            _transition = _cut = null;
            _weight = _frame = _time = _phaseFor = _standingFor = _intentFor = 0f;
            _handingOff = _handOffFromTransition = _stopAnticipated = _variation = false;
            _hadIntent = _intentFromStanding = _orderHandedOff = false;
            _lastIntent = null;
            _lastDriver = null;
            _hasPrevious = _hasYaw = _hasRelYaw = _hasTravelHeading = false;
            _inertia.Cancel();
            _lastSampled = _previousSampled = null;
            _lastSampleTime = _previousSampleTime = -1f;
            _hasShownFootbaseL = _hasShownFootbaseR = _hasShownSupport = false;
            _previousShownBaseL = _previousShownBaseR = _shownVelocityL = _shownVelocityR = null;
            _shownBaseTime = -1f;
            _nativeWalkPhaseActive = _nativeFamilyPhase = _phaseLocked = false;
            _nativeWalkLastFrame = -1;
            _speed = _quickSpeed = _previousSpeed = new Vector2(_player.Velocity.x, _player.Velocity.z).magnitude;
            _velocity = new Vector2(_player.Velocity.x, _player.Velocity.z);
            _travelYawRate = _yawRate = 0f;
            _wasSprinting = _sprintSeen = _player.IsSprintEnabled;
            _sprintSeenAt = Time.time;
            _sprintEndedAt = -10f;
            SpeedCeiling = VelocityCeiling = VelocityDrive = null;
        }

        public void Apply()
        {
            ReactionRootDrive = null;
            ResetSyncTelemetry();
            InvalidateNativeIdlePose();
            TickLandingRecovery();
            if (NativeJumpOwnership.Owns(_player)) { _reactionRequested = false; YieldToNativeJump(); return; }
            if (_nativeJumpOwned)
            {
                _nativeJumpOwned = false;
                _events.Add(string.Format("{0:F2}s Native jump released", Time.time));
            }
            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;
            _syncSampleFrame = Time.frameCount;
            _syncSampleTime = Time.time;
            // Sampling before any playback write gives the telemetry the native animator phase that drives the arms.
            // The value remains stable across after_pose, after_lock, and pre_render captures for this frame.
            CaptureAnimatorPhase();
            Vector3 position = _player.Position;
            if (_hasPrevious)
            {
                float instant = new Vector2(position.x - _previousPosition.x, position.z - _previousPosition.z).magnitude / dt;
                // a puppet teleport read as running speed and fired a bogus stop; no bot moves this fast
                if (instant < 8f)
                {
                    _travelled += instant * dt;
                    float smoothing = Mathf.Clamp01(dt / SpeedSmoothing);
                    _speed = Mathf.Lerp(_speed, instant, smoothing);
                    _quickSpeed = Mathf.Lerp(_quickSpeed, instant, Mathf.Clamp01(dt / QuickSpeedSmoothing));
                    _velocity = Vector2.Lerp(_velocity, new Vector2(position.x - _previousPosition.x, position.z - _previousPosition.z) / dt, smoothing);
                    if (_velocity.magnitude > StandingSpeed)
                    {
                        float heading = Mathf.Atan2(_velocity.x, _velocity.y) * Mathf.Rad2Deg;
                        float rate = _hasTravelHeading ? Mathf.DeltaAngle(_travelHeading, heading) / dt : 0f;
                        _travelYawRate = Mathf.Lerp(_travelYawRate, rate, smoothing);
                        _travelHeading = heading;
                        _hasTravelHeading = true;
                    }
                    else
                    {
                        _travelYawRate = Mathf.Lerp(_travelYawRate, 0f, smoothing);
                        _hasTravelHeading = false;
                    }
                }
            }
            _previousPosition = position;
            _hasPrevious = true;
            if (_sets.Count > 0)
            {
                ApplyStartStop(dt);
                if (_phase != Phase.Reaction && _reactionTorso.Active)
                {
                    Quaternion facing = Quaternion.Euler(0f, _player.Rotation.x, 0f);
                    _spine.rotation = facing * _reactionTorso.Advance(dt) * Quaternion.Inverse(facing) * _spine.rotation;
                }
                return;
            }

            bool moving = Enabled && _speed >= MovingSpeed;
            _weight = Mathf.MoveTowards(_weight, moving ? 1f : 0f, dt / BlendSeconds);
            if (_weight <= 0f)
            {
                // restart from the clip's first stride next time instead of mid-step
                _time = 0f;
                return;
            }

            Rate = Mathf.Clamp(_speed / Mathf.Max(_clip.SpeedMetersPerSecond, 0.1f), MinRate, MaxRate);
            float length = _clip.Frames / _clip.Fps;
            _time += dt * Rate;
            if (_time >= length)
                _time = _clip.Loop ? _time % length : length - 1e-4f;
            float framePosition = _time * _clip.Fps;
            int a = Mathf.FloorToInt(framePosition) % _clip.Frames;
            int b = _clip.Loop ? (a + 1) % _clip.Frames : Mathf.Min(a + 1, _clip.Frames - 1);
            float u = framePosition - Mathf.Floor(framePosition);

            Write(_clip, a, b, u, _weight);
        }

        private void ApplyStartStop(float dt)
        {
            Vector3 position = _player.Position;
            float yaw = _player.Rotation.x;
            float yawRate = _hasYaw ? Mathf.DeltaAngle(_previousYaw, yaw) / dt : 0f;
            // smoothed for the placer's step prediction; the raw rate flickers frame to frame
            _yawRate = Mathf.Lerp(_yawRate, yawRate, Mathf.Clamp01(dt / 0.1f));
            if (_hasYaw && _phase == Phase.Transition)
                _yawTurned += Mathf.DeltaAngle(_previousYaw, yaw);
            _previousYaw = yaw;
            _hasYaw = true;
            float radians = yaw * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians), sin = Mathf.Sin(radians);
            _localVelocity = new Vector2(_velocity.x * cos - _velocity.y * sin, _velocity.x * sin + _velocity.y * cos);
            ReadShownFeet();
            if (_animated == null || _animated.Length != _bones.Length)
                _animated = new Quaternion[_bones.Length];
            if (_previousAnimated == null || _previousAnimated.Length != _bones.Length)
                _previousAnimated = new Quaternion[_bones.Length];
            Array.Copy(_animated, _previousAnimated, _bones.Length);
            _previousAnimatedPelvis = _animatedPelvis;
            _previousAnimatedTime = _animatedTime;
            _animatedTime = Time.time;
            for (int i = 0; i < _bones.Length; i++)
                _animated[i] = _bones[i].localRotation;
            _animatedPelvis = _pelvis.localPosition;
            _inertia.Advance(dt);

            float? intent = Enabled ? _moveIntent() : null;
            _lastIntent = intent;
            bool wantsMove = intent.HasValue;
            // a cut is a change of travel in the world; a bot looking round while it runs swings the body-relative
            // intent by 170 deg with the travel unchanged (a stock scav fired six strafe cuts in a 12 s straight
            // run), and that only needs the directional cycle re-picked
            float bodyYaw = _player.Rotation.x;
            bool intentSwung = wantsMove && _hadIntent && Mathf.Abs(Mathf.DeltaAngle(bodyYaw + intent.Value, _previousIntentWorld)) > CutIntentChange;
            // a directional stop clip is chosen against the body, so a facing swing alone still invalidates it
            bool relativeSwung = wantsMove && _hadIntent && Mathf.Abs(Mathf.DeltaAngle(intent.Value, _previousIntent)) > CutIntentChange;
            _hadIntent = wantsMove;
            float? remaining = Enabled && wantsMove ? _goal() : null;
            SpeedCeiling = null;
            VelocityCeiling = null;
            VelocityDrive = null;
            string driver = _intent?.Driver != null ? _intent.Driver() : null;
            bool pathLost = _lastDriver == "mover" && driver == "measured";
            _lastDriver = driver;
            if (ApplyReaction(dt)) return;
            if (pathLost && wantsMove && _speed > StopTriggerSpeed && !_player.IsSprintEnabled
                && (_phase == Phase.SprintCycle || _phase == Phase.Start || _phase == Phase.Idle))
            {
                // the stock mover clears its path about a stride short of the goal and lets EFT brake; without a
                // goal left to anticipate, treat that as the order to stop over EFT's own braking distance. the
                // driver swap changes the intent yaw (corner vs velocity), which must not read as a swing, and the
                // measured driver keeps saying "move" while the body brakes, so the stop runs as an anticipated one
                intentSwung = false;
                relativeSwung = false;
                BeginStop(Mathf.Clamp(StopDistancePerSpeed * _speed, MinStopDistance, MaxStopDistance));
                if (_phase == Phase.Stop)
                {
                    _stopAnticipated = true;
                    _stopGoalLast = 0f;
                }
            }
            // Valve stops on goal distance, not on the controller letting go: with the goal known, start the stop clip
            // where its braking distance runs out at the goal plus EFT's own hard brake, at the rate the body runs
            if (AnticipateStops && wantsMove && remaining.HasValue && _speed > StopTriggerSpeed && !_player.IsSprintEnabled && Time.time >= _stopRefireAt
                && (_phase == Phase.SprintCycle || _phase == Phase.Start || _phase == Phase.Idle))
            {
                float brake = Mathf.Clamp(StopDistancePerSpeed * _speed, MinStopDistance, MaxStopDistance);
                float clipBraking = Mathf.Min(StopEntryDistance(_speed), MaxStopDistanceFor(_speed));
                // the window opens as early as the clip's full braking distance allows (entering with more clip
                // distance than the body has left only plays the tail after the halt; entering with less stalls
                // the clip while the body walks, which dragged both feet 40 cm in 2450 frames of a strafe raid)
                // and the stop waits inside it for a plant the loop's feet match, entering at the ideal distance
                // regardless
                // the window opens where the prediction can still be honoured (capped like the prediction itself
                // and by the clip's full braking run); opening at the clip's full 4.7 m fired a run stop 1.7 s
                // early, which ran out of clip at 2.6 m/s and locked both feet under a running body
                float clipFull = StopFullDistance(_speed);
                float window = Mathf.Min(MaxStopDistanceFor(_speed), clipFull) - brake;
                if (clipBraking > brake && remaining.Value <= Mathf.Max(clipBraking - brake, window))
                {
                    BeginStop(remaining.Value + brake, remaining.Value > clipBraking - brake);
                    if (_phase == Phase.Stop)
                    {
                        _stopAnticipated = true;
                        _stopGoalLast = remaining.Value;
                    }
                }
            }
            // "was standing when the order came": the body is already moving by the time the start is allowed
            // to fire, so the standing test has to be taken at the order's first frame
            if (wantsMove && _intentFor <= 0f)
            {
                _intentFromStanding = _standingFor >= StandingSeconds;
                _orderYaw = bodyYaw;
            }
            if (!wantsMove)
                _orderHandedOff = false;
            _intentFor = wantsMove ? _intentFor + dt : 0f;
            if (wantsMove)
            {
                _previousIntent = intent.Value;
                _previousIntentWorld = bodyYaw + intent.Value;
            }
            float acceleration = (_speed - _previousSpeed) / dt;
            _previousSpeed = _speed;
            _standingFor = _speed < StandingSpeed ? _standingFor + dt : 0f;
            _phaseFor += dt;
            // the speed ramp only caps rises, and a halted bot keeps its last Player.Speed: the next start then had
            // nothing to ramp and hit 2.4 m/s in 0.2 s under a start clip racing at 2.2x
            if (SpeedRampPatch.LimitPerSecond > 0f && !wantsMove && !_player.IsSprintEnabled && _standingFor > StandingSpeedResetSeconds && _player.Speed > StandingSpeedFloor)
                _player.ChangeSpeed(StandingSpeedFloor - _player.Speed);
            _sinceTurn = _phase == Phase.Cut && _cut != null && _frame >= _cut.TurnFrame ? _sinceTurn + dt : 0f;

            bool sprinting = _player.IsSprintEnabled;
            // a stock scav toggled sprint off and on within 0.4 s as one path ended and the next began, which played
            // enter, exit, enter transitions back to back; act on a sprint state only once it has held a moment
            if (sprinting != _sprintSeen)
            {
                _sprintSeen = sprinting;
                _sprintSeenAt = Time.time;
                // the slide guard (SprintStopGrace) must see every raw exit, debounced or not
                if (!sprinting)
                    _sprintEndedAt = Time.time;
            }
            if (sprinting != _wasSprinting && Time.time - _sprintSeenAt >= SprintStateHold)
            {
                // sprint -> stop is left to Tarkov: its power slide is one of the sprint animations the user keeps,
                // and Alyx has no skid clip to match it with
                if (!SprintTransitions) { }
                else if (sprinting && _speed > StopTriggerSpeed)
                    TryBeginSprintTransition(_sprintEnters, TravelYaw(), true);
                else if (!sprinting && wantsMove && _speed > SprintExitSpeed)
                    TryBeginSprintTransition(_sprintExits, intent.Value, false);
                if (!sprinting)
                    _sprintEndedAt = Time.time;
                _wasSprinting = sprinting;
            }
            else if (intentSwung && _speed > StopTriggerSpeed && !sprinting && _speed < SprintStopSpeed
                && _phase != Phase.Stop && _phase != Phase.Hold && _phase != Phase.Transition
                // legs Tarkov's animator owns turn with it: a cut clip under its run played 25 times a minute
                // (Customs batch 5), the backward strafe cut alone carrying most of the raid's flags
                && !(_phase == Phase.Idle && TarkovRunLegs && !HasTarkovCycles && _speed >= TarkovRunSpeed))
            {
                TryBeginCut(intent.Value);
            }

            switch (_phase)
            {
                case Phase.Turn:
                    if (wantsMove || sprinting)
                    {
                        // the order changed: a start or the sprint takes over from the inertialized hand-off
                        Begin(Phase.HandOff, _active, _frame);
                        break;
                    }
                    {
                        float stepYaw = Mathf.DeltaAngle(_turnPrevYaw, bodyYaw);
                        _turnPrevYaw = bodyYaw;
                        _turnTurned += stepYaw;
                        _turnReversedFor = Mathf.Sign(stepYaw) != Mathf.Sign(_turnGoal) && Mathf.Abs(stepYaw) > 0.5f * dt ? _turnReversedFor + dt : 0f;
                        bool done = Mathf.Abs(_turnTurned) >= Mathf.Abs(_active.YawChange) - TurnFinishMargin;
                        float step = dt * _active.Fps;
                        if (done || _frame >= _active.Frames - 1)
                        {
                            // the body has turned as far as the clip does: the settle plays out at its own pace
                            Rate = 1f;
                            _frame = Mathf.Min(_frame + step, _active.Frames - 1);
                            if (_frame >= _active.Frames - 1)
                                Begin(Phase.Hold, _active, _frame);
                            break;
                        }
                        int matched = FrameAtYaw(_active, _turnTurned);
                        float before = _frame;
                        // half speed at least: the authored turn sits late in the clip, and waiting for the body's
                        // yaw to reach it played the whole step after the body had finished turning
                        _frame = SlewTo(_frame, matched, step, MinTurnRate, MaxTurnRate, dt, _active.Frames - 1);
                        _turnStalledFor = _frame - before < 1e-3f && Mathf.Abs(_yawRate) < 10f ? _turnStalledFor + dt : 0f;
                        // review: abort on sustained opposite rotation or stalled progress, never rewind
                        if (_turnReversedFor > TurnReverseSeconds || _turnStalledFor > TurnStallSeconds)
                        {
                            _events.Add(string.Format("{0:F2}s Turn abandoned: {1} turned {2:F0} of {3:F0}", Time.time, _turnReversedFor > TurnReverseSeconds ? "reversed" : "stalled", _turnTurned, _active.YawChange));
                            Begin(Phase.HandOff, _active, _frame);
                        }
                    }
                    break;
                case Phase.Hold:
                    // Corrective steps finish the stance before ownership changes. Only
                    // hand over when the rendered feet and native pose already agree.
                    if (!wantsMove && !sprinting && LocomotionRefinement.Enabled && StopSettled != null
                        && _hasNativeIdlePose && _nativeIdlePoseFrame == Time.frameCount
                        && StopSettled(_nativeIdleCenterL, _nativeIdleCenterR, _nativeIdleHeadingL, _nativeIdleHeadingR) && _phaseFor > 0.15f)
                    {
                        Begin(Phase.HandOff, _active, _frame);
                        break;
                    }
                    // the held stance stays while the bot stands (user: handing off slid the feet into Tarkov's idle
                    // every time, and its idle stance after a turn is not the one after a long stand). a long idle
                    // hands off slowly instead
                    if (!wantsMove && _phaseFor >= HoldMaxSeconds
                        && !(LocomotionRefinement.Enabled && _active.EndsInTarkovIdle && StopSettled != null))
                    {
                        _holdRelease = true;
                        Begin(Phase.HandOff, _active, _active.Frames - 1);
                        break;
                    }
                    if (wantsMove)
                    {
                        if (Mathf.Abs(Mathf.DeltaAngle(0f, intent.Value)) <= 45f)
                        {
                            Begin(Phase.HandOff, _active, _active.Frames - 1);
                            _orderHandedOff = true;
                            _intentFromStanding = false;
                            break;
                        }
                        var next = NearestSet(intent.Value, CommandedSpeed(), true);
                        if (next != null)
                        {
                            _set = next;
                            CrossfadeTo(Phase.Start, next.Start, StartEntryFrame(next));
                        }
                        else
                        {
                            Begin(Phase.HandOff, _active, _active.Frames - 1);
                        }
                    }
                    else if (_speed > HoldDriftSpeed || sprinting)
                    {
                        Begin(Phase.HandOff, _active, _active.Frames - 1);
                    }
                    else if (Mathf.Abs(yawRate) > TurnStartRate && TryBeginTurn(_intent, bodyYaw))
                    {
                    }
                    else if (Mathf.Abs(Mathf.DeltaAngle(_holdYawStart, bodyYaw)) > HoldPivotRelease && Mathf.Abs(yawRate) < HoldPivotSettled)
                    {
                        // Tarkov's own turn keeps the feet planted through the whole rotation and plays its catch-up
                        // step (a 0.83 s base-layer state) once the rotation stops: the hold does the same and hands
                        // off at that moment so the step plays (control capture 232547)
                        Begin(Phase.HandOff, _active, _active.Frames - 1);
                    }
                    else if (Mathf.Abs(Mathf.DeltaAngle(_holdYawStart, bodyYaw)) > HoldMaxPivot)
                    {
                        Begin(Phase.HandOff, _active, _active.Frames - 1);
                    }
                    break;
                case Phase.Idle:
                    // the steadiness gate must not hold a start until the body is at full speed: the puppet turns
                    // onto its lane in one frame and the smoothed yaw rate stayed over the gate for 0.3 s, so the
                    // start opened at 1.4 m/s straight onto a mid-stride pose (user: "snaps into a wide open
                    // stance"). a held order starts anyway; the repick covers a direction read while turning
                    // never a start under a sprint: the sprint hand-off dropped to Idle and this fired again every
                    // frame (Start, HandOff, Idle, Start... 92 times in 4 s on a sprinting PMC in the fleet raids)
                    // never twice for one order: with Tarkov's run serving the run band, a start handed off and Idle
                    // fired the next start for the same order at once (Customs batch 5: starts and stops chained
                    // under running bots, "feet freaking out and scrambling", user)
                    if (wantsMove && !sprinting && _intentFromStanding && !_orderHandedOff && (Mathf.Abs(yawRate) < SteadyYawRate || _intentFor >= StartIntentSeconds)
                        && (_speed > StartMotionSpeed || _intentFor >= StartIntentSeconds))
                    {
                        // the body has been turning since the order came (the start waits 0.2 s for a held order):
                        // the pick and the entry frame count that turn, or a 90 read as a 60 picks the 45 clip
                        if (Mathf.Abs(Mathf.DeltaAngle(0f, intent.Value)) <= 45f)
                        {
                            _orderHandedOff = true;
                            _intentFromStanding = false;
                            break; // native forward acceleration/animation; stops and strafe starts stay authored
                        }
                        float preTurned = Mathf.DeltaAngle(_orderYaw, bodyYaw);
                        var next = NearestSet(intent.Value, CommandedSpeed(), true, preTurned);
                        if (next != null)
                        {
                            _set = next;
                            bool turns = Mathf.Abs(next.YawChange) > MaxStraightYawChange;
                            Begin(Phase.Start, next.Start, turns ? Mathf.Max(StartEntryFrame(next), FrameAtYaw(next.Start, preTurned)) : StartEntryFrame(next));
                            if (turns) _startTurned = preTurned;
                        }
                    }
                    else if (!wantsMove && Enabled && _speed > StopTriggerSpeed)
                    {
                        BeginStop();
                    }
                    else if (!wantsMove && !sprinting && Enabled && _speed < StandingSpeed && _phaseFor > 0.2f && TryBeginTurn(_intent, bodyYaw))
                    {
                    }
                    else if (wantsMove && SprintOnPlacer && sprinting && _speed > SprintExitSpeed && _phaseFor > 0.3f && Time.time - _sprintSeenAt > SprintStateHold)
                    {
                        TryBeginCycle(true);
                    }
                    else if (wantsMove && AlyxCycles && !_inertia.Active && !_upperRecovery.Active && _speed > StopTriggerSpeed && !_player.IsSprintEnabled && _phaseFor > 0.3f
                        && (!_intentFromStanding || _intentFor > 1f))
                    {
                        // a bot already running when the layer attaches (observe mode), or handed back from a
                        // Tarkov sprint without a transition, never passes through a start: pick its cycle here.
                        // not while a start is merely waiting for the body to finish its turn (the walk puppet
                        // turned onto the lane as the order came and lost its start clips to this rule)
                        TryBeginCycle();
                    }
                    break;
                case Phase.Start:
                    if (sprinting)
                    {
                        // Tarkov keeps its sprint (user decision); a PMC that hit sprint during an Alyx start kept the
                        // start clip running hurried under a sprinting body for 12 s in the first fleet raid.
                        // with the sprint pass on, the sprint family takes over from the start clip instead
                        if (!SprintOnPlacer)
                        {
                            Begin(Phase.HandOff, _set.Start, _frame);
                            break;
                        }
                        // the start carries the build-up until the body is fast enough for a sprint-time loop
                        if (_speed > SprintExitSpeed && TryBeginCycle(true))
                            break;
                    }
                    if (!wantsMove)
                    {
                        // a start abandoned below the stop speed hands back instead of falling through to the repick,
                        // which read the empty intent and threw (review)
                        if (_speed > StopTriggerSpeed)
                            BeginStop();
                        else
                            Begin(Phase.HandOff, _set.Start, _frame);
                        break;
                    }
                    // the body may still be swinging onto its new facing when a start fires, so the travel direction
                    // read then can be 90 deg stale: a left strafe once started on the backwards run clip
                    {
                        float stepYaw = Mathf.DeltaAngle(_startPrevYaw, bodyYaw);
                        _startPrevYaw = bodyYaw;
                        _startTurned += stepYaw;
                    }
                    bool turningStart = Mathf.Abs(_set.YawChange) > MaxStraightYawChange;
                    bool midTurn = turningStart && Mathf.Abs(_startTurned) > 10f && Mathf.Abs(_startTurned) < Mathf.Abs(_set.YawChange) - 15f;
                    if (RepickStart && !_startRepicked && !midTurn && Mathf.Abs(Mathf.DeltaAngle(_set.Yaw - _startTurned, intent.Value)) > MaxDirectionError
                        && _phaseFor < 0.6f && Mathf.Abs(yawRate) < SteadyYawRate)
                    {
                        var aimed = NearestSet(intent.Value, CommandedSpeed(), true);
                        if (aimed != null && aimed != _set)
                        {
                            _set = aimed;
                            _startRepicked = true;
                            CrossfadeTo(Phase.Start, aimed.Start, StartEntryFrame(aimed));
                            break;
                        }
                    }
                    if (_speed > _set.StartSpeed * 1.15f && !midTurn && !_variation)
                    {
                        // Player.Speed can still hold the last step's value when the start fires, so a run can begin on a slower clip
                        var faster = NearestSet(_set.Yaw, _speed, true);
                        if (faster != null && faster != _set)
                        {
                            _set = faster;
                            CrossfadeTo(Phase.Start, faster.Start, FirstPeakAtOrAbove(faster, _speed, 0));
                        }
                    }
                    {
                        int matched = _set.Start.FrameAtDistance(TargetDistance(), (int)_frame);
                        // a turning start also follows the body's turn: the later of the two matches leads (review:
                        // max of yaw and distance targets, as the sprint transitions do), never a rewind
                        if (turningStart)
                            matched = Mathf.Max(matched, FrameAtYaw(_set.Start, _startTurned));
                        _frame = Advance(_frame, matched, dt, ClipDrivenStarts ? 1f : MinMatchRate);
                    }
                    TrackPathBehind(_set.Start);
                    // the body may not outrun the clip's own acceleration (EFT is at full speed in 0.3 s, the clip
                    // in ~1 s); with the body held to the clip's root speed the distance match runs near 1x instead
                    // of racing at 2.5x (hurried, knees high, feet trailing: "janky and wobbly" starts, user)
                    if (ClipDrivenStarts && _set.Start.RootSpeed != null)
                    {
                        int sf = Mathf.Clamp(Mathf.RoundToInt(_frame), 0, _set.Start.Frames - 1);
                        float allowed = Mathf.Max(_set.Start.RootSpeed[sf], _set.Start.RootSpeed[Mathf.Min(sf + 3, _set.Start.Frames - 1)]) * StartCeilingSlack;
                        SpeedCeiling = CommandFromMetersPerSecond(Mathf.Max(allowed, StartCeilingFloor));
                        VelocityCeiling = Mathf.Max(allowed, StartCeilingFloor);
                    }
                    // with Alyx cycles the start plays out to its end (a hop's push and landing are the point of it);
                    // without them it hands back to Tarkov as soon as the body has settled at speed.
                    // a start the body has outrun (live bots at 1.8-2.4 m/s on a 1.5 m/s hop, fleet batch 2: locked
                    // feet dragged 35-46 cm because the distance match is capped at 1.25x) goes to the loop early,
                    // whose rate follows the body
                    bool outrun = AlyxCycles && _phaseFor > OutrunStartSeconds && _speed > _set.StartSpeed * OutrunStartFactor;
                    if (outrun && TryBeginCycle())
                        break;
                    if (AlyxCycles ? NearEnd(_set.Start, HandOffBlend) : (NearEnd(_set.Start, HandOffBlend) || (_frame > 3f && _speed > StopTriggerSpeed && acceleration < SettledAcceleration)))
                    {
                        if (AlyxCycles && TryBeginCycle())
                            break;
                        if (ReadyToHandOff(_set.Start, dt))
                            Begin(Phase.HandOff, _set.Start, _frame);
                    }
                    break;
                case Phase.Cut:
                    if (!wantsMove && _speed > StopTriggerSpeed)
                    {
                        BeginStop();
                        break;
                    }
                    // a strafe cut kept playing under a body that broke into a sprint, racing at 2.3-2.5x (SAIN fleet)
                    if (sprinting && Time.time - _sprintSeenAt > SprintStateHold && _speed > SprintExitSpeed)
                    {
                        if (SprintOnPlacer)
                        {
                            // no sprint clip fits a body still travelling sideways: the cut plays on until one does
                            if (TryBeginCycle(true))
                                break;
                        }
                        else
                        {
                            Begin(Phase.HandOff, _cut, _frame);
                            break;
                        }
                    }
                    AdvanceCut(dt);
                    if (_cut.RootSpeed != null && _frame >= _cut.TurnFrame)
                    {
                        int cf = Mathf.Clamp(Mathf.RoundToInt(_frame), 0, _cut.Frames - 1);
                        float authored = Mathf.Max(_cut.RootSpeed[cf], _cut.RootSpeed[Mathf.Min(cf + 3, _cut.Frames - 1)]);
                        VelocityDrive = Mathf.Max(authored, StartCeilingFloor);
                    }
                    // done once the bot is back up to speed in the new direction
                    if (NearEnd(_cut, HandOffBlend) || (!AlyxCycles && _frame > _cut.TurnFrame && _speed > StopTriggerSpeed && acceleration < SettledAcceleration
                        && wantsMove && Mathf.Abs(Mathf.DeltaAngle(TravelYaw(), intent.Value)) < CutDirectionError))
                    {
                        if (AlyxCycles && TryBeginCycle())
                            break;
                        if (ReadyToHandOff(_cut, dt))
                            Begin(Phase.HandOff, _cut, _frame);
                    }
                    break;
                case Phase.Transition:
                    if (!wantsMove && _speed > StopTriggerSpeed)
                    {
                        BeginStop();
                        break;
                    }
                    AdvanceTransition(dt);
                    // the body outran the sprint entry (2.3x+ at 5 m/s in the SAIN fleet): the sprint family takes over
                    if (sprinting && SprintOnPlacer && _speed > TransitionOutrunSpeed && Rate > TransitionOutrunRate && TryBeginCycle(true))
                        break;
                    if (NearEnd(_transition, TransitionHandOffBlend) || (Mathf.Abs(_yawTurned) >= Mathf.Abs(_transition.YawChange) - 10f
                        && acceleration < SettledAcceleration && ReadyToHandOff(_transition, dt)))
                    {
                        if (!TryBeginSprintCycle())
                            Begin(Phase.HandOff, _transition, _frame);
                    }
                    break;
                case Phase.SprintCycle:
                    if (sprinting && SprintOnPlacer && IsTarkovSprint(_active))
                    {
                        // Tarkov's own sprint on the placer: a stop is Tarkov's power slide, everything else stays
                        if (!wantsMove)
                        {
                            Begin(Phase.HandOff, _active, _frame);
                            break;
                        }
                    }
                    else if (sprinting && SprintOnPlacer && IsTarkov(_active) && _speed < SprintRunLoopBelow)
                    {
                        // the run loop carries the build-up; the sprint family takes over as the speed arrives
                        if (!wantsMove)
                        {
                            Begin(Phase.HandOff, _active, _frame);
                            break;
                        }
                    }
                    else if (sprinting && (!SprintTransitions || Time.time - _sprintSeenAt > SprintStateHold + 0.1f))
                    {
                        // the sprint transition had its chance; a cycle still showing under a sprint hands back,
                        // or moves to the sprint family when that is on the placer
                        // on the placer the loop showing stays until the sprint family can take it (a failed pick
                        // handed the legs to the animator and back: a quarter of sprint time off the layer, user
                        // saw the gizmos blink)
                        if (SprintOnPlacer)
                        {
                            if (TryBeginCycle(true))
                                break;
                        }
                        else
                        {
                            Begin(Phase.HandOff, _active, _frame);
                            break;
                        }
                    }
                    if (!wantsMove && _speed > StopTriggerSpeed)
                    {
                        BeginStop();
                        break;
                    }
                    // the quick speed estimate: the 0.2 s smoothed one lagged live bots' accelerations by 0.15 m/s
                    // median (0.6 at the 90th percentile) and their locked feet were dragged for it
                    Rate = Mathf.Clamp(_quickSpeed / Mathf.Max(_active.SpeedMetersPerSecond, 0.1f), MinCycleRate, MaxCycleRate);
                    _frame += dt * _active.Fps * Rate;
                    if (_frame >= _active.Frames)
                        _frame -= _active.Frames;
                    // a run loop under the sprint animator has no matching state to lock to
                    if (!(sprinting && !IsTarkovSprint(_active)))
                        LockPhaseToAnimator();
                    UpdateFamilyBlend(dt);
                    if (sprinting && SprintOnPlacer && IsTarkov(_active))
                    {
                        float sprintRatio = _speed / Mathf.Max(_active.SpeedMetersPerSecond, 0.1f);
                        if (Time.time - _clipSince >= CycleRepickSeconds && (sprintRatio < CycleRepickMinRatio || sprintRatio > CycleRepickMaxRatio))
                            TryBeginCycle(true);
                        break;
                    }
                    // sprinting stays Tarkov (user): the loop only bridges into its sprint cycle; everything slower stays Alyx
                    if (AlyxCycles && !_player.IsSprintEnabled)
                    {
                        // slowing to a halt without a stop clip firing (below its trigger speed): Tarkov's idle takes over
                        if (!wantsMove && _speed < StopTriggerSpeed)
                        {
                            Begin(Phase.HandOff, _active, _frame);
                            break;
                        }
                        float drift = Mathf.Abs(Mathf.DeltaAngle(_active.MoveYaw, TravelYaw()));
                        float ratio = _speed / Mathf.Max(_active.SpeedMetersPerSecond, 0.1f);
                        // up into Tarkov's run band straight ahead: its own legs take over on a matching pose
                        if (TarkovRunLegs && !HasTarkovCycles && _speed >= TarkovRunSpeed && Mathf.Abs(TravelYaw()) < TarkovRunYaw && Time.time - _clipSince > CycleSettleSeconds
                            && ReadyToHandOff(_active, dt))
                        {
                            Begin(Phase.HandOff, _active, _frame);
                            break;
                        }
                        // measured from the last clip change, not the phase: a loop-to-loop switch never reset it (review)
                        // a Tarkov cycle takes the band from an Alyx loop the start entered below it (the puppet's
                        // run start handed to the heavy loop at 2.1 m/s and kept it at 2.8)
                        bool bandSwap = TarkovRunLegs && HasTarkovCycles && !IsTarkov(_active) && _speed >= NativeWalkMinimumSpeed && Mathf.Abs(TravelYaw()) < TarkovRunYaw
                            && Time.time - _clipSince >= CycleSettleSeconds;
                        if (Time.time - _clipSince >= CycleRepickSeconds && _speed > StopTriggerSpeed && (bandSwap || drift > CycleRepickDirection || ratio < CycleRepickMinRatio || ratio > CycleRepickMaxRatio))
                            TryBeginCycle();
                        if (Variations > 0 && _phase == Phase.SprintCycle && Time.time - _clipSince > VariationLoopSeconds && Mathf.Abs(yawRate) < SteadyYawRate
                            && _speed > StopTriggerSpeed && !_stopAnticipated)
                        {
                            bool combatNow = InCombat != null && InCombat();
                            if (_nextVariationAt <= 0f)
                            {
                                ScheduleVariation(combatNow);
                                _events.Add(string.Format("{0:F2}s Variation scheduled in {1:F1} s ({2})", Time.time, _nextVariationAt - Time.time, combatNow ? "combat" : "patrol"));
                            }
                            else if (combatNow && !_variationForCombat)
                                // a patrol timer (15-40 s) set before the fight would outlast most strafes
                                ScheduleVariation(true);
                            else if (Time.time >= _nextVariationAt)
                            {
                                TryBeginVariation();
                                ScheduleVariation(combatNow);
                            }
                        }
                        break;
                    }
                    // with the sprint on the placer nothing hands back here: inside the sprint flag's debounce this
                    // rule dropped every loop to the animator the moment a sprint began, until 4.1 m/s (user saw the
                    // gizmos blink; a fifth of sprint time off the layer)
                    if (SprintOnPlacer)
                        break;
                    // hand back the moment Tarkov's own sprint pose lines up, rather than at an arbitrary frame
                    if (AnimatedFeetCost(_active, _frame) <= HandOffFeetCost || _phaseFor >= MaxSprintCycleSeconds)
                        Begin(Phase.HandOff, _active, _frame);
                    break;
                case Phase.Stop:
                    if (_stopAnticipated)
                    {
                        // the driver still says move while we brake toward its goal; only a new order ends that
                        bool replanned = remaining.HasValue && remaining.Value > _stopGoalLast + 0.5f;
                        // a swing with the goal almost reached is the mover flipping its corner behind the bot, not
                        // a new order: batch 4 raid 1 churned Stop, HandOff, Idle, Stop every frame (146 stops a
                        // minute) on bots 0.2-0.4 m from their goal, legs flailing (user). finish the stop instead
                        bool nearGoal = remaining.HasValue && remaining.Value < StopGoalSettled;
                        if ((intentSwung || relativeSwung) && !nearGoal || replanned)
                        {
                            _stopAnticipated = false;
                            _stopRefireAt = Time.time + StopRefireSeconds;
                            _events.Add(string.Format("{0:F2}s Stop abandoned: {1} remaining {2:F2} m", Time.time, replanned ? "replanned" : intentSwung ? "travel swung" : "facing swung", remaining ?? -1f));
                            // a new order under a moving body: a cut or a loop carries on from the stop's pose. the
                            // hand-off dropped to Idle and a start fired the next frame, knees jumping 20-30 m/s
                            if (wantsMove && _speed > StopTriggerSpeed)
                            {
                                TryBeginCut(intent.Value);
                                if (_phase == Phase.Cut || (AlyxCycles && TryBeginCycle(true)))
                                    break;
                            }
                            Begin(Phase.HandOff, _set.Stop, _frame);
                            break;
                        }
                        if (remaining.HasValue)
                            _stopGoalLast = remaining.Value;
                        if (!wantsMove)
                            _stopAnticipated = false;
                    }
                    else if (wantsMove)
                    {
                        Begin(Phase.HandOff, _set.Stop, _frame);
                        break;
                    }
                    if (_frame < _set.StopMotionEnd)
                    {
                        // distance matching: EFT coasts ~0.2 s at full speed then brakes hard, which a constant-deceleration
                        // model froze the clip through (feet skated with the body). the prediction runs slightly short so any
                        // leftover lands while the body is still sliding rather than after it has stopped
                        float travelled = new Vector2(position.x - _stopOrigin.x, position.z - _stopOrigin.z).magnitude;
                        float left = _quickSpeed < StoppedSpeed ? 0f : Mathf.Max(0f, _stopPredicted - travelled);
                        int matched = FirstStopFrameWithin(left, (int)_frame);
                        float step = dt * _set.Stop.Fps;
                        _frame = SlewTo(_frame, matched, step, MinStopRate, MaxStopRate, dt, _set.StopMotionEnd);
                    }
                    else
                    {
                        // planted settle plays at its own pace
                        Rate = 1f;
                        _frame = Mathf.Min(_frame + dt * _set.Stop.Fps, _set.Stop.Frames - 1);
                    }
                    // even a stop stepped into Tarkov's idle stance holds: the user saw feet rotate during an immediate
                    // blend, while the held stance is close enough to idle to look natural until the bot moves or turns
                    if (_frame >= _set.Stop.Frames - 1 && _speed < HoldDriftSpeed)
                        Begin(Phase.Hold, _set.Stop, _set.Stop.Frames - 1);
                    else if (_frame >= _set.Stop.Frames - 1)
                    {
                        // the clip is done but the body is still going (a braking estimate that ran short): waiting with
                        // the feet anchored stretched the legs into a split until the body stopped, then snapped (user
                        // screenshot). give the legs back to Tarkov after a moment instead
                        _stopOverrunFor += dt;
                        if (_stopOverrunFor > StopOverrunSeconds)
                            Begin(Phase.HandOff, _set.Stop, _frame);
                    }
                    else
                        _stopOverrunFor = 0f;
                    break;
                case Phase.HandOff:
                    // keep the clip moving at normal rate while Tarkov's legs fade back in
                    _frame = Mathf.Min(_frame + dt * _active.Fps, _active.Frames - 1);
                    break;
            }

            float clipSpeed = _active == null ? 0f : _active.SpeedMetersPerSecond;
            float wanted = clipSpeed > 0.2f && _speed > 0.2f ? Mathf.Clamp(_speed / clipSpeed, MinStrideScale, MaxStrideScale) : 1f;
            _strideScale = Mathf.Lerp(_strideScale, wanted, Mathf.Clamp01(dt / StrideSmoothing));

            bool startingHandOff = _phase == Phase.HandOff && !_handingOff;
            _handingOff = _phase == Phase.HandOff;
            if (startingHandOff && Inertialize && _lastSampled != null)
            {
                // from here Tarkov animates the legs again; our remaining difference bleeds off rather than dissolving
                float duration = _handOffFromTransition ? TransitionHandOffBlend : HandOffBlend;
                float historyDt = _lastSampleTime - _previousSampleTime;
                float nativeDt = _animatedTime - _previousAnimatedTime;
                if (_previousSampleTime >= 0f && _previousAnimatedTime >= 0f && historyDt > 0f && historyDt <= .05f
                    && Mathf.Abs(historyDt - nativeDt) < .001f && Time.time - _lastSampleTime <= .05f)
                    _inertia.Begin(_lastSampled, _animated, _lastPelvis, _animatedPelvis, duration,
                        _previousSampled, _previousAnimated, _previousSampledPelvis, _previousAnimatedPelvis,
                        historyDt, Time.time - _lastSampleTime);
                else
                    _inertia.Begin(_lastSampled, _animated, _lastPelvis, _animatedPelvis, duration);
                _weight = 0f;
            }

            float target = _phase == Phase.Idle || _phase == Phase.HandOff ? 0f : 1f;
            float blend = _phase == Phase.Start ? StartBlendIn : _phase == Phase.Cut || _phase == Phase.Transition || _phase == Phase.SprintCycle ? CutBlendIn
                : _phase == Phase.Stop || _phase == Phase.Hold ? StopBlendIn
                : _holdRelease ? HoldReleaseBlend : _handOffFromTransition ? TransitionHandOffBlend : HandOffBlend;
            _weight = Mathf.MoveTowards(_weight, target, dt / blend);
            if (_phase == Phase.HandOff && _weight <= 0f)
                Begin(Phase.Idle, null, 0f);
            if (_weight <= 0f && Inertialize && _inertia.Active)
            {
                ApplyInertia();
                return;
            }
            if (_active == null || _weight <= 0f)
                return;
            int a = Mathf.Clamp(Mathf.FloorToInt(_frame), 0, _active.Frames - 1);
            int b = _active.Loop ? (a + 1) % _active.Frames : Mathf.Min(a + 1, _active.Frames - 1);
            Rate = _phase == Phase.HandOff ? 1f : Rate;
            Write(_active, a, b, _frame - a, _weight);
        }

        // ground covered since this clip began, plus a lead so the stride lands ahead of the body instead of behind it
        private float TargetDistance() => _moveOriginPath + (_travelled - _travelledAtBegin + LeadSeconds * _speed) / Mathf.Max(_strideScale, 0.1f);

        private void TrackPathBehind(PoseClip clip)
        {
            if (clip.PathLength == null)
                return;
            int f = Mathf.Clamp(Mathf.RoundToInt(_frame), 0, clip.Frames - 1);
            _pathBehind = TargetDistance() - clip.PathLength[f];
        }

        public float PathBehind => _pathBehind;

        // pick by where the bot is travelling now (entering) or where it wants to go (leaving), then follow its turn
        private void TryBeginSprintTransition(List<PoseClip> clips, float localYaw, bool entering)
        {
            // the body swings onto its travel direction entering a sprint, and away from it when leaving
            float expectedTurn = entering ? localYaw : -localYaw;
            if (Mathf.Abs(expectedTurn) < SprintTurnMinimum)
                return;
            PoseClip best = null;
            float bestError = float.MaxValue;
            foreach (var clip in clips)
            {
                float direction = Mathf.Abs(Mathf.DeltaAngle(entering ? clip.FromYaw : clip.ToYaw, localYaw));
                float turn = Mathf.Abs(Mathf.DeltaAngle(clip.YawChange, expectedTurn));
                if (direction > SprintTransitionError || turn > SprintTurnError)
                    continue;
                if (direction + turn < bestError)
                {
                    bestError = direction + turn;
                    best = clip;
                }
            }
            if (best == null)
                return;

            // enter on the frame whose feet best match what is on screen; frame 0 left the blend 0.2-0.65 m2 to cover
            int entry = 0;
            float bestCost = float.MaxValue;
            int window = Mathf.Max(1, best.Frames / 3);
            for (int f = 0; f < window; f++)
            {
                float cost = FeetCost(best, f);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    entry = f;
                }
            }
            _transition = best;
            _yawTurned = best.YawProgress == null ? 0f : best.YawProgress[entry];
            if (_weight > 0f)
                CrossfadeTo(Phase.Transition, best, entry);
            else
                Begin(Phase.Transition, best, entry);
            _events.Add(string.Format("  sprint {0} at {1:F0} deg, turn {2:F0} (clip {3:F0} -> {4:F0}, turns {5:F0}), entry frame {6} feet {7:F3} m2",
                entering ? "enter" : "exit", localYaw, expectedTurn, best.FromYaw, best.ToYaw, best.YawChange, entry, bestCost));
        }

        // the clip turns the character, so follow whichever is further along: the bot's own rotation or its travel
        private void AdvanceTransition(float dt)
        {
            float step = dt * _transition.Fps;
            int byYaw = _transition.FrameAtYaw(_yawTurned, (int)_frame);
            int byDistance = _transition.FrameAtDistance(_moveOriginPath + _travelled - _travelledAtBegin, (int)_frame);
            int matched = Mathf.Max(byYaw, byDistance);
            _frame = SlewTo(_frame, matched, step, MinCutRate, _phaseFor < CatchUpSeconds ? CatchUpRate : MaxTransitionRate, dt, _transition.Frames - 1);
            TrackPathBehind(_transition);
        }

        // handing off only once the clip ran out left its last frame frozen through the whole blend (0.5 s of still
        // legs, user-visible). start the blend early enough that the clip is still playing while it fades
        private bool NearEnd(PoseClip clip, float blendSeconds)
        {
            // scaling this by playback rate made the window 33 of 45 frames wide, so transitions handed off instantly
            float blendFrames = Mathf.Min(blendSeconds * clip.Fps, clip.Frames * 0.2f);
            return _frame >= clip.Frames - 1 - blendFrames;
        }

        // the bot faces its sprint direction once a transition completes, so the forward cycle normally wins
        private bool TryBeginSprintCycle()
        {
            if (!SprintCycleBridge && !AlyxCycles)
                return false;
            return TryBeginCycle();
        }

        // directional families: same length, same foot phase within a few frames (Tarkov's 8-way sets are one blend
        // tree in the game). a facing swing under a straight run then changes blend weights between two
        // neighbours at one phase instead of starting a new cycle and lock every sector (24.7 loop switches a
        // minute in fleet batch 2)
        private readonly Dictionary<string, List<PoseClip>> _families = new Dictionary<string, List<PoseClip>>();
        private bool _familiesBuilt;
        private PoseClip _blendClip;
        private float _blendWeight, _relYaw;
        private bool _hasRelYaw;
        private const float FamilyYawSmoothing = 0.1f;

        // Tarkov's animator keeps its own copy of the same clip running on the arms and torso; the legs read as
        // out of step with the arm swing when the placer's cycle drifts from it (user, sprint). while a Tarkov
        // sprint cycle plays, its frame follows the native animator clock. Walking can
        // enter at a compatible foot phase and converge gradually; visible arm pumping
        // is a sprint concern and must not hold walking on an unsuitable source loop.
        public bool AnimatorPhaseLock { get; set; } = true;
        private object _animatorWrapper;
        private MethodInfo _stateInfo;
        private MethodInfo _stateTransition;
        private MemberInfo _stateNormalized, _stateLength, _stateHash;
        private bool _animatorLooked, _phaseLocked;
        private readonly NativeWalkPhase _nativeWalkPhase = new NativeWalkPhase();
        private bool _nativeWalkPhaseActive, _nativeFamilyPhase;
        private int _nativeWalkLastFrame = -1;
        public bool PhaseLocked => _phaseLocked && _syncSampleFrame == Time.frameCount;
        private const float PhaseLockMinLength = 0.5f, PhaseLockMaxLength = 3f;

        // The animator sample is taken once at the start of Apply and retained for every stage recorded later in
        // that frame. This keeps before/after rows comparable and avoids reflection reads for every bone signal.
        private bool _syncAnimatorSampled;
        private float _syncAnimatorPhase, _syncAnimatorLength;
        private bool _syncHasAnimatorStateHash;
        private int _syncAnimatorStateHash;
        private bool _syncHasTransition, _syncInTransition;
        private bool _syncPhaseLockEligible;
        private string _syncPhaseLockReason = "not_sampled";
        private int _syncSampleFrame = -1;
        private float _syncSampleTime;

        // ReadSync is intentionally an observation only. AnimatorPhase is the wrapped normalized time in [0, 1),
        // LegPhase is the frame actually being written by this playback, and PhaseErrorCycles is leg minus animator
        // wrapped to [-0.5, 0.5). A small phase error does not prove that the rendered arms and legs look identical.
        public MotionSyncSnapshot ReadSync()
        {
            bool currentFrame = _syncSampleFrame >= 0 && _syncSampleFrame == Time.frameCount;
            MotionSyncSnapshot snapshot = new MotionSyncSnapshot
            {
                SampleFrame = _syncSampleFrame,
                SampleTime = _syncSampleTime,
                HasAnimatorPhase = currentFrame && _syncAnimatorSampled,
                AnimatorPhase = currentFrame && _syncAnimatorSampled ? _syncAnimatorPhase : 0f,
                AnimatorLength = currentFrame && _syncAnimatorSampled ? _syncAnimatorLength : 0f,
                HasAnimatorStateHash = currentFrame && _syncHasAnimatorStateHash,
                AnimatorStateHash = currentFrame && _syncHasAnimatorStateHash ? _syncAnimatorStateHash : 0,
                HasTransition = currentFrame && _syncHasTransition,
                InTransition = currentFrame && _syncHasTransition && _syncInTransition,
                PhaseLocked = currentFrame && _phaseLocked,
                PhaseLockEligible = currentFrame && _syncPhaseLockEligible,
                PhaseLockReason = currentFrame ? _syncPhaseLockReason : (_syncSampleFrame < 0 ? "not_sampled" : "stale_sample")
            };

            float legFrame;
            int legFrameCount;
            if (currentFrame && TryGetPlayedLegFrame(out legFrame, out legFrameCount))
            {
                snapshot.HasLegPhase = true;
                snapshot.LegFrame = legFrame;
                snapshot.LegFrameCount = legFrameCount;
                snapshot.LegPhase = Mathf.Repeat(legFrame / legFrameCount, 1f);
                if (snapshot.HasAnimatorPhase)
                {
                    snapshot.HasPhaseError = true;
                    snapshot.PhaseErrorCycles = CircularPhaseError(snapshot.LegPhase, snapshot.AnimatorPhase);
                }
            }
            return snapshot;
        }

        private void ResetSyncTelemetry()
        {
            // PhaseLocked is consumed by FamilyFrame during this Apply, so clear it before any early return. Leaving
            // the previous frame's success here made diagnostics report a lock while a stop or disabled frame ran.
            _phaseLocked = false;
            _syncPhaseLockEligible = false;
            _syncPhaseLockReason = AnimatorPhaseLock ? "not_evaluated" : "disabled";
            _syncAnimatorSampled = false;
            _syncAnimatorPhase = 0f;
            _syncAnimatorLength = 0f;
            _syncHasAnimatorStateHash = false;
            _syncAnimatorStateHash = 0;
            _syncHasTransition = false;
            _syncInTransition = false;
            _syncSampleFrame = -1;
            _syncSampleTime = 0f;
        }

        private void CaptureAnimatorPhase()
        {
            float normalized;
            float length;
            if (!TryAnimatorPhase(out normalized, out length))
                return;

            if (float.IsNaN(normalized) || float.IsInfinity(normalized) || float.IsNaN(length) || float.IsInfinity(length))
                return;
            _syncAnimatorSampled = true;
            _syncAnimatorPhase = normalized - Mathf.Floor(normalized);
            _syncAnimatorLength = length;
        }

        private bool TryAnimatorPhase(out float normalized, out float length)
        {
            normalized = 0f; length = 0f;
            if (!_animatorLooked)
            {
                _animatorLooked = true;
                try
                {
                    var member = typeof(Player).GetProperty("BodyAnimatorCommon", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    object wrapper = member != null ? member.GetValue(_player) : null;
                    if (wrapper == null)
                    {
                        var field = typeof(Player).GetField("BodyAnimatorCommon", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        wrapper = field != null ? field.GetValue(_player) : null;
                    }
                    if (wrapper != null)
                    {
                        _stateInfo = wrapper.GetType().GetMethod("GetCurrentAnimatorStateInfo", new[] { typeof(int) });
                        // the wrapper hands back its own AnimatorStateInfoWrapper, not Unity's struct: the members
                        // are found on whatever comes back
                        if (_stateInfo != null)
                        {
                            object probe = _stateInfo.Invoke(wrapper, new object[] { 0 });
                            Type stateType = probe != null ? probe.GetType() : null;
                            _stateNormalized = FindMember(stateType, "normalizedTime");
                            _stateLength = FindMember(stateType, "length");
                            _stateHash = FindMember(stateType, "fullPathHash") ?? FindMember(stateType, "shortNameHash");
                            _stateTransition = wrapper.GetType().GetMethod(
                                "IsInTransition",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                null,
                                new[] { typeof(int) },
                                null);
                            if (_stateNormalized != null && _stateLength != null)
                                _animatorWrapper = wrapper;
                            else
                                _events.Add("  animator phase: state members not found on " + (stateType != null ? stateType.Name : "null"));
                        }
                    }
                }
                catch { _animatorWrapper = null; }
            }
            if (_animatorWrapper == null)
                return false;
            object info;
            try
            {
                info = _stateInfo.Invoke(_animatorWrapper, new object[] { 0 });
                normalized = Convert.ToSingle(ReadMember(_stateNormalized, info));
                length = Convert.ToSingle(ReadMember(_stateLength, info));
            }
            catch
            {
                return false;
            }

            _syncHasAnimatorStateHash = false;
            _syncAnimatorStateHash = 0;
            if (_stateHash != null)
            {
                try
                {
                    _syncHasAnimatorStateHash = TryReadInt(ReadMember(_stateHash, info), out _syncAnimatorStateHash);
                }
                catch
                {
                    _syncHasAnimatorStateHash = false;
                }
            }

            _syncHasTransition = false;
            _syncInTransition = false;
            if (_stateTransition != null)
            {
                try
                {
                    _syncHasTransition = TryReadBool(_stateTransition.Invoke(_animatorWrapper, new object[] { 0 }), out _syncInTransition);
                }
                catch
                {
                    _syncHasTransition = false;
                }
            }
            return length > 0.05f;
        }

        private static MemberInfo FindMember(Type type, string name)
        {
            if (type == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            MemberInfo m = type.GetProperty(name, flags);
            return m ?? type.GetField(name, flags);
        }

        private static object ReadMember(MemberInfo m, object target)
        {
            var prop = m as PropertyInfo;
            return prop != null ? prop.GetValue(target) : ((FieldInfo)m).GetValue(target);
        }

        // frame from the animator's phase when the base layer is playing a state of this clip's length
        private void LockPhaseToAnimator()
        {
            _phaseLocked = false;
            _nativeFamilyPhase = _nativeWalkPhaseActive && LocomotionRefinement.Enabled && !_player.IsSprintEnabled;
            _syncPhaseLockEligible = false;
            if (!AnimatorPhaseLock)
            {
                _syncPhaseLockReason = "disabled";
                return;
            }
            if (_active == null)
            {
                _syncPhaseLockReason = "no_active_clip";
                return;
            }
            if (!IsTarkov(_active))
            {
                _syncPhaseLockReason = "clip_not_tarkov";
                return;
            }
            if (!_active.Loop || _active.Frames <= 0)
            {
                _syncPhaseLockReason = "clip_not_loop";
                return;
            }
            if (!_syncAnimatorSampled)
            {
                _syncPhaseLockReason = "animator_unavailable";
                return;
            }
            float length = _syncAnimatorLength;
            // any locomotion-length base state: its normalized time is one cycle of whatever the arms are doing
            // (blend states read 1.26, 1.53, 2.24 s during sprints; only 1.2 +-6% was accepted and the legs ran free
            // of the arms the rest of the time, user)
            if (length < PhaseLockMinLength || length > PhaseLockMaxLength)
            {
                _syncPhaseLockReason = "animator_length_out_of_range";
                return;
            }
            _syncPhaseLockEligible = true;
            _nativeFamilyPhase = true;
            if (_nativeWalkPhaseActive && !_player.IsSprintEnabled && LocomotionRefinement.Enabled)
            {
                if (_nativeWalkLastFrame != Time.frameCount - 1)
                {
                    // The raw cycle kept advancing while the native clock was unavailable.
                    // Rebase to that actual pose instead of resuming an old helper phase.
                    _nativeWalkPhase.Reset(_frame / _active.Frames, _syncAnimatorPhase, _syncAnimatorStateHash);
                }
                else
                    _frame = _nativeWalkPhase.Step(_syncAnimatorPhase, length, Time.deltaTime,
                        _syncAnimatorStateHash, _syncHasTransition && _syncInTransition) * _active.Frames;
                _nativeWalkLastFrame = Time.frameCount;
                Rate = _nativeWalkPhase.CadenceScale * _active.Frames / (length * _active.Fps);
                _phaseLocked = Mathf.Abs(_nativeWalkPhase.OffsetCycles) < 0.001f;
                _syncPhaseLockReason = _phaseLocked ? "locked" : "native_entry_converging";
            }
            else
            {
                _frame = _syncAnimatorPhase * _active.Frames;
                _phaseLocked = true;
                _syncPhaseLockReason = "locked";
            }
        }

        private bool TryGetPlayedLegFrame(out float frame, out int frameCount)
        {
            frame = 0f;
            frameCount = 0;
            bool directional = _sets.Count > 0;
            PoseClip clip = directional ? _active : _clip;
            float weight = _weight;
            frame = directional ? _frame : (_clip == null ? 0f : _time * _clip.Fps);

            if (directional && weight <= 0f && _fadeClip != null && Time.time - _fadeStarted < FadeMaxSeconds)
            {
                clip = _fadeClip;
                frame = _fadeFrame;
                weight = 1f;
            }

            if (clip == null || clip.Frames <= 0 || weight <= 0f || float.IsNaN(frame) || float.IsInfinity(frame))
                return false;

            frameCount = clip.Frames;
            frame = Mathf.Repeat(frame, frameCount);
            return true;
        }

        private static float CircularPhaseError(float legPhase, float animatorPhase)
        {
            float error = legPhase - animatorPhase;
            // Keep the signed shortest distance. At exactly half a cycle this convention chooses -0.5.
            return error - Mathf.Floor(error + 0.5f);
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
                result = Convert.ToInt32(value);
                return true;
            }
            catch
            {
                result = 0;
                return false;
            }
        }

        private static bool TryReadBool(object value, out bool result)
        {
            if (value == null)
            {
                result = false;
                return false;
            }
            try
            {
                result = Convert.ToBoolean(value);
                return true;
            }
            catch
            {
                result = false;
                return false;
            }
        }

        private float _travelYawRate, _travelHeading;
        private bool _hasTravelHeading;
        public string BlendClipName => _blendClip?.Name;
        public float BlendWeight => _blendClip != null ? _blendWeight : 0f;
        private readonly FamilyStride _familyStride = new FamilyStride();
        private bool _familiesRefined;

        private void BuildFamilies()
        {
            _familiesBuilt = true;
            _familiesRefined = LocomotionRefinement.Enabled;
            _families.Clear();
            var groups = new Dictionary<string, List<PoseClip>>();
            foreach (var c in _sprintCycles)
            {
                c.Family = null;
                c.FamilyReference = null;
                if (!c.Loop || c.FootL == null || !c.Name.StartsWith("tarkov_", StringComparison.OrdinalIgnoreCase))
                    continue;
                int cut = c.Name.LastIndexOf('_');
                int yawDigits;
                if (cut <= 0 || !int.TryParse(c.Name.Substring(cut + 1), out yawDigits))
                    continue;
                string key = c.Name.Substring(0, cut);
                List<PoseClip> list;
                if (!groups.TryGetValue(key, out list))
                    groups[key] = list = new List<PoseClip>();
                list.Add(c);
            }
            foreach (var kv in groups)
            {
                var list = kv.Value;
                if (list.Count < 3)
                    continue;
                int frames = list[0].Frames;
                bool same = true;
                foreach (var c in list)
                    if (c.Frames != frames) same = false;
                if (!same)
                    continue;
                // reference phase: the member facing its travel; offsets from the left ankle's first touchdown
                PoseClip reference = list[0];
                foreach (var c in list)
                    if (Mathf.Abs(Mathf.DeltaAngle(c.MoveYaw, 0f)) < Mathf.Abs(Mathf.DeltaAngle(reference.MoveYaw, 0f)))
                        reference = c;
                int refStart = StanceStart(reference);
                foreach (var c in list)
                {
                    c.PhaseOffset = LocomotionRefinement.Enabled ? FamilyStride.FindPhaseOffset(reference, c)
                        : ((StanceStart(c) - refStart) % frames + frames) % frames;
                }
                // A shared native animator phase must agree on both support cycles. Exclude incompatible
                // members from directional interpolation; they remain available as ordinary crossfade clips.
                var compatible = LocomotionRefinement.Enabled ? list.AsValueEnumerable().Where(c => FamilyStride.Compatible(reference, c, true) &&
                    FamilyStride.Compatible(reference, c, false)).ToList() : list;
                if (compatible.Count < 3) continue;
                list = compatible;
                foreach (var c in list)
                {
                    c.Family = kv.Key;
                    c.FamilyReference = reference;
                }
                list.Sort((x, y) => x.MoveYaw.CompareTo(y.MoveYaw));
                _families[kv.Key] = list;
                _events.Add("  family " + kv.Key + ": " + list.Count + " members, " + frames + " frames");
            }
        }

        private static int StanceStart(PoseClip c)
        {
            int n = c.Frames;
            float lo = float.MaxValue;
            for (int f = 0; f < n; f++) lo = Mathf.Min(lo, c.FootL[f].y);
            for (int f = 0; f < n; f++)
            {
                bool down = c.FootL[f].y < lo + 0.015f, before = c.FootL[(f - 1 + n) % n].y < lo + 0.015f;
                if (down && !before) return f;
            }
            return 0;
        }

        // frame of `other` at the same phase as `_frame` on `_active`
        private float FamilyFrame(PoseClip other)
        {
            int n = other.Frames;
            if (_nativeFamilyPhase)
                return Mathf.Repeat(_frame, n);
            float f = _frame - _active.PhaseOffset + other.PhaseOffset;
            while (f < 0f) f += n;
            while (f >= n) f -= n;
            return f;
        }

        // pick the nearest member and its neighbour for the (smoothed) travel direction relative to the facing
        private void UpdateFamilyBlend(float dt)
        {
            if (_familiesBuilt && _familiesRefined != LocomotionRefinement.Enabled) BuildFamilies();
            if (_active == null || _active.Family == null || _phase != Phase.SprintCycle)
            {
                _blendClip = null;
                _hasRelYaw = false;
                return;
            }
            float rel = TravelYaw();
            _relYaw = _hasRelYaw ? Mathf.LerpAngle(_relYaw, rel, Mathf.Clamp01(dt / FamilyYawSmoothing)) : rel;
            _hasRelYaw = true;
            var list = _families[_active.Family];
            PoseClip nearest = null, partner = null;
            float dNearest = float.MaxValue, dPartner = float.MaxValue;
            foreach (var c in list)
            {
                float d = Mathf.Abs(Mathf.DeltaAngle(c.MoveYaw, _relYaw));
                if (d < dNearest) { partner = nearest; dPartner = dNearest; nearest = c; dNearest = d; }
                else if (d < dPartner) { partner = c; dPartner = d; }
            }
            // the neighbour must lie on the other side of the travel direction, or there is nothing to blend toward
            if (partner != null && Mathf.Sign(Mathf.DeltaAngle(nearest.MoveYaw, _relYaw)) != Mathf.Sign(Mathf.DeltaAngle(nearest.MoveYaw, partner.MoveYaw)))
                partner = null;
            if (nearest != _active)
            {
                _frame = FamilyFrame(nearest);
                _events.Add(string.Format("{0:F2}s family {1} -> {2} rel {3:F0}", Time.time, _active.Name, nearest.Name, _relYaw));
                _active = nearest;
            }
            _blendClip = partner;
            _blendWeight = partner == null ? 0f : Mathf.Clamp01(dNearest / Mathf.Max(dNearest + dPartner, 1e-3f));
        }

        // the loop closest to the bot's travel direction and speed (same asymmetric speed penalty as the sets:
        // a loop slower than the bot cannot cover the ground); the current loop is kept unless another is clearly better
        private bool TryBeginCycle(bool immediate = false)
        {
            if (!_familiesBuilt)
                BuildFamilies();
            // a body still gathering speed travels wherever its inertia carries it; the order says where it is headed
            // (an about-face start picked a south-west sprint loop at 0.4 m/s)
            float travel = _speed < CycleIntentSpeed && _lastIntent.HasValue ? _lastIntent.Value : TravelYaw();
            // the order counts while the body is still getting up to it; a loop that has run a while is chosen for
            // the speed the body actually holds (fleet batch 3: the grunt sprint loops stayed under bots slowed to
            // 1 m/s by aiming and obstacles because the order still said run, feet sliding at the 0.6x floor)
            float targetSpeed = _phase == Phase.SprintCycle && _phaseFor > CycleSettleSeconds ? _speed : Mathf.Max(_speed, CommandedSpeed() * 0.8f);
            // Tarkov's run legs: on the placer when the database carries Tarkov's own cycles (extracted from the
            // game, `tarkov_` prefix), otherwise Tarkov's animator takes the band
            bool inBand = !_player.IsSprintEnabled && targetSpeed >= NativeWalkMinimumSpeed && Mathf.Abs(Mathf.DeltaAngle(travel, 0f)) < TarkovRunYaw;
            bool sprintPick = SprintOnPlacer && _player.IsSprintEnabled;
            if (TarkovRunLegs && inBand && !HasTarkovCycles)
            {
                _pendingCycle = null;
                return false;
            }
            PoseClip best = null;
            float bestError = float.MaxValue;
            float currentError = float.MaxValue;
            IList<Vector3> futureCorners = null;
            if (LocomotionRefinement.Enabled && _intent != null && _intent.Corners != null)
            {
                try { futureCorners = _intent.Corners(); }
                catch { futureCorners = null; }
            }
            float? placementDemand = null;
            if (LocomotionRefinement.Enabled && CurrentPlacementCost != null)
            {
                try
                {
                    float? observed = CurrentPlacementCost();
                    if (observed.HasValue && IsFinite(observed.Value))
                        placementDemand = Mathf.Clamp01(observed.Value);
                }
                catch { placementDemand = null; }
            }
            foreach (var cycle in _sprintCycles)
            {
                float direction = Mathf.Abs(Mathf.DeltaAngle(cycle.MoveYaw, travel));
                // the active loop is always scored: past the limit its error was infinite and the retention margin
                // had nothing to hold (review), so a body swinging its facing walked the loop round the compass
                bool active = cycle == _active && _phase == Phase.SprintCycle;
                // Native mode uses Tarkov walking during acceleration too. Do not insert an
                // Alyx walk/jog bridge merely because the body has not reached 2.3 m/s yet.
                if (LocomotionRefinement.Enabled && TarkovRunLegs && HasTarkovCycles && inBand && !active
                    && (!IsTarkov(cycle) || cycle.Gait == "sprint")) continue;
                if (direction > MaxDirectionError && !active)
                    continue;
                // a loop plays within its rate band, so what matters is the speed it can cover at that rate: a walk
                // loop at 1.5x is a brisk walk and covers 2.8 m/s; the sprint loop slowed to 0.8x for the same speed
                // read as a slowed-down run with too much leg (user). sprint loops are for sprinting or beyond
                float clipSpeed = cycle.SpeedMetersPerSecond;
                float covered = clipSpeed * Mathf.Clamp(targetSpeed / Mathf.Max(clipSpeed, 0.1f), MinCycleRate, MaxCycleRate);
                float error = direction + (covered >= targetSpeed
                    ? FasterClipPenaltyDegreesPerMps * (covered - targetSpeed)
                    : SlowerClipPenaltyDegreesPerMps * (targetSpeed - covered));
                // the grunt set is the dynamic, consistent one (user): heavy clips only fill gaps the grunt has none for,
                // and the grunt sprint loops may serve a run (slowed to ~0.75x) instead of the heavy run cycle
                bool brisk = BriskWalkLegs && !_player.IsSprintEnabled && targetSpeed >= RunTierSpeed && targetSpeed < BriskWalkTop && Mathf.Abs(Mathf.DeltaAngle(travel, 0f)) < TarkovRunYaw;
                bool tarkovClip = IsTarkov(cycle);
                // Tarkov's sprint cycles are for sprinting bots only (a 5 m/s one served a 2.5 m/s body, batch 6)
                if (tarkovClip && cycle.Gait == "sprint" && !_player.IsSprintEnabled)
                    error += GaitMismatchPenalty + SlowerClipPenaltyDegreesPerMps;
                // a sprinting bot on the placer takes Tarkov's sprint family over the Alyx sprint loops
                // below the slow sprint's band the run loop carries a body still building up to it (3-4 m/s took
                // the 5 m/s loop at 0.6x)
                bool runUnderSprint = sprintPick && tarkovClip && cycle.Gait != "sprint" && targetSpeed < SprintRunLoopBelow;
                if (sprintPick && !IsTarkovSprint(cycle) && !runUnderSprint)
                    error += GaitMismatchPenalty + SprintLoopPenalty;
                if (TarkovRunLegs && HasTarkovCycles && inBand)
                    error += tarkovClip ? 0f : GaitMismatchPenalty;
                else if (runUnderSprint) { }
                else if (tarkovClip && !(TarkovRunLegs && inBand))
                    error += GaitMismatchPenalty; // Tarkov's cycles serve their band only
                else if (brisk)
                    error += (cycle.Gait == "walk" || cycle.Gait == "slow") ? 0f : GaitMismatchPenalty;
                else
                    error += GaitMismatch(cycle, targetSpeed);
                if (PreferGrunt && IsHeavy(cycle) && targetSpeed >= (WalkFamily == 1 ? SlowWalkSpeed : RunTierSpeed) && !brisk)
                    error += HeavyClipPenalty;
                // the grunt take slices are stiff-legged patrol walking (authored 0.96-0.98 straight): they only
                // serve slow walks unless the grunt-everywhere family is chosen
                if (WalkFamily != 1 && cycle.Name.StartsWith("motion_match", StringComparison.OrdinalIgnoreCase) && targetSpeed >= SlowWalkSpeed)
                    error += SlicedCyclePenalty;
                // sideways and backward running has no heavy loop above 2.07 m/s, and combat bots backpedal at 2.5
                // (fleet batch 2): the grunt sprint loops serve those directions from 2.0 m/s, straight ones from 2.3
                float sprintLoopFrom = Mathf.Abs(Mathf.DeltaAngle(cycle.MoveYaw, 0f)) > 45f ? LateralRunSpeed : GruntRunSpeed;
                if (cycle.HasRole("sprint_cycle") && !_player.IsSprintEnabled && !(PreferGrunt && targetSpeed >= sprintLoopFrom))
                    error += SprintLoopPenalty;
                // among loops that cover the speed, the one authored nearest it plays closest to 1x
                error += NativeSpeedPenaltyDegreesPerMps * Mathf.Abs(clipSpeed - targetSpeed);
                // a slice cut from a long capture take is a cycle of last resort: the walk raids that landed on the
                // motion_match_walk slice instead of the heavy walk loop tripled their reach clamps and plant slide
                // with the grunt preferred the slice IS the grunt walk (user: "grunt strafes look more natural")
                if (cycle.Frames / Mathf.Max(cycle.Fps, 1f) > AuthoredLoopMaxSeconds || (!PreferGrunt && cycle.Name.StartsWith("motion_match", StringComparison.OrdinalIgnoreCase)))
                    error += SlicedCyclePenalty;
                if (LocomotionRefinement.Enabled && futureCorners != null)
                {
                    int pathFrame = active ? Mathf.Clamp(Mathf.RoundToInt(_frame), 0, cycle.Frames - 1) : BestCycleEntry(cycle);
                    error += CycleFuturePathCost(cycle, pathFrame, targetSpeed, futureCorners);
                }
                if (active && placementDemand.HasValue)
                    error += placementDemand.Value * MaxPlacementDemandPenalty;
                if (active)
                {
                    currentError = error;
                    continue;
                }
                if (error < bestError)
                {
                    bestError = error;
                    best = cycle;
                }
            }
            if (best == null)
            {
                _pendingCycle = null;
                return false;
            }
            if (_phase == Phase.SprintCycle)
            {
                if (_active != null && _active.Family != null && best.Family == _active.Family)
                {
                    _pendingCycle = null;
                    return false;
                }
                // a loop is only swapped for one that is clearly better; the first pick after a start churned n -> nw -> walk
                bool forcedSprint = immediate && sprintPick && !IsTarkov(_active);
                if (!forcedSprint && bestError > currentError - CycleRepickMargin)
                {
                    _pendingCycle = null;
                    return false;
                }
                // and the better loop must hold for a dwell first (Tarkov bots swing their facing toward an enemy
                // while running straight: 24.7 loop switches a minute in fleet batch 2, each a fresh cycle and
                // lock). a loop far off the travel direction is not held that long
                float activeDrift = Mathf.Abs(Mathf.DeltaAngle(_active.MoveYaw, travel));
                if (best != _pendingCycle)
                {
                    _pendingCycle = best;
                    _pendingSince = Time.time;
                }
                // a sprint switch is not a facing wobble: it goes at once (the dwell sent a walk_aim at 4 m/s to
                // the animator instead of the sprint family, puppet sprintbase)
                if (!immediate && Time.time - _pendingSince < CycleSwitchDwell && activeDrift < CycleSwitchDriftLimit)
                    return false;
            }
            // Evaluate candidate stability through flight; only the actual switch waits for support.
            // Resetting the pending candidate every flight starves loops with short stance intervals.
            if (LocomotionRefinement.Enabled && !immediate && _phase == Phase.SprintCycle && _hasShownSupport
                && !_shownPlantedL && !_shownPlantedR) return false;
            // Keep a stable candidate through admission deferrals; restarting its dwell
            // here would skip short compatible support windows on every retry.
            // a sprinting bot is bridged, never kept: only the sprint loops fit that speed
            // (the Tarkov run loop carries a placer sprint's build-up; refusing it here left bots off the layer
            // from the sprint's first frame until 4.1 m/s)
            if (_player.IsSprintEnabled && !best.HasRole("sprint_cycle") && !(SprintOnPlacer && IsTarkov(best)))
                return false;
            int entry = BestCycleEntry(best);
            bool nativeEntry = LocomotionRefinement.Enabled && AnimatorPhaseLock && IsTarkov(best)
                && !_player.IsSprintEnabled && _syncAnimatorSampled && IsFinite(_syncAnimatorPhase)
                && IsFinite(_syncAnimatorLength) && _syncAnimatorLength >= PhaseLockMinLength && _syncAnimatorLength <= PhaseLockMaxLength;
            float entryFrame = nativeEntry ? Mathf.Repeat(_syncAnimatorPhase, 1f) * best.Frames : entry;
            float bestCost = FeetCost(best, Mathf.FloorToInt(entryFrame));
            // Prefer the arms' phase. If its feet do not fit, enter a compatible native
            // phase and converge cadence gradually instead of holding the Alyx bridge.
            if (LocomotionRefinement.Enabled && AnimatorPhaseLock && IsTarkov(best)
                && !_player.IsSprintEnabled && _active != null && _weight > 0.5f)
            {
                string reason;
                if (!CompatibleCycleHandoff(best, entryFrame, out reason)
                    && !TryCompatibleNativeEntry(best, out entryFrame))
                {
                    if (_deferredCycleEntry != best || _deferredCycleReason != reason)
                        _events.Add(string.Format("{0:F2}s cycle entry deferred {1}: {2}", Time.time, best.Name, reason));
                    _deferredCycleEntry = best;
                    _deferredCycleReason = reason;
                    return false;
                }
            }
            _pendingCycle = null;
            _deferredCycleEntry = null;
            _deferredCycleReason = null;
            bestCost = FeetCost(best, Mathf.FloorToInt(entryFrame));
            CrossfadeTo(Phase.SprintCycle, best, entryFrame);
            _events.Add(string.Format("  cycle {0} ({1:F2} m/s) entry frame {2} feet {3:F3} m2", best.Name, best.SpeedMetersPerSecond, entryFrame, bestCost));
            return true;
        }

        private float TravelYaw() => Mathf.Atan2(_localVelocity.x, _localVelocity.y) * Mathf.Rad2Deg;

        // what the placer drew last frame, when one runs: world ankle/sole per side and the
        // optional cyclic progression observation. Ankle remains available to existing hand-
        // off matching; selection uses the FootBase callback below.
        public Func<int, Vector3?> DrawnAnkle;
        public Func<int, Vector3?> DrawnFootbase;
        public Func<int, float?> DrawnProgression;
        // Fresh placement demand from the placer, normalized to [0, 1]. It is optional so
        // old placers and frames without a fresh result simply omit this feature.
        public Func<float?> CurrentPlacementCost;
        public Func<Vector3, Vector3, float, float, bool> StopSettled;
        public Func<int, bool> DrawnPlanted;
        private bool _shownPlantedL, _shownPlantedR, _hasShownSupport;
        private const float SupportMismatchCost = 0.15f;
        private PoseClip _deferredCycleEntry;
        private string _deferredCycleReason;
        private Vector3? _previousShownBaseL, _previousShownBaseR;
        private Vector3? _shownVelocityL, _shownVelocityR;
        private float _shownBaseTime = -1f;

        private void TrackShownFootMotion()
        {
            float elapsed = Time.time - _shownBaseTime;
            _shownVelocityL = _shownVelocityR = null;
            if (_shownBaseTime >= 0f && elapsed > 0f && elapsed <= 0.05f)
            {
                if (_hasShownFootbaseL && _previousShownBaseL.HasValue)
                    _shownVelocityL = (_shownFootbaseL - _previousShownBaseL.Value) / elapsed;
                if (_hasShownFootbaseR && _previousShownBaseR.HasValue)
                    _shownVelocityR = (_shownFootbaseR - _previousShownBaseR.Value) / elapsed;
            }
            _previousShownBaseL = _hasShownFootbaseL ? _shownFootbaseL : (Vector3?)null;
            _previousShownBaseR = _hasShownFootbaseR ? _shownFootbaseR : (Vector3?)null;
            _shownBaseTime = Time.time;
        }

        private bool CompatibleCycleHandoff(PoseClip clip, float frame, out string reason)
        {
            if (!_syncAnimatorSampled || !IsFinite(_syncAnimatorPhase) || !IsFinite(_syncAnimatorLength)
                || _syncAnimatorLength < PhaseLockMinLength || _syncAnimatorLength > PhaseLockMaxLength
                || (_syncHasTransition && _syncInTransition))
            {
                reason = "native phase is unavailable or transitioning";
                return false;
            }
            Vector3? left, right, leftVelocity, rightVelocity;
            bool? leftContact, rightContact;
            ReadCycleHandoffFoot(clip, frame, 0, out left, out leftVelocity, out leftContact);
            ReadCycleHandoffFoot(clip, frame, 1, out right, out rightVelocity, out rightContact);
            return CycleHandoff.Allow(
                _hasShownFootbaseL ? _shownFootbaseL : (Vector3?)null,
                _hasShownFootbaseR ? _shownFootbaseR : (Vector3?)null,
                left, right,
                _hasShownSupport ? _shownPlantedL : (bool?)null,
                _hasShownSupport ? _shownPlantedR : (bool?)null,
                leftContact, rightContact, _shownVelocityL, _shownVelocityR,
                leftVelocity, rightVelocity, out reason);
        }

        private bool TryCompatibleNativeEntry(PoseClip clip, out float entry)
        {
            entry = 0f;
            float best = float.MaxValue;
            for (int f = 0; f < clip.Frames; f++)
            {
                string reason;
                if (!CompatibleCycleHandoff(clip, f, out reason)) continue;
                float phaseError = Mathf.Abs(CircularPhaseError((float)f / clip.Frames, _syncAnimatorPhase));
                float cost = FeetCost(clip, f) + 0.05f * phaseError;
                if (cost >= best) continue;
                best = cost;
                entry = f;
            }
            return best < float.MaxValue;
        }

        private static void ReadCycleHandoffFoot(PoseClip clip, float at, int side,
            out Vector3? position, out Vector3? velocity, out bool? contact)
        {
            position = velocity = null;
            contact = null;
            if (clip.Stride == null || side >= clip.Stride.Length || clip.Stride[side] == null) return;
            StrideFoot stride = clip.Stride[side];
            int frame = Mathf.FloorToInt(at);
            // Sole support is what the placer uses. Raw ankle-height contacts can remain
            // true during a toe/heel roll even when the destination is authored swing.
            if (stride.Grounded != null && frame >= 0 && frame < stride.Grounded.Length)
                contact = stride.Grounded[frame];
            Vector3[] samples = stride.Footbase;
            if (samples == null || frame < 0 || frame >= samples.Length) return;
            int next = frame + 1;
            if (next >= samples.Length) next = clip.Loop ? 0 : frame;
            bool sameStride = stride.Cycle != null && stride.Progression != null
                && next < stride.Cycle.Length && frame < stride.Cycle.Length
                && next < stride.Progression.Length && frame < stride.Progression.Length
                && stride.Cycle[next] == stride.Cycle[frame]
                && stride.Progression[next] >= stride.Progression[frame] - 0.5f;
            position = Vector3.Lerp(samples[frame], samples[next], sameStride ? at - frame : 0f);
            if (sameStride && next != frame && clip.Fps > 0f)
                velocity = (samples[next] - samples[frame]) * clip.Fps;
        }

        // the feet currently on screen: this playback's pose while it owns the legs, otherwise Tarkov's animator pose
        private void ReadShownFeet()
        {
            if (!LocomotionRefinement.Enabled)
            {
                _shownBaseTime = -1f;
                _previousShownBaseL = _previousShownBaseR = null;
                ReadLegacyShownFeet();
                return;
            }
            Transform root = _pelvis.parent;
            CaptureNativeIdlePose();
            _animatedFootL = root.InverseTransformPoint(_footL.position);
            _animatedFootR = root.InverseTransformPoint(_footR.position);
            _shownFootL = _animatedFootL;
            _shownFootR = _animatedFootR;
            bool liveLeft, liveRight;
            _shownFootbaseL = ReadLiveFootbase(root, 0, out liveLeft);
            _shownFootbaseR = ReadLiveFootbase(root, 1, out liveRight);
            _hasShownFootbaseL = liveLeft;
            _hasShownFootbaseR = liveRight;
            _shownProgressionL = null;
            _shownProgressionR = null;
            _hasShownSupport = false;

            if (_weight > 0.5f && DrawnAnkle != null)
            {
                Vector3? l = DrawnAnkle(0), r = DrawnAnkle(1);
                if (l.HasValue) _shownFootL = root.InverseTransformPoint(l.Value);
                if (r.HasValue) _shownFootR = root.InverseTransformPoint(r.Value);
            }

            // A missing callback value is unknown for that side. Do not replace it with a
            // zero phase or a stale side from a previous frame. With no callback at all, an
            // active stride is a valid local observation; otherwise idle derives from the
            // current live Foot bone and the database's sole points.
            if (_weight > 0.5f && DrawnFootbase != null)
            {
                Vector3? l = DrawnFootbase(0), r = DrawnFootbase(1);
                _hasShownFootbaseL = l.HasValue;
                _hasShownFootbaseR = r.HasValue;
                if (l.HasValue) _shownFootbaseL = root.InverseTransformPoint(l.Value);
                if (r.HasValue) _shownFootbaseR = root.InverseTransformPoint(r.Value);
            }
            else if (_active != null && _active.Stride != null && _weight > 0.5f)
            {
                int f = Mathf.Clamp(Mathf.RoundToInt(_frame), 0, Mathf.Max(_active.Frames - 1, 0));
                if (_active.Stride.Length > 0 && _active.Stride[0] != null && _active.Stride[0].Footbase != null && f < _active.Stride[0].Footbase.Length)
                {
                    _shownFootbaseL = _active.Stride[0].Footbase[f];
                    _hasShownFootbaseL = true;
                }
                if (_active.Stride.Length > 1 && _active.Stride[1] != null && _active.Stride[1].Footbase != null && f < _active.Stride[1].Footbase.Length)
                {
                    _shownFootbaseR = _active.Stride[1].Footbase[f];
                    _hasShownFootbaseR = true;
                }
            }

            if (_weight > 0.5f && DrawnProgression != null)
            {
                float? l = DrawnProgression(0), r = DrawnProgression(1);
                if (l.HasValue && IsFinite(l.Value)) _shownProgressionL = l.Value;
                if (r.HasValue && IsFinite(r.Value)) _shownProgressionR = r.Value;
            }
            if (_weight > 0.5f && DrawnPlanted != null)
            {
                _shownPlantedL = DrawnPlanted(0);
                _shownPlantedR = DrawnPlanted(1);
                _hasShownSupport = DrawnAnkle == null || (DrawnAnkle(0).HasValue && DrawnAnkle(1).HasValue);
            }

            if (_weight > 0.5f && _active != null && _active.FootL != null)
            {
                int f = Mathf.Clamp(Mathf.RoundToInt(_frame), 0, Mathf.Max(_active.Frames - 1, 0));
                // Keep the legacy ankle reader useful when the placer has no fresh callback.
                if (DrawnAnkle == null)
                {
                    if (f < _active.FootL.Length) _shownFootL = _active.FootL[f];
                    if (_active.FootR != null && f < _active.FootR.Length) _shownFootR = _active.FootR[f];
                }
            }
            TrackShownFootMotion();
        }

        private void CaptureNativeIdlePose()
        {
            InvalidateNativeIdlePose();
            if (!_hasSolePoints || !_footL || !_footR)
                return;

            // Match FootPlacer's ShownHeading convention exactly: world-space toe
            // minus heel, measured around the world up axis.
            Vector3 leftHeel = _footL.TransformPoint(_soleHeel[0]);
            Vector3 leftToe = _footL.TransformPoint(_soleToe[0]);
            Vector3 rightHeel = _footR.TransformPoint(_soleHeel[1]);
            Vector3 rightToe = _footR.TransformPoint(_soleToe[1]);
            Vector3 leftCenter = (leftHeel + leftToe) * 0.5f;
            Vector3 rightCenter = (rightHeel + rightToe) * 0.5f;
            float leftHeading = Mathf.Atan2(leftToe.x - leftHeel.x, leftToe.z - leftHeel.z) * Mathf.Rad2Deg;
            float rightHeading = Mathf.Atan2(rightToe.x - rightHeel.x, rightToe.z - rightHeel.z) * Mathf.Rad2Deg;
            if (!IsFinite(leftCenter) || !IsFinite(rightCenter)
                || !IsFinite(leftHeading) || !IsFinite(rightHeading))
                return;

            _nativeIdleCenterL = leftCenter;
            _nativeIdleCenterR = rightCenter;
            _nativeIdleHeadingL = leftHeading;
            _nativeIdleHeadingR = rightHeading;
            _nativeIdlePoseFrame = Time.frameCount;
            _hasNativeIdlePose = true;
        }

        private void InvalidateNativeIdlePose()
        {
            _hasNativeIdlePose = false;
            _nativeIdlePoseFrame = -1;
        }

        private void ReadLegacyShownFeet()
        {
            Transform root = _pelvis.parent;
            _animatedFootL = root.InverseTransformPoint(_footL.position);
            _animatedFootR = root.InverseTransformPoint(_footR.position);
            _hasShownSupport = false;
            if (_weight > 0.5f && DrawnAnkle != null)
            {
                Vector3? l = DrawnAnkle(0), r = DrawnAnkle(1);
                if (l.HasValue && r.HasValue)
                {
                    _shownFootL = root.InverseTransformPoint(l.Value);
                    _shownFootR = root.InverseTransformPoint(r.Value);
                    if (DrawnPlanted != null)
                    {
                        _shownPlantedL = DrawnPlanted(0);
                        _shownPlantedR = DrawnPlanted(1);
                        _hasShownSupport = true;
                    }
                    return;
                }
            }
            if (_active != null && _active.FootL != null && _weight > 0.5f)
            {
                int f = Mathf.Clamp(Mathf.RoundToInt(_frame), 0, _active.Frames - 1);
                _shownFootL = _active.FootL[f];
                _shownFootR = _active.FootR[f];
                return;
            }
            _shownFootL = _animatedFootL;
            _shownFootR = _animatedFootR;
        }

        private Vector3 ReadLiveFootbase(Transform root, int side, out bool available)
        {
            Transform foot = side == 0 ? _footL : _footR;
            if (!_hasSolePoints || !foot)
            {
                available = false;
                return Vector3.zero;
            }
            Vector3 heel = foot.TransformPoint(_soleHeel[side]);
            Vector3 toe = foot.TransformPoint(_soleToe[side]);
            float width = toe.y - heel.y;
            float blend = Mathf.Clamp01(width / 0.02f * 0.5f + 0.5f);
            available = true;
            return root.InverseTransformPoint(Vector3.Lerp(toe, heel, blend));
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value)
            => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        // how far this clip's feet sit from the pose Tarkov's animator is holding underneath
        private float AnimatedFeetCost(PoseClip clip, float frame)
        {
            if (clip?.FootL == null)
                return 0f;
            int f = Mathf.Clamp(Mathf.RoundToInt(frame), 0, clip.Frames - 1);
            return (clip.FootL[f] - _animatedFootL).sqrMagnitude + (clip.FootR[f] - _animatedFootR).sqrMagnitude;
        }

        // the clip keeps playing until its feet line up with Tarkov's, so the blend has little to move
        private bool ReadyToHandOff(PoseClip clip, float dt)
        {
            // A rejected native-loop entry is not permission to bypass its support gate.
            // Finish the authored start and retry admission each frame before falling back.
            if (_phase == Phase.Start && _deferredCycleEntry != null && _frame < clip.Frames - 1)
                return false;
            if (!_handOffPending)
            {
                _handOffPending = true;
                _handOffWaited = 0f;
            }
            _handOffWaited += dt;
            if (AnimatedFeetCost(clip, _frame) <= HandOffFeetCost || _handOffWaited >= HandOffMatchSeconds || _frame >= clip.Frames - 1)
                return true;
            // Alyx legs rarely land within 10 cm of Tarkov's cycle, so the wait used to time out and blend anyway
            // with the feet up to a metre apart. a swinging foot hides that mismatch; a planted one shows it
            Vector3? left, right, leftVelocity, rightVelocity; bool? leftSupport, rightSupport;
            ReadCycleHandoffFoot(clip, _frame, 0, out left, out leftVelocity, out leftSupport);
            ReadCycleHandoffFoot(clip, _frame, 1, out right, out rightVelocity, out rightSupport);
            return leftSupport == false && rightSupport == false
                && _hasShownSupport && !_shownPlantedL && !_shownPlantedR;
        }

        private float FeetCost(PoseClip clip, int f)
        {
            if (clip == null)
                return 0f;
            int frame = Mathf.Clamp(f, 0, Mathf.Max(clip.Frames - 1, 0));
            if (!LocomotionRefinement.Enabled)
            {
                if (clip.FootL == null || clip.FootR == null) return 0f;
                float legacyCost = (clip.FootL[frame] - _shownFootL).sqrMagnitude + (clip.FootR[frame] - _shownFootR).sqrMagnitude;
                if (_hasShownSupport && clip.Contact != null)
                {
                    if (clip.Contact[0][frame] != _shownPlantedL) legacyCost += SupportMismatchCost;
                    if (clip.Contact[1][frame] != _shownPlantedR) legacyCost += SupportMismatchCost;
                }
                return legacyCost;
            }
            Vector3[] leftBase = clip.Stride != null && clip.Stride.Length > 0 && clip.Stride[0] != null ? clip.Stride[0].Footbase : null;
            Vector3[] rightBase = clip.Stride != null && clip.Stride.Length > 1 && clip.Stride[1] != null ? clip.Stride[1].Footbase : null;
            Vector3? shownLeft = _hasShownFootbaseL ? _shownFootbaseL : (Vector3?)null;
            Vector3? shownRight = _hasShownFootbaseR ? _shownFootbaseR : (Vector3?)null;
            float cost = MotionSelection.FootbaseCost(leftBase, rightBase, frame, shownLeft, shownRight);
            float[] leftProgression = clip.Stride != null && clip.Stride.Length > 0 && clip.Stride[0] != null ? clip.Stride[0].Progression : null;
            float[] rightProgression = clip.Stride != null && clip.Stride.Length > 1 && clip.Stride[1] != null ? clip.Stride[1].Progression : null;
            // Progression is a cyclic feature and is intentionally omitted when either
            // observed callback is unavailable; zero is a real phase, not an unknown value.
            cost += 0.25f * MotionSelection.ProgressionCost(leftProgression, rightProgression, frame, _shownProgressionL, _shownProgressionR);
            // a foot the viewer sees planted must be planted in the candidate frame too: position alone cannot tell
            // a planted foot from a swinging one passing through the same spot (review)
            if (_hasShownSupport && clip.Contact != null)
            {
                if (clip.Contact.Length > 0 && clip.Contact[0] != null && frame < clip.Contact[0].Length && clip.Contact[0][frame] != _shownPlantedL) cost += SupportMismatchCost;
                if (clip.Contact.Length > 1 && clip.Contact[1] != null && frame < clip.Contact[1].Length && clip.Contact[1][frame] != _shownPlantedR) cost += SupportMismatchCost;
            }
            return cost;
        }

        private int BestCycleEntry(PoseClip clip)
        {
            if (clip == null || clip.Frames <= 0)
                return 0;
            // Score the phase that LockPhaseToAnimator will actually play, including its entry pose.
            if (LocomotionRefinement.Enabled && AnimatorPhaseLock && IsTarkov(clip) && clip.Loop &&
                _syncAnimatorSampled && _syncAnimatorLength >= PhaseLockMinLength && _syncAnimatorLength <= PhaseLockMaxLength)
                return Mathf.Clamp(Mathf.RoundToInt(_syncAnimatorPhase * clip.Frames), 0, clip.Frames - 1);
            int entry = 0;
            float bestCost = float.MaxValue;
            for (int f = 0; f < clip.Frames; f++)
            {
                float cost = FeetCost(clip, f);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    entry = f;
                }
            }
            return entry;
        }

        private float CycleFuturePathCost(PoseClip clip, int frame, float targetSpeed, IList<Vector3> corners)
        {
            if (clip == null)
                return 0f;
            return FuturePathCostWeight * MotionSelection.FuturePathCost(
                corners,
                _player.Position,
                _player.Rotation.x,
                clip.RootVelocity,
                clip.YawProgress,
                clip.Fps,
                frame,
                clip.Loop,
                targetSpeed);
        }

        private void TryBeginCut(float intent)
        {
            float travel = TravelYaw();
            PoseClip best = null;
            float bestError = float.MaxValue;
            foreach (var cut in _cuts)
            {
                float fromError = Mathf.Abs(Mathf.DeltaAngle(cut.FromYaw, travel));
                float toError = Mathf.Abs(Mathf.DeltaAngle(cut.ToYaw, intent));
                if (fromError > CutDirectionError || toError > CutDirectionError)
                    continue;
                if (fromError + toError < bestError)
                {
                    bestError = fromError + toError;
                    best = cut;
                }
            }
            if (best == null)
                return;

            // each side of the reversal is scaled to the bot's current speed, since EFT keeps one speed both ways
            float peakIn = 0.1f, peakOut = 0.1f;
            for (int f = 0; f < best.Frames; f++)
            {
                if (f < best.TurnFrame) peakIn = Mathf.Max(peakIn, best.RootSpeed[f]);
                else peakOut = Mathf.Max(peakOut, best.RootSpeed[f]);
            }
            _cut = best;
            _cutScaleIn = _speed / peakIn;
            _cutScaleOut = _speed / peakOut;

            // enter where the footwork left before the clip's reversal takes about as long as EFT's
            int reversal = Mathf.RoundToInt(CutReversalSeconds * best.Fps);
            int first = Mathf.Max(0, best.TurnFrame - Mathf.RoundToInt(reversal * 1.6f));
            int last = Mathf.Max(first, best.TurnFrame - Mathf.RoundToInt(reversal * 0.5f));
            int entry = first;
            float bestCost = float.MaxValue;
            for (int f = first; f <= last; f++)
            {
                float cost = CutVelocityCost(f) + PoseCostWeight * FeetCost(best, f);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    entry = f;
                }
            }
            if (_weight > 0f)
                CrossfadeTo(Phase.Cut, best, entry);
            else
                Begin(Phase.Cut, best, entry);
            _events.Add(string.Format("  cut from {0:F0} to {1:F0} deg (clip {2:F0} -> {3:F0}), entry velocity cost {4:F3} feet {5:F3} m2", travel, intent, best.FromYaw, best.ToYaw, CutVelocityCost(entry), FeetCost(best, entry)));
        }

        private float CutVelocityCost(int f)
        {
            Vector2 clip = _cut.RootVelocity[f] * (f < _cut.TurnFrame ? _cutScaleIn : _cutScaleOut);
            return (clip - _localVelocity).sqrMagnitude / Mathf.Max(_speed * _speed, 0.25f);
        }

        // distance matching along the clip's own path, only forward and within a rate band; EFT's reversal is far
        // quicker than the clip's, so the rate cap bounds the catch-up and the foot lock absorbs what is left
        private void AdvanceCut(float dt)
        {
            float step = dt * _cut.Fps;
            int matched = _cut.FrameAtDistance(TargetDistance(), (int)_frame);
            TrackPathBehind(_cut);
            bool pushing = _phaseFor < CatchUpSeconds || (_sinceTurn > 0f && _sinceTurn < CatchUpSeconds);
            _frame = SlewTo(_frame, matched, step, MinCutRate, pushing ? StartCatchUpRate : MaxCutRate, dt, _cut.Frames - 1);
        }


        // switch clips without detouring through Tarkov's pose: fade from the last pose this playback wrote
        private void CrossfadeTo(Phase phase, PoseClip clip, float frame)
        {
            Begin(phase, clip, frame);
        }

        // any switch to a different clip or a jumped frame is a discontinuity, however it was reached
        private void CaptureInertia(PoseClip clip, float frame)
        {
            if (!Inertialize || _lastSampled == null || clip == null || _weight <= 0.05f)
                return;
            float historyDt = _lastSampleTime - _previousSampleTime;
            float sourceAge = Time.time - _lastSampleTime;
            bool velocityEntry = LocomotionRefinement.Enabled && AnimatorPhaseLock && IsTarkov(clip) && clip.Loop
                && _syncAnimatorSampled && _syncAnimatorLength >= PhaseLockMinLength && _syncAnimatorLength <= PhaseLockMaxLength
                && PelvisTiltFromClip >= 0.999f && !_player.IsSprintEnabled && _previousSampled != null
                && _previousSampleTime >= 0f && historyDt > 0f && historyDt <= 0.05f
                && sourceAge >= 0f && sourceAge <= 0.05f;
            if (velocityEntry)
            {
                // The incoming loop will follow the native animator clock. Sample that
                // clock on both sides of entry instead of assuming the clip's nominal FPS.
                float framesPerSecond = clip.Frames / _syncAnimatorLength
                    * (_nativeWalkPhaseActive ? _nativeWalkPhase.CadenceScale : 1f);
                var targetNow = new Quaternion[_bones.Length];
                var targetBefore = new Quaternion[_bones.Length];
                Vector3 pelvisNow, pelvisBefore;
                SampleEntryPose(clip, frame, targetNow, out pelvisNow);
                SampleEntryPose(clip, frame - framesPerSecond * historyDt, targetBefore, out pelvisBefore);
                _inertia.Begin(_lastSampled, targetNow, _lastPelvis, pelvisNow, CrossfadeSeconds,
                    _previousSampled, targetBefore, _previousSampledPelvis, pelvisBefore, historyDt, sourceAge);
                _events.Add(string.Format("{0:F2}s velocity-matched cycle entry {1} history {2:F4}s age {3:F4}s", Time.time, clip.Name, historyDt, sourceAge));
                return;
            }
            int f = Mathf.Clamp(Mathf.RoundToInt(frame), 0, clip.Frames - 1);
            var target = new Quaternion[_bones.Length];
            for (int i = 0; i < _bones.Length; i++)
                target[i] = clip.Rotations[i][f];
            _inertia.Begin(_lastSampled, target, _lastPelvis, clip.PelvisPosition[f], CrossfadeSeconds);
        }

        private static void SampleEntryPose(PoseClip clip, float frame, Quaternion[] rotations, out Vector3 pelvis)
        {
            float at = clip.Loop ? Mathf.Repeat(frame, clip.Frames) : Mathf.Clamp(frame, 0f, clip.Frames - 1);
            int a = Mathf.FloorToInt(at);
            int b = clip.Loop ? (a + 1) % clip.Frames : Mathf.Min(a + 1, clip.Frames - 1);
            float blend = at - a;
            for (int i = 0; i < rotations.Length; i++)
                rotations[i] = Quaternion.Slerp(clip.Rotations[i][a], clip.Rotations[i][b], blend);
            pelvis = Vector3.Lerp(clip.PelvisPosition[a], clip.PelvisPosition[b], blend);
        }

        // the clip's braking distance from the body's current speed: root travel left at the first frame whose root
        // speed has fallen to it. that is how far before a stand the stop clip has to start
        // the whole braking run the stop clip has from its first motion frame
        private float StopFullDistance(float speed)
        {
            var set = NearestSet(TravelYaw(), speed, false);
            return set == null || set.StopRemaining == null ? 0f : set.StopRemaining[0];
        }

        private float StopEntryDistance(float speed)
        {
            var set = NearestSet(TravelYaw(), speed, false);
            if (set == null || set.Stop == null || set.StopRemaining == null || set.Stop.RootSpeed == null)
                return 0f;
            // scan from the clip's fastest frame: stop clips start slower than the body (hop_2 from a stand), so the
            // first frame at or under the body's speed is frame 0 and the whole clip came back as braking distance
            int peak = 0;
            for (int f = 1; f <= set.StopMotionEnd; f++)
                if (set.Stop.RootSpeed[f] > set.Stop.RootSpeed[peak])
                    peak = f;
            for (int f = peak; f <= set.StopMotionEnd; f++)
                if (set.Stop.RootSpeed[f] <= speed)
                    return set.StopRemaining[f];
            return 0f;
        }

        // predictedOverride: braking distance known from the driver's goal (anticipated stop); otherwise EFT's own
        // returns false only when asked to defer and no entry frame's feet line up yet
        private bool BeginStop(float? predictedOverride = null, bool deferIfFeetPoor = false)
        {
            // Idle may still be returning the outgoing pose through inertia. A new stop
            // would reuse that offset against an unrelated clip while its weight is zero.
            // Native braking continues underneath; retry once this handoff has settled.
            if (_phase == Phase.Idle && (_inertia.Active || _upperRecovery.Active)) return false;
            if (Time.time < _stopRefireAt) return false;
            _stopAnticipated = false;
            // EFT brakes hard: 2.5-2.86 m/s bots stopped in 0.38-0.55 s over 0.63-0.93 m (~0.28 s of initial speed),
            // while Alyx stops brake over ~1.75 m; enter where the clip has that much distance left.
            // direction comes from actual travel, so a stop picks its set even when no start clip played
            // the flag drops before the body does, so a stop right after a sprint still belongs to the slide
            if (_player.IsSprintEnabled || _speed > SprintStopSpeed || Time.time - _sprintEndedAt < SprintStopGrace)
            {
                // Tarkov's power slide owns this; playing a 3.6 m/s Alyx stop at 4.8 scrambled the feet
                if (_phase != Phase.Idle)
                    Begin(Phase.HandOff, _active, _frame);
                return true;
            }
            var set = NearestSet(TravelYaw(), _speed, false);
            if (set == null)
            {
                if (_phase != Phase.Idle)
                    Begin(Phase.HandOff, _active, _frame);
                return true;
            }
            _set = set;
            _stopPredicted = predictedOverride.HasValue
                ? Mathf.Clamp(predictedOverride.Value, MinStopDistance, MaxStopDistanceFor(_speed))
                : Mathf.Clamp(StopDistancePerSpeed * _speed, MinStopDistance, MaxStopDistance);
            _stopOrigin = _player.Position;
            // never enter with less clip distance left than the body will travel: the distance-driven frame then
            // stalls at entry while the body runs on (0.3 m in a run stop), the foot predictions fall behind the clip
            // and the placer drags the landing foot 70 cm. a longer remainder just plays its tail after the body stops
            int entry = Mathf.Max(0, FirstStopFrameWithin(_stopPredicted, 0) - 1);
            if (LocomotionRefinement.Enabled)
            {
                entry = MotionSelection.FindStopEntryFallback(set.Stop.StepsRemaining, set.StopMotionEnd, entry);
                // Both feet need a remaining landing. Aggregate stepsRemaining allowed
                // entry at the right foot's final strike while only the left still stepped.
                while (entry >= 0 && !StopLanding.CanEnter(set.Stop.Stride, entry, set.Stop.Fps))
                    entry--;
                if (entry < 0)
                {
                    // A one-shot with no real step left cannot service a moving stop. Let
                    // the native controller own the transition rather than entering a
                    // terminal frame and dragging the last planted pose.
                    if (_phase != Phase.Idle)
                        Begin(Phase.HandOff, _active, _frame);
                    return false;
                }
            }
            float bestCost = float.MaxValue;
            float bestFeet = float.MaxValue;
            for (int f = 0; f <= set.StopMotionEnd; f++)
            {
                if (LocomotionRefinement.Enabled && (!MotionSelection.IsStopEntryEligible(set.Stop.StepsRemaining, f, set.StopMotionEnd)
                    || !StopLanding.CanEnter(set.Stop.Stride, f, set.Stop.Fps)))
                    continue;
                float distanceError = set.StopRemaining[f] - _stopPredicted;
                if (distanceError < 0f || distanceError > StopPoseWindowMeters)
                    continue;
                float feet = FeetCost(set.Stop, f);
                bestFeet = Mathf.Min(bestFeet, feet);
                float cost = PoseCostWeight * feet + distanceError * distanceError;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    entry = f;
                }
            }
            // a walk stop has 1.3 m of braking against a 0.95 m prediction, so a 0.3 m window held a quarter of a
            // stride and the feet phase could not match: both feet entered 60 cm from the clip's (one locked and
            // dragged to failure, the other steered mid-swing; user: "legs get caught up and tripped up"). an
            // anticipated stop can wait for the loop to reach a matching plant while the goal is still further than
            // EFT's own brake
            if (deferIfFeetPoor && bestFeet > StopFeetDeferCost)
                return false;
            if (LocomotionRefinement.Enabled && !deferIfFeetPoor && FeetCost(set.Stop, entry) > StopFeetDeferCost)
            {
                _stopRefireAt = Time.time + StopRefireSeconds;
                _events.Add(string.Format("{0:F2}s Stop rejected: incompatible feet {1:F3} m2", Time.time, FeetCost(set.Stop, entry)));
                if (_phase != Phase.Idle) Begin(Phase.HandOff, _active, _frame);
                return false;
            }
            if (_phase == Phase.Cut || _phase == Phase.Start)
                CrossfadeTo(Phase.Stop, set.Stop, entry);
            else
                Begin(Phase.Stop, set.Stop, entry);
            return true;
        }

        private int FirstStopFrameWithin(float distance, int from)
        {
            for (int f = Mathf.Max(0, from); f <= _set.StopMotionEnd; f++)
                if (_set.StopRemaining[f] <= distance)
                    return f;
            return _set.StopMotionEnd;
        }

        private void Begin(Phase phase, PoseClip clip, float frame)
        {
            if (_phase == Phase.Reaction && phase != Phase.Reaction)
                _reactionTorso.Release(HandOffBlend);
            bool clipChanged = clip != _active;
            _blendClip = null;
            _hasRelYaw = false;
            if (phase != Phase.HandOff) _holdRelease = false;
            if (phase == Phase.Hold) _holdYawStart = _player.Rotation.x;
            if (phase == Phase.Start) { _startPrevYaw = _player.Rotation.x; _startTurned = 0f; }
            if (phase != Phase.Start) _variation = false;
            if (clipChanged)
                _clipSince = Time.time;
            if (phase == Phase.HandOff && _hadIntent && (_phase == Phase.Start || _phase == Phase.SprintCycle || _phase == Phase.Cut || _phase == Phase.Reaction))
                _orderHandedOff = true;
            _nativeWalkPhaseActive = LocomotionRefinement.Enabled && AnimatorPhaseLock && phase == Phase.SprintCycle
                && IsTarkov(clip) && clip.Loop && clip.Gait != "sprint" && !_player.IsSprintEnabled
                && _syncAnimatorSampled && _syncAnimatorLength >= PhaseLockMinLength && _syncAnimatorLength <= PhaseLockMaxLength;
            _nativeFamilyPhase = _nativeWalkPhaseActive;
            if (_nativeWalkPhaseActive)
            {
                _nativeWalkPhase.Reset(frame / clip.Frames, _syncAnimatorPhase, _syncAnimatorStateHash);
                _nativeWalkLastFrame = Time.frameCount;
                Rate = _nativeWalkPhase.CadenceScale * clip.Frames / (_syncAnimatorLength * clip.Fps);
                _phaseLocked = Mathf.Abs(_nativeWalkPhase.OffsetCycles) < 0.001f;
                _syncPhaseLockEligible = true;
                _syncPhaseLockReason = _phaseLocked ? "locked" : "native_entry_converging";
            }
            if (phase != Phase.HandOff)
            {
                if (clip != _active || Mathf.Abs(frame - _frame) > 1.5f)
                    CaptureInertia(clip, frame);
                _active = clip;
                _frame = frame;
            }
            if (phase == Phase.Start || phase == Phase.Cut || phase == Phase.Transition)
            {
                _travelledAtBegin = _travelled;
                _moveOriginPath = clip.PathLength == null ? 0f : clip.PathLength[Mathf.Clamp(Mathf.RoundToInt(frame), 0, clip.Frames - 1)];
            }
            Phase previous = _phase;
            if (clipChanged || phase != _phase)
                _rateSmoothed = 1f;
            if (phase == Phase.Start && previous != Phase.Start)
                _startRepicked = false;
            if (phase == Phase.HandOff)
            {
                _handOffFromTransition = previous == Phase.Transition;
                // the placer keeps following this clip while the inertializer fades it out
                _fadeClip = clip ?? _active;
                _fadeFrame = frame;
                _fadeStarted = Time.time;
            }
            else
            {
                _handOffPending = false;
                if (clip != null)
                    _fadeClip = null;
            }
            if (phase != _phase)
                _phaseFor = 0f;
            _phase = phase;
            _events.Add(string.Format("{0:F2}s {1} {2} frame {3:F1} speed {4:F2} feet {6:F3}{5}", Time.time, phase, clip?.Name ?? "-", frame, _speed,
                phase == Phase.Stop ? string.Format(" predicted {0:F2} m{1}", _stopPredicted, _stopAnticipated ? " anticipated" : "") : (phase == Phase.HandOff || phase == Phase.Hold) && previous == Phase.Stop ? string.Format(" travelled {0:F2} m", new Vector2(_player.Position.x - _stopOrigin.x, _player.Position.z - _stopOrigin.z).magnitude) : "",
                clip == null ? 0f : AnimatedFeetCost(clip, frame)));
        }

        // move toward the speed-matched frame, never backward, within a rate band so the clip neither freezes nor rushes
        private float Advance(float current, int matched, float dt, float minRate = MinMatchRate)
        {
            float step = dt * _active.Fps;
            return SlewTo(current, matched, step, minRate, Mathf.Max(minRate, _phaseFor < CatchUpSeconds ? StartCatchUpRate : MaxMatchRate), dt, _active.Frames - 1);
        }

        // the 2.5x catch-up existed for a body at full speed in 0.2 s; under the inertia cap the body builds speed the
        // way the clip does, and racing the first frames is the scramble left over (fleet 220256: 1.2 -> 2.5x on a
        // body accelerating at a steady 4 m/s^2). the placer's stride scaling covers the distance difference
        private float StartCatchUpRate => BotInertiaPatch.Acceleration > 0f ? Mathf.Min(CatchUpRate, InertiaCatchUpRate) : CatchUpRate;
        private const float InertiaCatchUpRate = 1.4f;

        // the rate matching asks for, reached gradually and kept inside the band
        private float SlewTo(float current, float matched, float step, float minRate, float maxRate, float dt, float last)
        {
            float wanted = Mathf.Clamp(matched, current + step * minRate, current + step * maxRate);
            float rateWanted = (wanted - current) / Mathf.Max(step, 1e-5f);
            float rate = Mathf.Clamp(Mathf.MoveTowards(_rateSmoothed, rateWanted, RateSlewPerSecond * dt), minRate, maxRate);
            _rateSmoothed = rate;
            Rate = rate;
            return Mathf.Min(current + step * rate, last);
        }

        // EFT's slowest walk is about 1 m/s (command 0.1) while an Alyx start ramps from 0.2; the body is at 1 m/s
        // within 0.15 s, so matching from the clip's first motion frame hurried its first 25 frames at 2.5x with
        // the push-off foot pinned behind the body (user: "feet get dragged, fall behind the body"). enter where
        // the clip's own speed reaches the floor the body is about to be at, blended from the idle pose
        private int StartEntryFrame(MoveSet set)
        {
            int entry = set.Start.MotionStart;
            if (!ClipDrivenStarts || set.StartPeak == null)
                return entry;
            // the frame at the floor speed alone put a lateral start 30 percent in, mid-sidestep, and the bot snapped
            // into a wide stance from standing (user screenshots); weigh the feet against what is on screen too, so
            // from a stand the entry sits where the clip's feet are still near each other
            int latest = Mathf.RoundToInt(set.Start.Frames * MaxStartEntryFraction);
            float bestCost = float.MaxValue;
            for (int f = entry; f < set.Start.Frames && f <= latest; f++)
            {
                float deficit = Mathf.Max(0f, CommandMetersPerSecond[0] - set.StartPeak[f]);
                float cost = PoseCostWeight * FeetCost(set.Start, f) + deficit * deficit;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    entry = f;
                }
            }
            return entry;
        }

        private const float MaxStartEntryFraction = 0.45f;

        private static int FirstPeakAtOrAbove(MoveSet set, float speed, int from)
        {
            for (int f = Mathf.Max(0, from); f < set.Start.Frames; f++)
                if (set.StartPeak[f] >= speed)
                    return f;
            return set.Start.Frames - 1;
        }

        // the pose Tarkov keeps animating underneath, nudged by whatever offset is left over from the last switch
        private void ApplyInertia()
        {
            Quaternion torso = _spine.rotation;
            for (int i = 0; i < _bones.Length; i++)
                _bones[i].localRotation = _inertia.Rotation(i) * _bones[i].localRotation;
            // the pelvis offset is what keeps the hand-off continuous and it decays on its own; clamping it to the
            // 4 cm torso hold cut an 11 cm offset to 4 and moved both feet 8-11 cm in the switch frame
            Vector3 offset = _inertia.PelvisOffset;
            if (offset.sqrMagnitude > MaxInertiaPelvisOffset * MaxInertiaPelvisOffset)
                offset = offset.normalized * MaxInertiaPelvisOffset;
            _pelvis.localPosition += offset;
            _spine.rotation = torso;
        }

        private void Write(PoseClip clip, int a, int b, float u, float weight)
        {
            Quaternion torso = _spine.rotation;
            if (_lastSampled == null || _lastSampled.Length != _bones.Length)
            {
                _lastSampled = new Quaternion[_bones.Length];
                _lastSampleTime = _previousSampleTime = -1f;
            }
            if (_previousSampled == null || _previousSampled.Length != _bones.Length)
                _previousSampled = new Quaternion[_bones.Length];
            Array.Copy(_lastSampled, _previousSampled, _bones.Length);
            _previousSampledPelvis = _lastPelvis;
            _previousSampleTime = _lastSampleTime;
            _lastSampleTime = Time.time;
            _hasThighCompensation = false;
            // Lower-body-only starts/stops must not tilt the hips against a counter-held
            // native torso: that shears the waist skin even with constant bone lengths.
            float pelvisTilt = _phase == Phase.Start || _phase == Phase.Stop || _phase == Phase.Hold
                ? 0f : PelvisTiltFromClip;
            PoseClip partner = clip == _active ? _blendClip : null;
            int pa = 0, pb = 0; float pu = 0f;
            if (partner != null)
            {
                float pf = FamilyFrame(partner);
                pa = Mathf.Clamp(Mathf.FloorToInt(pf), 0, partner.Frames - 1);
                pb = (pa + 1) % partner.Frames;
                pu = pf - pa;
            }
            for (int i = 0; i < _bones.Length; i++)
            {
                Quaternion sampled = Quaternion.Slerp(clip.Rotations[i][a], clip.Rotations[i][b], u);
                if (partner != null)
                    sampled = Quaternion.Slerp(sampled, Quaternion.Slerp(partner.Rotations[i][pa], partner.Rotations[i][pb], pu), _blendWeight);
                if (Inertialize)
                    sampled = _inertia.Rotation(i) * sampled;
                if (i == _pelvisIndex && _animated != null && pelvisTilt < 1f)
                {
                    // the vertical in the pelvis parent's own frame
                    Vector3 up = _bones[i].parent.InverseTransformDirection(Vector3.up);
                    Quaternion clipPelvis = sampled;
                    sampled = BlendTilt(_animated[i], sampled, pelvisTilt, up);
                    // the thighs hang off the pelvis: without this their world orientation follows the blended pelvis
                    // and the legs' whole frame shifts (knees bent inward, review finding 2)
                    _thighCompensation = Quaternion.Inverse(sampled) * clipPelvis;
                    _hasThighCompensation = true;
                }
                else if (_hasThighCompensation && _thighIndex[0] >= 0 && (i == _thighIndex[0] || i == _thighIndex[1]))
                    sampled = _thighCompensation * sampled;
                _lastSampled[i] = sampled;
                _bones[i].localRotation = Quaternion.Slerp(_bones[i].localRotation, sampled, weight);
            }
            for (int i = 0; i < 2; i++)
                _thigh2[i].localRotation = Quaternion.Slerp(_thigh2[i].localRotation, _thigh2Rest[i], weight);
            Vector3 pelvisPosition = Vector3.Lerp(clip.PelvisPosition[a], clip.PelvisPosition[b], u);
            if (partner != null)
                pelvisPosition = Vector3.Lerp(pelvisPosition, Vector3.Lerp(partner.PelvisPosition[pa], partner.PelvisPosition[pb], pu), _blendWeight);
            pelvisPosition += Inertialize ? _inertia.PelvisOffset : Vector3.zero;
            _lastPelvis = pelvisPosition;
            _pelvis.localPosition = Vector3.Lerp(_pelvis.localPosition, pelvisPosition, weight);
            WarpStride(clip, a, b, u, weight);
            // Preserve native aiming orientation; let torso translation follow the pelvis at native bone lengths.
            _spine.rotation = torso;
            // Handoffs must begin from the blended pose actually displayed, not the full-strength target.
            for (int i = 0; i < _bones.Length; i++) _lastSampled[i] = _bones[i].localRotation;
            _lastPelvis = _pelvis.localPosition;
            AppliedFrames++;
            if (Rate > 1.5f) HurriedFrames++;
            PeakRate = Mathf.Max(PeakRate, Rate);
        }

        // swing-twist split about the parent's up axis: the twist (yaw) comes from the clip, the swing (pitch and roll)
        // is blended from Tarkov's animated pelvis toward the clip's by tiltWeight
        private static Quaternion BlendTilt(Quaternion tarkov, Quaternion clip, float tiltWeight, Vector3 up)
        {
            Quaternion clipTwist, clipSwing, tarkovTwist, tarkovSwing;
            SwingTwist(clip, up, out clipSwing, out clipTwist);
            SwingTwist(tarkov, up, out tarkovSwing, out tarkovTwist);
            // twist last, about the parent-frame vertical: q = twist * swing
            return clipTwist * Quaternion.Slerp(tarkovSwing, clipSwing, tiltWeight);
        }

        // q = twist * swing with the twist about `axis` given in the parent frame (the rotation's target frame). the
        // pelvis rest pose is turned 90 degrees, so the swing*twist order with a child-frame axis kept the wrong part
        private static void SwingTwist(Quaternion q, Vector3 axis, out Quaternion swing, out Quaternion twist)
        {
            Vector3 r = new Vector3(q.x, q.y, q.z);
            Vector3 p = Vector3.Project(r, axis);
            twist = new Quaternion(p.x, p.y, p.z, q.w);
            if (twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w < 1e-8f)
                twist = Quaternion.identity;
            else
                twist.Normalize();
            swing = Quaternion.Inverse(twist) * q;
        }

        // squeeze each foot's offset from the hips along the travel axis, so a slower bot takes shorter steps at
        // the clip's own cadence instead of long strides in slow motion
        private void WarpStride(PoseClip clip, int a, int b, float u, float weight)
        {
            if (!StrideWarp || clip.FootL == null || clip.RootVelocity == null || !_thigh[0] || !_calf[0] || !_thigh[1] || !_calf[1])
                return;
            float scale = Mathf.Lerp(1f, _strideScale, weight);
            if (Mathf.Abs(scale - 1f) < 0.02f)
                return;
            Vector2 velocity = Vector2.Lerp(clip.RootVelocity[a], clip.RootVelocity[b], u);
            // switching the warp off below a speed threshold snapped the foot back by whatever it was squeezing,
            // so it fades out as the clip's own motion dies away instead
            float moving = Mathf.Clamp01((velocity.magnitude - 0.1f) / 0.4f);
            if (moving <= 0f)
                return;
            scale = Mathf.Lerp(1f, scale, moving * moving * (3f - 2f * moving));
            if (Mathf.Abs(scale - 1f) < 0.02f)
                return;
            velocity.Normalize();
            Vector3 axis = new Vector3(velocity.x, 0f, velocity.y);
            Vector3 pelvis = Vector3.Lerp(clip.PelvisPosition[a], clip.PelvisPosition[b], u);
            Transform root = _pelvis.parent;
            Vector3 lateral = root.TransformDirection(Vector3.right);
            for (int leg = 0; leg < 2; leg++)
            {
                Vector3 local = Vector3.Lerp(leg == 0 ? clip.FootL[a] : clip.FootR[a], leg == 0 ? clip.FootL[b] : clip.FootR[b], u);
                Vector3 offset = local - pelvis;
                float along = Vector3.Dot(offset, axis);
                Vector3 correction = axis * (along * (scale - 1f));
                Transform foot = leg == 0 ? _footL : _footR;
                // Preserve the inertialized/family-blended pose. An absolute clip target here
                // discarded its transition offset as soon as stride warping became active.
                Vector3 target = LocomotionRefinement.Enabled
                    ? foot.position + root.TransformVector(correction)
                    : root.TransformPoint(local + correction);
                // never ask for more reach than the leg has: pulling it straight snapped the knee (7.7 m/s spikes)
                Vector3 hip = _thigh[leg].position;
                float chain = (_calf[leg].position - hip).magnitude + (foot.position - _calf[leg].position).magnitude;
                Vector3 fromHip = target - hip;
                float allowed = Mathf.Max(MaxReach * chain, (foot.position - hip).magnitude);
                if (fromHip.magnitude > allowed)
                    target = hip + fromHip.normalized * allowed;
                LegIk.Solve(_thigh[leg], _calf[leg], foot, target, lateral);
            }
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
    }

    internal struct MotionSyncSnapshot
    {
        public int SampleFrame;
        public float SampleTime;
        public bool HasAnimatorPhase;
        public float AnimatorPhase;
        public float AnimatorLength;
        public bool HasAnimatorStateHash;
        public int AnimatorStateHash;
        public bool HasTransition;
        public bool InTransition;
        public bool HasLegPhase;
        public float LegPhase;
        public float LegFrame;
        public int LegFrameCount;
        public bool HasPhaseError;
        public float PhaseErrorCycles;
        public bool PhaseLocked;
        public bool PhaseLockEligible;
        public string PhaseLockReason;
    }

    internal struct PosePlaybackSummary
    {
        public bool Enabled;
        public string Clip;
        public int AppliedFrames;
        public int HurriedFrames;
        public float MaxRate;
        public string[] Events;
    }
}
