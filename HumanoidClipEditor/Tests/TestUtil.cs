using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Midian.HumanoidClipEditor.Tests
{
    static class TestUtil
    {
        /// <summary>Clips at least this long are used for pose checks (shorter ones are usually single-frame takes).</summary>
        public const float MinLength = 0.5f;

        /// <summary>The first <paramref name="perRig"/> long clips of every sample rig.</summary>
        public static IEnumerable<(TestRigs.Rig rig, AnimationClip clip)> LongClips(int perRig = 1) =>
            TestRigs.RequireAny().SelectMany(r => r.Clips.Where(c => c.length >= MinLength).Take(perRig).Select(c => (r, c)));

        /// <summary>
        /// Frame times near 10%, 50% and 90% of the clip. Always whole frames: Unity plays a clip from values sampled
        /// at its frame rate with linear interpolation between them, so between frames it differs slightly from the
        /// editor curves; on frames they agree. The tool reads and writes on frames only (C12).
        /// </summary>
        public static float[] SampleTimes(AnimationClip clip) =>
            new[] { 0.1f, 0.5f, 0.9f }.Select(f => Mathf.Round(clip.length * f * clip.frameRate) / clip.frameRate).ToArray();

        /// <summary>Local rotation of every mapped human bone except the hips, indexed by <see cref="HumanBodyBones"/>.</summary>
        public static Quaternion?[] LocalRotations(Animator a)
        {
            var r = new Quaternion?[(int)HumanBodyBones.LastBone];
            for (int i = 0; i < r.Length; i++)
            {
                if (i == (int)HumanBodyBones.Hips) continue;
                Transform t = a.GetBoneTransform((HumanBodyBones)i);
                if (t) r[i] = t.localRotation;
            }
            return r;
        }

        /// <summary>World position of every mapped human bone, indexed by <see cref="HumanBodyBones"/>.</summary>
        public static Vector3?[] Positions(Animator a)
        {
            var r = new Vector3?[(int)HumanBodyBones.LastBone];
            for (int i = 0; i < r.Length; i++)
            {
                Transform t = a.GetBoneTransform((HumanBodyBones)i);
                if (t) r[i] = t.position;
            }
            return r;
        }

        public static string Describe(TestRigs.Rig rig, AnimationClip clip) =>
            $"{AssetDatabase.GetAssetPath(clip)}::{clip.name} on {rig.AvatarPath}";
    }
}
