using UnityEngine;

namespace Manimal.MotionMatching
{
    // Samples the spatial part of a directional family while leaving the reference member's temporal clock intact.
    // Source footbases/headings and root velocities are blended at the same phase pair as the pose. The resulting
    // source trajectory is then expressed against the reference progression and cycle intervals; averaging offsets
    // from members with different clocks would otherwise make the sampled foot leave its canonical endpoints.
    internal sealed class FamilyStride
    {
        private const float MinStrideLength = 0.02f;
        private const float MaxCycleStartErrorDegrees = 54f;

        private PoseClip _reference;
        private PoseClip _sample;
        private Vector3[] _rootPositions;
        private float[] _rootYawDegrees;
        private Vector3[][] _worldSoles;
        private float[][] _worldHeadings;
        private PoseClip _lastReference;
        private PoseClip _lastA;
        private PoseClip _lastB;
        private float _lastBlend;
        private bool _lastNativePhase;
        private bool _hasLastSample;

        // These arrays are owned and reused by the sampler. They are exposed for diagnostics and for the placer to
        // inspect the same unwrapped source trajectory without allocating another per-frame copy.
        public Vector3[] RootPositions => _rootPositions;
        public float[] RootYawDegrees => _rootYawDegrees;

        public Vector3[] GetWorldSole(int side)
        {
            return side >= 0 && side < 2 ? _worldSoles?[side] : null;
        }

        public float[] GetWorldHeading(int side)
        {
            return side >= 0 && side < 2 ? _worldHeadings?[side] : null;
        }

        // A family can only share this sampler when both feet carry the same cycle/contact topology. A missing or
        // truncated array is rejected explicitly because silently falling back to one foot makes a family appear
        // compatible while its other support phase is on a different clock.
        public static bool Compatible(PoseClip reference, PoseClip member, bool nativePhase)
        {
            if (reference == null || member == null || reference.Frames <= 0 || member.Frames != reference.Frames ||
                !reference.Loop || !member.Loop || !IsFinite(reference.Fps) || !IsFinite(member.Fps) ||
                reference.Fps <= 0f || Mathf.Abs(reference.Fps - member.Fps) > 0.01f)
                return false;
            if (!HasStrideData(reference) || !HasStrideData(member))
                return false;

            int n = reference.Frames;
            int shift = nativePhase ? 0 : member.PhaseOffset - reference.PhaseOffset;
            bool referenceContacts = HasContacts(reference);
            bool memberContacts = HasContacts(member);
            if (referenceContacts != memberContacts)
                return false;
            for (int side = 0; side < 2; side++)
            {
                StrideFoot a = reference.Stride[side];
                StrideFoot b = member.Stride[side];
                if (a.Cycles.Length != b.Cycles.Length || a.Cycles.Length == 0)
                    return false;

                int mismatch = 0;
                for (int f = 0; f < n; f++)
                {
                    if (a.Grounded[f] != b.Grounded[Wrap(f + shift, n)])
                        mismatch++;
                }
                // Sole support and raw ankle-height contacts have different semantics; do not count the latter
                // a second time and reject valid heel/toe rolls. Cycle correspondence is checked separately.
                if (mismatch > n / 4)
                    return false;

                // Do not use b.Cycle[memberFrame] for correspondence. A phase offset can place the sampled frame
                // one or two frames before the member's stance, where that lookup quite correctly names the previous
                // cycle. Match each reference stance to the nearest unclaimed member stance instead.
                bool[] used = new bool[b.Cycles.Length];
                for (int k = 0; k < a.Cycles.Length; k++)
                {
                    StrideCycle c = a.Cycles[k];
                    int expectedMemberStart = Wrap(c.StartFrame + shift, n);
                    int memberCycle = -1;
                    int nearestError = int.MaxValue;
                    for (int candidate = 0; candidate < b.Cycles.Length; candidate++)
                    {
                        if (used[candidate] || b.Cycles[candidate] == null)
                            continue;
                        int error = Mathf.Abs(FrameDelta(b.Cycles[candidate].StartFrame, expectedMemberStart, n));
                        if (error < nearestError)
                        {
                            nearestError = error;
                            memberCycle = candidate;
                        }
                    }
                    if (memberCycle < 0)
                        return false;
                    used[memberCycle] = true;
                    StrideCycle d = b.Cycles[memberCycle];
                    float frameError = nearestError * 360f / n;
                    if (frameError > MaxCycleStartErrorDegrees || c.VirtualStart != d.VirtualStart ||
                        c.VirtualEnd != d.VirtualEnd)
                        return false;
                }
            }
            return true;
        }

