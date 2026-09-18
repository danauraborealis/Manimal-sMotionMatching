using System;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 right => new Vector3(1f, 0f, 0f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);

        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);
        public Vector3 normalized
        {
            get
            {
                float length = magnitude;
                return length > 1e-8f ? this / length : zero;
            }
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator -(Vector3 value) => new Vector3(-value.x, -value.y, -value.z);
        public static Vector3 operator *(Vector3 value, float scalar) => new Vector3(value.x * scalar, value.y * scalar, value.z * scalar);
        public static Vector3 operator *(float scalar, Vector3 value) => value * scalar;
        public static Vector3 operator /(Vector3 value, float scalar) => new Vector3(value.x / scalar, value.y / scalar, value.z / scalar);

        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x);
    }

    public struct Quaternion
    {
        public float x, y, z, w;

        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Quaternion identity => new Quaternion(0f, 0f, 0f, 1f);

        public static Quaternion operator *(Quaternion lhs, Quaternion rhs)
        {
            return new Quaternion(
                lhs.w * rhs.x + lhs.x * rhs.w + lhs.y * rhs.z - lhs.z * rhs.y,
                lhs.w * rhs.y - lhs.x * rhs.z + lhs.y * rhs.w + lhs.z * rhs.x,
                lhs.w * rhs.z + lhs.x * rhs.y - lhs.y * rhs.x + lhs.z * rhs.w,
                lhs.w * rhs.w - lhs.x * rhs.x - lhs.y * rhs.y - lhs.z * rhs.z);
        }

        public static Vector3 operator *(Quaternion rotation, Vector3 point)
        {
            Vector3 q = new Vector3(rotation.x, rotation.y, rotation.z);
            Vector3 t = 2f * Vector3.Cross(q, point);
            return point + rotation.w * t + Vector3.Cross(q, t);
        }

        public static Quaternion AngleAxis(float angleDegrees, Vector3 axis)
        {
            Vector3 unit = axis.normalized;
            float half = angleDegrees * Mathf.Deg2Rad * 0.5f;
            float s = (float)Math.Sin(half);
            return new Quaternion(unit.x * s, unit.y * s, unit.z * s, (float)Math.Cos(half));
        }

        public static Quaternion Euler(float x, float y, float z)
        {
            return AngleAxis(y, Vector3.up) * AngleAxis(x, Vector3.right) * AngleAxis(z, Vector3.forward);
        }

        public static Quaternion Inverse(Quaternion value)
        {
            float norm = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
            if (norm < 1e-8f)
                return identity;
            return new Quaternion(-value.x / norm, -value.y / norm, -value.z / norm, value.w / norm);
        }

        public static float Angle(Quaternion a, Quaternion b)
        {
            float aNorm = (float)Math.Sqrt(a.x * a.x + a.y * a.y + a.z * a.z + a.w * a.w);
            float bNorm = (float)Math.Sqrt(b.x * b.x + b.y * b.y + b.z * b.z + b.w * b.w);
            if (aNorm < 1e-8f || bNorm < 1e-8f)
                return 180f;
            float dot = Math.Abs((a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w) / (aNorm * bNorm));
            dot = Math.Max(-1f, Math.Min(1f, dot));
            return 2f * (float)Math.Acos(dot) * Mathf.Rad2Deg;
        }

        public static Quaternion LookRotation(Vector3 forward, Vector3 upwards)
        {
            Vector3 f = forward.normalized;
            Vector3 r = Vector3.Cross(upwards.normalized, f).normalized;
            Vector3 u = Vector3.Cross(f, r).normalized;

            float trace = r.x + u.y + f.z;
            if (trace > 0f)
            {
                float s = (float)Math.Sqrt(trace + 1f) * 2f;
                return new Quaternion((u.z - f.y) / s, (f.x - r.z) / s, (r.y - u.x) / s, 0.25f * s);
            }
            if (r.x > u.y && r.x > f.z)
            {
                float s = (float)Math.Sqrt(1f + r.x - u.y - f.z) * 2f;
                return new Quaternion(0.25f * s, (r.y + u.x) / s, (r.z + f.x) / s, (u.z - f.y) / s);
            }
            if (u.y > f.z)
            {
                float s = (float)Math.Sqrt(1f + u.y - r.x - f.z) * 2f;
                return new Quaternion((r.y + u.x) / s, 0.25f * s, (u.z + f.y) / s, (f.x - r.z) / s);
            }

            float last = (float)Math.Sqrt(1f + f.z - r.x - u.y) * 2f;
            return new Quaternion((r.z + f.x) / last, (u.z + f.y) / last, 0.25f * last, (r.y - u.x) / last);
        }
    }

    public static class Mathf
    {
        public const float PI = (float)Math.PI;
        public const float Deg2Rad = PI / 180f;
        public const float Rad2Deg = 180f / PI;

        public static float Abs(float value) => Math.Abs(value);
        public static float Acos(float value) => (float)Math.Acos(value);
        public static float Cos(float value) => (float)Math.Cos(value);
        public static float Sin(float value) => (float)Math.Sin(value);
        public static float Sqrt(float value) => (float)Math.Sqrt(value);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Min(float a, float b) => Math.Min(a, b);
        public static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
        public static float Clamp01(float value) => Clamp(value, 0f, 1f);
    }

    public sealed class Transform
    {
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
    }
}
