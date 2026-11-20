using NUnit.Framework;
using UnityEngine;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>C12: setting one frame's value leaves every other frame's value as it was.</summary>
    public class CurveKeysTests
    {
        const float Rate = 30f;

        static AnimationCurve Sparse() => new AnimationCurve(
            new Keyframe(0f, 0f, 0f, 2f), new Keyframe(10f / Rate, 0.6f, 1f, -1f), new Keyframe(20f / Rate, -0.3f, 0.5f, 0f));

        static AnimationCurve Dense()
        {
            var c = new AnimationCurve();
            for (int f = 0; f <= 20; f++) c.AddKey(f / Rate, Mathf.Sin(f * 0.3f));
            return c;
        }

        static float[] Frames(AnimationCurve c, int last)
        {
            var v = new float[last + 1];
            for (int f = 0; f <= last; f++) v[f] = c.Evaluate(f / Rate);
            return v;
        }

        static void AssertOnlyFrameChanged(AnimationCurve before, AnimationCurve after, int frame, float value, int last)
        {
            float[] a = Frames(before, last), b = Frames(after, last);
            for (int f = 0; f <= last; f++)
            {
                if (f == frame) Assert.AreEqual(value, b[f], 1e-5f, "edited frame");
                else Assert.AreEqual(a[f], b[f], 1e-5f, $"frame {f}");
            }
        }

        [TestCase(5)]
        [TestCase(10)]
        [TestCase(0)]
        [TestCase(20)]
        [TestCase(13)]
        public void SparseCurve_OnlyTheEditedFrameChanges(int frame)
        {
            AnimationCurve before = Sparse(), after = Sparse();
            CurveKeys.SetValueOnFrame(after, frame, Rate, 20, 0.9f);
            AssertOnlyFrameChanged(before, after, frame, 0.9f, 20);
        }

        [TestCase(7)]
        [TestCase(0)]
        [TestCase(20)]
        public void DenseCurve_OnlyTheEditedKeyChanges(int frame)
        {
            AnimationCurve before = Dense(), after = Dense();
            CurveKeys.SetValueOnFrame(after, frame, Rate, 20, -0.5f);
            AssertOnlyFrameChanged(before, after, frame, -0.5f, 20);
            Assert.AreEqual(before.length, after.length, "No keys should be added to a curve keyed on every frame.");
        }

        [Test]
        public void Pin_DoesNotChangeTheCurvesShape()
        {
            AnimationCurve before = Sparse(), after = Sparse();
            CurveKeys.Pin(after, 4, Rate);
            CurveKeys.Pin(after, 15, Rate);
            for (int i = 0; i <= 200; i++)
            {
                float t = i / 200f * (20f / Rate);
                Assert.AreEqual(before.Evaluate(t), after.Evaluate(t), 1e-5f, $"t={t}");
            }
        }
    }
}
