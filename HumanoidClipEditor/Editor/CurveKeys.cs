using UnityEditor;
using UnityEngine;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// Sets a curve's value on one frame without changing its value on any other frame (C12). Imported clips may
    /// have keys only every few frames; changing one key would reshape the curve all the way to its neighbouring
    /// keys. So the frames either side are pinned first: a key is inserted there (if none exists) with the curve's
    /// current value and slope, which splits the existing segment without changing its shape. Only the stretch
    /// between those two frames then changes.
    /// </summary>
    public static class CurveKeys
    {
        /// <summary>Keys closer than this fraction of a frame count as being on that frame.</summary>
        const float FrameTolerance = 0.01f;

        public static float FrameTime(int frame, float frameRate) => frame / frameRate;

        /// <summary>Index of the key on <paramref name="frame"/>, or -1.</summary>
        public static int FindKey(AnimationCurve curve, int frame, float frameRate)
        {
            float t = FrameTime(frame, frameRate);
            float tol = FrameTolerance / frameRate;
            Keyframe[] keys = curve.keys;
            for (int i = 0; i < keys.Length; i++)
                if (Mathf.Abs(keys[i].time - t) <= tol) return i;
            return -1;
        }

        /// <summary>
        /// Sets the curve's value on <paramref name="frame"/> to <paramref name="value"/>; the key gets clamped-auto
        /// tangents. Frames other than <paramref name="frame"/> keep their values. <paramref name="lastFrame"/> is the
        /// clip's last frame (no pin is placed past either end).
        /// </summary>
        public static void SetValueOnFrame(AnimationCurve curve, int frame, float frameRate, int lastFrame, float value)
        {
            if (frame > 0) Pin(curve, frame - 1, frameRate);
            if (frame < lastFrame) Pin(curve, frame + 1, frameRate);

            int index = FindKey(curve, frame, frameRate);
            if (index < 0)
            {
                index = curve.AddKey(new Keyframe(FrameTime(frame, frameRate), value));
            }
            else
            {
                Keyframe k = curve[index];
                k.value = value;
                index = curve.MoveKey(index, k);
            }
            AnimationUtility.SetKeyBroken(curve, index, false);
            AnimationUtility.SetKeyLeftTangentMode(curve, index, AnimationUtility.TangentMode.ClampedAuto);
            AnimationUtility.SetKeyRightTangentMode(curve, index, AnimationUtility.TangentMode.ClampedAuto);
        }

        /// <summary>
        /// Puts a key on <paramref name="frame"/> holding the curve's current value and slope there, so the curve's
        /// shape doesn't change. Does nothing if the frame already has a key.
        /// </summary>
        public static void Pin(AnimationCurve curve, int frame, float frameRate)
        {
            if (FindKey(curve, frame, frameRate) >= 0) return;
            float t = FrameTime(frame, frameRate);
            float slope = Slope(curve, t, 0.25f / frameRate);
            int index = curve.AddKey(new Keyframe(t, curve.Evaluate(t), slope, slope));
            if (index < 0) return;
            AnimationUtility.SetKeyBroken(curve, index, false);
            AnimationUtility.SetKeyLeftTangentMode(curve, index, AnimationUtility.TangentMode.Free);
            AnimationUtility.SetKeyRightTangentMode(curve, index, AnimationUtility.TangentMode.Free);
        }

        /// <summary>
        /// Slope of the curve at <paramref name="t"/>. Inside an unweighted segment it is the exact derivative of the
        /// segment's Hermite cubic, so a key placed with it splits the segment without changing its shape; stepped
        /// segments (infinite tangents) and the flat stretches before the first / after the last key have slope 0.
        /// Weighted segments fall back to a central difference over <paramref name="h"/>.
        /// </summary>
        public static float Slope(AnimationCurve curve, float t, float h)
        {
            int n = curve.length;
            if (n < 2 || t <= curve[0].time || t >= curve[n - 1].time) return 0f;
            int i = 0;
            while (i < n - 2 && curve[i + 1].time <= t) i++;
            Keyframe k0 = curve[i], k1 = curve[i + 1];
            if (float.IsInfinity(k0.outTangent) || float.IsInfinity(k1.inTangent)) return 0f;
            bool weighted = (k0.weightedMode & WeightedMode.Out) != 0 || (k1.weightedMode & WeightedMode.In) != 0;
            if (weighted)
            {
                float t0 = Mathf.Max(k0.time, t - h), t1 = Mathf.Min(k1.time, t + h);
                return (curve.Evaluate(t1) - curve.Evaluate(t0)) / (t1 - t0);
            }
            float dt = k1.time - k0.time;
            float u = (t - k0.time) / dt;
            return (6f * u * u - 6f * u) / dt * k0.value + (3f * u * u - 4f * u + 1f) * k0.outTangent
                 + (-6f * u * u + 6f * u) / dt * k1.value + (3f * u * u - 2f * u) * k1.inTangent;
        }
    }
}
