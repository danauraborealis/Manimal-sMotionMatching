using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ZLinq;

namespace Manimal.MotionMatching
{
    internal sealed class FidgetGestureClip
    {
        
        private const int FingerCount = 15;
        private const int Digits = 5;
        private const int Segments = 3;
        private const int MaximumSampleCount = 60000;
        private const float UnitQuaternionTolerance = 0.001f;
        private const float NeutralPositionTolerance = 0.00001f;
        private const float NeutralRotationToleranceDegrees = 0.01f;

        private readonly Vector3[] _handPositions;
        private readonly Quaternion[] _handRotations;
        private readonly Quaternion[][] _fingerRotations;
        private readonly float _fps;
        private readonly float _duration;

        private FidgetGestureClip(
            Vector3[] handPositions,
            Quaternion[] handRotations,
            Quaternion[][] fingerRotations,
            float fps)
        {
            _handPositions = handPositions;
            _handRotations = handRotations;
            _fingerRotations = fingerRotations;
            _fps = fps;
            _duration = (handPositions.Length - 1) / fps;
        }

        internal float Duration => _duration;

        internal static FidgetGestureClip Load(string action = "fidget2", string group = "")
        {
            string prefix = string.IsNullOrEmpty(group) ? "" : group + ".";
            string resourceName = $"Manimal.MotionMatching.Data.{prefix}{action}.gesture.json";
            using var stream = typeof(FidgetGestureClip).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException("Embedded fidget gesture resource is missing: " + resourceName);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            if ((string)JObject.Parse(json)["action"] != action) throw Invalid("action mismatch");
            return Parse(json);
        }

        internal static FidgetGestureClip Parse(string json)
        {
            JObject data = ParseObject(json);
            if (ReadRequiredInt(data, "schemaVersion") != 1)
                throw Invalid("unsupported schemaVersion; expected 1");
            string action = ReadRequiredString(data, "action");
            if (action != "fidget" && action != "fidget1" && action != "fidget2" && action != "fidget3"
                && action != "fidget 1" && action != "fidget 2" && action != "fidget 3")
                throw Invalid("unsupported fidget action");
            if (ReadRequiredString(data, "coordinateSystem") != "anatomical-local")
                throw Invalid("unsupported coordinateSystem; expected anatomical-local");

            // The source hash is provenance metadata. The runtime does not need to
            // recompute it, but a missing or empty value is still malformed data.
            ReadRequiredString(data, "sourceSha256");

            float fps = ReadFiniteFloat(Require(data, "fps"), "fps");
            if (fps <= 0f || fps > 1000f)
                throw Invalid("fps must be finite and in the range (0, 1000]");

            JObject hand = RequireObject(Require(data, "hand"), "hand");
            JArray handSamples = RequireArray(Require(hand, "samples"), "hand.samples");
            ValidateSampleCount(handSamples.Count, "hand.samples");
            var handSamplesParsed = handSamples.AsValueEnumerable()
                .Select(token => ReadHandSample(token))
                .ToArray();
            var handPositions = handSamplesParsed.AsValueEnumerable()
                .Select(sample => sample.Position)
                .ToArray();
            var handRotations = handSamplesParsed.AsValueEnumerable()
                .Select(sample => sample.Rotation)
                .ToArray();
            ValidateHandEndpoints(handPositions, handRotations);

            JArray fingers = RequireArray(Require(data, "fingers"), "fingers");
            if (fingers.Count != FingerCount)
                throw Invalid("fingers must contain exactly 15 tracks");
            var parsedFingers = fingers.AsValueEnumerable()
                .Select(token => ReadFingerTrack(token, handSamples.Count))
                .ToArray();

            var keys = parsedFingers.AsValueEnumerable()
                .Select(finger => finger.Digit * Segments + finger.Segment)
                .ToArray();
            if (keys.AsValueEnumerable().Distinct().Count() != FingerCount)
                throw Invalid("fingers must contain every digit/segment key exactly once");
            var fingerRotations = parsedFingers.AsValueEnumerable()
                .OrderBy(finger => finger.Digit * Segments + finger.Segment)
                .Select(finger => finger.Rotations)
                .ToArray();

            return new FidgetGestureClip(handPositions, handRotations, fingerRotations, fps);
        }

