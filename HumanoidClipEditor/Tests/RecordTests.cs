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
    /// Phase 4's "done when" (C13, C14): re-baking after editing, muting or deleting a fix reproduces the expected
    /// output, frames no fix touches match the source exactly, eases blend, fixes apply in list order, pins hold a
    /// hand or foot to the world, a bone or a prop, and a looping clip's fix wraps around its ends. All through
    /// <see cref="FrameEditSession"/>, the path the window uses.
    /// </summary>
    public class RecordTests
    {
        const float Reached = 0.005f;          // metres: counts as at the spot
        const float EditedTolerance = 0.2f;    // degrees: a frame plays back as previewed
        const float UntouchedTolerance = 0.01f; // degrees
        const float ValueTolerance = 1e-5f;    // curve values on untouched frames

        [SetUp] public void SetUp() => TempAssets.Create();
        [TearDown] public void TearDown() => TempAssets.Delete();
        [OneTimeTearDown] public void Release() => TestRigs.Release();

        [Test]
        public void AnEasedFix_BlendsInAndOut_AndLeavesOtherFramesAsTheSource()
        {
            (TestRigs.Rig rig, AnimationClip source) = TestUtil.LongClips().First();
            ClipEditsAsset edits = TestEdits.Create(rig, source);
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            using FrameEditSession session = TestEdits.Session(edits, pc, ease: 3);
            HumanBodyBones bone = ArmBone(pc);
            if (bone == HumanBodyBones.LastBone) Assert.Ignore("No mapped arm bone.");
            int mid = MidFrame(session);
            if (mid - 4 < 0 || mid + 4 > session.LastFrame) Assert.Ignore("Clip too short for a 3-frame ease.");
            string where = TestUtil.Describe(rig, source);

            Transform t = pc.Animator.GetBoneTransform(bone);
            Quaternion[] played = PlayedLocal(session, t);
            session.GoToFrame(mid);
            Assert.IsTrue(TryTurn(session, pc, bone), "Couldn't turn the arm, " + where);
            Quaternion edited = t.localRotation;
            session.Commit();

            session.GoToFrame(mid);
            Assert.Less(Quaternion.Angle(edited, t.localRotation), EditedTolerance, "The fixed frame, " + where);
            foreach (int side in new[] { -1, 1 })
            {
                float a1 = AngleAt(session, t, played, mid + side);
                float a3 = AngleAt(session, t, played, mid + 3 * side);
                Assert.Greater(a3, 0.05f, $"Frame {mid + 3 * side} (end of the ease) didn't move, {where}");
                Assert.Greater(a1, a3, $"The ease doesn't fade towards frame {mid + 3 * side}, {where}");
            }
            for (int f = 0; f <= session.LastFrame; f++)
            {
                if (Mathf.Abs(f - mid) < 4) continue;
                Assert.Less(AngleAt(session, t, played, f), UntouchedTolerance, $"Frame {f} moved, {where}");
                Assert.Less(TestEdits.MaxDifference(TestEdits.ValuesAt(source, f), TestEdits.ValuesAt(edits.Output, f)),
                    ValueTolerance, $"Frame {f}'s curves differ from the source, {where}");
            }
        }

        [Test]
        public void MutingUnmutingAndDeleting_RebuildFromTheSource()
        {
            (TestRigs.Rig rig, AnimationClip source) = TestUtil.LongClips().First();
            ClipEditsAsset edits = TestEdits.Create(rig, source);
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            using FrameEditSession session = TestEdits.Session(edits, pc, ease: 2);
            session.GoToFrame(MidFrame(session));
            Assert.IsTrue(TryTurn(session, pc, ArmBone(pc)), "Couldn't turn the arm.");
            session.Commit();
            AnimationClip output = edits.Output;
            Dictionary<string, Keyframe[]> sourceKeys = AllKeys(source);
            List<Dictionary<string, float>> baked = AllFrames(output, session.LastFrame);
            Assert.IsFalse(SameKeys(sourceKeys, AllKeys(output)), "The fix changed nothing.");

            session.ChangeRecords("Mute", l => l[0].muted = true);
            Assert.IsTrue(SameKeysWithin(sourceKeys, AllKeys(output)), "With the fix muted the output should be the source.");

            session.ChangeRecords("Unmute", l => l[0].muted = false);
            List<Dictionary<string, float>> again = AllFrames(output, session.LastFrame);
            for (int f = 0; f < baked.Count; f++)
                Assert.Less(TestEdits.MaxDifference(baked[f], again[f]), ValueTolerance, $"Frame {f} isn't baked the same again.");

            session.ChangeRecords("Delete", l => l.Clear());
            Assert.IsTrue(SameKeysWithin(sourceKeys, AllKeys(output)), "With no fixes the output should be the source.");
        }

        [Test]
        public void EditingAFixAgain_ReplacesItsKey_AndAnotherFrameAddsOne()
        {
            (TestRigs.Rig rig, AnimationClip source) = TestUtil.LongClips().First();
            ClipEditsAsset edits = TestEdits.Create(rig, source);
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            using FrameEditSession session = TestEdits.Session(edits, pc);
            HumanBodyBones bone = ArmBone(pc);
            int mid = MidFrame(session);
            int other = Mathf.Min(mid + 2, session.LastFrame);
            Assume.That(other > mid, "Clip too short.");
            Transform t = pc.Animator.GetBoneTransform(bone);

            session.GoToFrame(mid);
            Assert.IsTrue(TryTurn(session, pc, bone), "Couldn't turn the arm.");
            session.Commit();
            Assert.AreEqual(0, session.SelectedRecord, "A new fix should be selected.");

            // Turning again on the same frame changes that key; nothing is stacked on top.
            session.GoToFrame(mid);
            Assert.IsTrue(TryTurn(session, pc, bone), "Couldn't turn the arm again.");
            Quaternion atMid = t.localRotation;
            session.Commit();
            Assert.AreEqual(1, edits.Records.Count, "Editing the selected fix made a new one.");
            Assert.AreEqual(1, ((OffsetRecord)edits.Records[0]).keys.Count, "Turning on the same frame added a key.");
            session.GoToFrame(mid);
            Assert.Less(Quaternion.Angle(atMid, t.localRotation), EditedTolerance, "The re-edited frame.");

            // Turning on another frame adds a key there.
            session.GoToFrame(other);
            Assert.IsTrue(TryTurn(session, pc, bone), "Couldn't turn the arm on the other frame.");
            Quaternion atOther = t.localRotation;
            session.Commit();
            Assert.AreEqual(2, ((OffsetRecord)edits.Records[0]).keys.Count, "A second frame didn't add a key.");
            session.GoToFrame(other);
            Assert.Less(Quaternion.Angle(atOther, t.localRotation), EditedTolerance, "The second key's frame.");
            session.GoToFrame(mid);
            Assert.Less(Quaternion.Angle(atMid, t.localRotation), EditedTolerance, "The first key's frame after adding a second.");
        }

        [Test]
        public void AWorldPin_HoldsTheHandStill()
        {
            int meaningful = 0;
            foreach ((TestRigs.Rig rig, AnimationClip source) in TestUtil.LongClips())
            {
                ClipEditsAsset edits = TestEdits.Create(rig, source);
                using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
                using FrameEditSession session = TestEdits.Session(edits, pc);
                if (!session.CanMove(AvatarIKGoal.LeftHand) || !Range(session, out int first, out int last)) continue;
                Transform hand = pc.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
                string where = TestUtil.Describe(rig, source);

                session.GoToFrame(first);
                Vector3 spot = hand.position;
                float wander = 0f;
                for (int f = first; f <= last; f++) { session.GoToFrame(f); wander = Mathf.Max(wander, (hand.position - spot).magnitude); }

                session.GoToFrame(first);
                int index = session.AddPin(AvatarIKGoal.LeftHand, PinAnchor.World);
                session.ChangeRecords("Pin range", l => ((PinRecord)l[index]).end = last);
                float shortfall = session.LastBake.ReachShortfall[index];
                for (int f = first; f <= last; f++)
                {
                    session.GoToFrame(f);
                    Assert.Less((hand.position - spot).magnitude, Reached + shortfall, $"Frame {f}, {where}");
                }
                if (wander > 0.01f && shortfall < 0.01f) meaningful++;
            }
            if (meaningful == 0) Assert.Ignore("No sample clip moves the left hand more than 1 cm over five frames within reach.");
        }

        [Test]
        public void ABonePin_KeepsTheHandOnTheOtherHand()
        {
            int meaningful = 0;
            foreach ((TestRigs.Rig rig, AnimationClip source) in TestUtil.LongClips())
            {
                ClipEditsAsset edits = TestEdits.Create(rig, source);
                using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
                using FrameEditSession session = TestEdits.Session(edits, pc);
                Transform right = pc.Animator.GetBoneTransform(HumanBodyBones.RightHand);
                if (!session.CanMove(AvatarIKGoal.LeftHand) || !right || !Range(session, out int first, out int last)) continue;
                Transform left = pc.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
                string where = TestUtil.Describe(rig, source);

                session.GoToFrame(first);
                Vector3 held = Relative(right, left.position);
                float wander = 0f;
                for (int f = first; f <= last; f++) { session.GoToFrame(f); wander = Mathf.Max(wander, (Relative(right, left.position) - held).magnitude); }

                session.GoToFrame(first);
                int index = session.AddPin(AvatarIKGoal.LeftHand, PinAnchor.Bone, HumanBodyBones.RightHand);
                session.ChangeRecords("Pin range", l => ((PinRecord)l[index]).end = last);
                float shortfall = session.LastBake.ReachShortfall[index];
                for (int f = first; f <= last; f++)
                {
                    session.GoToFrame(f);
                    Assert.Less((Relative(right, left.position) - held).magnitude, Reached + shortfall, $"Frame {f}, {where}");
                }
                if (wander > 0.01f && shortfall < 0.01f) meaningful++;
            }
            if (meaningful == 0) Assert.Ignore("No sample clip moves the hands more than 1 cm apart over five frames within reach.");
        }

        [Test]
        public void APropPin_FollowsTheProp()
        {
            (TestRigs.Rig rig, AnimationClip source) = TestUtil.LongClips().First();
            ClipEditsAsset edits = TestEdits.Create(rig, source);
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            using FrameEditSession session = TestEdits.Session(edits, pc);
            Transform right = pc.Animator.GetBoneTransform(HumanBodyBones.RightHand);
            bool longEnough = Range(session, out int first, out int last);
            if (!session.CanMove(AvatarIKGoal.LeftHand) || !right || !longEnough)
                Assert.Ignore("Needs both hands and a clip of at least 5 frames.");
            Transform left = pc.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
            GameObject model = MakePropModel();

            // A prop 10 cm out from the right hand, along the line to the left hand.
            session.GoToFrame(first);
            Vector3 towards = (left.position - right.position).normalized;
            Vector3 local = right.InverseTransformPoint(right.position + towards * 0.1f);
            session.ChangeProps("Add Prop", l => l.Add(new ReferenceProp { prefab = model, bone = HumanBodyBones.RightHand, position = local }));
            ReferenceProp prop = edits.Props[0];

            GameObject shown = session.Props.InstanceOf(prop.id);
            Assert.IsNotNull(shown, "The prop isn't shown.");
            Assert.IsTrue(prop.TryGetPose(pc.Animator, out Vector3 propAt, out Quaternion propRot));
            Assert.Less((shown.transform.position - propAt).magnitude, 1e-4f, "The shown prop isn't where its pose says.");
            Assert.Less(Quaternion.Angle(shown.transform.rotation, propRot), 0.01f, "The shown prop isn't turned as its pose says.");
            Assert.AreEqual(HideFlags.DontSave, shown.hideFlags & HideFlags.DontSave, "The shown prop would be saved with the scene.");

            int index = session.AddPin(AvatarIKGoal.LeftHand, PinAnchor.Prop, propId: prop.id);
            session.ChangeRecords("Pin range", l => ((PinRecord)l[index]).end = last);
            Vector3 held = RelativeToProp(prop, pc, left.position);
            float shortfall = session.LastBake.ReachShortfall[index];
            for (int f = first; f <= last; f++)
            {
                session.GoToFrame(f);
                Assert.Less((RelativeToProp(prop, pc, left.position) - held).magnitude, Reached + shortfall, $"Frame {f}");
            }

            // Moving the prop on its bone moves the hand pinned to it.
            session.GoToFrame(first);
            Vector3 handBefore = left.position;
            Vector3 movedLocal = right.InverseTransformPoint(right.position + towards * 0.13f);
            session.ChangeProps("Move Prop", l => l[0].position = movedLocal);
            prop = edits.Props[0];
            session.GoToFrame(first);
            shortfall = session.LastBake.ReachShortfall[index];
            Assert.Less((RelativeToProp(prop, pc, left.position) - held).magnitude, Reached + shortfall, "After moving the prop.");
            Assert.Greater((left.position - handBefore).magnitude, 0.02f, "Moving the prop didn't move the pinned hand.");
        }

        [Test]
        public void FixesApplyInListOrder_AFootPinnedAfterTheHipsDropStaysPut()
        {
            int done = 0;
            foreach ((TestRigs.Rig rig, AnimationClip source) in TestUtil.LongClips())
            {
                ClipEditsAsset edits = TestEdits.Create(rig, source);
                using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
                using FrameEditSession session = TestEdits.Session(edits, pc);
                if (!session.CanMove(AvatarIKGoal.LeftFoot)) continue;
                string where = TestUtil.Describe(rig, source);
                int mid = MidFrame(session);
                Transform foot = pc.Animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                Transform hips = pc.Animator.GetBoneTransform(HumanBodyBones.Hips);

                session.GoToFrame(mid);
                Vector3 spot = foot.position;
                Quaternion spotRotation = foot.rotation;
                float hipsHeight = hips.position.y;
                session.MoveHips(hips.position + Vector3.down * 0.05f, hips.rotation);
                session.Commit();
                session.GoToFrame(mid);
                Assert.Less(hips.position.y, hipsHeight - 0.04f, "The hips didn't drop, " + where);
                Assert.Greater((foot.position - spot).magnitude, 0.03f, "The foot didn't drop with the hips, " + where);

                session.ChangeRecords("Pin", l => l.Add(new PinRecord
                {
                    limb = AvatarIKGoal.LeftFoot, start = mid, end = mid, anchor = PinAnchor.World,
                    position = spot, rotation = spotRotation,
                }));
                float shortfall = session.LastBake.ReachShortfall[1];
                session.GoToFrame(mid);
                Assert.Less((foot.position - spot).magnitude, Reached + shortfall, "The pinned foot, " + where);
                Assert.Less(shortfall, 0.01f, "Dropping the hips 5 cm should leave the foot in reach, " + where);
                Assert.Less(hips.position.y, hipsHeight - 0.04f, "The pin undid the hips drop, " + where);
                done++;
            }
            if (done == 0) Assert.Ignore("No sample character has a fully mapped left leg.");
        }

        [Test]
        public void OnALoopingClip_AFixNearTheEnd_WrapsToTheStart()
        {
            (TestRigs.Rig rig, AnimationClip source) = TestUtil.LongClips().First();
            ClipEditsAsset edits = TestEdits.Create(rig, source);
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(edits.Output);
            settings.loopTime = true;
            settings.loopBlend = false;
            AnimationUtility.SetAnimationClipSettings(edits.Output, settings);

            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            using FrameEditSession session = TestEdits.Session(edits, pc, ease: 3);
            int last = session.LastFrame;
            if (last < 10) Assert.Ignore("Clip too short.");
            HumanBodyBones bone = ArmBone(pc);
            Transform t = pc.Animator.GetBoneTransform(bone);
            Quaternion[] played = PlayedLocal(session, t);

            session.GoToFrame(last - 1);
            Assert.IsTrue(TryTurn(session, pc, bone), "Couldn't turn the arm.");
            session.Commit();

            // Key on last-1, eased out over 3 frames: last (= frame 0 in a loop), then frames 1 and 2.
            float atStart = AngleAt(session, t, played, 0), atEnd = AngleAt(session, t, played, last);
            Assert.Greater(atStart, 0.05f, "Frame 0 didn't get the wrapped ease.");
            Assert.Less(Mathf.Abs(atStart - atEnd), 0.3f, "Frame 0 and the last frame (the same pose in a loop) got different amounts.");
            Assert.Greater(AngleAt(session, t, played, 1), 0.05f, "Frame 1 didn't get the wrapped ease.");
            Assert.Greater(AngleAt(session, t, played, 2), 0.01f, "Frame 2 didn't get the wrapped ease.");
            for (int f = 3; f <= last - 5; f++)
                Assert.Less(AngleAt(session, t, played, f), UntouchedTolerance, $"Frame {f} moved.");
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Five frames around the middle (first..last), false if the clip is too short.</summary>
        static bool Range(FrameEditSession session, out int first, out int last)
        {
            int mid = MidFrame(session);
            first = mid - 2;
            last = mid + 2;
            return first >= 0 && last <= session.LastFrame;
        }

        static Quaternion[] PlayedLocal(FrameEditSession session, Transform t)
        {
            var r = new Quaternion[session.LastFrame + 1];
            for (int f = 0; f < r.Length; f++) { session.GoToFrame(f); r[f] = t.localRotation; }
            return r;
        }

        static float AngleAt(FrameEditSession session, Transform t, Quaternion[] played, int frame)
        {
            session.GoToFrame(frame);
            return Quaternion.Angle(played[frame], t.localRotation);
        }

        static List<Dictionary<string, float>> AllFrames(AnimationClip clip, int lastFrame) =>
            Enumerable.Range(0, lastFrame + 1).Select(f => TestEdits.ValuesAt(clip, f)).ToList();

        static Vector3 Relative(Transform anchor, Vector3 world) =>
            Quaternion.Inverse(anchor.rotation) * (world - anchor.position);

        static Vector3 RelativeToProp(ReferenceProp prop, PreviewCharacter pc, Vector3 world)
        {
            Assert.IsTrue(prop.TryGetPose(pc.Animator, out Vector3 at, out Quaternion rot));
            return Quaternion.Inverse(rot) * (world - at);
        }

        static GameObject MakePropModel()
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.localScale = Vector3.one * 0.05f;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(go, TempAssets.PathFor("prop.prefab"));
            Object.DestroyImmediate(go);
            return prefab;
        }
    }
}