        public static int FindPhaseOffset(PoseClip reference, PoseClip member)
        {
            if (!HasStrideData(reference) || !HasStrideData(member) || reference.Frames != member.Frames) return 0;
            int n = reference.Frames, best = 0;
            float bestCost = float.MaxValue;
            for (int shift = 0; shift < n; shift++)
            {
                float cost = 0f;
                for (int side = 0; side < 2; side++)
                {
                    var a = reference.Stride[side]; var b = member.Stride[side];
                    for (int frame = 0; frame < n; frame++)
                        if (a.Grounded[frame] != b.Grounded[Wrap(frame + shift, n)]) cost += 1f;
                    for (int cycle = 0; cycle < a.Cycles.Length; cycle++)
                    {
                        int nearest = n;
                        for (int candidate = 0; candidate < b.Cycles.Length; candidate++)
                            nearest = Mathf.Min(nearest, Mathf.Abs(FrameDelta(b.Cycles[candidate].StartFrame, a.Cycles[cycle].StartFrame + shift, n)));
                        cost += nearest * 0.25f;
                    }
                }
                // Prefer the native clock when equally plausible. Both feet determine the shared offset.
                if (cost < bestCost || (cost == bestCost && Mathf.Min(shift, n - shift) < Mathf.Min(best, n - best)))
                { best = shift; bestCost = cost; }
            }
            return best;
        }

        // Produce a reusable sampled stride clip. `reference` supplies Cycle and Progression verbatim; `a` and `b`
        // supply the pose-space spatial samples at their phase-aligned frames. The sampler is intended to be called
        // as the family blend changes, not once per rendered foot, so all working arrays are retained between calls.
        public PoseClip Sample(PoseClip reference, PoseClip a, PoseClip b, float weight, bool nativePhase)
        {
            if (reference == null || a == null)
                return reference;
            b = b ?? a;
            if (!HasStrideData(reference) || !HasStrideData(a) || !HasStrideData(b))
                return reference;

            float blend = IsFinite(weight) ? Mathf.Clamp01(weight) : 0f;
            if (_hasLastSample && ReferenceEquals(_lastReference, reference) && ReferenceEquals(_lastA, a) &&
                ReferenceEquals(_lastB, b) && _lastNativePhase == nativePhase && _lastBlend == blend)
                return _sample;

            EnsureStorage(reference);
            int n = reference.Frames;
            int shiftA = nativePhase ? 0 : a.PhaseOffset - reference.PhaseOffset;
            int shiftB = nativePhase ? 0 : b.PhaseOffset - reference.PhaseOffset;

            _sample.SpeedMetersPerSecond = Lerp(a.SpeedMetersPerSecond, b.SpeedMetersPerSecond, blend);
            _sample.PhaseOffset = reference.PhaseOffset;
            _sample.MoveYaw = Lerp(a.MoveYaw, b.MoveYaw, blend);
            _sample.FromYaw = Lerp(a.FromYaw, b.FromYaw, blend);
            _sample.ToYaw = Lerp(a.ToYaw, b.ToYaw, blend);
            _sample.TurnFrame = reference.TurnFrame;
            _sample.YawChange = Lerp(a.YawChange, b.YawChange, blend);
            _sample.YawExcursion = Lerp(a.YawExcursion, b.YawExcursion, blend);

            for (int side = 0; side < 2; side++)
            {
                StrideFoot x = a.Stride[side];
                StrideFoot y = b.Stride[side];
                _sample.Stride[side].Floor = Lerp(x.Floor, y.Floor, blend);
            }

            float distance = 0f;
            for (int f = 0; f < n; f++)
            {
                int af = Wrap(f + shiftA, n);
                int bf = Wrap(f + shiftB, n);
                _sample.PathLength[f] = distance;

                Vector2 va = RootVelocityAt(a, af);
                Vector2 vb = RootVelocityAt(b, bf);
                _sample.RootVelocity[f] = Lerp(va, vb, blend);
                // RootSpeed is a scalar path-distance basis. Derive it from the blended velocity so the cached
                // root trajectory and the placer’s distance scale describe the same motion, including diagonals.
                _sample.RootSpeed[f] = _sample.RootVelocity[f].magnitude;
                distance += _sample.RootSpeed[f] / Mathf.Max(reference.Fps, 1e-4f);
                _sample.YawProgress[f] = Lerp(UnwrappedYawAt(a, f + shiftA), UnwrappedYawAt(b, f + shiftB), blend);

                _sample.PelvisPosition[f] = BlendVector(PelvisAt(a, af), PelvisAt(b, bf), blend);
                _sample.FootL[f] = BlendVector(FootAt(a, af, 0), FootAt(b, bf, 0), blend);
                _sample.FootR[f] = BlendVector(FootAt(a, af, 1), FootAt(b, bf, 1), blend);

                for (int side = 0; side < 2; side++)
                {
                    StrideFoot x = a.Stride[side];
                    StrideFoot y = b.Stride[side];
                    StrideFoot s = _sample.Stride[side];
                    s.Footbase[f] = BlendVector(x.Footbase[af], y.Footbase[bf], blend);
                    s.Heading[f] = LerpAngle(x.Heading[af], y.Heading[bf], blend);
                    s.Grounded[f] = blend <= 0f ? x.Grounded[af] : blend >= 1f ? y.Grounded[bf] : x.Grounded[af] && y.Grounded[bf];
                }
            }

            _sample.SpeedMetersPerSecond = n > 0
                ? distance / (n / Mathf.Max(reference.Fps, 1e-4f))
                : 0f;

            IntegrateRootPositions(reference.Fps);
            BuildUnwrappedWorldSoles(n);
            ReDecompose(reference);

            _lastReference = reference;
            _lastA = a;
            _lastB = b;
            _lastBlend = blend;
            _lastNativePhase = nativePhase;
            _hasLastSample = true;
            return _sample;
        }

