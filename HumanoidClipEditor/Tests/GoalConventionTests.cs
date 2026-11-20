using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>
    /// C7: goals computed from a clip frame by <see cref="GoalCurves.FromClipFrame"/> must reproduce the goal curves
    /// Unity stored when it imported the clip. Only clips imported against the preview model's own Avatar are used:
    /// their goals were computed on exactly that skeleton.
    /// </summary>
    public class GoalConventionTests
    {
        const float PositionTolerance = 0.005f; // normalized units (about 5 mm on a 1 m human scale)
        const float RotationTolerance = 0.5f;   // degrees

        [OneTimeTearDown]
        public void Release() => TestRigs.Release();

        [Test]
        public void GoalsFromClipFrame_ReproduceImportedGoalCurves()
        {
            var cases = TestUtil.LongClips(perRig: 2)
                .Where(x => x.rig.OwnAvatar && new HumanoidClipCurves(x.clip).HasGoal(AvatarIKGoal.LeftHand))
                .ToList();
            if (cases.Count == 0) Assert.Ignore("No sample clip with goal curves on its model's own Avatar.");

            foreach ((TestRigs.Rig rig, AnimationClip clip) in cases)
            {
                using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
                AnimationClip raw = RawClipCopy.Create(clip);
                try
                {
                    var curves = new HumanoidClipCurves(clip);
                    foreach (float t in TestUtil.SampleTimes(clip))
                    {
                        GoalCurveValues[] computed = GoalCurves.FromClipFrame(pc.Animator, raw, t);
                        for (int g = 0; g < HumanoidCurveNames.GoalCount; g++)
                        {
                            var goal = (AvatarIKGoal)g;
                            if (!curves.HasGoal(goal)) continue;
                            curves.SampleGoal(goal, t, out Vector3 storedT, out Quaternion storedQ);
                            string where = $"{goal} at t={t:F2}, {TestUtil.Describe(rig, clip)}";
                            Assert.Less((computed[g].Position - storedT).magnitude, PositionTolerance, "Position, " + where);
                            Assert.Less(Quaternion.Angle(computed[g].Rotation, storedQ), RotationTolerance, "Rotation, " + where);
                        }
                    }
                }
                finally { Object.DestroyImmediate(raw); }
            }
        }

        [Test]
        public void FromClipFrame_RefusesAClipThatDoesntPlayItsCurvesAsStored()
        {
            TestRigs.Rig rig = TestRigs.RequireAny()[0];
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            AnimationClip raw = RawClipCopy.Create(rig.Clips[0]);
            try
            {
                UnityEditor.AnimationClipSettings settings = UnityEditor.AnimationUtility.GetAnimationClipSettings(raw);
                settings.loopBlend = true;
                UnityEditor.AnimationUtility.SetAnimationClipSettings(raw, settings);
                Assert.Throws<System.ArgumentException>(() => GoalCurves.FromClipFrame(pc.Animator, raw, 0f));
            }
            finally { Object.DestroyImmediate(raw); }
        }
    }
}
