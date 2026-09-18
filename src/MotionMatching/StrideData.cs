using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // one foot cycle: stance to stance, as tools/stride_data.py cut it (Valve's foot cycle definition)
    internal sealed class StrideCycle
    {
        public int StartFrame;
        public int EndFrame;
        public int StrikeFrame;
        public Vector3 StancePosition;
        public float StanceDirection;
        public float StrideLength;
        public float StrideYaw;
        public float RotationChange;
        public Vector3 ToStrideStartPos;
        // Midpoint trajectory sample. MiddleFrame is deliberately unwrapped
        // for loop cycles (it may be greater than the clip frame count).
        public bool HasMidpoint;
        public int MiddleFrame;
        public Vector3 MiddlePosition;
        public Vector3 MiddleOffset;
        public float MiddleProgression;
        public float LiftCycle, OffCycle, StrikeCycle, LandCycle;
        public bool Stationary;
        public bool VirtualStart;
        public bool VirtualEnd;

        // Event fractions are measured in clip time, while a frame's progression is a spatial projection and can
        // legitimately overshoot or go backwards. `clipFrame` is the frame used for this sample after the caller's
        // same-stride interpolation decision; unwrap it once for a loop cycle that crosses the clip boundary.
        public float CycleTime(float clipFrame, int period, bool loop)
        {
            float unwrapped = clipFrame;
            if (loop && period > 0 && unwrapped < StartFrame)
                unwrapped += period;
            int span = EndFrame - StartFrame;
            return span > 0 ? (unwrapped - StartFrame) / span : 0f;
        }

        // Lift/strike define the authored airborne interval. The upper edge is exclusive, matching the exporter.
        public bool InAuthoredSwing(float cycleTime) => cycleTime >= LiftCycle && cycleTime < StrikeCycle;
    }

    // per-foot stride data for a clip: cycles plus the per-frame stride-relative trajectory
    internal sealed class StrideFoot
    {
        public StrideCycle[] Cycles;
        public int[] Cycle;
        public float[] Progression;
        public Vector3[] Offset;
        public float[] RotationOffset;
        // character-local FootBase (x, y, z) and heading in degrees, per frame
        public Vector3[] Footbase;
        public float[] Heading;
        public bool[] Grounded;
        public float Floor;

        public static StrideFoot Parse(JObject o, int frames)
        {
            var foot = new StrideFoot { Floor = o["floor"] == null ? 0f : (float)o["floor"] };
            var cycles = (JArray)o["cycles"];
            foot.Cycles = new StrideCycle[cycles.Count];
            for (int i = 0; i < cycles.Count; i++)
            {
                var c = (JObject)cycles[i];
                foot.Cycles[i] = new StrideCycle
                {
                    StartFrame = (int)c["startFrame"],
                    EndFrame = (int)c["endFrame"],
                    StrikeFrame = (int)c["strikeFrame"],
                    StancePosition = ToVector((JArray)c["stancePosition"]),
                    StanceDirection = (float)c["stanceDirection"],
                    StrideLength = (float)c["strideLength"],
                    StrideYaw = (float)c["strideYaw"],
                    RotationChange = (float)c["rotationChange"],
                    ToStrideStartPos = ToVector((JArray)c["toStrideStartPos"]),
                    HasMidpoint = c["hasMidpoint"] == null || (bool)c["hasMidpoint"],
                    MiddleFrame = c["middleFrame"] == null
                        ? ((int)c["startFrame"] + (int)c["endFrame"]) / 2
                        : (int)c["middleFrame"],
                    MiddlePosition = c["middlePosition"] == null
                        ? new Vector3()
                        : ToVector((JArray)c["middlePosition"]),
                    MiddleOffset = c["middleOffset"] == null
                        ? new Vector3()
                        : ToVector((JArray)c["middleOffset"]),
                    MiddleProgression = c["middleProgression"] == null
                        ? 0.5f
                        : (float)c["middleProgression"],
                    LiftCycle = (float)c["footLiftCycle"],
                    OffCycle = (float)c["footOffCycle"],
                    StrikeCycle = (float)c["footStrikeCycle"],
                    LandCycle = (float)c["footLandCycle"],
                    Stationary = (bool)c["stationary"],
                    VirtualStart = (bool)c["virtualStart"],
                    VirtualEnd = (bool)c["virtualEnd"]
                };
            }
            var f = (JObject)o["frames"];
            foot.Cycle = f["cycle"].ToObject<int[]>();
            foot.Progression = f["progression"].ToObject<float[]>();
            foot.RotationOffset = f["rotationOffset"].ToObject<float[]>();
            var offsets = (JArray)f["translationOffset"];
            var bases = (JArray)f["footbase"];
            var grounded = (JArray)f["grounded"];
            foot.Offset = new Vector3[frames];
            foot.Footbase = new Vector3[frames];
            foot.Heading = new float[frames];
            foot.Grounded = new bool[frames];
            for (int i = 0; i < frames; i++)
            {
                foot.Offset[i] = ToVector((JArray)offsets[i]);
                var b = (JArray)bases[i];
                foot.Footbase[i] = new Vector3((float)b[0], (float)b[1], (float)b[2]);
                foot.Heading[i] = (float)b[3];
                foot.Grounded[i] = (int)grounded[i] != 0;
            }
            // Older databases already contain middleOffset/middleProgression
            // but predate the explicit frame and position fields. Derive both
            // from the loaded character-local FootBase array. Keep the frame
            // unwrapped and wrap only for the array lookup.
            for (int i = 0; i < foot.Cycles.Length; i++)
            {
                var cycle = foot.Cycles[i];
                var midpointToken = ((JObject)cycles[i])["hasMidpoint"];
                bool explicitlyDisabled = midpointToken != null && !(bool)midpointToken;
                if (explicitlyDisabled)
                {
                    foot.Cycles[i] = cycle;
                    continue;
                }
                bool hasFrame = ((JObject)cycles[i])["middleFrame"] != null;
                bool hasPosition = ((JObject)cycles[i])["middlePosition"] != null;
                if (!hasFrame)
                    cycle.MiddleFrame = cycle.StartFrame + (cycle.EndFrame - cycle.StartFrame) / 2;
                int middleIndex = cycle.MiddleFrame;
                if (foot.Footbase.Length > 0)
                {
                    middleIndex %= foot.Footbase.Length;
                    if (middleIndex < 0)
                        middleIndex += foot.Footbase.Length;
                    if (!hasPosition)
                        cycle.MiddlePosition = foot.Footbase[middleIndex];
                    if (((JObject)cycles[i])["middleOffset"] == null && middleIndex < foot.Offset.Length)
                        cycle.MiddleOffset = foot.Offset[middleIndex];
                    if (((JObject)cycles[i])["middleProgression"] == null && middleIndex < foot.Progression.Length)
                        cycle.MiddleProgression = foot.Progression[middleIndex];
                    cycle.HasMidpoint = true;
                }
                else if (midpointToken == null)
                {
                    cycle.HasMidpoint = false;
                }
                foot.Cycles[i] = cycle;
            }
            return foot;
        }

        // Kept as a local helper so the loader remains compatible with the
        // small JSON token stubs used by the runtime timing harness.
        private static Vector3 ToVector(JArray a) => new Vector3((float)a[0], (float)a[1], (float)a[2]);
    }
}
