using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// C13a: a clip's adjustment layer. It references the <see cref="Source"/> clip (never changed; it may be a
    /// read-only clip inside a model file), the <see cref="Output"/> clip the bake writes (an editable <c>.anim</c>),
    /// the character the fixes are made for (C6), the fix records in the order they apply, and the reference props
    /// (C14). The output is rebuilt from the source and the records on every bake, so changes made to the output by
    /// other means are replaced on the next bake.
    ///
    /// A new clip (C16b) has no source: it has a <see cref="StartPose"/>, a length and a frame rate instead, and the
    /// bake starts from that pose held for the whole length (<see cref="OpenBase"/>).
    /// </summary>
    public sealed class ClipEditsAsset : ScriptableObject
    {
        [SerializeField] AnimationClip source;
        [SerializeField] AnimationClip output;
        [SerializeField] GameObject character;
        [SerializeField] Avatar avatar;
        [SerializeReference] List<EditRecord> records = new List<EditRecord>();
        [SerializeField] List<ReferenceProp> props = new List<ReferenceProp>();
        [SerializeField] StartPose startPose = new StartPose();
        [SerializeField] int newClipFrames;
        [SerializeField] float newClipFrameRate = 30f;

        public AnimationClip Source { get => source; set => source = value; }
        public AnimationClip Output { get => output; set => output = value; }
        /// <summary>The model or prefab the fixes are made for (C6).</summary>
        public GameObject Character { get => character; set => character = value; }
        /// <summary>Optional Avatar to use instead of the character's own.</summary>
        public Avatar Avatar { get => avatar; set => avatar = value; }
        /// <summary>The records in the order they apply. Records whose type can't be loaded are dropped.</summary>
        public List<EditRecord> Records
        {
            get { records.RemoveAll(r => r == null); return records; }
        }
        public List<ReferenceProp> Props
        {
            get { props.RemoveAll(p => p == null); return props; }
        }

        public ReferenceProp FindProp(string id) => Props.FirstOrDefault(p => p.id == id);

        /// <summary>True for a new clip (C16): no source clip, a start pose instead.</summary>
        public bool IsNewClip => !source && startPose != null && startPose.IsValid;

        /// <summary>A new clip's start pose (C16b); not valid on a fixed clip.</summary>
        public StartPose StartPose => startPose;

        /// <summary>A new clip's length in frames (its last frame's index).</summary>
        public int NewClipFrames => newClipFrames;

        public float NewClipFrameRate => newClipFrameRate;

        /// <summary>Why the asset can't be edited or baked right now, or null when it can.</summary>
        public string Problem()
        {
            if (IsNewClip)
            {
                if (newClipFrames < 1) return "The new clip's length must be at least one frame.";
                if (!(newClipFrameRate > 0f)) return "The new clip's frame rate must be above 0.";
            }
            else
            {
                if (!source) return "The source clip is missing.";
                if (!source.humanMotion) return $"The source clip '{source.name}' isn't a Humanoid clip.";
            }
            if (!output) return "The output clip is missing.";
            if (!ClipDuplicator.IsEditable(output)) return $"The output clip '{output.name}' isn't an editable .anim clip.";
            if (output == source) return "The output clip can't be the source clip.";
            return null;
        }

        /// <summary>
        /// The clip the bake and the preview start from: the source, or for a new clip the start pose held for its
        /// whole length, generated for <paramref name="animator"/> (its IK goal curves are worked out on that
        /// character, C7). Dispose it when done (a generated clip is destroyed then).
        /// </summary>
        public BaseClip OpenBase(Animator animator)
        {
            if (!IsNewClip) return new BaseClip(source, owned: false);
            return new BaseClip(startPose.CreateClip(newClipFrames, newClipFrameRate, animator, $"{name} (start pose)"), owned: true);
        }

        /// <summary>See <see cref="OpenBase"/>.</summary>
        public sealed class BaseClip : IDisposable
        {
            public AnimationClip Clip { get; private set; }
            readonly bool owned;

            internal BaseClip(AnimationClip clip, bool owned)
            {
                Clip = clip;
                this.owned = owned;
            }

            public void Dispose()
            {
                if (owned && Clip) Object.DestroyImmediate(Clip);
                Clip = null;
            }
        }

        /// <summary>
        /// Starts a new clip (C16a–b, C16h) at <paramref name="outputPath"/> (an <c>.anim</c> path under Assets; a
        /// number is added if it exists): <paramref name="frames"/> frames long (frames 0 to
        /// <paramref name="frames"/>) at <paramref name="frameRate"/>, holding <paramref name="start"/> on every frame,
        /// with Loop Time set to <paramref name="loop"/> and Bake Into Pose off (travel keyed through the hips becomes
        /// root motion, C15). Its edits asset, <c>&lt;name&gt;_Edits.asset</c>, goes next to it. The character is
        /// needed to work out the IK goal curves. Undoable.
        /// </summary>
        public static ClipEditsAsset CreateNew(string outputPath, int frames, float frameRate, bool loop, StartPose start,
                                               GameObject character, Avatar avatar = null)
        {
            if (string.IsNullOrEmpty(outputPath) || !outputPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                !outputPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Give an .anim path under Assets.", nameof(outputPath));
            if (frames < 1) throw new ArgumentOutOfRangeException(nameof(frames), "A clip needs at least one frame.");
            if (!(frameRate > 0f)) throw new ArgumentOutOfRangeException(nameof(frameRate));
            if (start == null || !start.IsValid) throw new ArgumentException("The start pose has no muscle values.", nameof(start));
            if (!character) throw new ArgumentNullException(nameof(character));

            string path = AssetDatabase.GenerateUniqueAssetPath(outputPath);
            string clipName = Path.GetFileNameWithoutExtension(path);
            using PreviewCharacter preview = PreviewCharacter.Create(character, avatar);
            AnimationClip startClip = start.CreateClip(frames, frameRate, preview.Animator, clipName);
            try
            {
                var clip = new AnimationClip { name = clipName, frameRate = frameRate };
                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(startClip);
                AnimationUtility.SetEditorCurves(clip, bindings,
                    bindings.Select(b => AnimationUtility.GetEditorCurve(startClip, b)).ToArray());
                AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = loop;
                settings.loopBlend = false;
                settings.loopBlendOrientation = false;
                settings.loopBlendPositionY = false;
                settings.loopBlendPositionXZ = false;
                settings.mirror = false;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                AssetDatabase.CreateAsset(clip, path);
                Undo.RegisterCreatedObjectUndo(clip, "New Humanoid Clip");

                string folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
                var edits = CreateInstance<ClipEditsAsset>();
                edits.output = clip;
                edits.character = character;
                edits.avatar = avatar;
                edits.startPose = new StartPose
                {
                    muscles = (float[])start.muscles.Clone(),
                    bodyPosition = start.bodyPosition,
                    bodyRotation = start.bodyRotation,
                    description = start.description,
                };
                edits.newClipFrames = frames;
                edits.newClipFrameRate = frameRate;
                AssetDatabase.CreateAsset(edits, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{clipName}_Edits.asset"));
                Undo.RegisterCreatedObjectUndo(edits, "New Humanoid Clip");
                return edits;
            }
            finally
            {
                Object.DestroyImmediate(startClip);
            }
        }

        /// <summary>
        /// Starts an adjustment layer for <paramref name="source"/> (C3, C13e): makes the output as an editable copy
        /// (<c>&lt;name&gt;_Edit.anim</c>) and the edits asset (<c>&lt;name&gt;_Edits.asset</c>), both in
        /// <paramref name="folder"/> (default: the source's folder). Nothing existing is overwritten. Undoable.
        /// </summary>
        public static ClipEditsAsset Create(AnimationClip source, GameObject character = null, Avatar avatar = null,
                                            string folder = null)
        {
            if (!source) throw new ArgumentNullException(nameof(source));
            if (!source.humanMotion) throw new ArgumentException($"'{source.name}' isn't a Humanoid clip.", nameof(source));
            string name = ClipDuplicator.Sanitize(source.name);
            string outputPath = folder != null ? $"{folder}/{name}_Edit.anim" : null;
            AnimationClip output = ClipDuplicator.DuplicateAsEditable(source, outputPath);

            string outputFolder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(output))?.Replace('\\', '/');
            string path = AssetDatabase.GenerateUniqueAssetPath($"{outputFolder}/{name}_Edits.asset");
            var edits = CreateInstance<ClipEditsAsset>();
            edits.source = source;
            edits.output = output;
            edits.character = character;
            edits.avatar = avatar;
            AssetDatabase.CreateAsset(edits, path);
            Undo.RegisterCreatedObjectUndo(edits, "Start Humanoid Clip Edits");
            return edits;
        }

        /// <summary>The edits asset whose output is <paramref name="clip"/>, else one whose source it is, else null.</summary>
        public static ClipEditsAsset FindFor(AnimationClip clip)
        {
            if (!clip) return null;
            ClipEditsAsset bySource = null;
            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(ClipEditsAsset)}"))
            {
                var e = AssetDatabase.LoadAssetAtPath<ClipEditsAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (!e) continue;
                if (e.output == clip) return e;
                if (e.source == clip && !bySource) bySource = e;
            }
            return bySource;
        }
    }
}
