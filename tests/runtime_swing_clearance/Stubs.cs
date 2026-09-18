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

        public static Vector3 operator *(Vector3 value, float scale)
        {
            return new Vector3(value.x * scale, value.y * scale, value.z * scale);
        }
    }
}