        internal void SampleHand(float seconds, out Vector3 position, out Quaternion rotation)
        {
            float frame = FrameAt(seconds);
            int left = Mathf.FloorToInt(frame);
            int right = Math.Min(left + 1, _handPositions.Length - 1);
            float blend = frame - left;
            position = Vector3.LerpUnclamped(_handPositions[left], _handPositions[right], blend);
            rotation = Quaternion.SlerpUnclamped(_handRotations[left], _handRotations[right], blend);
        }

        internal Quaternion SampleFinger(int digit, int segment, float seconds)
        {
            if (digit < 0 || digit >= Digits)
                throw new ArgumentOutOfRangeException(nameof(digit), "Finger digit must be in the range 0..4");
            if (segment < 0 || segment >= Segments)
                throw new ArgumentOutOfRangeException(nameof(segment), "Finger segment must be in the range 0..2");

            Quaternion[] rotations = _fingerRotations[digit * Segments + segment];
            float frame = FrameAt(seconds);
            int left = Mathf.FloorToInt(frame);
            int right = Math.Min(left + 1, rotations.Length - 1);
            return Quaternion.SlerpUnclamped(rotations[left], rotations[right], frame - left);
        }

        private float FrameAt(float seconds)
        {
            if (float.IsNaN(seconds))
                throw new ArgumentOutOfRangeException(nameof(seconds), "Sample time must be finite");
            if (float.IsNegativeInfinity(seconds) || seconds <= 0f)
                return 0f;
            if (float.IsPositiveInfinity(seconds) || seconds >= _duration)
                return _handPositions.Length - 1;
            return seconds * _fps;
        }

        private static JObject ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw Invalid("JSON is empty");
            try
            {
                return JObject.Parse(json);
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("Invalid fidget gesture JSON: " + error.Message, error);
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException("Invalid fidget gesture JSON: " + error.Message, error);
            }
        }

        private static HandSample ReadHandSample(JToken token)
        {
            JObject sample = RequireObject(token, "hand sample");
            return new HandSample(
                ReadPosition(Require(sample, "position"), "hand sample position"),
                ReadRotation(Require(sample, "rotation"), "hand sample rotation"));
        }

        private static FingerTrack ReadFingerTrack(JToken token, int expectedCount)
        {
            JObject finger = RequireObject(token, "finger track");
            int digit = ReadRequiredInt(finger, "digit");
            int segment = ReadRequiredInt(finger, "segment");
            if (digit < 0 || digit >= Digits)
                throw Invalid($"finger digit must be in the range 0..4; got {digit}");
            if (segment < 0 || segment >= Segments)
                throw Invalid($"finger segment must be in the range 0..2; got {segment}");

            JArray samples = RequireArray(Require(finger, "samples"), $"finger {digit}:{segment} samples");
            if (samples.Count != expectedCount)
                throw Invalid($"finger {digit}:{segment} sample count {samples.Count} differs from hand count {expectedCount}");
            var rotations = samples.AsValueEnumerable()
                .Select(sample => ReadFingerSample(sample, digit, segment))
                .ToArray();
            if (!IsNeutral(rotations[0]) || !IsNeutral(rotations[rotations.Length - 1]))
                throw Invalid($"finger {digit}:{segment} must begin and end at neutral");
            return new FingerTrack(digit, segment, rotations);
        }

        private static Quaternion ReadFingerSample(JToken token, int digit, int segment)
        {
            JObject sample = RequireObject(token, $"finger {digit}:{segment} sample");
            return ReadRotation(Require(sample, "rotation"), $"finger {digit}:{segment} rotation");
        }

        private static Vector3 ReadPosition(JToken token, string description)
        {
            float[] values = ReadArray(token, 3, description);
            return new Vector3(values[0], values[1], values[2]);
        }

