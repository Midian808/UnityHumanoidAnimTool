using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>C9: clip curve names ↔ muscle indices, checked against Unity's own muscle handles and real clips.</summary>
    public class CurveNameTests
    {
        [OneTimeTearDown]
        public void Release() => TestRigs.Release();

        [TestCase("Left Index 1 Stretched", "LeftHand.Index.1 Stretched")]
        [TestCase("Right Thumb Spread", "RightHand.Thumb.Spread")]
        [TestCase("Right Little 3 Stretched", "RightHand.Little.3 Stretched")]
        [TestCase("Left Arm Down-Up", "Left Arm Down-Up")]
        [TestCase("Jaw Close", "Jaw Close")]
        public void TraitNameToCurveName_RenamesOnlyFingerMuscles(string trait, string curve)
        {
            Assert.AreEqual(curve, HumanoidCurveNames.TraitNameToCurveName(trait));
        }

        [Test]
        public void EveryMuscle_HasItsOwnCurveName()
        {
            IReadOnlyList<string> names = HumanoidCurveNames.MuscleCurveNames;
            Assert.AreEqual(HumanTrait.MuscleCount, names.Count);
            Assert.AreEqual(names.Count, names.Distinct().Count(), "Two muscles share a curve name.");
            for (int i = 0; i < names.Count; i++)
            {
                Assert.IsTrue(HumanoidCurveNames.TryGetMuscleIndex(names[i], out int back), names[i]);
                Assert.AreEqual(i, back, names[i]);
            }
        }

        [Test]
        public void UnityMuscleHandles_MapOntoEveryMuscleOnce()
        {
            IReadOnlyList<int> map = HumanStreamProbe.TraitIndexOfHandle;
            Assert.AreEqual(HumanTrait.MuscleCount, map.Count);
            CollectionAssert.AreEquivalent(Enumerable.Range(0, HumanTrait.MuscleCount), map);
        }

        [Test]
        public void RealClips_StoreEveryMuscleUnderItsMappedName()
        {
            var seen = new bool[HumanTrait.MuscleCount];
            foreach ((TestRigs.Rig _, AnimationClip clip) in TestRigs.SampleClips())
            {
                var curves = new HumanoidClipCurves(clip);
                for (int i = 0; i < seen.Length; i++) seen[i] |= curves.HasMuscle(i);
            }
            string[] missing = Enumerable.Range(0, seen.Length).Where(i => !seen[i])
                .Select(i => HumanoidCurveNames.MuscleCurveNames[i]).ToArray();
            Assert.IsEmpty(missing, "No sample clip has a curve under these mapped names.");
        }

        static readonly Regex MuscleLike =
            new Regex(@"(Stretched|Stretch|Spread|Down-Up|Front-Back|Left-Right|In-Out|Close)$");

        [Test]
        public void RealClips_HaveNoUnmappedMuscleCurves()
        {
            var unmapped = new HashSet<string>();
            foreach ((TestRigs.Rig _, AnimationClip clip) in TestRigs.SampleClips())
                foreach (var b in new HumanoidClipCurves(clip).OtherAnimatorBindings)
                    if (MuscleLike.IsMatch(b.propertyName)) unmapped.Add(b.propertyName);
            Assert.IsEmpty(unmapped, "Muscle-like curves the name map doesn't know.");
        }
    }
}
