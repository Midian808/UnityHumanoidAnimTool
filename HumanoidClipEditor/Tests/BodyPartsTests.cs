using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Midian.HumanoidClipEditor.Tests
{
    public class BodyPartsTests
    {
        [Test]
        public void EveryHumanBone_HasAPart()
        {
            foreach (HumanBodyBones b in Enumerable.Range(0, (int)HumanBodyBones.LastBone).Cast<HumanBodyBones>())
                Assert.DoesNotThrow(() => BodyParts.Of(b), b.ToString());
        }

        [Test]
        public void EveryMuscle_HasAPart_AndFingerMusclesBelongToTheirHand()
        {
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
            {
                HumanBodyPart part = BodyParts.OfMuscle(i);
                string name = HumanoidCurveNames.MuscleCurveNames[i];
                if (name.StartsWith("LeftHand.", StringComparison.Ordinal)) Assert.AreEqual(HumanBodyPart.LeftFingers, part, name);
                if (name.StartsWith("RightHand.", StringComparison.Ordinal)) Assert.AreEqual(HumanBodyPart.RightFingers, part, name);
            }
        }

        [TestCase("Left Forearm Twist In-Out", HumanBodyPart.LeftArm)]
        [TestCase("Right Upper Leg Twist In-Out", HumanBodyPart.RightLeg)]
        [TestCase("Spine Twist Left-Right", HumanBodyPart.Body)]
        [TestCase("Head Nod Down-Up", HumanBodyPart.Head)]
        [TestCase("Left Toes Up-Down", HumanBodyPart.LeftLeg)]
        public void Muscles_MapToTheExpectedPart(string muscle, HumanBodyPart expected)
        {
            Assert.IsTrue(HumanoidCurveNames.TryGetMuscleIndex(muscle, out int i), muscle);
            Assert.AreEqual(expected, BodyParts.OfMuscle(i));
        }
    }
}
