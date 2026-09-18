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

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float scale) => new Vector3(a.x * scale, a.y * scale, a.z * scale);
    }

    public static class Mathf
    {
        public static float Clamp01(float value) => value < 0f ? 0f : (value > 1f ? 1f : value);
        public static float Sqrt(float value) => (float)System.Math.Sqrt(value);
    }
}
