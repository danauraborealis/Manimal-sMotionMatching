using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ZLinq;

namespace Manimal.MotionMatching
{
    internal sealed class FidgetClip
    {
        private readonly Vector3[] _positions;
        private readonly Quaternion[] _rotations;
        private readonly float _fps;
        internal float Duration => (_positions.Length - 1) / _fps;

        private FidgetClip(Vector3[] positions, Quaternion[] rotations, float fps)
        { _positions = positions; _rotations = rotations; _fps = fps; }

        internal static FidgetClip Load(string action = "fidget2", string group = "")
        {
            string prefix = string.IsNullOrEmpty(group) ? "" : group + ".";
            using var stream = typeof(FidgetClip).Assembly.GetManifestResourceStream($"Manimal.MotionMatching.Data.{prefix}{action}.weapon.json")
                ?? throw new InvalidDataException($"Embedded {action} clip missing");
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            if ((string)JObject.Parse(json)["action"] != action) throw new InvalidDataException("Fidget action mismatch");
            return Parse(json);
        }

        internal static FidgetClip Parse(string json)
        {
            var data = JObject.Parse(json);
            if ((int?)data["schemaVersion"] != 1 || (string)data["coordinateSystem"] != "unity-camera")
                throw new InvalidDataException("Unsupported fidget coordinate system/schema");
            float fps = (float?)data["fps"] ?? 0;
            if (!Finite(fps) || fps <= 0 || fps > 1000) throw new InvalidDataException("Invalid fidget frame rate");
            var samples = data["samples"] as JArray;
            if (samples == null || samples.Count < 2 || samples.Count > 60000)
                throw new InvalidDataException("Invalid fidget samples");
            var positions = samples.AsValueEnumerable().Select(s => ReadPosition(s["position"])).ToArray();
            var rotations = samples.AsValueEnumerable().Select(s => ReadRotation(s["rotation"])).ToArray();
            if (positions[0].sqrMagnitude > 1e-8f || Quaternion.Angle(rotations[0], Quaternion.identity) > 0.01f)
                throw new InvalidDataException("Fidget must begin at neutral");
            if (positions[positions.Length - 1].sqrMagnitude > 1e-8f || Quaternion.Angle(rotations[rotations.Length - 1], Quaternion.identity) > 0.01f)
                throw new InvalidDataException("Fidget must end at neutral");
            return new FidgetClip(positions, rotations, fps);
        }

        internal void Sample(float seconds, out Vector3 position, out Quaternion rotation)
        {
            float frame = Mathf.Clamp(seconds * _fps, 0, _positions.Length - 1);
            int left = Mathf.FloorToInt(frame), right = Math.Min(left + 1, _positions.Length - 1);
            float blend = frame - left;
            position = Vector3.LerpUnclamped(_positions[left], _positions[right], blend);
            rotation = Quaternion.SlerpUnclamped(_rotations[left], _rotations[right], blend);
        }

        private static float[] ReadArray(JToken token, int count)
        {
            if (!(token is JArray array) || array.Count != count)
                throw new InvalidDataException("Malformed fidget transform");
            var values = array.AsValueEnumerable().Select(t => (float)t).ToArray();
            if (values.AsValueEnumerable().Any(v => !Finite(v))) throw new InvalidDataException("Non-finite fidget transform");
            return values;
        }
        private static Vector3 ReadPosition(JToken token)
        { var v = ReadArray(token, 3); return new Vector3(v[0], v[1], v[2]); }
        private static Quaternion ReadRotation(JToken token)
        {
            var v = ReadArray(token, 4);
            var q = new Quaternion(v[0], v[1], v[2], v[3]);
            float norm = Quaternion.Dot(q, q);
            if (Math.Abs(norm - 1) > 0.001f) throw new InvalidDataException("Non-unit fidget rotation");
            return q.normalized;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

