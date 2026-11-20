using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>Edits assets and sessions for tests, made in <see cref="TempAssets"/> the way the window makes them.</summary>
    static class TestEdits
    {
        /// <summary>An edits asset for <paramref name="source"/> on the rig's character, its output in the scratch folder.</summary>
        public static ClipEditsAsset Create(TestRigs.Rig rig, AnimationClip source) =>
            ClipEditsAsset.Create(source, rig.Model, rig.Avatar, TempAssets.Folder);

        /// <summary>A session whose new fixes have no ease, so a fix changes exactly the frame it was made on.</summary>
        public static FrameEditSession Session(ClipEditsAsset edits, PreviewCharacter pc, int ease = 0) =>
            new FrameEditSession(edits, pc) { NewEditEaseFrames = ease };

        /// <summary>
        /// A prefab (saved in <see cref="TempAssets"/>) whose top object has no Animator and holds the rig's model as
        /// a child, the model keeping its own Animator: how game prefabs often hold a character.
        /// </summary>
        public static GameObject NestedPrefab(TestRigs.Rig rig)
        {
            var top = new GameObject("Nested rig");
            try
            {
                GameObject model = Object.Instantiate(rig.Model);
                model.transform.SetParent(top.transform, false);
                Assert.IsNotNull(model.GetComponent<Animator>(), "The sample model has no Animator of its own.");
                return PrefabUtility.SaveAsPrefabAsset(top, TempAssets.PathFor("NestedRig.prefab"));
            }
            finally { Object.DestroyImmediate(top); }
        }

        /// <summary>Every float curve's value on <paramref name="frame"/>, by binding.</summary>
        public static Dictionary<string, float> ValuesAt(AnimationClip clip, int frame)
        {
            float t = CurveKeys.FrameTime(frame, clip.frameRate);
            var result = new Dictionary<string, float>();
            foreach (EditorCurveBinding b in AnimationUtility.GetCurveBindings(clip))
                result[$"{b.path}|{b.type.Name}|{b.propertyName}"] = AnimationUtility.GetEditorCurve(clip, b).Evaluate(t);
            return result;
        }

        /// <summary>The largest difference between two <see cref="ValuesAt"/> results; infinity if their curves differ.</summary>
        public static float MaxDifference(Dictionary<string, float> a, Dictionary<string, float> b)
        {
            if (a.Count != b.Count) return float.PositiveInfinity;
            float max = 0f;
            foreach (KeyValuePair<string, float> kv in a)
            {
                if (!b.TryGetValue(kv.Key, out float other)) return float.PositiveInfinity;
                max = Mathf.Max(max, Mathf.Abs(other - kv.Value));
            }
            return max;
        }
    }
}
