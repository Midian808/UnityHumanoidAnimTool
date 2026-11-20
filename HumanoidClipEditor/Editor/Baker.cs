using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// C13c: rebuilds an edits asset's output clip. The output's curves are first reset to the source's (so frames no
    /// record touches are the source's, exactly); then every frame an unmuted record touches is posed from the
    /// source frame, the pose keys applied first as one layer (<see cref="PoseLayer"/>, C16e) and the other records
    /// on top in list order (<see cref="RecordApplier"/>), and written (<see cref="ClipWriter"/>). A new clip's
    /// "source" is its start pose held for the whole length (<see cref="ClipEditsAsset.OpenBase"/>, C16b). The output's clip settings and animation events are its own and are kept; the
    /// source is played with the output's settings (Loop Pose, root motion), since those are what the game plays.
    /// The whole bake is one Undo step.
    ///
    /// Where the source stands while it is posed (the chain travel) has to make the posed frame land where the
    /// output will show it, since world pins aim at fixed spots and handles are dragged in the output's world. Neither
    /// root-motion path does that on its own. On a frame no record touches, the output's pose relative to its root
    /// is the source's, so the output's own path is right. On a touched frame it isn't: where the root follows the
    /// body, the output's path already carries that frame's edit (a 5 cm hips drop lowers the root 5 cm), so posing
    /// the edit again on it counts it twice; and the source's own path misses any shift of the whole path from an edit
    /// near the first frame. So the bake measures it: after writing, it compares the hips it posed on each touched
    /// frame with the hips the output shows there, and moves that frame's travel by the difference. Only world pins
    /// depend on the travel, so with world pins the frames are posed and written again on the corrected travel until
    /// the two agree (within <see cref="MatchTolerance"/>).
    /// </summary>
    public static class Baker
    {
        /// <summary>Posed and shown hips count as agreeing within this distance (metres).</summary>
        public const float MatchTolerance = 0.0005f;
        /// <summary>Posed and shown hips count as agreeing within this angle (degrees).</summary>
        public const float MatchAngleTolerance = 0.05f;
        const int MaxPasses = 5;

        public sealed class Result
        {
            /// <summary>Frames written into the output.</summary>
            public int FramesWritten;
            /// <summary>How many times the frames were posed and written (more than 1 only with world pins).</summary>
            public int Passes;
            /// <summary>Largest distance (metres) between the posed hips and the hips the output shows, on any touched frame of the last pass.</summary>
            public float Mismatch;
            /// <summary>Per record (same order as the asset's list): largest reach shortfall (metres) on any frame, or 0.</summary>
            public float[] ReachShortfall = Array.Empty<float>();
            /// <summary>Per record: why it couldn't apply on some frame (a missing bone or prop), or null.</summary>
            public string[] Problems = Array.Empty<string>();
            /// <summary>Where the source stands on each frame (index = frame) so that, posed with the records, it lands where the output shows it.</summary>
            public List<Pose> ChainTravel = new List<Pose>();
        }

        /// <summary>
        /// Bakes <paramref name="edits"/> on <paramref name="preview"/> (the character the fixes are made for, C6).
        /// <paramref name="chainTravelGuess"/> is the chain travel to start from (the last bake's saves a pass with
        /// world pins); default: the source's own root-motion path.
        /// </summary>
        public static Result Bake(ClipEditsAsset edits, PreviewCharacter preview, IReadOnlyList<Pose> chainTravelGuess = null)
        {
            if (!edits) throw new ArgumentNullException(nameof(edits));
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            string problem = edits.Problem();
            if (problem != null) throw new InvalidOperationException(problem);

            using ClipEditsAsset.BaseClip baseClip = edits.OpenBase(preview.Animator);
            AnimationClip source = baseClip.Clip, output = edits.Output;
            List<EditRecord> records = edits.Records;
            Undo.RegisterCompleteObjectUndo(output, "Bake Humanoid Clip Edits");

            var result = new Result
            {
                ReachShortfall = new float[records.Count],
                Problems = new string[records.Count],
            };

            ResetToSource(output, source);
            PoseLayer layer = PoseLayer.For(edits, records, output);
            SortedSet<int> frames = RecordApplier.TouchedFrames(edits, layer, output);
            if (frames.Count == 0)
            {
                EditorUtility.SetDirty(output);
                result.ChainTravel = TravelOf(output, preview);
                return result;
            }

            bool worldPins = records.Any(r => r is PinRecord p && !p.muted && p.anchor == PinAnchor.World);
            using var chain = new FramePoser(source, preview, Usable(chainTravelGuess, output), settingsFrom: output);
            for (int pass = 1; pass <= MaxPasses; pass++)
            {
                if (pass > 1) ResetToSource(output, source);
                result.Passes = pass;
                List<FrameEdit> written = PoseFrames(edits, layer, chain, frames, result, out Dictionary<int, Pose> posedHips);
                ClipWriter.WriteFrames(output, written, preview.Animator);
                result.FramesWritten = written.Count;

                List<Pose> travel = MatchTravel(output, preview, posedHips, chain.Travel, out result.Mismatch, out float turn);
                chain.SetTravel(travel);
                result.ChainTravel = travel;
                if (!worldPins || result.Mismatch <= MatchTolerance && turn <= MatchAngleTolerance) break;
            }
            EditorUtility.SetDirty(output);
            return result;
        }

        /// <summary>
        /// The chain travel for <paramref name="edits"/> as its output is now (the result's
        /// <see cref="Result.ChainTravel"/> without baking), for a session opened on an output baked earlier or
        /// restored by Undo. <paramref name="guess"/> is where to start (default: the source's own path).
        /// </summary>
        public static List<Pose> ChainTravel(ClipEditsAsset edits, PreviewCharacter preview, IReadOnlyList<Pose> guess = null)
        {
            if (!edits) throw new ArgumentNullException(nameof(edits));
            if (preview == null) throw new ArgumentNullException(nameof(preview));
            AnimationClip output = edits.Output;
            PoseLayer layer = PoseLayer.For(edits, edits.Records, output);
            SortedSet<int> frames = RecordApplier.TouchedFrames(edits, layer, output);
            if (frames.Count == 0) return TravelOf(output, preview);
            using ClipEditsAsset.BaseClip baseClip = edits.OpenBase(preview.Animator);
            using var chain = new FramePoser(baseClip.Clip, preview, Usable(guess, output), settingsFrom: output);
            var scratch = new Result
            {
                ReachShortfall = new float[edits.Records.Count],
                Problems = new string[edits.Records.Count],
            };
            PoseFrames(edits, layer, chain, frames, scratch, out Dictionary<int, Pose> posedHips);
            return MatchTravel(output, preview, posedHips, chain.Travel, out _, out _);
        }

        /// <summary>
        /// Poses every frame in <paramref name="frames"/> on <paramref name="chain"/>: the pose-key
        /// <paramref name="layer"/> first, then the unmuted Offset and Pin records in list order, noting shortfalls and
        /// problems in <paramref name="result"/> and where the hips ended up. Returns the frames an edit changed, for
        /// the writer.
        /// </summary>
        static List<FrameEdit> PoseFrames(ClipEditsAsset edits, PoseLayer layer, FramePoser chain, SortedSet<int> frames,
                                          Result result, out Dictionary<int, Pose> posedHips)
        {
            List<EditRecord> records = edits.Records;
            Array.Clear(result.ReachShortfall, 0, result.ReachShortfall.Length);
            Array.Clear(result.Problems, 0, result.Problems.Length);
            posedHips = new Dictionary<int, Pose>();
            var written = new List<FrameEdit>();
            foreach (int f in frames)
            {
                chain.Show(f);
                chain.ApplyPoseLayer(layer.Evaluate(chain.Frame));
                for (int i = 0; i < records.Count; i++)
                {
                    EditRecord r = records[i];
                    if (r.muted) continue;
                    RecordApplier.Outcome o = RecordApplier.Apply(chain, edits, r, edits.Output);
                    if (o.ReachShortfall is float reach) result.ReachShortfall[i] = Mathf.Max(result.ReachShortfall[i], reach);
                    if (o.Problem != null) result.Problems[i] = o.Problem;
                }
                Transform hips = chain.Bone(HumanBodyBones.Hips);
                posedHips[chain.Frame] = new Pose(hips.position, hips.rotation);
                if (chain.HasEdit)
                    written.Add(new FrameEdit(chain.Frame, chain.Played, chain.Current, chain.EditedParts, chain.HipsEdited));
            }
            return written;
        }

        /// <summary>
        /// The chain travel that makes posed frames land where <paramref name="output"/> shows them: the output's own
        /// path on frames not in <paramref name="posedHips"/>; on the posed frames, the travel they were posed on
        /// (<paramref name="used"/>) moved rigidly by how far the output's hips are from the posed hips.
        /// <paramref name="mismatch"/> / <paramref name="turn"/>: the largest such distance (metres) and angle (degrees).
        /// </summary>
        static List<Pose> MatchTravel(AnimationClip output, PreviewCharacter preview, Dictionary<int, Pose> posedHips,
                                      IReadOnlyList<Pose> used, out float mismatch, out float turn)
        {
            mismatch = 0f;
            turn = 0f;
            using var shown = new FramePoser(output, preview);
            var travel = new List<Pose>(shown.Travel);
            Transform hips = shown.Bone(HumanBodyBones.Hips);
            foreach (KeyValuePair<int, Pose> kv in posedHips)
            {
                if (kv.Key >= travel.Count) continue;
                shown.Show(kv.Key);
                Pose posed = kv.Value;
                mismatch = Mathf.Max(mismatch, (hips.position - posed.position).magnitude);
                turn = Mathf.Max(turn, Quaternion.Angle(hips.rotation, posed.rotation));
                Quaternion m = hips.rotation * Quaternion.Inverse(posed.rotation);
                Pose u = used[Mathf.Min(kv.Key, used.Count - 1)];
                travel[kv.Key] = new Pose(hips.position + m * (u.position - posed.position), m * u.rotation);
            }
            return travel;
        }

        /// <summary><paramref name="travel"/> when it has one pose per frame of <paramref name="clip"/>, else null.</summary>
        static IReadOnlyList<Pose> Usable(IReadOnlyList<Pose> travel, AnimationClip clip) =>
            travel != null && travel.Count == ClipWriter.LastFrame(clip) + 1 ? travel : null;

        /// <summary>The root-motion path <paramref name="clip"/> gives the preview character (one pose per frame).</summary>
        public static List<Pose> TravelOf(AnimationClip clip, PreviewCharacter preview)
        {
            AnimationClip copy = RawClipCopy.CreateForPreview(clip);
            try { return HumanStreamProbe.TravelPath(preview.Animator, copy, clip.frameRate); }
            finally { Object.DestroyImmediate(copy); }
        }

        /// <summary>
        /// Makes every curve of <paramref name="output"/> the source's: the source's float and object-reference
        /// curves are copied, and curves the source doesn't have are removed. Settings and events are left alone.
        /// </summary>
        static void ResetToSource(AnimationClip output, AnimationClip source)
        {
            EditorCurveBinding[] sourceBindings = AnimationUtility.GetCurveBindings(source);
            var keep = new HashSet<EditorCurveBinding>(sourceBindings);
            EditorCurveBinding[] extra = AnimationUtility.GetCurveBindings(output).Where(b => !keep.Contains(b)).ToArray();
            EditorCurveBinding[] bindings = sourceBindings.Concat(extra).ToArray();
            AnimationCurve[] curves = sourceBindings.Select(b => AnimationUtility.GetEditorCurve(source, b))
                .Concat(extra.Select(_ => (AnimationCurve)null)).ToArray();
            AnimationUtility.SetEditorCurves(output, bindings, curves);

            EditorCurveBinding[] sourceRefs = AnimationUtility.GetObjectReferenceCurveBindings(source);
            var keepRefs = new HashSet<EditorCurveBinding>(sourceRefs);
            foreach (EditorCurveBinding b in AnimationUtility.GetObjectReferenceCurveBindings(output))
                if (!keepRefs.Contains(b)) AnimationUtility.SetObjectReferenceCurve(output, b, null);
            foreach (EditorCurveBinding b in sourceRefs)
                AnimationUtility.SetObjectReferenceCurve(output, b, AnimationUtility.GetObjectReferenceCurve(source, b));
        }
    }
}
