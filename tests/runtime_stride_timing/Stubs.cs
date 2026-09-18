// StrideData.cs also contains the JSON loader and Unity vector fields. The timing tests only need these tiny shapes,
// so this harness stays dependency-free and compiles the production StrideCycle implementation directly.
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
    }
}

namespace Newtonsoft.Json.Linq
{
    public class JToken
    {
        public JToken this[string key] => null;
        public JToken this[int index] => null;
        public T ToObject<T>() => default(T);

        public static explicit operator int(JToken token) => 0;
        public static explicit operator float(JToken token) => 0f;
        public static explicit operator bool(JToken token) => false;
    }

    public class JObject : JToken
    {
    }

    public class JArray : JToken
    {
        public int Count => 0;
    }
}
