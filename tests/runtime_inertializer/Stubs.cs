using System;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public float magnitude => (float)Math.Sqrt(x * x + y * y + z * z);

        public static Vector3 Cross(Vector3 left, Vector3 right)
        {
            return new Vector3(
                left.y * right.z - left.z * right.y,
                left.z * right.x - left.x * right.z,
                left.x * right.y - left.y * right.x);
        }

        public static Vector3 operator +(Vector3 left, Vector3 right) => new Vector3(left.x + right.x, left.y + right.y, left.z + right.z);
        public static Vector3 operator -(Vector3 left, Vector3 right) => new Vector3(left.x - right.x, left.y - right.y, left.z - right.z);
        public static Vector3 operator *(Vector3 value, float scale) => new Vector3(value.x * scale, value.y * scale, value.z * scale);
        public static Vector3 operator *(float scale, Vector3 value) => value * scale;
        public static Vector3 operator /(Vector3 value, float scale) => new Vector3(value.x / scale, value.y / scale, value.z / scale);

        public override string ToString() => "(" + x + ", " + y + ", " + z + ")";
    }

    public struct Quaternion
    {
        public float x;
        public float y;
        public float z;
        public float w;

        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Quaternion identity => new Quaternion(0f, 0f, 0f, 1f);

        public static Quaternion Inverse(Quaternion rotation)
        {
            float magnitudeSquared = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            return new Quaternion(-rotation.x / magnitudeSquared, -rotation.y / magnitudeSquared, -rotation.z / magnitudeSquared, rotation.w / magnitudeSquared);
        }

        public static Quaternion Slerp(Quaternion from, Quaternion to, float t)
        {
            float dot = from.x * to.x + from.y * to.y + from.z * to.z + from.w * to.w;
            if (dot < 0f)
            {
                to = new Quaternion(-to.x, -to.y, -to.z, -to.w);
                dot = -dot;
            }
            dot = Math.Max(-1f, Math.Min(1f, dot));
            if (dot > 0.9995f)
            {
                Quaternion linear = new Quaternion(
                    from.x + t * (to.x - from.x),
                    from.y + t * (to.y - from.y),
                    from.z + t * (to.z - from.z),
                    from.w + t * (to.w - from.w));
                return Normalize(linear);
            }

            float angle = (float)Math.Acos(dot);
            float denominator = (float)Math.Sin(angle);
            float leftWeight = (float)Math.Sin((1f - t) * angle) / denominator;
            float rightWeight = (float)Math.Sin(t * angle) / denominator;
            return new Quaternion(
                from.x * leftWeight + to.x * rightWeight,
                from.y * leftWeight + to.y * rightWeight,
                from.z * leftWeight + to.z * rightWeight,
                from.w * leftWeight + to.w * rightWeight);
        }

        public static Quaternion operator *(Quaternion left, Quaternion right)
        {
            return new Quaternion(
                left.w * right.x + left.x * right.w + left.y * right.z - left.z * right.y,
                left.w * right.y - left.x * right.z + left.y * right.w + left.z * right.x,
                left.w * right.z + left.x * right.y - left.y * right.x + left.z * right.w,
                left.w * right.w - left.x * right.x - left.y * right.y - left.z * right.z);
        }

        public static Vector3 operator *(Quaternion rotation, Vector3 point)
        {
            Quaternion vector = new Quaternion(point.x, point.y, point.z, 0f);
            Quaternion rotated = rotation * vector * Inverse(rotation);
            return new Vector3(rotated.x, rotated.y, rotated.z);
        }

        private static Quaternion Normalize(Quaternion value)
        {
            float magnitude = (float)Math.Sqrt(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            return new Quaternion(value.x / magnitude, value.y / magnitude, value.z / magnitude, value.w / magnitude);
        }
    }

    public static class Mathf
    {
        public static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }
    }
}
