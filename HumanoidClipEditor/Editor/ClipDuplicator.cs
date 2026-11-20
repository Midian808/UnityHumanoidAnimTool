using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// C3: makes an editable <c>.anim</c> copy of a clip (clips inside an FBX are read-only). The copy holds every curve
    /// of the source (humanoid curves, any extra transform or property curves, object-reference curves), its
    /// animation events and its clip settings (loop, root-motion options, mirror, Loop Pose). The source is never
    /// modified.
    /// </summary>
    public static class ClipDuplicator
    {
        /// <summary>True when <paramref name="clip"/> is an <c>.anim</c> asset of its own, which the tool can edit.</summary>
        public static bool IsEditable(AnimationClip clip)
        {
            if (!clip) return false;
            string path = AssetDatabase.GetAssetPath(clip);
            return path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) && AssetDatabase.IsMainAsset(clip);
        }

        /// <summary><c>&lt;clip name&gt;_Edit.anim</c> in the source's folder (not yet made unique).</summary>
        public static string DefaultPath(AnimationClip source)
        {
            string folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(source))?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder)) folder = "Assets";
            return $"{folder}/{Sanitize(source.name)}_Edit.anim";
        }

        /// <summary>
        /// Creates the editable copy at <paramref name="assetPath"/> (default <see cref="DefaultPath"/>; an existing
        /// file is never overwritten, a number is added instead) and returns it. Undoable.
        /// </summary>
        public static AnimationClip DuplicateAsEditable(AnimationClip source, string assetPath = null)
        {
            if (!source) throw new ArgumentNullException(nameof(source));
            string path = AssetDatabase.GenerateUniqueAssetPath(assetPath ?? DefaultPath(source));

            var clip = new AnimationClip
            {
                name = Path.GetFileNameWithoutExtension(path),
                frameRate = source.frameRate,
                wrapMode = source.wrapMode,
                legacy = source.legacy,
            };

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(source);
            AnimationCurve[] curves = bindings.Select(b => AnimationUtility.GetEditorCurve(source, b)).ToArray();
            AnimationUtility.SetEditorCurves(clip, bindings, curves);
            foreach (EditorCurveBinding b in AnimationUtility.GetObjectReferenceCurveBindings(source))
                AnimationUtility.SetObjectReferenceCurve(clip, b, AnimationUtility.GetObjectReferenceCurve(source, b));

            AnimationUtility.SetAnimationClipSettings(clip, AnimationUtility.GetAnimationClipSettings(source));
            AnimationUtility.SetAnimationEvents(clip, AnimationUtility.GetAnimationEvents(source));
            clip.localBounds = source.localBounds;

            AssetDatabase.CreateAsset(clip, path);
            Undo.RegisterCreatedObjectUndo(clip, "Duplicate Humanoid Clip as Editable");
            return clip;
        }

        internal static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        [MenuItem("Assets/Humanoid Clip Editor/Duplicate as Editable", true)]
        static bool CanDuplicateSelection() =>
            Selection.objects.OfType<AnimationClip>().Any(c => c.humanMotion);

        [MenuItem("Assets/Humanoid Clip Editor/Duplicate as Editable")]
        static void DuplicateSelection()
        {
            AnimationClip[] made = Selection.objects.OfType<AnimationClip>().Where(c => c.humanMotion)
                .Select(c => DuplicateAsEditable(c)).ToArray();
            foreach (AnimationClip c in made) Debug.Log($"Humanoid Clip Editor: made {AssetDatabase.GetAssetPath(c)}", c);
            Selection.objects = made.Cast<UnityEngine.Object>().ToArray();
        }
    }
}