        private void EnsureStorage(PoseClip reference)
        {
            int n = reference.Frames;
            if (_sample != null && _reference == reference && _sample.Frames == n && _rootPositions != null &&
                _rootPositions.Length == n + 1)
                return;

            _hasLastSample = false;
            _reference = reference;
            _sample = new PoseClip
            {
                Name = (reference.Name ?? "family") + " [family stride]",
                Family = reference.Family,
                Frames = n,
                Fps = reference.Fps,
                Loop = reference.Loop,
                RootSpeed = new float[n],
                PathLength = new float[n],
                RootVelocity = new Vector2[n],
                YawProgress = new float[n],
                FootL = new Vector3[n],
                FootR = new Vector3[n],
                PelvisPosition = new Vector3[n],
                Stride = new StrideFoot[2]
            };

            _rootPositions = new Vector3[n + 1];
            _rootYawDegrees = new float[2 * n + 1];
            _worldSoles = new Vector3[2][];
            _worldHeadings = new float[2][];
            for (int side = 0; side < 2; side++)
            {
                StrideFoot r = reference.Stride[side];
                StrideFoot s = new StrideFoot
                {
                    // The reference clock is immutable for the lifetime of this sampled object.
                    Cycle = r.Cycle,
                    Progression = r.Progression,
                    Cycles = new StrideCycle[r.Cycles.Length],
                    Offset = new Vector3[n],
                    RotationOffset = new float[n],
                    Footbase = new Vector3[n],
                    Heading = new float[n],
                    Grounded = new bool[n]
                };
                for (int k = 0; k < s.Cycles.Length; k++)
                    s.Cycles[k] = new StrideCycle();
                _sample.Stride[side] = s;
                _worldSoles[side] = new Vector3[2 * n + 1];
                _worldHeadings[side] = new float[2 * n + 1];
            }
        }

        private void IntegrateRootPositions(float fps)
        {
            int n = _sample.Frames;
            _rootPositions[0] = Vector3.zero;
            float invFps = 1f / Mathf.Max(fps, 1e-4f);
            for (int f = 0; f < n; f++)
            {
                Vector2 velocity = _sample.RootVelocity[f];
                Vector3 local = new Vector3(velocity.x * invFps, 0f, velocity.y * invFps);
                Vector3 world = RotateRootLocal(local, YawAt(_sample, f));
                _rootPositions[f + 1] = _rootPositions[f] + world;
            }

            float loopYaw = n > 1 ? YawAt(_sample, n - 1) - YawAt(_sample, 0) : 0f;
            float previousRaw = 0f;
            for (int i = 0; i <= 2 * n; i++)
            {
                int period = i / n;
                int frame = Wrap(i, n);
                float raw = YawAt(_sample, frame) + period * loopYaw;
                if (i == 0)
                    _rootYawDegrees[i] = raw;
                else
                    _rootYawDegrees[i] = _rootYawDegrees[i - 1] + Mathf.DeltaAngle(previousRaw, raw);
                previousRaw = raw;
            }
        }

