using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>
    /// Moving and turning the hips on a new clip (C16), one stage at a time, so a failure names the stage that loses
    /// the edit: (a) the preview while dragging, (b) the pose key it stores, (c) the clip's body curves, (d) what
    /// plays back. Each edit is made on frame 10 of a new clip with a pose key on frame 0, so it shows as a change
    /// from frame 0 (a change made to every frame alike is root motion's starting point, and shows nowhere).
    /// Runs on every sample character, on each with its model nested under a prefab object, and on every character
    /// the project's own edits assets were made for.
    /// </summary>
    public class HipsEditTests
    {
        const int Frame = 10;
        const float Lift = 0.05f;     // metres
        const float Forward = 0.10f;  // metres
        const float Tilt = 15f;       // degrees
        const float Turn = 30f;       // degrees

        [SetUp] public void SetUp() => TempAssets.Create();
        [TearDown] public void TearDown() => TempAssets.Delete();
        [OneTimeTearDown] public void Release() => TestRigs.Release();

        public enum Edit { Lift, Forward, Tilt, Turn }

        [Test]
        public void HipsEdits_AreStoredWrittenAndShown([Values] Edit edit)
        {
            int ran = 0;
            foreach ((string name, GameObject character, Avatar avatar) in Characters())
            {
                Run(edit, name, character, avatar);
                ran++;
            }
            if (ran == 0) Assert.Ignore("No characters to try.");
        }

        static void Run(Edit edit, string name, GameObject character, Avatar avatar)
        {
            string where = $"{edit} on {name}";
            using PreviewCharacter pc = PreviewCharacter.Create(character, avatar);
            ClipEditsAsset edits = ClipEditsAsset.CreateNew(TempAssets.PathFor($"Hips{edit}.anim"), 20, 30f, false,
                StartPose.Neutral(pc), character, avatar);
            using FrameEditSession session = TestEdits.Session(edits, pc);
            Transform hips = pc.Animator.GetBoneTransform(HumanBodyBones.Hips);
            Transform root = pc.Root.transform;

            session.GoToFrame(0);
            session.AddPoseKey();
            (Vector3 before0, Quaternion beforeTurn0) = (hips.position, hips.rotation);

            session.GoToFrame(Frame);
            (Vector3 before10, Quaternion beforeTurn10) = (hips.position, hips.rotation);
            Vector3 start = hips.position;
            Quaternion startRotation = hips.rotation;
            Vector3 up = root.up, forward = root.forward, right = root.right;
            switch (edit)
            {
                case Edit.Lift: session.MoveHips(start + up * Lift, startRotation); break;
                case Edit.Forward: session.MoveHips(start + forward * Forward, startRotation); break;
                case Edit.Tilt: session.RotateBone(HumanBodyBones.Hips, Quaternion.AngleAxis(Tilt, right) * startRotation); break;
                case Edit.Turn: session.RotateBone(HumanBodyBones.Hips, Quaternion.AngleAxis(Turn, up) * startRotation); break;
            }

            // (a) the preview while dragging
            Vector3 movedBy = hips.position - start;
            float turnedBy = Quaternion.Angle(startRotation, hips.rotation);
            AssertEdit(edit, movedBy, turnedBy, up, forward, $"(a) preview while dragging, {where}");
            session.Commit();

            // (b) the stored key, against the key on frame 0
            PoseKeyRecord key0 = edits.Records.OfType<PoseKeyRecord>().Single(k => k.frame == 0);
            PoseKeyRecord key = edits.Records.OfType<PoseKeyRecord>().SingleOrDefault(k => k.frame == Frame);
            Assert.IsNotNull(key, $"(b) no pose key on frame {Frame}, {where}");
            AssertEdit(edit, key.pose.hipsPosition - key0.pose.hipsPosition,
                Quaternion.Angle(key0.pose.hipsRotation, key.pose.hipsRotation), Vector3.up, Vector3.forward,
                $"(b) stored pose key, {where}");

            // (c) the clip's body curves, frame 10 against frame 0
            var curves = new HumanoidClipCurves(edits.Output);
            HumanPose body0 = curves.SamplePose(0f), body = curves.SamplePose(CurveKeys.FrameTime(Frame, 30f));
            if (edit == Edit.Lift || edit == Edit.Forward)
                Assert.Greater((body.bodyPosition - body0.bodyPosition).magnitude * pc.HumanScale, 0.6f * (edit == Edit.Lift ? Lift : Forward),
                    $"(c) RootT barely changed, {where}");
            else
                Assert.Greater(Quaternion.Angle(body0.bodyRotation, body.bodyRotation), 0.6f * (edit == Edit.Tilt ? Tilt : Turn),
                    $"(c) RootQ barely changed, {where}");

            // (d) what plays back, in the world (root motion included): how frame 10 differs from frame 0 now,
            // against how it differed before the edit
            session.GoToFrame(0);
            (Vector3 after0, Quaternion afterTurn0) = (hips.position, hips.rotation);
            session.GoToFrame(Frame);
            Vector3 shownMove = (hips.position - after0) - (before10 - before0);
            float shownTurn = Quaternion.Angle(Quaternion.Inverse(beforeTurn0) * beforeTurn10, Quaternion.Inverse(afterTurn0) * hips.rotation);
            AssertEdit(edit, shownMove, shownTurn, up, forward, $"(d) playback, {where}");
        }

        static void AssertEdit(Edit edit, Vector3 moved, float turned, Vector3 up, Vector3 forward, string where)
        {
            switch (edit)
            {
                case Edit.Lift:
                    Assert.AreEqual(Lift, Vector3.Dot(moved, up), 0.01f, "Hips height: " + where);
                    break;
                case Edit.Forward:
                    Assert.AreEqual(Forward, Vector3.Dot(moved, forward), 0.01f, "Hips forward: " + where);
                    break;
                case Edit.Tilt:
                    Assert.AreEqual(Tilt, turned, 2f, "Hips tilt: " + where);
                    break;
                case Edit.Turn:
                    Assert.AreEqual(Turn, turned, 2f, "Hips turn: " + where);
                    break;
            }
        }

        /// <summary>Each sample character as it is and nested under a prefab object, then the project's own edits assets' characters.</summary>
        static IEnumerable<(string name, GameObject character, Avatar avatar)> Characters()
        {
            foreach (TestRigs.Rig rig in TestRigs.RequireAny().Take(2))
            {
                yield return (rig.AvatarPath, rig.Model, rig.Avatar);
                yield return (rig.AvatarPath + " (nested)", TestEdits.NestedPrefab(rig), rig.Avatar);
            }
            var seen = new HashSet<(GameObject, Avatar)>();
            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(ClipEditsAsset)}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.StartsWith(TempAssets.Folder + "/")) continue;
                var e = AssetDatabase.LoadAssetAtPath<ClipEditsAsset>(path);
                if (!e || !e.Character || !seen.Add((e.Character, e.Avatar))) continue;
                yield return ($"{AssetDatabase.GetAssetPath(e.Character)} (from {path})", e.Character, e.Avatar);
            }
        }
    }
}
