using System;
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
    /// Phase 5's "done when" (C16): a new clip made from pose keys plays back in Unity matching the keyed poses on
    /// the key frames; a looping clip's last frame matches its first (its travel carrying on); start poses; easing;
    /// body-part masks and eases of pose keys on a fixed clip; copy / paste between clips; offsets and pins applying
    /// on top of pose keys. All through <see cref="FrameEditSession"/>, the path the window uses; playback is read
    /// from Unity's own evaluation of the written clip.
    /// </summary>
    public class PoseKeyTests
    {
        const float MuscleTolerance = 1e-3f;
        const float HipsTolerance = 0.005f;     // metres
        const float HipsAngleTolerance = 1f;    // degrees
        const float Reached = 0.005f;           // metres

        [SetUp] public void SetUp() => TempAssets.Create();
        [TearDown] public void TearDown() => TempAssets.Delete();
        [OneTimeTearDown] public void Release() => TestRigs.Release();

        [Test]
        public void ANewClip_PlaysBackItsPoseKeysOnTheirFrames_AndBlendsBetweenThem()
        {
            (TestRigs.Rig rig, _) = TestUtil.LongClips().First();
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            ClipEditsAsset edits = NewClip(rig, pc, frames: 20, loop: false);
            using FrameEditSession session = TestEdits.Session(edits, pc);
            HumanBodyBones arm = ArmBone(pc);
            if (arm == HumanBodyBones.LastBone) Assert.Ignore("No mapped arm bone.");
            Transform hips = pc.Animator.GetBoneTransform(HumanBodyBones.Hips);

            foreach (int f in new[] { 0, 10, 20 })
            {
                session.GoToFrame(f);
                Assert.IsTrue(TryTurn(session, pc, arm), $"Couldn't turn the arm on frame {f}.");
                session.Commit();
            }
            session.GoToFrame(10);
            Vector3 keyHipsBefore = edits.Records.OfType<PoseKeyRecord>().Single(k => k.frame == 10).pose.hipsPosition;
            Undo.IncrementCurrentGroup();
            session.MoveHips(hips.position + Vector3.down * 0.04f, hips.rotation);
            session.Commit();

            List<PoseKeyRecord> keys = edits.Records.OfType<PoseKeyRecord>().OrderBy(k => k.frame).ToList();
            CollectionAssert.AreEqual(new[] { 0, 10, 20 }, keys.Select(k => k.frame).ToArray(),
                "Handle edits on a new clip should make one pose key per frame.");
            Assert.AreEqual(keys.Count, edits.Records.Count, "Handle edits on a new clip made other records too.");

            foreach (PoseKeyRecord k in keys)
            {
                session.GoToFrame(k.frame);
                KeyPose shown = session.CopyPose();
                AssertSameMuscles(k.pose.muscles, PlayedMuscles(pc, edits.Output, k.frame), $"frame {k.frame}, Unity's playback");
                Assert.Less((shown.hipsPosition - k.pose.hipsPosition).magnitude, HipsTolerance, $"Hips on frame {k.frame}.");
                Assert.Less(Quaternion.Angle(shown.hipsRotation, k.pose.hipsRotation), HipsAngleTolerance, $"Hips turn on frame {k.frame}.");
            }

            // Between keys every muscle stays within its neighbouring keys (smooth, no overshoot) and moves at all.
            float[] at5 = PlayedMuscles(pc, edits.Output, 5);
            bool moved = false;
            for (int m = 0; m < at5.Length; m++)
            {
                float a = keys[0].pose.muscles[m], b = keys[1].pose.muscles[m];
                Assert.GreaterOrEqual(at5[m], Mathf.Min(a, b) - MuscleTolerance, $"{HumanTrait.MuscleName[m]} overshoots on frame 5.");
                Assert.LessOrEqual(at5[m], Mathf.Max(a, b) + MuscleTolerance, $"{HumanTrait.MuscleName[m]} overshoots on frame 5.");
                if (Mathf.Abs(a - b) > 0.01f && Mathf.Abs(at5[m] - a) > 1e-4f && Mathf.Abs(at5[m] - b) > 1e-4f) moved = true;
            }
            Assert.IsTrue(moved, "Frame 5 doesn't blend between the keys on frames 0 and 10.");

            Undo.PerformUndo();
            session.Reload();
            Assert.AreEqual(3, edits.Records.Count, "Undoing an edit of a pose key should keep the key.");
            Vector3 undone = edits.Records.OfType<PoseKeyRecord>().Single(k => k.frame == 10).pose.hipsPosition;
            Assert.Less((undone - keyHipsBefore).magnitude, 1e-4f, "Undo didn't take back the hips edit of the pose key.");
        }

        [Test]
        public void Easing_HoldLinearAndSmooth_BlendAsNamed()
        {
            (TestRigs.Rig rig, _) = TestUtil.LongClips().First();
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            ClipEditsAsset edits = NewClip(rig, pc, frames: 20, loop: false);
            using FrameEditSession session = TestEdits.Session(edits, pc);
            int m = Array.IndexOf(HumanTrait.MuscleName, "Left Arm Down-Up");
            Assert.GreaterOrEqual(m, 0);

            session.GoToFrame(0);
            KeyPose a = session.CopyPose();
            KeyPose b = a.Clone();
            b.muscles[m] = a.muscles[m] + 0.5f;
            session.PastePose(a);
            session.GoToFrame(20);
            session.PastePose(b);
            int first = edits.Records.FindIndex(r => r is PoseKeyRecord k && k.frame == 0);

            float At(int frame) => PlayedMuscles(pc, edits.Output, frame)[m];

            SetEasing(session, first, PoseEasing.Linear);
            Assert.AreEqual(a.muscles[m] + 0.25f, At(10), MuscleTolerance, "Linear, halfway.");
            Assert.AreEqual(a.muscles[m] + 0.05f, At(2), MuscleTolerance, "Linear, a tenth of the way.");

            SetEasing(session, first, PoseEasing.Hold);
            Assert.AreEqual(a.muscles[m], At(10), MuscleTolerance, "Hold keeps the first key's pose.");
            Assert.AreEqual(a.muscles[m], At(19), MuscleTolerance, "Hold keeps the first key's pose up to the next key.");
            Assert.AreEqual(b.muscles[m], At(20), MuscleTolerance, "Hold reaches the next key on its frame.");

            SetEasing(session, first, PoseEasing.Smooth);
            float s2 = At(2), s10 = At(10);
            Assert.Greater(s2, a.muscles[m] - MuscleTolerance, "Smooth leaves the first key without dipping below it.");
            Assert.Less(s2, a.muscles[m] + 0.05f, "Smooth eases out of the first key (slower than linear).");
            Assert.Greater(s10, a.muscles[m] + 0.1f, "Smooth blends towards the second key.");
            Assert.Less(s10, b.muscles[m] - 0.1f, "Smooth blends towards the second key.");
        }

        [Test]
        public void StartPoses_NeutralStandsOnTheGround_AndAClipFrameKeepsItsStoredPose()
        {
            (TestRigs.Rig rig, AnimationClip source) = TestUtil.LongClips().First();
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);

            ClipEditsAsset neutral = NewClip(rig, pc, frames: 10, loop: false);
            float[] played = PlayedMuscles(pc, neutral.Output, 0);
            Assert.Less(played.Max(Mathf.Abs), MuscleTolerance, "The neutral start pose should play with every muscle 0.");
            Assert.AreEqual(0f, LowestSole(pc, neutral.Output), 0.01f, "The neutral start pose should stand on the ground.");
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(neutral.Output);
            Assert.IsFalse(settings.loopBlendOrientation || settings.loopBlendPositionY || settings.loopBlendPositionXZ,
                "A new clip starts with Bake Into Pose off.");
            Assert.AreEqual(10f / 30f, neutral.Output.length, 1e-5f, "Length.");

            int frame = ClipWriter.LastFrame(source) / 2;
            StartPose fromClip = StartPose.FromClipFrame(source, frame);
            ClipEditsAsset copy = ClipEditsAsset.CreateNew(TempAssets.PathFor("FromClip.anim"), 10, 30f, false, fromClip, rig.Model, rig.Avatar);
            HumanPose stored = new HumanoidClipCurves(source).SamplePose(CurveKeys.FrameTime(frame, source.frameRate));
            HumanPose written = new HumanoidClipCurves(copy.Output).SamplePose(CurveKeys.FrameTime(5, 30f));
            for (int m = 0; m < stored.muscles.Length; m++)
                Assert.AreEqual(stored.muscles[m], written.muscles[m], 1e-5f, HumanTrait.MuscleName[m]);
            Assert.AreEqual(stored.bodyPosition.y, written.bodyPosition.y, 1e-5f, "Body height.");
            Assert.AreEqual(0f, new Vector2(written.bodyPosition.x, written.bodyPosition.z).magnitude, 1e-5f, "Body moved over the origin.");
            Assert.Less(Quaternion.Angle(stored.bodyRotation, written.bodyRotation), 0.01f, "Body facing.");
        }

        [Test]
        public void ALoopingNewClip_EndsOnItsFirstPose_WhileItsTravelCarriesOn()
        {
            (TestRigs.Rig rig, _) = TestUtil.LongClips().First();
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            ClipEditsAsset edits = NewClip(rig, pc, frames: 20, loop: true);
            using FrameEditSession session = TestEdits.Session(edits, pc);
            HumanBodyBones arm = ArmBone(pc);
            if (arm == HumanBodyBones.LastBone) Assert.Ignore("No mapped arm bone.");
            Transform hips = pc.Animator.GetBoneTransform(HumanBodyBones.Hips);
            Vector3 forward = pc.Root.transform.forward;

            session.GoToFrame(0);
            Assert.IsTrue(TryTurn(session, pc, arm), "Couldn't turn the arm on frame 0.");
            session.Commit();
            session.GoToFrame(10);
            session.MoveHips(hips.position + forward * 0.2f, hips.rotation);
            session.Commit();
            session.GoToFrame(20);
            session.MoveHips(hips.position + forward * 0.2f, hips.rotation);
            session.Commit();

            AssertSameMuscles(PlayedMuscles(pc, edits.Output, 0), PlayedMuscles(pc, edits.Output, 20), "last frame vs first");
            session.GoToFrame(0);
            KeyPose start = session.CopyPose();
            session.GoToFrame(20);
            KeyPose end = session.CopyPose();
            Assert.AreEqual(start.hipsPosition.y, end.hipsPosition.y, HipsTolerance, "The hips' height should loop.");
            float travelled = Vector3.Dot(end.hipsPosition - start.hipsPosition, Quaternion.Inverse(pc.Root.transform.rotation) * forward);
            Assert.Greater(travelled, 0.3f, "The hips should travel on through the loop, not return to the start.");
        }

        [Test]
        public void APoseKeyOnAFixedClip_PosesOnlyItsParts_AndEasesInAndOutOfTheClip()
        {
            (TestRigs.Rig rig, AnimationClip source) = TestUtil.LongClips().First();
            ClipEditsAsset edits = TestEdits.Create(rig, source);
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            using FrameEditSession session = TestEdits.Session(edits, pc, ease: 3);
            int mid = MidFrame(session);
            if (mid - 5 < 0 || mid + 5 > session.LastFrame) Assert.Ignore("Clip too short for a 3-frame ease.");
            int m = Array.IndexOf(HumanTrait.MuscleName, "Left Arm Down-Up");
            string where = TestUtil.Describe(rig, source);

            session.GoToFrame(mid);
            KeyPose hipsBefore = session.CopyPose();
            float sourceValue = PlayedMuscles(pc, edits.Output, mid)[m];
            int index = session.AddPoseKey();
            float target = Mathf.Clamp(sourceValue + 0.3f, -1f, 1f);
            session.ChangeRecords("Pose the left arm", l =>
            {
                var k = (PoseKeyRecord)l[index];
                k.mask = PoseKeyRecord.Bit(HumanBodyPart.LeftArm);
                k.pose.muscles[m] = target;
                for (int i = 0; i < k.pose.muscles.Length; i++)
                    if (BodyParts.OfMuscle(i) != HumanBodyPart.LeftArm) k.pose.muscles[i] += 0.2f; // masked out: must not apply
                k.pose.hipsPosition += Vector3.up; // masked out: must not apply
            });

            Assert.AreEqual(target, PlayedMuscles(pc, edits.Output, mid)[m], MuscleTolerance, "The key's frame, " + where);
            Assert.IsTrue(SameKeysWithin(MuscleKeys(source, p => p != HumanBodyPart.LeftArm),
                    MuscleKeys(edits.Output, p => p != HumanBodyPart.LeftArm)),
                "Muscles outside the key's mask changed, " + where);
            session.GoToFrame(mid);
            Assert.Less((session.CopyPose().hipsPosition - hipsBefore.hipsPosition).magnitude, HipsTolerance,
                "A key posing only the left arm moved the hips, " + where);

            foreach (int side in new[] { -1, 1 })
            {
                float d1 = Mathf.Abs(PlayedMuscles(pc, edits.Output, mid + side)[m] - PlayedMuscles(pc, edits.Source, mid + side)[m]);
                float d3 = Mathf.Abs(PlayedMuscles(pc, edits.Output, mid + 3 * side)[m] - PlayedMuscles(pc, edits.Source, mid + 3 * side)[m]);
                Assert.Greater(d3, 1e-3f, $"Frame {mid + 3 * side} (end of the ease) didn't move, {where}");
                Assert.Greater(d1, d3, $"The ease doesn't fade towards frame {mid + 3 * side}, {where}");
            }
            for (int f = 0; f <= session.LastFrame; f++)
            {
                if (Mathf.Abs(f - mid) < 4) continue;
                Assert.Less(TestEdits.MaxDifference(TestEdits.ValuesAt(source, f), TestEdits.ValuesAt(edits.Output, f)),
                    1e-5f, $"Frame {f}'s curves differ from the source, {where}");
            }
        }

        [Test]
        public void CopyAndPaste_CarryAPoseFromOneClipToAnother()
        {
            (TestRigs.Rig rig, AnimationClip source) = TestUtil.LongClips().First();
            KeyPose copied;
            using (PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar))
            using (FrameEditSession fixing = TestEdits.Session(TestEdits.Create(rig, source), pc))
            {
                fixing.GoToFrame(MidFrame(fixing));
                copied = fixing.CopyPose();
            }
            PoseClipboard.Copy(copied);
            Assert.IsTrue(PoseClipboard.TryGet(out KeyPose fromClipboard), "The clipboard didn't keep the pose.");
            AssertSameMuscles(copied.muscles, fromClipboard.muscles, "clipboard round trip");

            using (PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar))
            {
                ClipEditsAsset edits = NewClip(rig, pc, frames: 10, loop: false);
                using FrameEditSession session = TestEdits.Session(edits, pc);
                session.GoToFrame(5);
                session.PastePose(fromClipboard);
                AssertSameMuscles(copied.muscles, PlayedMuscles(pc, edits.Output, 5), "pasted frame, Unity's playback");
                session.GoToFrame(5);
                KeyPose shown = session.CopyPose();
                Assert.Less((shown.hipsPosition - copied.hipsPosition).magnitude, HipsTolerance, "Pasted hips.");
                Assert.Less(Quaternion.Angle(shown.hipsRotation, copied.hipsRotation), HipsAngleTolerance, "Pasted hips turn.");
            }
        }

        [Test]
        public void OffsetsAndPins_ApplyOnTopOfPoseKeys()
        {
            (TestRigs.Rig rig, _) = TestUtil.LongClips().First();
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            ClipEditsAsset edits = NewClip(rig, pc, frames: 20, loop: false);
            using FrameEditSession session = TestEdits.Session(edits, pc);
            if (!session.CanMove(AvatarIKGoal.LeftFoot)) Assert.Ignore("No fully mapped left leg.");
            Transform foot = pc.Animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform hips = pc.Animator.GetBoneTransform(HumanBodyBones.Hips);

            session.GoToFrame(10);
            Vector3 spot = foot.position;
            float hipsHeight = hips.position.y;
            session.AddPin(AvatarIKGoal.LeftFoot, PinAnchor.World);
            session.MoveHips(hips.position + Vector3.down * 0.05f, hips.rotation);
            session.Commit();

            Assert.IsTrue(edits.Records.Any(r => r is PoseKeyRecord k && k.frame == 10), "Moving the hips should make a pose key.");
            int pin = edits.Records.FindIndex(r => r is PinRecord);
            float shortfall = session.LastBake.ReachShortfall[pin];
            session.GoToFrame(10);
            Assert.Less(hips.position.y, hipsHeight - 0.04f, "The pose key didn't lower the hips.");
            Assert.Less((foot.position - spot).magnitude, Reached + shortfall, "The pin on top didn't hold the foot.");
            Assert.Less(shortfall, 0.01f, "Lowering the hips 5 cm should leave the foot in reach.");
        }

        [Test]
        public void ANewClip_OnAPrefabWhoseAnimatorIsOnAChild_ShowsItsPose_AndAFootHandleMovesTheFoot()
        {
            (TestRigs.Rig rig, _) = TestUtil.LongClips().First();
            GameObject prefab = TestEdits.NestedPrefab(rig);
            using PreviewCharacter pc = PreviewCharacter.Create(prefab, rig.Avatar);
            ClipEditsAsset edits = ClipEditsAsset.CreateNew(TempAssets.PathFor("Nested.anim"), 20, 30f, false,
                StartPose.Neutral(pc), prefab, rig.Avatar);
            using FrameEditSession session = TestEdits.Session(edits, pc);
            if (!session.CanMove(AvatarIKGoal.LeftFoot)) Assert.Ignore("No fully mapped left leg.");
            Transform foot = pc.Animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform thigh = pc.Animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);

            // The frame is shown on the bones: they match the neutral pose applied to them directly.
            session.GoToFrame(10);
            Quaternion?[] shown = TestUtil.LocalRotations(pc.Animator);
            HumanPose neutral = pc.ReadPose();
            neutral.muscles = new float[HumanTrait.MuscleCount];
            pc.ApplyPose(neutral);
            AssertRotations(TestUtil.LocalRotations(pc.Animator), shown, 1f, "the shown frame vs the neutral pose");
            session.GoToFrame(10);

            Vector3 target = foot.position + (thigh.position - foot.position).normalized * 0.04f;
            session.MoveEffector(AvatarIKGoal.LeftFoot, target, foot.rotation);
            Assert.Less(session.ReachShortfall ?? 1f, Reached, "The foot didn't reach a handle 4 cm towards the hip.");
            session.Commit();
            Assert.IsTrue(edits.Records.Any(r => r is PoseKeyRecord k && k.frame == 10), "Moving the foot should key frame 10.");
            session.GoToFrame(10);
            Assert.Less((foot.position - target).magnitude, Reached, "The written frame doesn't put the foot at the handle.");
        }

        // ---------------------------------------------------------------- helpers

        static ClipEditsAsset NewClip(TestRigs.Rig rig, PreviewCharacter pc, int frames, bool loop) =>
            ClipEditsAsset.CreateNew(TempAssets.PathFor("New.anim"), frames, 30f, loop, StartPose.Neutral(pc), rig.Model, rig.Avatar);

        static void SetEasing(FrameEditSession session, int index, PoseEasing easing) =>
            session.ChangeRecords("Easing", l => ((PoseKeyRecord)l[index]).easing = easing);

        /// <summary>The muscles Unity plays for <paramref name="clip"/> on <paramref name="frame"/> (its curves as stored).</summary>
        internal static float[] PlayedMuscles(PreviewCharacter pc, AnimationClip clip, int frame)
        {
            AnimationClip raw = RawClipCopy.Create(clip);
            try { return HumanStreamProbe.EvaluateClip(pc.Animator, raw, CurveKeys.FrameTime(frame, clip.frameRate)).Muscles; }
            finally { Object.DestroyImmediate(raw); }
        }

        internal static void AssertSameMuscles(float[] expected, float[] actual, string where)
        {
            for (int m = 0; m < expected.Length; m++)
                Assert.AreEqual(expected[m], actual[m], MuscleTolerance, $"{HumanTrait.MuscleName[m]}, {where}");
        }

        /// <summary>The lower foot sole's height when <paramref name="clip"/> plays frame 0 with the character at the origin.</summary>
        static float LowestSole(PreviewCharacter pc, AnimationClip clip)
        {
            pc.Root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            AnimationClip raw = RawClipCopy.Create(clip);
            try
            {
                HumanStreamSample s = HumanStreamProbe.EvaluateClip(pc.Animator, raw, 0f, goalsFromPose: true);
                float lowest = float.PositiveInfinity;
                foreach (AvatarIKGoal foot in new[] { AvatarIKGoal.LeftFoot, AvatarIKGoal.RightFoot })
                {
                    int g = (int)foot;
                    float height = foot == AvatarIKGoal.LeftFoot ? s.LeftFootHeight : s.RightFootHeight;
                    lowest = Mathf.Min(lowest, (s.GoalPositionFromPose[g] + s.GoalLocalRotation[g] * new Vector3(height, 0f, 0f)).y);
                }
                return lowest;
            }
            finally { Object.DestroyImmediate(raw); }
        }
    }
}
