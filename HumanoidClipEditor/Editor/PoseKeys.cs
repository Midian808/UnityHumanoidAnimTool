using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.HumanoidClipEditor
{
    /// <summary>How a pose key blends into the next pose key (C16d).</summary>
    public enum PoseEasing
    {
        /// <summary>A smooth curve through the keys that never goes past them (a monotone cubic, flat at the first and last key).</summary>
        Smooth,
        /// <summary>A straight blend to the next key.</summary>
        Linear,
        /// <summary>Holds this key's pose until the next key.</summary>
        Hold,
    }

    /// <summary>
    /// The pose a pose key holds: every muscle (indexed like <see cref="HumanTrait.MuscleName"/>) and where the hips
    /// are, in the character's root space (metres). The hips are kept rather than the centre of mass so a key that
    /// poses only some body parts doesn't move the hips: the centre of mass moves whenever a limb does.
    /// </summary>
    [Serializable]
    public sealed class KeyPose
    {
        /// <summary>
        /// Starts empty (not valid) rather than sized from <see cref="HumanTrait.MuscleCount"/>: Unity runs field
        /// initializers while loading an asset, where its own API may not be called. A key's pose is always captured
        /// or pasted whole before it counts.
        /// </summary>
        public float[] muscles = Array.Empty<float>();
        public Vector3 hipsPosition;
        public Quaternion hipsRotation = Quaternion.identity;

        public bool IsValid => muscles != null && muscles.Length == HumanTrait.MuscleCount;

        public KeyPose Clone() => new KeyPose
        {
            muscles = (float[])muscles.Clone(),
            hipsPosition = hipsPosition,
            hipsRotation = hipsRotation,
        };
    }

    /// <summary>
    /// C16c: a Pose key, the third record kind. It holds the whole pose at one frame; its <see cref="mask"/> picks the
    /// body parts it poses (the parts of Unity's Avatar Mask), the rest coming from the clip underneath or other keys.
    /// Pose keys always apply first, blended with each other per muscle (<see cref="PoseLayer"/>); Offsets and Pins
    /// apply on top of them in list order (C16e). <see cref="easing"/> says how this key blends into the next one.
    /// On a fixed clip a key also eases in from the clip and back out over <see cref="EditRecord.easeIn"/> /
    /// <see cref="EditRecord.easeOut"/> frames (C16f); on a new clip keys always blend into each other and the first
    /// and last keys hold to the clip's ends.
    /// </summary>
    [Serializable]
    public sealed class PoseKeyRecord : EditRecord
    {
        /// <summary>Every <see cref="HumanBodyPart"/>.</summary>
        public const int AllParts = (1 << 8) - 1;

        public int frame;
        /// <summary>Body parts this key poses: bit <c>1 &lt;&lt; (int)HumanBodyPart</c>. Body includes the hips' place.</summary>
        public int mask = AllParts;
        public PoseEasing easing;
        public KeyPose pose = new KeyPose();

        public override int FirstFrame => frame;
        public override int LastFrame => frame;

        public bool Covers(HumanBodyPart part) => (mask & Bit(part)) != 0;

        public static int Bit(HumanBodyPart part) => 1 << (int)part;

        public static int MaskOf(IEnumerable<HumanBodyPart> parts) => parts.Aggregate(0, (m, p) => m | Bit(p));

        public override string Describe()
        {
            string parts = (mask & AllParts) == AllParts ? "" : $" ({PartsText(mask)})";
            return $"Pose key, frame {frame}{parts}";
        }

        public override EditRecord Clone()
        {
            var c = (PoseKeyRecord)MemberwiseClone();
            c.pose = pose != null ? pose.Clone() : new KeyPose();
            return c;
        }

        public static string PartsText(int mask)
        {
            var names = new List<string>();
            foreach (HumanBodyPart p in (HumanBodyPart[])Enum.GetValues(typeof(HumanBodyPart)))
                if ((mask & Bit(p)) != 0) names.Add(ObjectNames.NicifyVariableName(p.ToString()).ToLowerInvariant());
            return names.Count == 0 ? "no parts" : string.Join(", ", names);
        }
    }

    /// <summary>
    /// C16b: the pose a new clip starts from, held for its whole length underneath its pose keys: muscles and the
    /// body (the values a clip stores as RootT / RootQ). Either the Avatar's neutral pose (every muscle 0, standing
    /// on the ground) or the stored pose of a frame of any humanoid clip, moved to the origin.
    /// </summary>
    [Serializable]
    public sealed class StartPose
    {
        public float[] muscles = Array.Empty<float>();
        public Vector3 bodyPosition;
        public Quaternion bodyRotation = Quaternion.identity;
        /// <summary>Where the pose came from, shown in the window.</summary>
        public string description = "";

        public bool IsValid => muscles != null && muscles.Length == HumanTrait.MuscleCount;

        /// <summary>
        /// Every muscle 0 and the body unturned, raised so the lower foot's sole rests on the ground (y = 0) when the
        /// clip plays on <paramref name="preview"/>.
        /// </summary>
        public static StartPose Neutral(PreviewCharacter preview)
        {
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            var pose = new StartPose { muscles = new float[HumanTrait.MuscleCount], description = "Neutral (every muscle 0)" };
            AnimationClip probe = pose.CreateClip(1, 30f, preview.Animator, "Neutral probe", withGoals: false);
            Transform root = preview.Root.transform;
            root.GetPositionAndRotation(out Vector3 oldPos, out Quaternion oldRot);
            try
            {
                root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                HumanStreamSample s = HumanStreamProbe.EvaluateClip(preview.Animator, probe, 0f, goalsFromPose: true);
                float lowest = float.PositiveInfinity;
                foreach (AvatarIKGoal foot in new[] { AvatarIKGoal.LeftFoot, AvatarIKGoal.RightFoot })
                {
                    int g = (int)foot;
                    float height = foot == AvatarIKGoal.LeftFoot ? s.LeftFootHeight : s.RightFootHeight;
                    Vector3 sole = s.GoalPositionFromPose[g] + s.GoalLocalRotation[g] * new Vector3(height, 0f, 0f);
                    lowest = Mathf.Min(lowest, sole.y);
                }
                pose.bodyPosition = new Vector3(0f, -lowest / s.HumanScale, 0f);
            }
            finally
            {
                root.SetPositionAndRotation(oldPos, oldRot);
                Object.DestroyImmediate(probe);
            }
            return pose;
        }

        /// <summary>
        /// The pose <paramref name="clip"/> stores on <paramref name="frame"/> (its curves, before Loop Pose or Mirror),
        /// with the body moved over the origin (its height and facing kept).
        /// </summary>
        public static StartPose FromClipFrame(AnimationClip clip, int frame)
        {
            if (!clip) throw new ArgumentNullException(nameof(clip));
            if (!clip.humanMotion) throw new ArgumentException($"'{clip.name}' isn't a humanoid clip.", nameof(clip));
            int f = Mathf.Clamp(frame, 0, ClipWriter.LastFrame(clip));
            HumanPose p = new HumanoidClipCurves(clip).SamplePose(CurveKeys.FrameTime(f, clip.frameRate));
            return new StartPose
            {
                muscles = p.muscles,
                bodyPosition = new Vector3(0f, p.bodyPosition.y, 0f),
                bodyRotation = p.bodyRotation,
                description = $"{clip.name}, frame {f}",
            };
        }

        /// <summary>
        /// An in-memory clip (not saved; destroy it when done) holding this pose on every frame of a clip
        /// <paramref name="frames"/> frames long: every muscle curve, RootT / RootQ, and (with
        /// <paramref name="withGoals"/>) the IK goal curves Unity computes for the pose on <paramref name="animator"/> (C7).
        /// </summary>
        public AnimationClip CreateClip(int frames, float frameRate, Animator animator, string name, bool withGoals = true)
        {
            if (!IsValid) throw new InvalidOperationException("The start pose has no muscle values.");
            if (frames < 1) throw new ArgumentOutOfRangeException(nameof(frames), "A clip needs at least one frame.");
            if (!(frameRate > 0f)) throw new ArgumentOutOfRangeException(nameof(frameRate));
            float length = frames / frameRate;
            var clip = new AnimationClip { name = name, frameRate = frameRate, hideFlags = HideFlags.DontSave };
            var bindings = new List<EditorCurveBinding>();
            var curves = new List<AnimationCurve>();
            void Add(string property, float value)
            {
                bindings.Add(EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), property));
                curves.Add(AnimationCurve.Constant(0f, length, value));
            }
            for (int i = 0; i < HumanTrait.MuscleCount; i++) Add(HumanoidCurveNames.MuscleCurveNames[i], muscles[i]);
            Quaternion q = EditRecord.Normalized(bodyRotation);
            for (int a = 0; a < 3; a++) Add(HumanoidCurveNames.RootTName(a), bodyPosition[a]);
            for (int c = 0; c < 4; c++) Add(HumanoidCurveNames.RootQName(c), q[c]);
            AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());
            if (!withGoals) return clip;

            try
            {
                GoalCurveValues[] goals = GoalCurves.FromClipFrame(animator, clip, 0f);
                bindings.Clear();
                curves.Clear();
                for (int g = 0; g < HumanoidCurveNames.GoalCount; g++)
                {
                    for (int a = 0; a < 3; a++) Add(HumanoidCurveNames.GoalTName((AvatarIKGoal)g, a), goals[g].Position[a]);
                    for (int c = 0; c < 4; c++) Add(HumanoidCurveNames.GoalQName((AvatarIKGoal)g, c), goals[g].Rotation[c]);
                }
                AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());
                return clip;
            }
            catch
            {
                Object.DestroyImmediate(clip);
                throw;
            }
        }
    }

    /// <summary>
    /// C16g: the copied pose, shared between frames, clips and windows. It is kept in the system copy buffer (as text
    /// marked as this tool's), so it survives script reloads.
    /// </summary>
    public static class PoseClipboard
    {
        const string Marker = "HumanoidClipEditor.Pose:";

        public static void Copy(KeyPose pose)
        {
            if (pose == null || !pose.IsValid) throw new ArgumentException("Not a complete pose.", nameof(pose));
            EditorGUIUtility.systemCopyBuffer = Marker + JsonUtility.ToJson(pose);
        }

        public static bool TryGet(out KeyPose pose)
        {
            pose = null;
            string text = EditorGUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(text) || !text.StartsWith(Marker, StringComparison.Ordinal)) return false;
            try { pose = JsonUtility.FromJson<KeyPose>(text.Substring(Marker.Length)); }
            catch (ArgumentException) { return false; }
            return pose != null && pose.IsValid;
        }
    }
}
