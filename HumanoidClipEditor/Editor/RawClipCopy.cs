using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// In-memory copies of a clip's humanoid curves (muscles, RootT/RootQ, goals, Translation DoF, MotionT/Q), set
    /// through <see cref="AnimationUtility.SetEditorCurves"/> so nothing is compressed, for evaluating a clip through
    /// Unity. Two kinds, differing in the clip settings they keep:
    /// <list type="bullet">
    /// <item><see cref="Create"/> plays exactly the curves: <b>Loop Pose off</b> (with it on, Unity adds a correction
    /// that grows linearly through the clip so the last frame meets the first; measured up to 9° on a finger) and
    /// <b>Mirror off</b> (mirroring swaps the body's sides at playback). Used for pose checks and goals (C7).</item>
    /// <item><see cref="CreateForPreview"/> plays what the game plays apart from Mirror: Loop Pose stays on if the
    /// clip has it, Mirror is off so the preview shows the sides the curves are written for (C18). It can take its
    /// settings from another clip: an edits asset's source is played with the output's settings, which are what the
    /// game plays.</item>
    /// </list>
    /// Root-motion settings are kept in both (they move only the root). Destroy a copy when done.
    /// </summary>
    public static class RawClipCopy
    {
        public static AnimationClip Create(AnimationClip source) => Copy(source, source, keepLoopPose: false, " (raw)");

        /// <summary>A copy of <paramref name="source"/>'s curves with the clip settings of <paramref name="settingsFrom"/> (default: its own), Mirror off.</summary>
        public static AnimationClip CreateForPreview(AnimationClip source, AnimationClip settingsFrom = null) =>
            Copy(source, settingsFrom ? settingsFrom : source, keepLoopPose: true, " (preview)");

        /// <summary>True when <paramref name="clip"/> plays its curves as stored (Loop Pose and Mirror off).</summary>
        public static bool PlaysCurvesAsStored(AnimationClip clip)
        {
            AnimationClipSettings s = AnimationUtility.GetAnimationClipSettings(clip);
            return !s.loopBlend && !s.mirror;
        }

        static AnimationClip Copy(AnimationClip source, AnimationClip settingsFrom, bool keepLoopPose, string suffix)
        {
            var copy = new AnimationClip
            {
                name = source.name + suffix,
                frameRate = source.frameRate,
                hideFlags = HideFlags.DontSave,
            };
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(source)
                .Where(b => b.type == typeof(Animator) && string.IsNullOrEmpty(b.path))
                .ToArray();
            AnimationCurve[] curves = bindings.Select(b => AnimationUtility.GetEditorCurve(source, b)).ToArray();
            AnimationUtility.SetEditorCurves(copy, bindings, curves);

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(settingsFrom);
            if (!keepLoopPose) settings.loopBlend = false;
            settings.mirror = false;
            AnimationUtility.SetAnimationClipSettings(copy, settings);
            return copy;
        }
    }
}
