using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using static Midian.HumanoidClipEditor.Tests.FrameEditTests;
using static Midian.HumanoidClipEditor.Tests.PoseKeyTests;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>
    /// C16g: <see cref="PoseMirror"/> mirrors a pose the way Unity's clip Mirror option does. Checked against
    /// Unity's own evaluation of real clips played with Mirror on; then Mirror pose in a session, end to end.
    /// </summary>
    public class MirrorTests
    {
        [SetUp] public void SetUp() => TempAssets.Create();
        [TearDown] public void TearDown() => TempAssets.Delete();
        [OneTimeTearDown] public void Release() => TestRigs.Release();

        [Test]
        public void TheMuscleAndBodyConvention_MatchesUnitysMirrorOption()
        {
            int checkedFrames = 0;
            foreach ((TestRigs.Rig rig, AnimationClip clip) in TestUtil.LongClips())
            {
                using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
                AnimationClip plain = RawClipCopy.Create(clip);
                AnimationClip mirrored = RawClipCopy.Create(clip);
                try
                {
                    AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(mirrored);
                    settings.mirror = true;
                    AnimationUtility.SetAnimationClipSettings(mirrored, settings);
                    foreach (float t in TestUtil.SampleTimes(clip))
                    {
                        string where = $"{TestUtil.Describe(rig, clip)} at {t:F3} s";
                        HumanStreamSample a = HumanStreamProbe.EvaluateClip(pc.Animator, plain, t);
                        HumanStreamSample b = HumanStreamProbe.EvaluateClip(pc.Animator, mirrored, t);
                        float[] ours = PoseMirror.MirrorMuscles(a.Muscles);
                        for (int m = 0; m < ours.Length; m++)
                            Assert.AreEqual(b.Muscles[m], ours[m], 1e-3f, $"{HumanTrait.MuscleName[m]}, {where}");
                        Assert.Less(Quaternion.Angle(b.BodyLocalRotation, PoseMirror.MirrorRotation(a.BodyLocalRotation)), 0.5f,
                            "Body rotation, " + where);
                        Assert.Less((b.BodyLocalPosition - PoseMirror.MirrorPosition(a.BodyLocalPosition)).magnitude, 0.005f,
                            "Body position, " + where);
                        checkedFrames++;
                    }
                }
                finally
                {
                    Object.DestroyImmediate(plain);
                    Object.DestroyImmediate(mirrored);
                }
            }
            Assert.Greater(checkedFrames, 0);
        }

        [Test]
        public void MirrorPose_KeysTheMirroredPose_AndUndoesWhenMirroredAgain()
        {
            (TestRigs.Rig rig, AnimationClip source) = TestUtil.LongClips().First();
            ClipEditsAsset edits = TestEdits.Create(rig, source);
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            using FrameEditSession session = TestEdits.Session(edits, pc, ease: 2);
            int mid = MidFrame(session);
            string where = TestUtil.Describe(rig, source);

            session.GoToFrame(mid);
            KeyPose original = session.CopyPose();
            int index = session.MirrorPose();
            Assert.IsTrue(edits.Records[index] is PoseKeyRecord k && k.frame == mid, "Mirror pose should key this frame.");
            session.GoToFrame(mid);
            KeyPose once = session.CopyPose();
            AssertSameMuscles(PoseMirror.MirrorMuscles(original.muscles), once.muscles, "mirrored once, " + where);
            Assert.AreEqual(original.hipsPosition.y, once.hipsPosition.y, 0.005f, "Mirroring kept the hips' height, " + where);

            session.MirrorPose();
            session.GoToFrame(mid);
            KeyPose twice = session.CopyPose();
            AssertSameMuscles(original.muscles, twice.muscles, "mirrored twice, " + where);
            Assert.Less((twice.hipsPosition - original.hipsPosition).magnitude, 0.005f, "Mirrored twice: hips, " + where);
            Assert.Less(Quaternion.Angle(twice.hipsRotation, original.hipsRotation), 1f, "Mirrored twice: hips turn, " + where);
            Assert.AreEqual(1, edits.Records.Count(r => r is PoseKeyRecord), "Mirroring twice should reuse the frame's key.");
        }
    }
}
