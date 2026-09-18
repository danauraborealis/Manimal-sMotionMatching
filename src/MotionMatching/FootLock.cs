using EFT;
using RootMotion;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // milestone 3 prototype: pins a planted foot horizontally to where it landed, visual only.
    // runs after VisualPass, which is the last leg writer inside Player.LateUpdate
    internal sealed class FootLock
    {
        private const float FootStepThreshold = 0.5f;
        // same "clearly slower than the body" cut the summarizer uses for core stance
        private const float LockSpeedRatio = 0.35f;
        private const float LockSpeedFloor = 0.15f;
        private const float MaxDrift = 0.12f;
        // clip-driven locks exist to absorb the gap between Alyx footwork and EFT's shorter stops, so they hold further
        private const float MaxClipDrift = 0.3f;
        private const float EngageSeconds = 0.12f;
        // the animation's own toe-off: the foot speeding up means the clip wants it gone
        private const float ReleaseSpeedRatio = 0.6f;
        // a fixed-time blend-out popped (a 15 cm offset over 80 ms is ~1.9 m/s on top of the swing);
        // bleeding the held offset at a capped speed keeps releases under what the eye reads as a snap
        private const float MaxOffsetDecaySpeed = 0.6f;
        // walking has double support: the curve flips to the other foot at its heel strike while this one is
        // still planted, so a flip alone released walks early (35-60% reduction vs ~100% at run speeds)
        private const float HoldAfterFlipSeconds = 0.5f;
        // knee pops all came from legs pulled to 98-99.9% extension: near straight, a small foot move swings
        // the knee fast. release before that and never ask the solver for more
        private const float MaxReachRatio = 0.97f;

        private readonly Player _player;
        private readonly Leg[] _legs = new Leg[2];
        private Vector3 _previousRoot;
        private bool _hasPrevious;

        private FootLock(Player player, BipedReferences references)
        {
            _player = player;
            _legs[0] = new Leg(references.leftThigh, references.leftCalf, references.leftFoot, -1f);
            _legs[1] = new Leg(references.rightThigh, references.rightCalf, references.rightFoot, 1f);
        }

        public int Locks => _legs[0].LockCount + _legs[1].LockCount;
        public int DriftReleases => _legs[0].DriftReleases + _legs[1].DriftReleases;
        public int ReachReleases => _legs[0].ReachReleases + _legs[1].ReachReleases;

        // off lets held feet bleed out through the normal capped release instead of snapping back
        public bool Active { get; set; } = true;

        public bool TryGetAnchor(int leg, out Vector3 anchor) => _legs[leg].TryGetAnchor(out anchor);

        public static FootLock Create(Player player)
        {
            var references = player?.Grounder?.ik?.references;
            if (references == null || !references.leftThigh || !references.leftCalf || !references.leftFoot || !references.rightThigh || !references.rightCalf || !references.rightFoot)
                return null;
            return new FootLock(player, references);
        }

        // pose: when Alyx legs are on screen their own contact flags drive the lock. EFT's FootStep curve is out of
        // phase with them, which is why the first prototype couldn't be combined with pose playback
        public void Apply(float footStepCurve, PosePlayback pose = null)
        {
            if (NativeJumpOwnership.Owns(_player))
            {
                _hasPrevious = false;
                for (int i = 0; i < _legs.Length; i++) _legs[i].Reset();
                return;
            }
            float dt = Time.deltaTime;
            Vector3 root = _player.Position;
            if (!_hasPrevious || dt <= 0f)
            {
                _previousRoot = root;
                _hasPrevious = true;
                for (int i = 0; i < _legs.Length; i++) _legs[i].Reset();
                return;
            }
            float rootSpeed = Horizontal(root - _previousRoot) / dt;
            _previousRoot = root;
            Vector3 lateral = Quaternion.Euler(0f, _player.Rotation.x, 0f) * Vector3.right;
            bool leftPlanted = false, rightPlanted = false;
            bool fromClip = pose != null && pose.TryGetContacts(out leftPlanted, out rightPlanted);
            for (int i = 0; i < _legs.Length; i++)
                _legs[i].Update(fromClip ? ((i == 0 ? leftPlanted : rightPlanted) ? _legs[i].StanceSign : -_legs[i].StanceSign) : footStepCurve, rootSpeed, dt, lateral, Active, fromClip ? MaxClipDrift : MaxDrift, fromClip);
        }

        private static float Horizontal(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        private sealed class Leg
        {
            private readonly Transform _thigh;
            private readonly Transform _calf;
            private readonly Transform _foot;
            private readonly float _stanceSign;
            private Vector3 _previousFoot;
            private bool _hasPreviousFoot;
            private bool _locked;
            private Vector3 _anchor;
            private Vector2 _offset;
            private float _engaged;
            private Vector3 _bendAxis;
            private float _flippedFor;

            public float StanceSign => _stanceSign;

            public int LockCount;
            public int DriftReleases;
            public int ReachReleases;

            public Leg(Transform thigh, Transform calf, Transform foot, float stanceSign)
            {
                _thigh = thigh;
                _calf = calf;
                _foot = foot;
                _stanceSign = stanceSign;
            }

            public void Reset()
            {
                _hasPreviousFoot = false;
                _locked = false;
                _offset = Vector2.zero;
            }

            public bool TryGetAnchor(out Vector3 anchor)
            {
                anchor = _anchor;
                return _locked;
            }

            public void Update(float curve, float rootSpeed, float dt, Vector3 lateral, bool active, float maxDrift, bool fromClip)
            {
                Vector3 animated = _foot.position;
                float footSpeed = _hasPreviousFoot ? Horizontal(animated - _previousFoot) / dt : float.MaxValue;
                _previousFoot = animated;
                _hasPreviousFoot = true;

                bool stance = active && curve * _stanceSign >= FootStepThreshold;
                if (!active)
                    _locked = false;
                float slowEnough = Mathf.Max(LockSpeedRatio * rootSpeed, LockSpeedFloor);
                if (!_locked)
                {
                    if (stance && (fromClip || footSpeed < slowEnough) && _offset.sqrMagnitude < 1e-6f)
                    {
                        _locked = true;
                        _anchor = animated;
                        LockCount++;
                    }
                }
                else if (!fromClip && footSpeed > Mathf.Max(ReleaseSpeedRatio * rootSpeed, LockSpeedFloor * 2f))
                {
                    _locked = false;
                }
                else if (!stance && (fromClip || (_flippedFor += dt) > HoldAfterFlipSeconds))
                {
                    _locked = false;
                }
                else if (Horizontal(_anchor - animated) > maxDrift)
                {
                    _locked = false;
                    DriftReleases++;
                }

                if (stance)
                    _flippedFor = 0f;
                if (_locked)
                {
                    // easing in stops the pin from yanking the foot on the frame it catches
                    _engaged = Mathf.Min(1f, _engaged + dt / EngageSeconds);
                    _offset = new Vector2(_anchor.x - animated.x, _anchor.z - animated.z) * _engaged;
                }
                else
                {
                    _engaged = 0f;
                    float length = _offset.magnitude;
                    float decay = MaxOffsetDecaySpeed * dt;
                    _offset = length <= decay ? Vector2.zero : _offset * ((length - decay) / length);
                }
                if (_offset.sqrMagnitude < 1e-8f)
                    return;

                Vector3 target = new Vector3(animated.x + _offset.x, animated.y, animated.z + _offset.y);
                Vector3 hip = _thigh.position;
                float chain = (_calf.position - hip).magnitude + (animated - _calf.position).magnitude;
                // never demand more reach than the clip already uses, and cap it below straight
                float allowed = Mathf.Max(MaxReachRatio * chain, (animated - hip).magnitude);
                Vector3 fromHip = target - hip;
                if (fromHip.magnitude > allowed)
                {
                    if (_locked)
                    {
                        _locked = false;
                        ReachReleases++;
                    }
                    target = hip + fromHip.normalized * allowed;
                    _offset = new Vector2(target.x - animated.x, target.z - animated.z);
                }
                Quaternion footRotation = _foot.rotation;
                SolveTwoBone(target, lateral);
                // keep the clip's heel/toe roll; only the leg bends to reach
                _foot.rotation = footRotation;
            }

            // analytic two-bone ik (theorangeduck form): fix the knee angle for the new reach, then swing the chain onto the target
            private void SolveTwoBone(Vector3 target, Vector3 lateral)
            {
                Vector3 a = _thigh.position, b = _calf.position, c = _foot.position;
                float lab = (b - a).magnitude, lcb = (c - b).magnitude;
                if (lab < 1e-4f || lcb < 1e-4f)
                    return;
                float lat = Mathf.Clamp((target - a).magnitude, 0.01f, lab + lcb - 0.001f);

                Vector3 ac = (c - a).normalized, ab = (b - a).normalized, ba = (a - b).normalized, bc = (c - b).normalized, at = (target - a).normalized;
                float acAb0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ac, ab), -1f, 1f));
                float baBc0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ba, bc), -1f, 1f));
                float acAt0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot(ac, at), -1f, 1f));
                float acAb1 = Mathf.Acos(Mathf.Clamp((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f));
                float baBc1 = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb), -1f, 1f));

                Vector3 bend = Vector3.Cross(c - a, b - a);
                if (bend.sqrMagnitude > 1e-8f)
                    _bendAxis = bend.normalized;
                else if (_bendAxis.sqrMagnitude < 0.5f)
                    _bendAxis = lateral; // straight leg on the first frame; knees bend about the body's lateral axis
                Vector3 swing = Vector3.Cross(c - a, target - a);

                _thigh.rotation = Quaternion.AngleAxis((acAb1 - acAb0) * Mathf.Rad2Deg, _bendAxis) * _thigh.rotation;
                _calf.rotation = Quaternion.AngleAxis((baBc1 - baBc0) * Mathf.Rad2Deg, _bendAxis) * _calf.rotation;
                if (swing.sqrMagnitude > 1e-10f)
                    _thigh.rotation = Quaternion.AngleAxis(acAt0 * Mathf.Rad2Deg, swing.normalized) * _thigh.rotation;
            }
        }
    }

    internal struct PoseProbeSnapshot
    {
        public bool Active;
        public string Phase;
        public string Driver;
        public string Clip;
        public string UpperOverlay;
        public WeaponGripProbe Grip;
        public float UpperOverlayFrame;
        public float Frame;
        public float Weight;
        public float Rate;
        public bool ContactL;
        public bool ContactR;
        public float PathBehind;
        public MotionSyncSnapshot Sync;
        public SprintInertiaProbe SprintInertia;
        public SprintEntrySnapshot SprintEntry;
        public float LeanPitch;
        public float LeanRoll;
        public float AimShift;
        // hands in Root_Joint space: comparing before_visual with after_pose shows what our writes do to the arms
        public bool HasHands;
        public float[] HandL;
        public float[] HandR;
        public bool LockedL;
        public bool LockedR;
        public FootPlacerProbe PlacerL;
        public FootPlacerProbe PlacerR;
    }

    internal struct BodyLeanSummary
    {
        public bool Enabled;
        public int AppliedFrames;
        public float PeakPitch;
        public float PeakRoll;
        public float PeakAimShift;
    }

    internal struct FootLockSummary
    {
        public bool Enabled;
        public int Locks;
        public int DriftReleases;
        public int ReachReleases;
    }
}
