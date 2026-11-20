using System;
using UnityEngine;

namespace Midian.HumanoidClipEditor
{
    /// <summary>The values a Humanoid clip stores for one IK goal at one frame (curves &lt;Goal&gt;T and &lt;Goal&gt;Q).</summary>
    public struct GoalCurveValues
    {
        public Vector3 Position;
        public Quaternion Rotation;
    }

    /// <summary>
    /// Computes the IK goal curve values for one frame of a clip (C7), using Unity's own goal-from-pose evaluation on
    /// the character. The pose is evaluated from the clip, not from a <see cref="HumanPose"/>, because a HumanPose has
    /// no Translation DoF: on an Avatar with Translation DoF the clip's TDOF curves move the leg bones, and leaving
    /// them out put foot goals about 1 cm off (measured).
    /// <list type="bullet">
    /// <item>Rotation: the goal rotation relative to the root, made relative to the body (RootQ):
    /// <c>Q = inverse(bodyLocalRotation) * goalLocalRotation</c>.</item>
    /// <item>Position: the goal position Unity computes from the pose (world), moved by the foot height along the goal's
    /// own x axis for feet (hands have no offset), made relative to the body and divided by human scale:
    /// <c>T = inverse(bodyRotation) * (goalFromPose + rootRotation * goalLocalRotation * (footHeight, 0, 0) - bodyPosition) / humanScale</c>.</item>
    /// </list>
    /// Checked against the goal curves stored in real imported clips (GoalConventionTests).
    /// </summary>
    public static class GoalCurves
    {
        /// <summary>
        /// Goal curve values for the frame <paramref name="clip"/> plays at <paramref name="time"/> on
        /// <paramref name="animator"/>, indexed by <see cref="AvatarIKGoal"/>. The clip must play its curves as stored
        /// (a <see cref="RawClipCopy"/>): goal curves hold the pose before Loop Pose and Mirror are applied.
        /// </summary>
        public static GoalCurveValues[] FromClipFrame(Animator animator, AnimationClip clip, float time)
        {
            if (!clip) throw new ArgumentNullException(nameof(clip));
            if (!RawClipCopy.PlaysCurvesAsStored(clip))
                throw new ArgumentException(
                    $"Clip '{clip.name}' has Loop Pose or Mirror on, so it doesn't play its curves as stored; use RawClipCopy.Create.",
                    nameof(clip));

            HumanStreamSample s = HumanStreamProbe.EvaluateClip(animator, clip, time, goalsFromPose: true);
            Quaternion rootRotation = animator.transform.rotation;
            Quaternion invBody = Quaternion.Inverse(s.BodyRotation);
            Quaternion invBodyLocal = Quaternion.Inverse(s.BodyLocalRotation);

            var result = new GoalCurveValues[HumanoidCurveNames.GoalCount];
            for (int g = 0; g < result.Length; g++)
            {
                float footHeight = g == (int)AvatarIKGoal.LeftFoot ? s.LeftFootHeight
                                 : g == (int)AvatarIKGoal.RightFoot ? s.RightFootHeight
                                 : 0f;
                Quaternion local = s.GoalLocalRotation[g];
                Vector3 sole = s.GoalPositionFromPose[g] + rootRotation * (local * new Vector3(footHeight, 0f, 0f));
                result[g].Position = invBody * (sole - s.BodyPosition) / s.HumanScale;
                result[g].Rotation = Normalize(invBodyLocal * local);
            }
            return result;
        }

        static Quaternion Normalize(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m > 1e-6f ? new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m) : Quaternion.identity;
        }
    }
}