        private void BuildUnwrappedWorldSoles(int n)
        {
            float loopYaw = n > 1 ? _rootYawDegrees[n - 1] - _rootYawDegrees[0] : 0f;
            for (int side = 0; side < 2; side++)
            {
                StrideFoot source = _sample.Stride[side];
                Vector3[] worldSole = _worldSoles[side];
                float[] worldHeading = _worldHeadings[side];
                float previousHeadingRaw = 0f;
                for (int i = 0; i <= 2 * n; i++)
                {
                    int period = i / n;
                    int frame = Wrap(i, n);
                    // Root positions are integrated in the first period's frame. At a turning loop, each repeated
                    // period is an SE(2) image: rotate both the local frame position and the accumulated period
                    // displacement by the loop yaw instead of translating every period along the original axis.
                    Vector3 periodTranslation = Vector3.zero;
                    for (int previousPeriod = 0; previousPeriod < period; previousPeriod++)
                        periodTranslation += RotateRootLocal(_rootPositions[n], previousPeriod * loopYaw);
                    Vector3 root = periodTranslation + RotateRootLocal(_rootPositions[frame], period * loopYaw);
                    worldSole[i] = root + RotateRootLocal(source.Footbase[frame], _rootYawDegrees[i]);

                    float rawHeading = source.Heading[frame] + _rootYawDegrees[i];
                    if (i == 0)
                        worldHeading[i] = rawHeading;
                    else
                        worldHeading[i] = worldHeading[i - 1] + Mathf.DeltaAngle(previousHeadingRaw, rawHeading);
                    previousHeadingRaw = rawHeading;
                }
            }
        }

