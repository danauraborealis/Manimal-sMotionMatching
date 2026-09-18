using System;
using System.Collections.Generic;
using System.IO;
using AnimationSystem;
using EFT;
using Newtonsoft.Json;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // writes a bot body's bone hierarchy plus avatar info, so retargeting in blender works against the real rig
    internal static class SkeletonDump
    {
        public static string Write(Player player, string directory)
        {
            var wrapper = player.BodyAnimatorCommon as UnityAnimatorWrapper;
            Animator animator = wrapper?.Animator;
            if (!animator)
                throw new InvalidOperationException("Body animator is not a UnityAnimatorWrapper with an Animator.");

            var bones = new List<BoneRecord>();
            var index = new Dictionary<Transform, int>();
            Collect(animator.transform, -1, bones, index);

            var references = player.Grounder?.ik?.references;
            var document = new SkeletonDocument
            {
                Schema = "manimal.motionmatching.skeleton.v1",
                Profile = player.Profile?.Info?.Settings?.Role.ToString(),
                AnimatorObject = animator.gameObject.name,
                // pose is whatever the animator produced this frame, not a bind pose; the avatar skeleton below is the rest pose
                CapturedAt = "current animated pose",
                Avatar = DescribeAvatar(animator.avatar),
                References = references == null ? null : new Dictionary<string, string>
                {
                    ["root"] = references.root ? references.root.name : null,
                    ["pelvis"] = references.pelvis ? references.pelvis.name : null,
                    ["leftThigh"] = references.leftThigh ? references.leftThigh.name : null,
                    ["leftCalf"] = references.leftCalf ? references.leftCalf.name : null,
                    ["leftFoot"] = references.leftFoot ? references.leftFoot.name : null,
                    ["rightThigh"] = references.rightThigh ? references.rightThigh.name : null,
                    ["rightCalf"] = references.rightCalf ? references.rightCalf.name : null,
                    ["rightFoot"] = references.rightFoot ? references.rightFoot.name : null,
                },
                Bones = bones,
                BindPose = DescribeBindPose(animator.transform)
            };

            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "skeleton-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
            File.WriteAllText(path, JsonConvert.SerializeObject(document, Formatting.Indented));
            return path;
        }

        private static void Collect(Transform transform, int parent, List<BoneRecord> bones, Dictionary<Transform, int> index)
        {
            int me = bones.Count;
            index[transform] = me;
            bones.Add(new BoneRecord
            {
                Name = transform.name,
                Parent = parent,
                LocalPosition = V(transform.localPosition),
                LocalRotation = Q(transform.localRotation),
                LocalScale = V(transform.localScale),
                WorldPosition = V(transform.position),
                WorldRotation = Q(transform.rotation)
            });
            for (int i = 0; i < transform.childCount; i++)
                Collect(transform.GetChild(i), me, bones, index);
        }

        // generic avatar has no rest pose; the skinned body mesh with the most bones carries the true bind pose.
        // inverse(bindpose) is each bone's matrix in the renderer's space when the mesh was bound
        private static BindPoseRecord DescribeBindPose(Transform root)
        {
            SkinnedMeshRenderer best = null;
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!renderer.sharedMesh || renderer.bones == null || renderer.sharedMesh.bindposes.Length != renderer.bones.Length)
                    continue;
                if (!best || renderer.bones.Length > best.bones.Length)
                    best = renderer;
            }
            if (!best)
                return null;
            Matrix4x4[] bindposes = best.sharedMesh.bindposes;
            var record = new BindPoseRecord { Renderer = best.name, RootBone = best.rootBone ? best.rootBone.name : null, Bones = new List<BoneRecord>() };
            for (int i = 0; i < best.bones.Length; i++)
            {
                if (!best.bones[i])
                    continue;
                Matrix4x4 bind = bindposes[i].inverse;
                record.Bones.Add(new BoneRecord { Name = best.bones[i].name, Parent = -1, WorldPosition = V(bind.GetColumn(3)), WorldRotation = Q(bind.rotation), LocalScale = V(bind.lossyScale) });
            }
            return record;
        }

        private static AvatarRecord DescribeAvatar(Avatar avatar)
        {
            if (!avatar)
                return null;
            var record = new AvatarRecord { Name = avatar.name, IsValid = avatar.isValid, IsHuman = avatar.isHuman };
            if (!avatar.isHuman)
                return record;
            HumanDescription description = avatar.humanDescription;
            record.HumanBones = new Dictionary<string, string>();
            foreach (HumanBone bone in description.human)
                record.HumanBones[bone.humanName] = bone.boneName;
            record.RestPose = new List<BoneRecord>();
            foreach (SkeletonBone bone in description.skeleton)
                record.RestPose.Add(new BoneRecord { Name = bone.name, Parent = -1, LocalPosition = V(bone.position), LocalRotation = Q(bone.rotation), LocalScale = V(bone.scale) });
            return record;
        }

        private static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
        private static float[] Q(Quaternion q) => new[] { q.x, q.y, q.z, q.w };

        private sealed class SkeletonDocument
        {
            public string Schema;
            public string Profile;
            public string AnimatorObject;
            public string CapturedAt;
            public AvatarRecord Avatar;
            public Dictionary<string, string> References;
            public List<BoneRecord> Bones;
            public BindPoseRecord BindPose;
        }

        private sealed class BindPoseRecord
        {
            public string Renderer;
            public string RootBone;
            public List<BoneRecord> Bones;
        }

        private sealed class AvatarRecord
        {
            public string Name;
            public bool IsValid;
            public bool IsHuman;
            public Dictionary<string, string> HumanBones;
            public List<BoneRecord> RestPose;
        }

        private sealed class BoneRecord
        {
            public string Name;
            public int Parent;
            public float[] LocalPosition;
            public float[] LocalRotation;
            public float[] LocalScale;
            public float[] WorldPosition;
            public float[] WorldRotation;
        }
    }
}
