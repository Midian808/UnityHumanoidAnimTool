using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// One fix in a clip's adjustment layer (C13). A record covers a run of full-strength frames
    /// (<see cref="FirstFrame"/> to <see cref="LastFrame"/>) and fades in over <see cref="easeIn"/> frames before it
    /// and out over <see cref="easeOut"/> frames after it. On a clip with Loop Time on, a fade that runs past either
    /// end continues from the other end, and the first and last frames (the same pose in a loop) get the same fix.
    /// </summary>
    [Serializable]
    public abstract class EditRecord
    {
        /// <summary>Optional name shown in the record list; empty shows <see cref="Describe"/>.</summary>
        public string label = "";
        /// <summary>A muted record is kept but not baked.</summary>
        public bool muted;
        /// <summary>Frames before <see cref="FirstFrame"/> that blend the fix in (0 = it starts at full strength).</summary>
        public int easeIn;
        /// <summary>Frames after <see cref="LastFrame"/> that blend the fix out (0 = it stops at full strength).</summary>
        public int easeOut;

        public abstract int FirstFrame { get; }
        public abstract int LastFrame { get; }

        /// <summary>What the record does, e.g. "Move left hand, frames 10–14".</summary>
        public abstract string Describe();

        /// <summary>A deep copy (edits in progress work on a copy until they are committed).</summary>
        public abstract EditRecord Clone();

        public string Title => string.IsNullOrEmpty(label) ? Describe() : label;

        /// <summary>
        /// The record's strength (0–1) on clip frame <paramref name="frame"/>, and the frame in the record's own
        /// timeline that it is read at (different from <paramref name="frame"/> only when a looping clip wraps).
        /// </summary>
        public float Weight(int frame, int clipLastFrame, bool loop, out int recordFrame)
        {
            recordFrame = frame;
            if (!loop || clipLastFrame <= 0) return RawWeight(frame);

            // In a loop the last frame is the first frame again, so both read the record the same way.
            int period = clipLastFrame;
            int f = frame == period ? 0 : frame;
            float best = 0f;
            foreach (int x in new[] { f, f - period, f + period })
            {
                float w = RawWeight(x);
                if (w > best) { best = w; recordFrame = x; }
            }
            return best;
        }

        /// <summary>Adds every clip frame this record changes (weight above 0) to <paramref name="frames"/>.</summary>
        public void AddTouchedFrames(ISet<int> frames, int clipLastFrame, bool loop)
        {
            for (int x = FirstFrame - easeIn; x <= LastFrame + easeOut; x++)
            {
                if (RawWeight(x) <= 0f) continue;
                if (loop && clipLastFrame > 0)
                {
                    int f = ((x % clipLastFrame) + clipLastFrame) % clipLastFrame;
                    frames.Add(f);
                    if (f == 0) frames.Add(clipLastFrame);
                }
                else if (x >= 0 && x <= clipLastFrame)
                {
                    frames.Add(x);
                }
            }
        }

        float RawWeight(int x)
        {
            if (x >= FirstFrame && x <= LastFrame) return 1f;
            if (x < FirstFrame)
            {
                int d = FirstFrame - x;
                return d > easeIn ? 0f : Smooth(1f - d / (easeIn + 1f));
            }
            int a = x - LastFrame;
            return a > easeOut ? 0f : Smooth(1f - a / (easeOut + 1f));
        }

        static float Smooth(float t) => t * t * (3f - 2f * t);

        protected string FrameText() =>
            FirstFrame == LastFrame ? $"frame {FirstFrame}" : $"frames {FirstFrame}–{LastFrame}";

        internal static string LimbName(AvatarIKGoal goal)
        {
            switch (goal)
            {
                case AvatarIKGoal.LeftHand: return "left hand";
                case AvatarIKGoal.RightHand: return "right hand";
                case AvatarIKGoal.LeftFoot: return "left foot";
                case AvatarIKGoal.RightFoot: return "right foot";
                default: return goal.ToString();
            }
        }

        internal static Quaternion Normalized(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m > 1e-6f ? new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m) : Quaternion.identity;
        }
    }

    public enum OffsetTarget
    {
        /// <summary>A hand or foot, moved with Unity's IK (C4); the elbow / knee hint can be shifted too.</summary>
        Limb,
        /// <summary>One bone turned (C5); for the hips, also moved.</summary>
        Bone,
    }

    /// <summary>One keyed amount of an <see cref="OffsetRecord"/>.</summary>
    [Serializable]
    public struct OffsetKey
    {
        public int frame;
        /// <summary>Movement in the character's own space (follows root motion): of the hand / foot, or of the hips.</summary>
        public Vector3 position;
        /// <summary>Turn in the bone's own space (the hand / foot, or the turned bone), applied after its current rotation.</summary>
        public Quaternion rotation;
        /// <summary>Shift of the elbow / knee hint from its default spot, in the character's own space (limbs only).</summary>
        public Vector3 hint;

        public static OffsetKey Zero(int frame) =>
            new OffsetKey { frame = frame, position = Vector3.zero, rotation = Quaternion.identity, hint = Vector3.zero };
    }

    /// <summary>
    /// C13b Offset: moves a hand or foot (with the elbow / knee hint), turns a bone, or moves and turns the hips, by
    /// keyed amounts. Between keys the amount follows a smooth curve that doesn't overshoot (clamped auto tangents);
    /// before the first key and after the last it holds that key's amount, scaled by the ease.
    /// Amounts are relative to the pose the earlier records leave, so the fix follows the motion.
    /// </summary>
    [Serializable]
    public sealed class OffsetRecord : EditRecord
    {
        public OffsetTarget target;
        public AvatarIKGoal limb;
        public HumanBodyBones bone;
        public List<OffsetKey> keys = new List<OffsetKey>();

        public static OffsetRecord ForLimb(AvatarIKGoal goal) => new OffsetRecord { target = OffsetTarget.Limb, limb = goal };

        public static OffsetRecord ForBone(HumanBodyBones bone) => new OffsetRecord { target = OffsetTarget.Bone, bone = bone };

        public bool IsHips => target == OffsetTarget.Bone && bone == HumanBodyBones.Hips;

        public override int FirstFrame => keys.Count > 0 ? keys.Min(k => k.frame) : 0;
        public override int LastFrame => keys.Count > 0 ? keys.Max(k => k.frame) : 0;

        public override string Describe()
        {
            string what = target == OffsetTarget.Limb ? $"Move {LimbName(limb)}"
                        : IsHips ? "Move / turn hips"
                        : $"Turn {ObjectNames.NicifyVariableName(bone.ToString()).ToLowerInvariant()}";
            return $"{what}, {FrameText()}";
        }

        public override EditRecord Clone()
        {
            var c = (OffsetRecord)MemberwiseClone();
            c.keys = new List<OffsetKey>(keys);
            return c;
        }

        /// <summary>Index of the key on <paramref name="frame"/>, or -1.</summary>
        public int KeyIndex(int frame) => keys.FindIndex(k => k.frame == frame);

        /// <summary>Sets (or adds) the key on <paramref name="key"/>'s frame, keeping keys in frame order.</summary>
        public void SetKey(OffsetKey key)
        {
            key.rotation = Normalized(key.rotation);
            int i = KeyIndex(key.frame);
            if (i >= 0) { keys[i] = key; return; }
            int at = keys.FindIndex(k => k.frame > key.frame);
            if (at < 0) keys.Add(key); else keys.Insert(at, key);
        }

        /// <summary>
        /// The keyed amount at <paramref name="frame"/> (record timeline), before easing: the key's value on a key,
        /// a smooth curve between keys, the nearest key's value outside them. No keys reads as no change.
        /// </summary>
        public OffsetKey Sample(int frame)
        {
            if (keys.Count == 0) return OffsetKey.Zero(frame);
            List<OffsetKey> sorted = keys.OrderBy(k => k.frame).ToList();
            if (frame <= sorted[0].frame) return At(sorted[0], frame);
            if (frame >= sorted[sorted.Count - 1].frame) return At(sorted[sorted.Count - 1], frame);
            int exact = sorted.FindIndex(k => k.frame == frame);
            if (exact >= 0) return At(sorted[exact], frame);

            // Quaternion components are kept on one hemisphere so the curves don't pass through a flip.
            var rotations = new Quaternion[sorted.Count];
            for (int i = 0; i < sorted.Count; i++)
            {
                Quaternion q = Normalized(sorted[i].rotation);
                if (i > 0 && Quaternion.Dot(q, rotations[i - 1]) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                rotations[i] = q;
            }
            var result = new OffsetKey { frame = frame };
            var values = new float[10];
            for (int c = 0; c < values.Length; c++)
            {
                var curve = new AnimationCurve();
                for (int i = 0; i < sorted.Count; i++)
                    curve.AddKey(new Keyframe(sorted[i].frame, Component(sorted[i], rotations[i], c)));
                for (int i = 0; i < curve.length; i++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
                    AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
                }
                values[c] = curve.Evaluate(frame);
            }
            result.position = new Vector3(values[0], values[1], values[2]);
            result.rotation = Normalized(new Quaternion(values[3], values[4], values[5], values[6]));
            result.hint = new Vector3(values[7], values[8], values[9]);
            return result;
        }

        /// <summary>The amount at <paramref name="frame"/> scaled by <paramref name="weight"/>.</summary>
        public OffsetKey Scaled(int frame, float weight)
        {
            OffsetKey k = Sample(frame);
            if (weight >= 1f) return k;
            k.position *= weight;
            k.hint *= weight;
            k.rotation = Quaternion.Slerp(Quaternion.identity, Normalized(k.rotation), weight);
            return k;
        }

        static OffsetKey At(OffsetKey k, int frame)
        {
            k.frame = frame;
            k.rotation = Normalized(k.rotation);
            return k;
        }

        static float Component(OffsetKey k, Quaternion q, int c)
        {
            switch (c)
            {
                case 0: return k.position.x;
                case 1: return k.position.y;
                case 2: return k.position.z;
                case 3: return q.x;
                case 4: return q.y;
                case 5: return q.z;
                case 6: return q.w;
                case 7: return k.hint.x;
                case 8: return k.hint.y;
                default: return k.hint.z;
            }
        }
    }

    public enum PinAnchor
    {
        /// <summary>A fixed spot in the world the character moves through (stops foot sliding).</summary>
        World,
        /// <summary>A spot that moves with another humanoid bone (a hand on the other hand).</summary>
        Bone,
        /// <summary>A spot on a reference prop (C14), e.g. a sword's second grip.</summary>
        Prop,
    }

    /// <summary>
    /// C13b Pin: holds a hand or foot at one spot from <see cref="start"/> to <see cref="end"/>, solved with Unity's
    /// IK (C4). The spot is stored in its anchor's space: the world, a bone, or a prop. During the ease the hand or
    /// foot blends between where the earlier records leave it and the spot.
    /// </summary>
    [Serializable]
    public sealed class PinRecord : EditRecord
    {
        public AvatarIKGoal limb;
        public int start, end;
        public PinAnchor anchor;
        public HumanBodyBones anchorBone = HumanBodyBones.Hips;
        public string propId = "";
        /// <summary>The hand / foot bone's position in the anchor's space.</summary>
        public Vector3 position;
        /// <summary>The hand / foot bone's rotation in the anchor's space.</summary>
        public Quaternion rotation = Quaternion.identity;
        /// <summary>Also hold the hand / foot's rotation (otherwise only its position is pinned).</summary>
        public bool holdRotation = true;
        /// <summary>Shift of the elbow / knee hint from its default spot, in the character's own space.</summary>
        public Vector3 hint;

        public override int FirstFrame => Mathf.Min(start, end);
        public override int LastFrame => Mathf.Max(start, end);

        public override string Describe()
        {
            string to = anchor == PinAnchor.World ? "world"
                      : anchor == PinAnchor.Bone ? ObjectNames.NicifyVariableName(anchorBone.ToString()).ToLowerInvariant()
                      : "prop";
            return $"Pin {LimbName(limb)} to {to}, {FrameText()}";
        }

        public override EditRecord Clone() => (PinRecord)MemberwiseClone();
    }
}