        private void ReDecompose(PoseClip reference)
        {
            int n = reference.Frames;
            for (int side = 0; side < 2; side++)
            {
                StrideFoot canonical = reference.Stride[side];
                StrideFoot sample = _sample.Stride[side];
                for (int k = 0; k < canonical.Cycles.Length; k++)
                {
                    StrideCycle sourceCycle = canonical.Cycles[k];
                    StrideCycle outputCycle = sample.Cycles[k];
                    // Exporter loop cycles are normalized to the first clip window while their end may extend into
                    // the next image (for example start 36/end 60 in a 48-frame clip). Keep that representation;
                    // each canonical frame below is lifted into this interval when its ownership requires it.
                    int start = ExtendedIndex(sourceCycle.StartFrame, n);
                    int end = ExtendedIndex(sourceCycle.EndFrame, n);
                    if (end <= start)
                        end += n;
                    if (end > 2 * n)
                        end = 2 * n;

                    Vector3 sourceStart = _worldSoles[side][start];
                    Vector3 sourceEnd = _worldSoles[side][end];
                    Vector3 stride = sourceEnd - sourceStart;
                    stride.y = 0f;
                    float length = new Vector2(stride.x, stride.z).magnitude;
                    float axisYaw = length < MinStrideLength
                        ? _rootYawDegrees[start]
                        : Mathf.Atan2(stride.x, stride.z) * Mathf.Rad2Deg;
                    float axisRadians = axisYaw * Mathf.Deg2Rad;
                    float axisSin = Mathf.Sin(axisRadians);
                    float axisCos = Mathf.Cos(axisRadians);
                    float startHeading = _worldHeadings[side][start];
                    float endHeading = _worldHeadings[side][end];

                    // The loop endpoint is the next image of the cycle start. It has no separate slot in the n-frame
                    // arrays, so keep the start image as the stored frame and use the endpoint only for cycle
                    // geometry. This mirrors the exporter window [n, 2n), avoiding an end-frame write that would
                    // overwrite frame zero with the wrong progression.
                    for (int frame = 0; frame < n; frame++)
                    {
                        // Exporter stance boundaries are shared: the boundary frame remains owned by the cycle
                        // that ends there. The sampled offset must use that cycle's anchors because FootPlacer
                        // selects the geometry through canonical.Cycle[frame].
                        if (canonical.Cycle[frame] != k)
                            continue;
                        int extended = frame;
                        if (extended < start)
                            extended += n;
                        if (extended > end)
                            continue;
                        float referenceProgression = sample.Progression[frame];
                        if (!IsFinite(referenceProgression))
                            referenceProgression = 0f;
                        // A one-cycle loop can own both images of frame zero. Preserve the authored image selected
                        // by the canonical clock: progression near one denotes the ending stance, while progression
                        // near zero denotes the starting stance.
                        if (frame == Wrap(sourceCycle.StartFrame, n) && extended == start && referenceProgression > 0.5f)
                            extended = end;
                        Vector3 baseline = sourceStart + (sourceEnd - sourceStart) * referenceProgression;
                        Vector3 residual = _worldSoles[side][extended] - baseline;
                        sample.Offset[frame] = ToStrideOffset(residual, _worldSoles[side][extended].y - sample.Floor,
                            axisSin, axisCos);
                        sample.RotationOffset[frame] = _worldHeadings[side][extended] -
                            (startHeading + (endHeading - startHeading) * referenceProgression);
                    }

                    int startFrame = Wrap(sourceCycle.StartFrame, n);
                    float rootYawAtStart = _rootYawDegrees[start];
                    outputCycle.StartFrame = sourceCycle.StartFrame;
                    outputCycle.EndFrame = sourceCycle.EndFrame;
                    outputCycle.StrikeFrame = sourceCycle.StrikeFrame;
                    outputCycle.LiftCycle = sourceCycle.LiftCycle;
                    outputCycle.OffCycle = sourceCycle.OffCycle;
                    outputCycle.StrikeCycle = sourceCycle.StrikeCycle;
                    outputCycle.LandCycle = sourceCycle.LandCycle;
                    outputCycle.VirtualStart = sourceCycle.VirtualStart;
                    outputCycle.VirtualEnd = sourceCycle.VirtualEnd;
                    outputCycle.Stationary = length < MinStrideLength;
                    outputCycle.StrideLength = length;
                    outputCycle.StrideYaw = Mathf.DeltaAngle(axisYaw, rootYawAtStart) * -1f;
                    outputCycle.RotationChange = endHeading - startHeading;
                    outputCycle.StancePosition = sample.Footbase[startFrame];
                    outputCycle.StanceDirection = sample.Heading[startFrame];

                    Vector3 fromEnd = sourceStart - sourceEnd;
                    outputCycle.ToStrideStartPos = RotateWorldToRoot(fromEnd, _rootYawDegrees[end]);
                    outputCycle.HasMidpoint = sourceCycle.HasMidpoint;
                    outputCycle.MiddleFrame = sourceCycle.MiddleFrame;
                    int middle = MiddleIndex(sourceCycle.MiddleFrame, start, end, n);
                    int middleFrame = Wrap(middle, n);
                    float middleProgression = sample.Progression[middleFrame];
                    if (!IsFinite(middleProgression))
                        middleProgression = 0f;
                    Vector3 middleBaseline = sourceStart + (sourceEnd - sourceStart) * middleProgression;
                    Vector3 middleResidual = _worldSoles[side][middle] - middleBaseline;
                    outputCycle.MiddlePosition = sample.Footbase[middleFrame];
                    outputCycle.MiddleOffset = ToStrideOffset(middleResidual, _worldSoles[side][middle].y - sample.Floor,
                        axisSin, axisCos);
                    outputCycle.MiddleProgression = sample.Progression[middleFrame];
                }
            }
        }

        private static Vector3 ToStrideOffset(Vector3 residual, float y, float axisSin, float axisCos)
        {
            float localX = residual.x * axisCos - residual.z * axisSin;
            float localZ = residual.x * axisSin + residual.z * axisCos;
            return new Vector3(localX, y, localZ);
        }

        private static bool HasStrideData(PoseClip clip)
        {
            if (clip == null || clip.Frames <= 0 || clip.Stride == null || clip.Stride.Length < 2 ||
                clip.RootVelocity == null || clip.RootVelocity.Length < clip.Frames)
                return false;
            for (int side = 0; side < 2; side++)
            {
                StrideFoot foot = clip.Stride[side];
                if (foot == null || foot.Cycles == null || foot.Cycles.Length == 0 || foot.Cycle == null ||
                    foot.Cycle.Length < clip.Frames || foot.Progression == null || foot.Progression.Length < clip.Frames ||
                    foot.Offset == null || foot.Offset.Length < clip.Frames || foot.RotationOffset == null ||
                    foot.RotationOffset.Length < clip.Frames || foot.Footbase == null || foot.Footbase.Length < clip.Frames ||
                    foot.Heading == null || foot.Heading.Length < clip.Frames || foot.Grounded == null ||
                    foot.Grounded.Length < clip.Frames)
                    return false;
                for (int cycle = 0; cycle < foot.Cycles.Length; cycle++)
                    if (foot.Cycles[cycle] == null)
                        return false;
            }
            if (clip.Contact != null)
            {
                if (clip.Contact.Length < 2 || clip.Contact[0] == null || clip.Contact[1] == null ||
                    clip.Contact[0].Length < clip.Frames || clip.Contact[1].Length < clip.Frames)
                    return false;
            }
            return true;
        }