        private static Quaternion ReadRotation(JToken token, string description)
        {
            float[] values = ReadArray(token, 4, description);
            Quaternion rotation = new Quaternion(values[0], values[1], values[2], values[3]);
            float normSquared = Quaternion.Dot(rotation, rotation);
            if (!Finite(normSquared) || Math.Abs(normSquared - 1f) > UnitQuaternionTolerance)
                throw Invalid(description + " must be a unit quaternion within tolerance 0.001");
            return rotation.normalized;
        }

        private static float[] ReadArray(JToken token, int count, string description)
        {
            JArray array = token as JArray;
            if (array == null || array.Count != count)
                throw Invalid(description + " must be an array of " + count + " finite numbers");
            return array.AsValueEnumerable()
                .Select(value => ReadFiniteFloat(value, description))
                .ToArray();
        }

        private static void ValidateHandEndpoints(Vector3[] positions, Quaternion[] rotations)
        {
            int last = positions.Length - 1;
            if (!IsNeutral(positions[0]) || !IsNeutral(rotations[0]))
                throw Invalid("hand must begin at neutral");
            if (!IsNeutral(positions[last]) || !IsNeutral(rotations[last]))
                throw Invalid("hand must end at neutral");
        }

        private static bool IsNeutral(Vector3 position)
        {
            float squared = position.sqrMagnitude;
            return Finite(squared) && squared <= NeutralPositionTolerance * NeutralPositionTolerance;
        }

        private static bool IsNeutral(Quaternion rotation)
            => Finite(rotation) && Quaternion.Angle(rotation, Quaternion.identity) <= NeutralRotationToleranceDegrees;

        private static void ValidateSampleCount(int count, string description)
        {
            if (count < 2 || count > MaximumSampleCount)
                throw Invalid(description + " must contain between 2 and " + MaximumSampleCount + " samples");
        }

        private static JToken Require(JObject parent, string name)
        {
            JToken token = parent[name];
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                throw Invalid("missing " + name);
            return token;
        }

        private static JObject RequireObject(JToken token, string description)
        {
            JObject value = token as JObject;
            if (value == null)
                throw Invalid(description + " must be an object");
            return value;
        }

        private static JArray RequireArray(JToken token, string description)
        {
            JArray value = token as JArray;
            if (value == null)
                throw Invalid(description + " must be an array");
            return value;
        }

        private static int ReadRequiredInt(JObject parent, string name)
        {
            JToken token = Require(parent, name);
            if (token.Type != JTokenType.Integer)
                throw Invalid(name + " must be an integer");
            try
            {
                long value = token.Value<long>();
                if (value < int.MinValue || value > int.MaxValue)
                    throw Invalid(name + " is outside the supported integer range");
                return (int)value;
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new InvalidDataException("Invalid fidget gesture " + name + ": " + error.Message, error);
            }
        }

        private static string ReadRequiredString(JObject parent, string name)
        {
            JToken token = Require(parent, name);
            if (token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token))
                throw Invalid(name + " must be a non-empty string");
            return (string)token;
        }

        private static float ReadFiniteFloat(JToken token, string description)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
                throw Invalid(description + " contains a non-numeric value");
            float value;
            try
            {
                value = token.Value<float>();
            }
            catch (Exception error)
            {
                throw new InvalidDataException("Invalid fidget gesture " + description + ": " + error.Message, error);
            }
            if (!Finite(value))
                throw Invalid(description + " contains a non-finite value");
            return value;
        }

        private static bool Finite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool Finite(Vector3 value)
            => Finite(value.x) && Finite(value.y) && Finite(value.z);

        private static bool Finite(Quaternion value)
            => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);

        private static InvalidDataException Invalid(string message)
            => new InvalidDataException("Invalid fidget gesture: " + message);

        private sealed class HandSample
        {
            internal readonly Vector3 Position;
            internal readonly Quaternion Rotation;

            internal HandSample(Vector3 position, Quaternion rotation)
            {
                Position = position;
                Rotation = rotation;
            }
        }

        private sealed class FingerTrack
        {
            internal readonly int Digit;
            internal readonly int Segment;
            internal readonly Quaternion[] Rotations;

            internal FingerTrack(int digit, int segment, Quaternion[] rotations)
            {
                Digit = digit;
                Segment = segment;
                Rotations = rotations;
            }
        }
    }
}


