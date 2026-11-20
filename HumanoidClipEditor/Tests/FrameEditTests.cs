using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>
    /// Phase 2's "done when", through the adjustment layer: turning a bone on a frame (through
    /// <see cref="FrameEditSession"/>, the path the window uses) stores a fix with no ease, and the bake writes curves
    /// that play back as edited in Unity, on real imported clips. Also: no other frame changes (C12), no other body
    /// part's muscle curves change (C17), goals of untouched limbs keep their place and goals of the edited limb match
    /// the new pose (C7), and Undo restores the output.
    /// </summary>
    public class FrameEditTests
    {
        const float EditedTolerance = 0.2f;    // degrees: the frame plays back as edited
        const float UntouchedTolerance = 0.01f; // degrees: neighbouring frames don't move

        [SetUp] public void SetUp() => TempAssets.Create();
        [TearDown] public void TearDown() => TempAssets.Delete();
        [OneTimeTearDown] public void Release() => TestRigs.Release();

        [Test]
        public void TurningAnArm_PlaysBackAsEdited_AndTouchesNothingElse()
        {
            int edited = 0;
            foreach ((TestRigs.Rig rig, AnimationClip source) in TestUtil.LongClips())
            {
                if (EditArm(rig, source, loopPose: null)) edited++;
            }
            if (edited == 0) Assert.Ignore("No sample character had a mapped lower or upper arm that could be turned.");
        }

        [Test]
        public void TurningAnArm_OnALoopPoseClip_PlaysBackAsEdited()
        {
            (TestRigs.Rig rig, AnimationClip source) = TestUtil.LongClips().First();
            Assert.IsTrue(EditArm(rig, source, loopPose: true), "Couldn't turn the arm on " + TestUtil.Describe(rig, source));
        }

        [Test]
        public void TurningTheHips_PlaysBackAsEdited()
        {
            foreach ((TestRigs.Rig rig, AnimationClip source) in TestUtil.LongClips())
            {
                ClipEditsAsset edits = TestEdits.Create(rig, source);
                using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
                using FrameEditSession session = TestEdits.Session(edits, pc);
                int frame = MidFrame(session);
                session.GoToFrame(frame);
                Transform hips = pc.Animator.GetBoneTransform(HumanBodyBones.Hips);
                Quaternion before = hips.rotation;

                // A forward tilt: turning about the vertical would be taken into root motion on clips whose root
                // rotation follows the body, and the preview root doesn't move.
                session.RotateBone(HumanBodyBones.Hips, Quaternion.AngleAxis(8f, hips.right) * before);
                Quaternion editedHips = hips.rotation;
                Quaternion?[] editedLocal = TestUtil.LocalRotations(pc.Animator);
                Assert.Greater(Quaternion.Angle(before, editedHips), 4f, "The hips didn't turn.");
                session.Commit();

                string where = TestUtil.Describe(rig, source);
                Assert.Less(Quaternion.Angle(editedHips, hips.rotation), EditedTolerance, "Hips, " + where);
                AssertRotations(editedLocal, TestUtil.LocalRotations(pc.Animator), EditedTolerance, "after commit, " + where);
            }
        }

        [Test]
        public void Undo_RestoresTheOutputAndTheFixes()
        {
            (TestRigs.Rig rig, AnimationClip source) = TestUtil.LongClips().First();
            ClipEditsAsset edits = TestEdits.Create(rig, source);
            AnimationClip clip = edits.Output;
            Dictionary<string, Keyframe[]> original = AllKeys(clip);
            Undo.IncrementCurrentGroup();

            using (PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar))
            using (FrameEditSession session = TestEdits.Session(edits, pc))
            {
                session.GoToFrame(MidFrame(session));
                Assert.IsTrue(TryTurn(session, pc, ArmBone(pc)), "Couldn't turn the arm.");
                session.Commit();
            }
            Assert.IsFalse(SameKeys(original, AllKeys(clip)), "The edit changed nothing.");
            Assert.AreEqual(1, edits.Records.Count, "The edit wasn't stored as a fix.");

            Undo.PerformUndo();
            Assert.IsTrue(SameKeys(original, AllKeys(clip)), "Undo didn't restore the output.");
            Assert.AreEqual(0, edits.Records.Count, "Undo didn't remove the fix.");
        }

        [Test]
        public void WriterAndSession_RefuseAReadOnlyOutput()
        {
            (TestRigs.Rig rig, AnimationClip source) = TestUtil.LongClips().First();
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            var edits = ScriptableObject.CreateInstance<ClipEditsAsset>();
            try
            {
                edits.Source = source;
                edits.Output = source;
                Assert.IsNotNull(edits.Problem());
                Assert.Throws<ArgumentException>(() => new FrameEditSession(edits, pc));
            }
            finally { Object.DestroyImmediate(edits); }
            var pose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
            Assert.Throws<ArgumentException>(() => ClipWriter.WriteFrames(source,
                new[] { new FrameEdit(0, pose, pose, new[] { HumanBodyPart.LeftArm }, false) }, pc.Animator));
        }

        /// <summary>Turns an arm bone on the middle frame and checks the result; false if no arm bone could be turned.</summary>
        static bool EditArm(TestRigs.Rig rig, AnimationClip source, bool? loopPose)
        {
            ClipEditsAsset edits = TestEdits.Create(rig, source);
            AnimationClip clip = edits.Output;
            {
                if (loopPose.HasValue)
                {
                    AnimationClipSettings s = AnimationUtility.GetAnimationClipSettings(clip);
                    s.loopBlend = loopPose.Value;
                    AnimationUtility.SetAnimationClipSettings(clip, s);
                }
                string where = TestUtil.Describe(rig, source) + (loopPose == true ? " (Loop Pose on)" : "");

                using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
                using FrameEditSession session = TestEdits.Session(edits, pc);
                HumanBodyBones bone = ArmBone(pc);
                if (bone == HumanBodyBones.LastBone) return false;
                int frame = MidFrame(session);
                float time = CurveKeys.FrameTime(frame, clip.frameRate);

                session.GoToFrame(frame - 1);
                Quaternion?[] prev = TestUtil.LocalRotations(pc.Animator);
                session.GoToFrame(frame + 1);
                Quaternion?[] next = TestUtil.LocalRotations(pc.Animator);
                session.GoToFrame(frame);

                var curvesBefore = new HumanoidClipCurves(clip);
                Dictionary<string, Keyframe[]> otherParts = MuscleKeys(clip, part => part != HumanBodyPart.LeftArm);
                Vector3 footBefore = GoalInClipSpace(curvesBefore, AvatarIKGoal.RightFoot, time);

                Quaternion?[] playedLocal = TestUtil.LocalRotations(pc.Animator);
                if (!TryTurn(session, pc, bone)) return false;
                Quaternion?[] editedLocal = TestUtil.LocalRotations(pc.Animator);
                AssertRotations(OnlyParts(playedLocal, p => p != HumanBodyPart.LeftArm), editedLocal, UntouchedTolerance,
                    "preview during the edit: a bone outside the edited part moved, " + where);
                session.Commit();

                AssertRotations(editedLocal, TestUtil.LocalRotations(pc.Animator), EditedTolerance, "edited frame, " + where);
                session.GoToFrame(frame - 1);
                AssertRotations(prev, TestUtil.LocalRotations(pc.Animator), UntouchedTolerance, "previous frame, " + where);
                session.GoToFrame(frame + 1);
                AssertRotations(next, TestUtil.LocalRotations(pc.Animator), UntouchedTolerance, "next frame, " + where);

                Assert.IsTrue(SameKeys(otherParts, MuscleKeys(clip, part => part != HumanBodyPart.LeftArm)),
                    "Muscle curves of body parts that weren't edited changed, " + where);

                var curvesAfter = new HumanoidClipCurves(clip);
                if (curvesAfter.HasGoal(AvatarIKGoal.RightFoot))
                    Assert.Less((GoalInClipSpace(curvesAfter, AvatarIKGoal.RightFoot, time) - footBefore).magnitude, 1e-4f,
                        "The right foot goal moved, " + where);
                if (curvesAfter.HasGoal(AvatarIKGoal.LeftHand))
                {
                    AnimationClip raw = RawClipCopy.Create(clip);
                    try
                    {
                        GoalCurveValues expected = GoalCurves.FromClipFrame(pc.Animator, raw, time)[(int)AvatarIKGoal.LeftHand];
                        curvesAfter.SampleGoal(AvatarIKGoal.LeftHand, time, out Vector3 t, out Quaternion q);
                        Assert.Less((t - expected.Position).magnitude, 1e-3f, "Left hand goal position, " + where);
                        Assert.Less(Quaternion.Angle(q, expected.Rotation), 0.1f, "Left hand goal rotation, " + where);
                    }
                    finally { Object.DestroyImmediate(raw); }
                }
                return true;
            }
        }

        internal static HumanBodyBones ArmBone(PreviewCharacter pc)
        {
            if (pc.Animator.GetBoneTransform(HumanBodyBones.LeftLowerArm)) return HumanBodyBones.LeftLowerArm;
            if (pc.Animator.GetBoneTransform(HumanBodyBones.LeftUpperArm)) return HumanBodyBones.LeftUpperArm;
            return HumanBodyBones.LastBone;
        }

        /// <summary>
        /// Turns <paramref name="bone"/> 12° about each of its own axes in turn until the snapped pose actually moved
        /// it (a joint can be at its muscle limit in one direction). Leaves the edit pending; false if none moved it.
        /// </summary>
        internal static bool TryTurn(FrameEditSession session, PreviewCharacter pc, HumanBodyBones bone)
        {
            Transform t = pc.Animator.GetBoneTransform(bone);
            Quaternion start = t.rotation;
            Vector3[] axes = { t.right, t.up, t.forward, -t.right, -t.up, -t.forward };
            foreach (Vector3 axis in axes)
            {
                session.RotateBone(bone, Quaternion.AngleAxis(12f, axis) * start);
                if (Quaternion.Angle(start, t.rotation) > 4f) return true;
                session.Cancel();
            }
            return false;
        }

        /// <summary>The rotations of bones in the parts <paramref name="include"/> accepts; null elsewhere.</summary>
        internal static Quaternion?[] OnlyParts(Quaternion?[] rotations, Func<HumanBodyPart, bool> include) =>
            rotations.Select((r, i) => include(BodyParts.Of((HumanBodyBones)i)) ? r : null).ToArray();

        internal static int MidFrame(FrameEditSession session) => Mathf.Clamp(session.LastFrame / 2, 1, Mathf.Max(1, session.LastFrame - 1));

        internal static Vector3 GoalInClipSpace(HumanoidClipCurves curves, AvatarIKGoal goal, float time)
        {
            if (!curves.HasGoal(goal)) return Vector3.zero;
            HumanPose body = curves.SamplePose(time);
            curves.SampleGoal(goal, time, out Vector3 t, out _);
            return body.bodyPosition + body.bodyRotation * t;
        }

        internal static void AssertRotations(Quaternion?[] expected, Quaternion?[] actual, float tolerance, string where)
        {
            for (int i = 0; i < expected.Length; i++)
                if (expected[i].HasValue)
                    Assert.Less(Quaternion.Angle(expected[i].Value, actual[i].Value), tolerance, $"{(HumanBodyBones)i}, {where}");
        }

        internal static Dictionary<string, Keyframe[]> MuscleKeys(AnimationClip clip, Func<HumanBodyPart, bool> include)
        {
            var result = new Dictionary<string, Keyframe[]>();
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
            {
                if (!include(BodyParts.OfMuscle(i))) continue;
                string name = HumanoidCurveNames.MuscleCurveNames[i];
                AnimationCurve c = AnimationUtility.GetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), name));
                if (c != null) result[name] = c.keys;
            }
            return result;
        }

        internal static Dictionary<string, Keyframe[]> AllKeys(AnimationClip clip) =>
            AnimationUtility.GetCurveBindings(clip).ToDictionary(
                b => $"{b.path}|{b.type.Name}|{b.propertyName}", b => AnimationUtility.GetEditorCurve(clip, b).keys);

        /// <summary>
        /// Same curves with the same keys within <paramref name="tolerance"/> (time, value, tangents; infinite
        /// tangents must match exactly). For comparing a copy with the clip it was copied from.
        /// </summary>
        internal static bool SameKeysWithin(Dictionary<string, Keyframe[]> a, Dictionary<string, Keyframe[]> b, float tolerance = 1e-6f)
        {
            if (a.Count != b.Count) return false;
            foreach (KeyValuePair<string, Keyframe[]> kv in a)
            {
                if (!b.TryGetValue(kv.Key, out Keyframe[] other) || other.Length != kv.Value.Length) return false;
                for (int i = 0; i < other.Length; i++)
                    if (!Near(other[i].time, kv.Value[i].time, tolerance) || !Near(other[i].value, kv.Value[i].value, tolerance) ||
                        !Near(other[i].inTangent, kv.Value[i].inTangent, tolerance) || !Near(other[i].outTangent, kv.Value[i].outTangent, tolerance))
                        return false;
            }
            return true;
        }

        static bool Near(float x, float y, float tolerance) => x == y || Mathf.Abs(x - y) <= tolerance;

        internal static bool SameKeys(Dictionary<string, Keyframe[]> a, Dictionary<string, Keyframe[]> b)
        {
            if (a.Count != b.Count) return false;
            foreach (KeyValuePair<string, Keyframe[]> kv in a)
            {
                if (!b.TryGetValue(kv.Key, out Keyframe[] other) || other.Length != kv.Value.Length) return false;
                for (int i = 0; i < other.Length; i++)
                    if (other[i].time != kv.Value[i].time || other[i].value != kv.Value[i].value ||
                        other[i].inTangent != kv.Value[i].inTangent || other[i].outTangent != kv.Value[i].outTangent)
                        return false;
            }
            return true;
        }
    }
}