        private static bool HasContacts(PoseClip clip)
        {
            return clip.Contact != null && clip.Contact.Length >= 2 && clip.Contact[0] != null && clip.Contact[1] != null &&
                clip.Contact[0].Length >= clip.Frames && clip.Contact[1].Length >= clip.Frames;
        }

        private static Vector2 RootVelocityAt(PoseClip clip, int frame)
        {
            return clip.RootVelocity != null && frame >= 0 && frame < clip.RootVelocity.Length
                ? clip.RootVelocity[frame]
                : Vector2.zero;
        }

        private static float RootSpeedAt(PoseClip clip, int frame)
        {
            Vector2 velocity = RootVelocityAt(clip, frame);
            return velocity.magnitude;
        }

        private static float UnwrappedYawAt(PoseClip clip, int rawFrame)
        {
            if (clip == null || clip.Frames <= 0)
                return 0f;
            int period = FloorDivide(rawFrame, clip.Frames);
            int frame = rawFrame - period * clip.Frames;
            float loopYaw = clip.Frames > 1 ? YawAt(clip, clip.Frames - 1) - YawAt(clip, 0) : 0f;
            return YawAt(clip, frame) + period * loopYaw;
        }

        private static float YawAt(PoseClip clip, int frame)
        {
            return clip.YawProgress != null && frame >= 0 && frame < clip.YawProgress.Length && IsFinite(clip.YawProgress[frame])
                ? clip.YawProgress[frame]
                : 0f;
        }

        private static Vector3 PelvisAt(PoseClip clip, int frame)
        {
            return clip.PelvisPosition != null && frame >= 0 && frame < clip.PelvisPosition.Length
                ? clip.PelvisPosition[frame]
                : Vector3.zero;
        }

        private static Vector3 FootAt(PoseClip clip, int frame, int side)
        {
            Vector3[] feet = side == 0 ? clip.FootL : clip.FootR;
            return feet != null && frame >= 0 && frame < feet.Length ? feet[frame] : Vector3.zero;
        }

        private static Vector2 Lerp(Vector2 a, Vector2 b, float t)
        {
            return a + (b - a) * t;
        }

        private static Vector3 BlendVector(Vector3 a, Vector3 b, float t)
        {
            return a + (b - a) * t;
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        private static float LerpAngle(float a, float b, float t)
        {
            return a + Mathf.DeltaAngle(a, b) * t;
        }

        private static Vector3 RotateRootLocal(Vector3 local, float yawDegrees)
        {
            float radians = yawDegrees * Mathf.Deg2Rad;
            float sin = Mathf.Sin(radians);
            float cos = Mathf.Cos(radians);
            return new Vector3(local.x * cos + local.z * sin, local.y, -local.x * sin + local.z * cos);
        }

        private static Vector3 RotateWorldToRoot(Vector3 world, float yawDegrees)
        {
            float radians = yawDegrees * Mathf.Deg2Rad;
            float sin = Mathf.Sin(radians);
            float cos = Mathf.Cos(radians);
            return new Vector3(world.x * cos - world.z * sin, world.y, world.x * sin + world.z * cos);
        }

        private static int ExtendedIndex(int frame, int n)
        {
            if (n <= 0)
                return 0;
            int result = frame;
            while (result < 0)
                result += n;
            while (result > 2 * n)
                result -= n;
            return Mathf.Clamp(result, 0, 2 * n);
        }

        private static int MiddleIndex(int frame, int start, int end, int n)
        {
            int result = ExtendedIndex(frame, n);
            while (result < start && result + n <= 2 * n)
                result += n;
            while (result > end && result - n >= 0)
                result -= n;
            return Mathf.Clamp(result, start, end);
        }

        private static int Wrap(int frame, int n)
        {
            if (n <= 0)
                return 0;
            int result = frame % n;
            return result < 0 ? result + n : result;
        }

        private static int FrameDelta(int a, int b, int n)
        {
            int delta = Wrap(a - b, n);
            return delta > n / 2 ? delta - n : delta;
        }

        private static int FloorDivide(int value, int divisor)
        {
            if (divisor <= 0)
                return 0;
            int quotient = value / divisor;
            int remainder = value % divisor;
            return remainder < 0 ? quotient - 1 : quotient;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
