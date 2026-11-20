using NUnit.Framework;
using UnityEngine;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>
    /// Reading a pose straight from a clip's curves (<see cref="HumanoidClipCurves"/>) and applying it to the preview
    /// character must give what Unity itself plays for a <see cref="RawClipCopy"/> of the clip (imported clips are
    /// compressed, and Loop Pose / Mirror change playback); reading the pose back must not move the skeleton.
    /// </summary>
    public class PoseTests
    {
        [OneTimeTearDown]
        public void Release() => TestRigs.Release();

        [Test]
        public void CurvePose_MatchesUnityEvaluation()
        {
            foreach ((TestRigs.Rig rig, AnimationClip source) in TestUtil.LongClips())
            {
                AnimationClip clip = RawClipCopy.Create(source);
                try
                {
                    using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
                    var curves = new HumanoidClipCurves(clip);
                    foreach (float t in TestUtil.SampleTimes(clip))
                    {
                        HumanPose pose = curves.SamplePose(t);
                        HumanStreamSample s = HumanStreamProbe.EvaluateClip(pc.Animator, clip, t);
                        for (int i = 0; i < pose.muscles.Length; i++)
                            Assert.AreEqual(s.Muscles[i], pose.muscles[i], 1e-3f,
                                $"{HumanoidCurveNames.MuscleCurveNames[i]} at t={t:F2}, {TestUtil.Describe(rig, source)}");
                    }
                }
                finally { Object.DestroyImmediate(clip); }
            }
        }

        [Test]
        public void AppliedCurvePose_GivesUnityBoneRotations()
        {
            foreach ((TestRigs.Rig rig, AnimationClip source) in TestUtil.LongClips())
            {
                AnimationClip clip = RawClipCopy.Create(source);
                try
                {
                    using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
                    var curves = new HumanoidClipCurves(clip);
                    foreach (float t in TestUtil.SampleTimes(clip))
                    {
                        HumanStreamProbe.EvaluateClip(pc.Animator, clip, t);
                        Quaternion?[] unity = TestUtil.LocalRotations(pc.Animator);
                        pc.ApplyPose(curves.SamplePose(t));
                        Quaternion?[] ours = TestUtil.LocalRotations(pc.Animator);
                        for (int i = 0; i < unity.Length; i++)
                            if (unity[i].HasValue)
                                Assert.Less(Quaternion.Angle(unity[i].Value, ours[i].Value), 0.1f,
                                    $"{(HumanBodyBones)i} at t={t:F2}, {TestUtil.Describe(rig, source)}");
                    }
                }
                finally { Object.DestroyImmediate(clip); }
            }
        }

        /// <summary>
        /// Muscle values themselves don't survive a read-back exactly (Unity re-splits twist between neighbouring bones
        /// and clamps out-of-range values), so the check is on what matters: the body and every bone stay put.
        /// </summary>
        [Test]
        public void ReadBack_KeepsBodyAndBonesInPlace()
        {
            foreach ((TestRigs.Rig rig, AnimationClip clip) in TestUtil.LongClips())
            {
                using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
                var curves = new HumanoidClipCurves(clip);
                foreach (float t in TestUtil.SampleTimes(clip))
                {
                    HumanPose pose = curves.SamplePose(t);
                    pc.ApplyPose(pose);
                    Vector3?[] before = TestUtil.Positions(pc.Animator);
                    HumanPose back = pc.ReadPose();
                    string where = $"t={t:F2}, {TestUtil.Describe(rig, clip)}";
                    Assert.Less((back.bodyPosition - pose.bodyPosition).magnitude, 1e-3f, "Body position, " + where);
                    Assert.Less(Quaternion.Angle(back.bodyRotation, pose.bodyRotation), 0.05f, "Body rotation, " + where);

                    pc.ApplyPose(back);
                    Vector3?[] after = TestUtil.Positions(pc.Animator);
                    for (int i = 0; i < before.Length; i++)
                        if (before[i].HasValue)
                            Assert.Less((before[i].Value - after[i].Value).magnitude, 0.005f,
                                $"{(HumanBodyBones)i} moved, {where}");
                }
            }
        }
    }
}
