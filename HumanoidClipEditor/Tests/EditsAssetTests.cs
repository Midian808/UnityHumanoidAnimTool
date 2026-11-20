using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static Midian.HumanoidClipEditor.Tests.FrameEditTests;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>
    /// The edits asset (C13a, C3, C13e): starting one makes the output as an exact copy of the source, it can be
    /// found again from either clip, and baking never changes the source and keeps the output's own settings and
    /// events.
    /// </summary>
    public class EditsAssetTests
    {
        [SetUp] public void SetUp() => TempAssets.Create();
        [TearDown] public void TearDown() => TempAssets.Delete();
        [OneTimeTearDown] public void Release() => TestRigs.Release();

        [Test]
        public void Create_MakesTheOutputAsACopy_AndFindForFindsIt()
        {
            (TestRigs.Rig rig, AnimationClip source) = TestUtil.LongClips().First();
            ClipEditsAsset edits = TestEdits.Create(rig, source);

            Assert.AreSame(source, edits.Source);
            Assert.AreSame(rig.Model, edits.Character);
            Assert.IsTrue(ClipDuplicator.IsEditable(edits.Output), "The output isn't an editable .anim.");
            StringAssert.StartsWith(TempAssets.Folder + "/", AssetDatabase.GetAssetPath(edits.Output));
            StringAssert.StartsWith(TempAssets.Folder + "/", AssetDatabase.GetAssetPath(edits));
            Assert.IsNull(edits.Problem());
            Assert.IsTrue(SameKeysWithin(AllKeys(source), AllKeys(edits.Output)), "The output doesn't start as the source.");

            Assert.AreSame(edits, ClipEditsAsset.FindFor(edits.Output), "Not found from its output.");
            Assert.IsNotNull(ClipEditsAsset.FindFor(source), "Not found from its source.");
        }

        [Test]
        public void Baking_LeavesTheSourceAlone_AndKeepsTheOutputsSettingsAndEvents()
        {
            (TestRigs.Rig rig, AnimationClip imported) = TestUtil.LongClips().First();
            AnimationClip source = ClipDuplicator.DuplicateAsEditable(imported, TempAssets.PathFor("source.anim"));
            var sourceKeys = AllKeys(source);
            ClipEditsAsset edits = ClipEditsAsset.Create(source, rig.Model, rig.Avatar, TempAssets.Folder);

            AnimationClip output = edits.Output;
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(output);
            bool loop = !settings.loopTime;
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(output, settings);
            AnimationUtility.SetAnimationEvents(output, new[] { new AnimationEvent { time = 0f, functionName = "OnTestEvent" } });

            using (PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar))
            using (FrameEditSession session = TestEdits.Session(edits, pc))
            {
                session.GoToFrame(MidFrame(session));
                Assert.IsTrue(TryTurn(session, pc, ArmBone(pc)), "Couldn't turn the arm.");
                session.Commit();
            }

            Assert.IsTrue(SameKeys(sourceKeys, AllKeys(source)), "Baking changed the source.");
            Assert.IsFalse(SameKeys(sourceKeys, AllKeys(output)), "The bake didn't change the output.");
            Assert.AreEqual(loop, AnimationUtility.GetAnimationClipSettings(output).loopTime, "The bake reset the output's settings.");
            Assert.AreEqual("OnTestEvent", AnimationUtility.GetAnimationEvents(output).Single().functionName,
                "The bake dropped the output's events.");
        }
    }
}
