using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary><see cref="RawClipCopy"/>: same humanoid curves, Loop Pose and Mirror off, other settings kept.</summary>
    public class RawClipCopyTests
    {
        [OneTimeTearDown]
        public void Release() => TestRigs.Release();

        [Test]
        public void Create_CopiesEveryHumanoidCurveAndTurnsOffLoopPoseAndMirror()
        {
            foreach ((TestRigs.Rig rig, AnimationClip source) in TestUtil.LongClips())
            {
                AnimationClip raw = RawClipCopy.Create(source);
                try
                {
                    string[] expected = AnimationUtility.GetCurveBindings(source)
                        .Where(b => b.type == typeof(Animator) && string.IsNullOrEmpty(b.path))
                        .Select(b => b.propertyName).OrderBy(n => n).ToArray();
                    string[] actual = AnimationUtility.GetCurveBindings(raw)
                        .Select(b => b.propertyName).OrderBy(n => n).ToArray();
                    CollectionAssert.AreEqual(expected, actual, TestUtil.Describe(rig, source));

                    AnimationClipSettings a = AnimationUtility.GetAnimationClipSettings(source);
                    AnimationClipSettings b2 = AnimationUtility.GetAnimationClipSettings(raw);
                    Assert.IsFalse(b2.loopBlend);
                    Assert.IsFalse(b2.mirror);
                    Assert.IsTrue(RawClipCopy.PlaysCurvesAsStored(raw));
                    Assert.AreEqual(a.loopTime, b2.loopTime);
                    Assert.AreEqual(a.loopBlendOrientation, b2.loopBlendOrientation);
                    Assert.AreEqual(a.loopBlendPositionY, b2.loopBlendPositionY);
                    Assert.AreEqual(a.loopBlendPositionXZ, b2.loopBlendPositionXZ);
                    Assert.AreEqual(a.keepOriginalOrientation, b2.keepOriginalOrientation);
                    Assert.AreEqual(a.keepOriginalPositionY, b2.keepOriginalPositionY);
                    Assert.AreEqual(a.keepOriginalPositionXZ, b2.keepOriginalPositionXZ);
                    Assert.AreEqual(a.heightFromFeet, b2.heightFromFeet);
                    Assert.AreEqual(a.orientationOffsetY, b2.orientationOffsetY);
                    Assert.AreEqual(a.level, b2.level);
                    Assert.AreEqual(a.cycleOffset, b2.cycleOffset);
                    Assert.AreEqual(source.frameRate, raw.frameRate);
                    Assert.AreEqual(source.length, raw.length, 1e-4f);
                }
                finally { Object.DestroyImmediate(raw); }
            }
        }
    }
}
