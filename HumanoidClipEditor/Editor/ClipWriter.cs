using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.HumanoidClipEditor
{
    /// <summary>One edited frame for <see cref="ClipWriter.WriteFrames"/>.</summary>
    public readonly struct FrameEdit
    {
        public readonly int Frame;
        /// <summary>The frame as Unity played it before the edit (exact muscles; see <see cref="ClipWriter"/>).</summary>
        public readonly HumanPose Before;
        /// <summary>The frame as the preview shows it after the edit (exact muscles for the edited parts).</summary>
        public readonly HumanPose After;
        /// <summary>Body parts whose bones the edit turned.</summary>
        public readonly HumanBodyPart[] EditedParts;
        /// <summary>True when the edit turned or moved the hips themselves.</summary>
        public readonly bool HipsEdited;

        public FrameEdit(int frame, HumanPose before, HumanPose after, IEnumerable<HumanBodyPart> editedParts, bool hipsEdited)
        {
            Frame = frame;
            Before = before;
            After = after;
            EditedParts = editedParts?.ToArray() ?? throw new ArgumentNullException(nameof(editedParts));
            HipsEdited = hipsEdited;
        }
    }

    /// <summary>
    /// Writes edited frames into an editable humanoid clip (C12, C17, C18, C7).
    ///
    /// Each edit arrives as two poses: the frame as it played before the edit and as the preview shows it after.
    /// Their muscles must be exact, not read back from bones (a read-back re-splits twist and clamps at the limits,
    /// and the difference would play back as a different pose): before = the muscles Unity's stream played, after =
    /// the muscles applied to the preview. Their bodies are read back from the preview (exact for the body). What
    /// gets written is the change between them, added to the curves, so anything playback adds on top of the curves
    /// (Loop Pose's correction, root-motion extraction) stays out of the curves:
    /// <list type="bullet">
    /// <item><b>Muscles</b>: only those of the body parts the edit touched (C17); every other muscle curve is left
    /// as it is. Reading a pose back isn't lossless (twist is re-split, out-of-range values clamped), so rewriting
    /// untouched parts from a read-back would nudge them.</item>
    /// <item><b>Body (RootT/RootQ)</b>: the body is the centre of mass, so it moves whenever a limb moves; it is
    /// rewritten whenever it changed, mapped from the preview's space back into the clip's.</item>
    /// <item><b>Goals</b>: for each limb the edit moved, recomputed from the new frame by Unity (C7). Every other
    /// goal keeps its place relative to the clip's origin and is only re-expressed against the new body.</item>
    /// </list>
    /// Only the edited frames change; the frames either side of each are pinned (<see cref="CurveKeys"/>). Undo is
    /// the caller's (the baker records the whole bake as one step).
    /// </summary>
    public static class ClipWriter
    {
        const float MuscleEpsilon = 1e-6f;
        const float BodyEpsilon = 1e-6f;

        /// <summary>
        /// Writes each edit's change into <paramref name="clip"/> on its frame. Both poses of every edit are in the
        /// space of <paramref name="animator"/> (the preview character's root), which is also used to recompute goals;
        /// see the class summary for where their muscles must come from.
        /// </summary>
        public static void WriteFrames(AnimationClip clip, IReadOnlyList<FrameEdit> edits, Animator animator)
        {
            if (!ClipDuplicator.IsEditable(clip))
                throw new ArgumentException($"'{(clip ? clip.name : "null")}' isn't an editable .anim clip.", nameof(clip));
            if (edits == null) throw new ArgumentNullException(nameof(edits));
            if (edits.Count == 0) return;

            float rate = clip.frameRate;
            int lastFrame = LastFrame(clip);
            var curves = new HumanoidClipCurves(clip);
            var batch = new CurveBatch(clip, lastFrame);
            var recompute = new List<(int frame, List<AvatarIKGoal> goals)>();

            foreach (FrameEdit e in edits.OrderBy(e => e.Frame))
            {
                int frame = e.Frame;
                if (frame < 0 || frame > lastFrame) throw new ArgumentOutOfRangeException(nameof(edits), $"Frame {frame} is outside the clip.");
                float time = CurveKeys.FrameTime(frame, rate);
                HumanPose stored = curves.SamplePose(time);
                HumanPose before = e.Before, after = e.After;

                // Muscles of the edited parts: add the change.
                for (int i = 0; i < HumanTrait.MuscleCount; i++)
                {
                    if (Array.IndexOf(e.EditedParts, BodyParts.OfMuscle(i)) < 0) continue;
                    float delta = after.muscles[i] - before.muscles[i];
                    if (Mathf.Abs(delta) < MuscleEpsilon) continue;
                    batch.Set(HumanoidCurveNames.MuscleCurveNames[i], frame, stored.muscles[i] + delta);
                }

                // Body: map the preview-space change back into clip space. At this frame playback places the stored
                // body (RootT, RootQ) at before.body; that mapping (root-motion extraction, orientation offsets) is
                // the same after the edit, so the new stored body is the mapping's inverse applied to after.body.
                Quaternion map = before.bodyRotation * Quaternion.Inverse(stored.bodyRotation);
                Vector3 offset = before.bodyPosition - map * stored.bodyPosition;
                Quaternion newBodyRotation = Continuous(Quaternion.Inverse(map) * after.bodyRotation, stored.bodyRotation);
                Vector3 newBodyPosition = Quaternion.Inverse(map) * (after.bodyPosition - offset);
                bool bodyChanged = curves.HasRoot &&
                    ((newBodyPosition - stored.bodyPosition).sqrMagnitude > BodyEpsilon * BodyEpsilon ||
                     Quaternion.Angle(newBodyRotation, stored.bodyRotation) > 1e-4f);

                // Goals the edit didn't move keep their place in clip space: read them before the body changes.
                var keep = new List<(AvatarIKGoal goal, Vector3 position, Quaternion rotation)>();
                var moved = new List<AvatarIKGoal>();
                for (int g = 0; g < HumanoidCurveNames.GoalCount; g++)
                {
                    var goal = (AvatarIKGoal)g;
                    if (!curves.HasGoal(goal)) continue;
                    if (GoalMoved(goal, e.EditedParts, e.HipsEdited)) { moved.Add(goal); continue; }
                    if (!bodyChanged) continue;
                    curves.SampleGoal(goal, time, out Vector3 t, out Quaternion q);
                    keep.Add((goal, stored.bodyPosition + stored.bodyRotation * t, stored.bodyRotation * q));
                }
                if (moved.Count > 0) recompute.Add((frame, moved));

                if (bodyChanged)
                {
                    for (int a = 0; a < 3; a++)
                        batch.Set(HumanoidCurveNames.RootTName(a), frame, newBodyPosition[a]);
                    for (int c = 0; c < 4; c++)
                        batch.Set(HumanoidCurveNames.RootQName(c), frame, newBodyRotation[c]);

                    Quaternion invBody = Quaternion.Inverse(newBodyRotation);
                    foreach ((AvatarIKGoal goal, Vector3 position, Quaternion rotation) in keep)
                    {
                        curves.SampleGoal(goal, time, out _, out Quaternion oldQ);
                        WriteGoal(batch, goal, frame, invBody * (position - newBodyPosition), Continuous(invBody * rotation, oldQ));
                    }
                }
            }
            batch.Flush(); // the goal recompute below evaluates the clip with these changes in it

            if (recompute.Count > 0)
            {
                AnimationClip raw = RawClipCopy.Create(clip);
                try
                {
                    foreach ((int frame, List<AvatarIKGoal> goals) in recompute)
                    {
                        float time = CurveKeys.FrameTime(frame, rate);
                        GoalCurveValues[] values = GoalCurves.FromClipFrame(animator, raw, time);
                        foreach (AvatarIKGoal goal in goals)
                        {
                            curves.SampleGoal(goal, time, out _, out Quaternion oldQ);
                            WriteGoal(batch, goal, frame, values[(int)goal].Position, Continuous(values[(int)goal].Rotation, oldQ));
                        }
                    }
                    batch.Flush();
                }
                finally { Object.DestroyImmediate(raw); }
            }

            EditorUtility.SetDirty(clip);
        }

        /// <summary>The clip's last frame index.</summary>
        public static int LastFrame(AnimationClip clip) => Mathf.Max(0, Mathf.RoundToInt(clip.length * clip.frameRate));

        /// <summary>
        /// Whether an edit of <paramref name="editedParts"/> moves <paramref name="goal"/> relative to the body: its
        /// own limb, or the torso (which carries the arms) — the hips carry everything.
        /// </summary>
        public static bool GoalMoved(AvatarIKGoal goal, IReadOnlyCollection<HumanBodyPart> editedParts, bool hipsEdited)
        {
            if (hipsEdited || editedParts.Contains(HumanBodyPart.Body) &&
                (goal == AvatarIKGoal.LeftHand || goal == AvatarIKGoal.RightHand))
                return true;
            switch (goal)
            {
                case AvatarIKGoal.LeftHand: return editedParts.Contains(HumanBodyPart.LeftArm);
                case AvatarIKGoal.RightHand: return editedParts.Contains(HumanBodyPart.RightArm);
                case AvatarIKGoal.LeftFoot: return editedParts.Contains(HumanBodyPart.LeftLeg);
                case AvatarIKGoal.RightFoot: return editedParts.Contains(HumanBodyPart.RightLeg);
                default: return false;
            }
        }

        static void WriteGoal(CurveBatch batch, AvatarIKGoal goal, int frame, Vector3 t, Quaternion q)
        {
            for (int a = 0; a < 3; a++) batch.Set(HumanoidCurveNames.GoalTName(goal, a), frame, t[a]);
            for (int c = 0; c < 4; c++) batch.Set(HumanoidCurveNames.GoalQName(goal, c), frame, q[c]);
        }

        /// <summary>
        /// Collects changed Animator curves and writes them in one <see cref="AnimationUtility.SetEditorCurves"/> call:
        /// Unity rebuilds a humanoid clip on every curve write, so writing them one by one stalls the Editor.
        /// A muscle with no curve reads as 0 everywhere, so a new curve starts as 0 on the first and last frames (and
        /// is pinned either side of each edited frame) to keep that elsewhere.
        /// </summary>
        sealed class CurveBatch
        {
            readonly AnimationClip clip;
            readonly int lastFrame;
            readonly Dictionary<string, AnimationCurve> pending = new Dictionary<string, AnimationCurve>();

            public CurveBatch(AnimationClip clip, int lastFrame)
            {
                this.clip = clip;
                this.lastFrame = lastFrame;
            }

            public void Set(string property, int frame, float value)
            {
                if (!pending.TryGetValue(property, out AnimationCurve curve))
                {
                    curve = AnimationUtility.GetEditorCurve(clip, Binding(property));
                    if (curve == null)
                    {
                        curve = new AnimationCurve();
                        curve.AddKey(new Keyframe(0f, 0f));
                        if (lastFrame > 0) curve.AddKey(new Keyframe(CurveKeys.FrameTime(lastFrame, clip.frameRate), 0f));
                    }
                    pending[property] = curve;
                }
                CurveKeys.SetValueOnFrame(curve, frame, clip.frameRate, lastFrame, value);
            }

            public void Flush()
            {
                if (pending.Count == 0) return;
                EditorCurveBinding[] bindings = pending.Keys.Select(Binding).ToArray();
                AnimationCurve[] curves = pending.Values.ToArray();
                AnimationUtility.SetEditorCurves(clip, bindings, curves);
                pending.Clear();
            }

            static EditorCurveBinding Binding(string property) =>
                EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), property);
        }

        /// <summary><paramref name="q"/> or its negation, whichever is closer to <paramref name="reference"/> (same rotation, no flip in the curves).</summary>
        static Quaternion Continuous(Quaternion q, Quaternion reference) =>
            Quaternion.Dot(q, reference) < 0f ? new Quaternion(-q.x, -q.y, -q.z, -q.w) : q;
    }
}
