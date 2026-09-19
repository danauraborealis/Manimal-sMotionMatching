using System;
using NumericsQuaternion = System.Numerics.Quaternion;
using NumericsVector3 = System.Numerics.Vector3;

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
        public float magnitude => MathF.Sqrt(sqrMagnitude);

        public Vector3 normalized
        {
            get
            {
                float length = magnitude;
                return length > 1e-8f ? this / length : zero;
            }
        }

        public static Vector3 operator +(Vector3 left, Vector3 right)
            => new Vector3(left.x + right.x, left.y + right.y, left.z + right.z);
        public static Vector3 operator -(Vector3 left, Vector3 right)
            => new Vector3(left.x - right.x, left.y - right.y, left.z - right.z);
        public static Vector3 operator -(Vector3 value)
            => new Vector3(-value.x, -value.y, -value.z);
        public static Vector3 operator *(Vector3 value, float scalar)
            => new Vector3(value.x * scalar, value.y * scalar, value.z * scalar);
        public static Vector3 operator *(float scalar, Vector3 value) => value * scalar;
        public static Vector3 operator /(Vector3 value, float scalar)
            => new Vector3(value.x / scalar, value.y / scalar, value.z / scalar);

        public static float Dot(Vector3 left, Vector3 right)
            => left.x * right.x + left.y * right.y + left.z * right.z;

        public static Vector3 Cross(Vector3 left, Vector3 right)
            => new Vector3(
                left.y * right.z - left.z * right.y,
                left.z * right.x - left.x * right.z,
                left.x * right.y - left.y * right.x);

        public static Vector3 LerpUnclamped(Vector3 left, Vector3 right, float amount)
            => left + (right - left) * amount;

        public override string ToString() => $"({x}, {y}, {z})";
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

        public Quaternion normalized
        {
            get
            {
                float length = MathF.Sqrt(Dot(this, this));
                return length > 1e-8f
                    ? new Quaternion(x / length, y / length, z / length, w / length)
                    : identity;
            }
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
            NumericsVector3 transformed = NumericsVector3.Transform(
                new NumericsVector3(point.x, point.y, point.z),
                new NumericsQuaternion(rotation.x, rotation.y, rotation.z, rotation.w));
            return new Vector3(transformed.X, transformed.Y, transformed.Z);
        }

        public static float Dot(Quaternion left, Quaternion right)
            => left.x * right.x + left.y * right.y + left.z * right.z + left.w * right.w;

        public static Quaternion Inverse(Quaternion value)
        {
            float norm = Dot(value, value);
            return norm > 1e-12f
                ? new Quaternion(-value.x / norm, -value.y / norm, -value.z / norm, value.w / norm)
                : identity;
        }

        public static float Angle(Quaternion left, Quaternion right)
        {
            float dot = MathF.Abs(Dot(left.normalized, right.normalized));
            dot = MathF.Min(1f, MathF.Max(0f, dot));
            return 2f * MathF.Acos(dot) * (180f / MathF.PI);
        }

        public static Quaternion SlerpUnclamped(Quaternion left, Quaternion right, float amount)
        {
            NumericsQuaternion result = NumericsQuaternion.Slerp(
                new NumericsQuaternion(left.x, left.y, left.z, left.w),
                new NumericsQuaternion(right.x, right.y, right.z, right.w),
                amount);
            return new Quaternion(result.X, result.Y, result.Z, result.W).normalized;
        }

        public static Quaternion AngleAxis(float degrees, Vector3 axis)
        {
            Vector3 unit = axis.normalized;
            float half = degrees * MathF.PI / 360f;
            float sine = MathF.Sin(half);
            return new Quaternion(unit.x * sine, unit.y * sine, unit.z * sine, MathF.Cos(half)).normalized;
        }

        public static Quaternion Euler(float x, float y, float z)
            => (AngleAxis(y, Vector3.up) * AngleAxis(x, Vector3.right) * AngleAxis(z, Vector3.forward)).normalized;

        public static Quaternion LookRotation(Vector3 forward, Vector3 upwards)
        {
            Vector3 f = forward.normalized;
            Vector3 u = (upwards - f * Vector3.Dot(upwards, f)).normalized;
            if (f.sqrMagnitude < 1e-12f || u.sqrMagnitude < 1e-12f)
                return identity;
            Vector3 r = Vector3.Cross(u, f).normalized;
            u = Vector3.Cross(f, r).normalized;

            // Columns of this matrix are the world directions of local X/Y/Z.
            float m00 = r.x, m01 = u.x, m02 = f.x;
            float m10 = r.y, m11 = u.y, m12 = f.y;
            float m20 = r.z, m21 = u.z, m22 = f.z;
            float trace = m00 + m11 + m22;
            Quaternion q;
            if (trace > 0f)
            {
                float s = MathF.Sqrt(trace + 1f) * 2f;
                q = new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, .25f * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f;
                q = new Quaternion(.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f;
                q = new Quaternion((m01 + m10) / s, .25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f;
                q = new Quaternion((m02 + m20) / s, (m12 + m21) / s, .25f * s, (m10 - m01) / s);
            }
            return q.normalized;
        }

        public override string ToString() => $"({x}, {y}, {z}, {w})";
    }

    public static class Mathf
    {
        public static float Abs(float value) => MathF.Abs(value);
        public static float Sqrt(float value) => MathF.Sqrt(value);
        public static float Clamp(float value, float minimum, float maximum)
            => value < minimum ? minimum : value > maximum ? maximum : value;
        public static int FloorToInt(float value) => (int)MathF.Floor(value);
    }

    public sealed class Transform
    {
        private Transform _parent;
        private Vector3 _localPosition;
        private Quaternion _localRotation = Quaternion.identity;

        public Transform(string name = null) { this.name = name ?? string.Empty; }

        public string name { get; set; }
        public Transform parent
        {
            get => _parent;
            set
            {
                Vector3 worldPosition = position;
                Quaternion worldRotation = rotation;
                _parent = value;
                position = worldPosition;
                rotation = worldRotation;
            }
        }

        public Vector3 localPosition
        {
            get => _localPosition;
            set => _localPosition = value;
        }

        public Quaternion localRotation
        {
            get => _localRotation;
            set => _localRotation = value;
        }

        public Vector3 position
        {
            get => _parent == null ? _localPosition : _parent.position + _parent.rotation * _localPosition;
            set => _localPosition = _parent == null ? value : Quaternion.Inverse(_parent.rotation) * (value - _parent.position);
        }

        public Quaternion rotation
        {
            get => _parent == null ? _localRotation : _parent.rotation * _localRotation;
            set => _localRotation = _parent == null ? value : Quaternion.Inverse(_parent.rotation) * value;
        }

        public Vector3 forward => rotation * Vector3.forward;
        public Vector3 TransformDirection(Vector3 direction) => rotation * direction;

        public static bool operator ==(Transform left, Transform right) => ReferenceEquals(left, right);
        public static bool operator !=(Transform left, Transform right) => !ReferenceEquals(left, right);
        public static bool operator !(Transform value) => value == null;
        public static bool operator true(Transform value) => value != null;
        public static bool operator false(Transform value) => value == null;
        public static implicit operator bool(Transform value) => value != null;

        public override bool Equals(object obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => base.GetHashCode();
    }
}

namespace EFT
{
    using RootMotion.FinalIK;
    using UnityEngine;

    public sealed class Player
    {
        private LimbIK[] _limbs;
        public PlayerBones PlayerBones;
        public HandPoser[] HandPosers;

        public static Player Create(PlayerBones bones, HandPoser[] posers, LimbIK[] limbs)
            => new Player { PlayerBones = bones, HandPosers = posers, _limbs = limbs };
    }

    public sealed class PlayerBones
    {
        public Transform LeftPalm;
    }

    public sealed class HandPoser
    {
        public Transform[] Children;
    }
}

namespace RootMotion.FinalIK
{
    using UnityEngine;

    public sealed class IKSolverBone
    {
        public Transform transform;
    }

    public sealed class IKSolverLimb
    {
        public IKSolverBone bone3 = new IKSolverBone();
        public float IKPositionWeight = 1f;
        public Vector3 IKPosition;
        public Quaternion IKRotation = Quaternion.identity;
        public Transform target;

        public void SetIKPosition(Vector3 position) => IKPosition = position;
        public void SetIKRotation(Quaternion rotation) => IKRotation = rotation;
    }

    public sealed class LimbIK
    {
        public IKSolverLimb solver = new IKSolverLimb();
    }
}
