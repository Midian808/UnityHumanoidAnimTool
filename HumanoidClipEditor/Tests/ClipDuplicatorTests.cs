using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>C3: the editable copy holds everything the source clip has, and the source is left alone.</summary>
    public class ClipDuplicatorTests
    {
        [SetUp] public void SetUp() => TempAssets.Create();
        [TearDown] public void TearDown() => TempAssets.Delete();
        [OneTimeTearDown] public void Release() => TestRigs.Release();

        [Test]
        public void Duplicate_CopiesCurvesEventsAndSettings()
        {
            foreach ((TestRigs.Rig rig, AnimationClip source) in TestUtil.LongClips())
            {
                string where = TestUtil.Describe(rig, source);
                Assert.IsFalse(ClipDuplicator.IsEditable(source), "An imported clip should be read-only: " + where);

                AnimationClip copy = ClipDuplicator.DuplicateAsEditable(source, TempAssets.PathFor("copy.anim"));
                Assert.IsTrue(ClipDuplicator.IsEditable(copy), where);
                Assert.IsTrue(copy.humanMotion, where);
                Assert.AreEqual(source.frameRate, copy.frameRate, where);
                Assert.AreEqual(source.length, copy.length, 1e-4f, where);

                EditorCurveBinding[] a = AnimationUtility.GetCurveBindings(source);
                EditorCurveBinding[] b = AnimationUtility.GetCurveBindings(copy);
                CollectionAssert.AreEquivalent(a.Select(Key), b.Select(Key), where);
                foreach (EditorCurveBinding binding in a)
                {
                    Keyframe[] ka = AnimationUtility.GetEditorCurve(source, binding).keys;
                    Keyframe[] kb = AnimationUtility.GetEditorCurve(copy, binding).keys;
                    Assert.AreEqual(ka.Length, kb.Length, $"{Key(binding)}, {where}");
                    for (int i = 0; i < ka.Length; i++)
                    {
                        Assert.AreEqual(ka[i].time, kb[i].time, 1e-6f, Key(binding));
                        Assert.AreEqual(ka[i].value, kb[i].value, 1e-6f, Key(binding));
                    }
                }
                CollectionAssert.AreEquivalent(
                    AnimationUtility.GetObjectReferenceCurveBindings(source).Select(Key),
                    AnimationUtility.GetObjectReferenceCurveBindings(copy).Select(Key), where);

                AnimationEvent[] ea = AnimationUtility.GetAnimationEvents(source);
                AnimationEvent[] eb = AnimationUtility.GetAnimationEvents(copy);
                Assert.AreEqual(ea.Length, eb.Length, where);
                for (int i = 0; i < ea.Length; i++)
                {
                    Assert.AreEqual(ea[i].functionName, eb[i].functionName, where);
                    Assert.AreEqual(ea[i].time, eb[i].time, 1e-5f, where);
                }

                AnimationClipSettings sa = AnimationUtility.GetAnimationClipSettings(source);
                AnimationClipSettings sb = AnimationUtility.GetAnimationClipSettings(copy);
                foreach (FieldInfo f in typeof(AnimationClipSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                    Assert.AreEqual(f.GetValue(sa), f.GetValue(sb), $"Clip setting {f.Name}, {where}");
                foreach (PropertyInfo p in typeof(AnimationClipSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                             .Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
                    Assert.AreEqual(p.GetValue(sa), p.GetValue(sb), $"Clip setting {p.Name}, {where}");

                AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(copy));
            }
        }

        [Test]
        public void Duplicate_NeverOverwritesAnExistingFile()
        {
            AnimationClip source = TestUtil.LongClips().First().clip;
            string path = TempAssets.PathFor("same.anim");
            AnimationClip first = ClipDuplicator.DuplicateAsEditable(source, path);
            AnimationClip second = ClipDuplicator.DuplicateAsEditable(source, path);
            Assert.AreNotEqual(AssetDatabase.GetAssetPath(first), AssetDatabase.GetAssetPath(second));
            Assert.IsTrue(first, "The first copy was replaced.");
        }

        [Test]
        public void DefaultPath_IsNextToTheSourceWithEditSuffix()
        {
            AnimationClip source = TestUtil.LongClips().First().clip;
            string folder = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(source)).Replace('\\', '/');
            StringAssert.StartsWith(folder + "/", ClipDuplicator.DefaultPath(source));
            StringAssert.EndsWith("_Edit.anim", ClipDuplicator.DefaultPath(source));
        }

        static string Key(EditorCurveBinding b) => $"{b.path}|{b.type.Name}|{b.propertyName}";
    }
}
