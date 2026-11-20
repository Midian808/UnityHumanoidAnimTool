using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using static Midian.HumanoidClipEditor.Tests.FrameEditTests;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>
    /// Phase 3's "done when", through the adjustment layer: moving a hand or foot with Unity's IK (C4, through
    /// <see cref="FrameEditSession"/> as the window does) stores a fix whose bake plays back with it at the target, or
    /// as close as the muscle limits allow with the shortfall reported (C8); the elbow / knee hint sets the bend; only
    /// that limb changes (C17).
    /// </summary>
    public class LimbIKTests
    {
        const float Reached = 0.005f;          // metres: counts as at the target
        const float EditedTolerance = 0.2f;    // degrees
        const float UntouchedTolerance = 0.01f; // degrees

        [SetUp] public void SetUp() => TempAssets.Create();
        [TearDown] public void TearDown() => TempAssets.Delete();
        [OneTimeTearDown] public void Release() => TestRigs.Release();

        [Test]
        public void MovingAHand_ReachesTheTarget_AndPlaysBackThere() => MoveLimbOnEveryRig(AvatarIKGoal.LeftHand);

        [Test]
        public void MovingAFoot_ReachesTheTarget_AndPlaysBackThere() => MoveLimbOnEveryRig(AvatarIKGoal.LeftFoot);

        [Test]
        public void AnUnreachableTarget_ReportsTheShortfall_AndWritesWhatThePreviewShows()
        {
            (TestRigs.Rig rig, AnimationClip source) = TestUtil.LongClips().First();
            WithSession(rig, source, (session, pc) =>
            {
                if (!session.CanMove(AvatarIKGoal.LeftHand)) Assert.Ignore("Left arm not mapped.");
                Transform hand = Bone(pc, HumanBodyBones.LeftHand), shoulder = Bone(pc, HumanBodyBones.LeftUpperArm);
                Vector3 far = shoulder.position + (hand.position - shoulder.position).normalized * 3f;
                session.MoveEffector(AvatarIKGoal.LeftHand, far, hand.rotation, session.DefaultHint(AvatarIKGoal.LeftHand));

                Assert.Greater(session.ReachShortfall ?? 0f, 2f, "A target 3 m from the shoulder should be reported as out of reach.");
                Quaternion?[] previewed = TestUtil.LocalRotations(pc.Animator);
                Vector3 previewedHand = hand.position;
                session.Commit();
                AssertRotations(previewed, TestUtil.LocalRotations(pc.Animator), EditedTolerance, TestUtil.Describe(rig, source));
                Assert.Less((hand.position - previewedHand).magnitude, Reached, "The hand plays back where the preview showed it.");
            });
        }

        [Test]
        public void TheHint_ChoosesWhichWayTheElbowBends()
        {
            (TestRigs.Rig rig, AnimationClip source) = TestUtil.LongClips().First();
            WithSession(rig, source, (session, pc) =>
            {
                if (!session.CanMove(AvatarIKGoal.LeftHand)) Assert.Ignore("Left arm not mapped.");
                Transform hand = Bone(pc, HumanBodyBones.LeftHand), elbow = Bone(pc, HumanBodyBones.LeftLowerArm),
                          shoulder = Bone(pc, HumanBodyBones.LeftUpperArm);
                Vector3 target = hand.position;
                Quaternion turn = hand.rotation;
                Vector3 axis = (hand.position - shoulder.position).normalized;
                Vector3 bendHint = session.DefaultHint(AvatarIKGoal.LeftHand);
                Vector3 Swing(float degrees) =>
                    shoulder.position + Quaternion.AngleAxis(degrees, axis) * (bendHint - shoulder.position);

                session.MoveEffector(AvatarIKGoal.LeftHand, target, turn, Swing(30f));
                Vector3 elbowA = elbow.position;
                float reachA = session.ReachShortfall ?? 1f;
                session.MoveEffector(AvatarIKGoal.LeftHand, target, turn, Swing(-30f));
                Vector3 elbowB = elbow.position;
                float reachB = session.ReachShortfall ?? 1f;

                string where = TestUtil.Describe(rig, source);
                Assert.Less(reachA, Reached, "Hint one way: the hand should stay put, " + where);
                Assert.Less(reachB, Reached, "Hint the other way: the hand should stay put, " + where);
                Assert.Greater((elbowA - elbowB).magnitude, 0.01f, "Moving the hint didn't move the elbow, " + where);
            });
        }

        [Test]
        public void TheDefaultHint_KeepsTheClipsBend()
        {
            foreach ((TestRigs.Rig rig, AnimationClip source) in TestUtil.LongClips())
            {
                WithSession(rig, source, (session, pc) =>
                {
                    if (!session.CanMove(AvatarIKGoal.LeftHand)) return;
                    Transform hand = Bone(pc, HumanBodyBones.LeftHand), elbow = Bone(pc, HumanBodyBones.LeftLowerArm),
                              shoulder = Bone(pc, HumanBodyBones.LeftUpperArm);
                    Vector3 elbowBefore = elbow.position;
                    // 2 mm towards the shoulder: always reachable, and not zero, so the solver runs.
                    Vector3 target = hand.position + (shoulder.position - hand.position).normalized * 0.002f;
                    session.MoveEffector(AvatarIKGoal.LeftHand, target, hand.rotation,
                        session.DefaultHint(AvatarIKGoal.LeftHand));
                    string where = TestUtil.Describe(rig, source);
                    Assert.Less(session.ReachShortfall ?? 1f, Reached, "Asking for the hand 2 mm nearer the shoulder, " + where);
                    Assert.Less((elbow.position - elbowBefore).magnitude, 0.01f, "The elbow moved without being asked to, " + where);
                });
            }
        }

        /// <summary>
        /// On every sample character: move <paramref name="goal"/>'s hand / foot 4 cm, then check the preview, the
        /// written frame, the neighbouring frames, the other body parts and the goal curve.
        /// </summary>
        static void MoveLimbOnEveryRig(AvatarIKGoal goal)
        {
            int moved = 0;
            foreach ((TestRigs.Rig rig, AnimationClip source) in TestUtil.LongClips())
            {
                WithSession(rig, source, (session, pc) =>
                {
                    if (!session.CanMove(goal)) return;
                    HumanBodyPart part = LimbIK.Part(goal);
                    string where = $"{goal}, {TestUtil.Describe(rig, source)}";
                    int frame = session.Frame;
                    float time = session.Time;
                    AnimationClip clip = session.Clip;

                    session.GoToFrame(frame - 1);
                    Quaternion?[] prev = TestUtil.LocalRotations(pc.Animator);
                    session.GoToFrame(frame + 1);
                    Quaternion?[] next = TestUtil.LocalRotations(pc.Animator);
                    session.GoToFrame(frame);
                    Quaternion?[] playedLocal = TestUtil.LocalRotations(pc.Animator);
                    Vector3?[] playedLocalPositions = LocalPositions(pc.Animator);
                    Dictionary<string, Keyframe[]> otherParts = MuscleKeys(clip, p => p != part);

                    Vector3 target = ReachableTarget(session, pc, goal, out Quaternion turn);
                    Assert.IsFalse(float.IsNaN(target.x), "No nearby target was reachable, " + where);
                    Transform end = Bone(pc, LimbIK.EndBone(goal));

                    // The preview: only the limb moved, and every bone kept its played local position (Translation DoF).
                    AssertRotations(OnlyParts(playedLocal, p => p != part), TestUtil.LocalRotations(pc.Animator),
                        UntouchedTolerance, "preview, outside the limb, " + where);
                    Vector3?[] previewPositions = LocalPositions(pc.Animator);
                    for (int i = 0; i < previewPositions.Length; i++)
                        if (playedLocalPositions[i].HasValue)
                            Assert.Less((previewPositions[i].Value - playedLocalPositions[i].Value).magnitude, 1e-5f,
                                $"preview local position of {(HumanBodyBones)i}, {where}");
                    Quaternion?[] previewed = TestUtil.LocalRotations(pc.Animator);

                    session.Commit();
                    Assert.Less((end.position - target).magnitude, Reached, "The written frame doesn't put it at the target, " + where);
                    AssertRotations(previewed, TestUtil.LocalRotations(pc.Animator), EditedTolerance, "written frame vs preview, " + where);
                    session.GoToFrame(frame - 1);
                    AssertRotations(prev, TestUtil.LocalRotations(pc.Animator), UntouchedTolerance, "previous frame, " + where);
                    session.GoToFrame(frame + 1);
                    AssertRotations(next, TestUtil.LocalRotations(pc.Animator), UntouchedTolerance, "next frame, " + where);
                    Assert.IsTrue(SameKeys(otherParts, MuscleKeys(clip, p => p != part)),
                        "Muscle curves outside the limb changed, " + where);

                    var curves = new HumanoidClipCurves(clip);
                    if (curves.HasGoal(goal))
                    {
                        AnimationClip raw = RawClipCopy.Create(clip);
                        try
                        {
                            GoalCurveValues expected = GoalCurves.FromClipFrame(pc.Animator, raw, time)[(int)goal];
                            curves.SampleGoal(goal, time, out Vector3 t, out Quaternion q);
                            Assert.Less((t - expected.Position).magnitude, 1e-3f, "Goal position curve, " + where);
                            Assert.Less(Quaternion.Angle(q, expected.Rotation), 0.1f, "Goal rotation curve, " + where);
                        }
                        finally { Object.DestroyImmediate(raw); }
                    }
                    moved++;
                });
            }
            if (moved == 0) Assert.Ignore($"No sample character has a fully mapped {goal} limb.");
        }

        /// <summary>
        /// Asks for the hand / foot 4 cm away in a few directions (towards the limb's root first: always shorter) and
        /// keeps the first one the solver reaches within <see cref="Reached"/>, leaving that edit pending. NaN if none.
        /// </summary>
        static Vector3 ReachableTarget(FrameEditSession session, PreviewCharacter pc, AvatarIKGoal goal, out Quaternion turn)
        {
            Transform end = Bone(pc, LimbIK.EndBone(goal)), root = Bone(pc, LimbIK.RootBone(goal));
            Vector3 start = end.position;
            turn = end.rotation;
            Vector3 hint = session.DefaultHint(goal);
            Vector3[] directions =
            {
                (root.position - start).normalized, Vector3.up, Vector3.forward, Vector3.back, Vector3.left, Vector3.right,
            };
            foreach (Vector3 d in directions)
            {
                Vector3 target = start + d * 0.04f;
                session.MoveEffector(goal, target, turn, hint);
                if (session.ReachShortfall < Reached) return target;
                session.Cancel();
            }
            return new Vector3(float.NaN, float.NaN, float.NaN);
        }

        static void WithSession(TestRigs.Rig rig, AnimationClip source,
                                System.Action<FrameEditSession, PreviewCharacter> body)
        {
            ClipEditsAsset edits = TestEdits.Create(rig, source);
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            using FrameEditSession session = TestEdits.Session(edits, pc);
            session.GoToFrame(MidFrame(session));
            body(session, pc);
        }

        static Transform Bone(PreviewCharacter pc, HumanBodyBones b) => pc.Animator.GetBoneTransform(b);

        static Vector3?[] LocalPositions(Animator a) =>
            Enumerable.Range(0, (int)HumanBodyBones.LastBone)
                .Select(i => a.GetBoneTransform((HumanBodyBones)i) is Transform t && t ? t.localPosition : (Vector3?)null)
                .ToArray();
    }
}
