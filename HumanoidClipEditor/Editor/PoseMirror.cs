using System;
using UnityEngine;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// C16g: mirrors a humanoid pose left ↔ right the way Unity's clip Mirror option does, in muscle space:
    /// <list type="bullet">
    /// <item>a muscle of one side (its name starts with "Left " or "Right ") swaps value with the same muscle of the
    /// other side, unchanged in sign (muscle space is already mirrored between the sides);</item>
    /// <item>a centre muscle that bends or turns sideways (its name has "Left-Right": the spine, chest, upper chest,
    /// neck, head and jaw) changes sign; every other centre muscle (front-back, down-up, jaw close) is kept;</item>
    /// <item>the body is reflected across the character's centre plane (its root's YZ plane): position x changes
    /// sign, and the rotation <c>(x, y, z, w)</c> becomes <c>(x, -y, -z, w)</c>.</item>
    /// </list>
    /// <c>MirrorTests</c> pins this against what Unity plays for a clip with Mirror on.
    /// </summary>
    public static class PoseMirror
    {
        static int[] counterpart;
        static bool[] negate;

        /// <summary>The muscles of the mirrored pose, indexed like <see cref="HumanTrait.MuscleName"/>.</summary>
        public static float[] MirrorMuscles(float[] muscles)
        {
            if (muscles == null || muscles.Length != HumanTrait.MuscleCount)
                throw new ArgumentException("Needs every muscle.", nameof(muscles));
            Build();
            var result = new float[muscles.Length];
            for (int i = 0; i < muscles.Length; i++)
            {
                float v = muscles[counterpart[i]];
                result[i] = negate[i] ? -v : v;
            }
            return result;
        }

        /// <summary>The mirrored pose: muscles as <see cref="MirrorMuscles"/>, body reflected across the root's YZ plane.</summary>
        public static HumanPose Mirror(HumanPose pose) => new HumanPose
        {
            muscles = MirrorMuscles(pose.muscles),
            bodyPosition = MirrorPosition(pose.bodyPosition),
            bodyRotation = MirrorRotation(pose.bodyRotation),
        };

        public static Vector3 MirrorPosition(Vector3 p) => new Vector3(-p.x, p.y, p.z);

        public static Quaternion MirrorRotation(Quaternion q) => EditRecord.Normalized(new Quaternion(q.x, -q.y, -q.z, q.w));

        static void Build()
        {
            if (counterpart != null) return;
            string[] names = HumanTrait.MuscleName;
            var other = new int[names.Length];
            var flip = new bool[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                string n = names[i];
                string swapped = n.StartsWith("Left ", StringComparison.Ordinal) ? "Right " + n.Substring(5)
                               : n.StartsWith("Right ", StringComparison.Ordinal) ? "Left " + n.Substring(6)
                               : null;
                if (swapped != null)
                {
                    int j = Array.IndexOf(names, swapped);
                    if (j < 0) throw new InvalidOperationException($"Muscle '{n}' has no '{swapped}' counterpart.");
                    other[i] = j;
                }
                else
                {
                    other[i] = i;
                    flip[i] = n.Contains("Left-Right");
                }
            }
            negate = flip;
            counterpart = other;
        }
    }
}
