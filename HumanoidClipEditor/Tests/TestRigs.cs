using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>
    /// Finds a small sample of real imported humanoid clips in the project, each paired with the character whose
    /// Avatar the clip was imported against (the model's own Avatar, or the importer's source Avatar). The tests run
    /// on these, since the tool's job is editing real imported clips. A project with none gets the tests ignored.
    ///
    /// Projects can hold thousands of humanoid models, so candidates are chosen from importer settings alone
    /// (nothing loaded), at most one model file per top-level Assets folder, and only the chosen files are loaded.
    /// <see cref="Release"/> unloads them again.
    /// </summary>
    static class TestRigs
    {
        /// <summary>Most model files the sample loads.</summary>
        const int MaxFiles = 8;

        /// <summary>Shortest clip, in source frames, worth testing (single-frame T-pose takes are skipped).</summary>
        const float MinClipFrames = 15f;

        /// <summary>Files with more clips than this are skipped: loading one loads every clip in it.</summary>
        const int MaxClipsPerFile = 100;

        public sealed class Rig
        {
            public string AvatarPath;
            public GameObject Model;
            public Avatar Avatar;
            /// <summary>The clips were imported against this model's own Avatar (not one copied from another model),
            /// so their stored goals were computed on exactly this skeleton.</summary>
            public bool OwnAvatar;
            public readonly List<AnimationClip> Clips = new List<AnimationClip>();
            public override string ToString() => AvatarPath;
        }

        static List<Rig> sample;

        /// <summary>The sample rigs (each with the clips of the file that was picked for it).</summary>
        public static IReadOnlyList<Rig> RequireAny()
        {
            if (sample == null) sample = BuildSample();
            if (sample.Count == 0) Assert.Ignore("No imported humanoid clips with a valid humanoid Avatar in this project.");
            return sample;
        }

        /// <summary>Every clip of the sample rigs.</summary>
        public static IEnumerable<(Rig rig, AnimationClip clip)> SampleClips() =>
            RequireAny().SelectMany(r => r.Clips.Select(c => (r, c)));

        /// <summary>
        /// The first humanoid clip (in path order) whose name contains <paramref name="nameContains"/>, found from
        /// importer settings and loading only its own file; null when there is none.
        /// </summary>
        public static (Rig rig, AnimationClip clip)? FindClip(string nameContains)
        {
            foreach (string path in Candidates())
            {
                var mi = (ModelImporter)AssetImporter.GetAtPath(path);
                if (!mi.clipAnimations.Any(c => c.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0
                                               && c.lastFrame - c.firstFrame >= MinClipFrames))
                    continue;
                Rig rig = LoadRig(path, mi);
                AnimationClip clip = rig?.Clips.FirstOrDefault(
                    c => c.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);
                if (clip) return (rig, clip);
            }
            return null;
        }

        /// <summary>Drops the sample and unloads everything the tests loaded.</summary>
        public static void Release()
        {
            sample = null;
            EditorUtility.UnloadUnusedAssetsImmediate();
        }

        static List<Rig> BuildSample()
        {
            var rigs = new List<Rig>();
            var foldersUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Candidates())
            {
                if (rigs.Count >= MaxFiles) break;
                string folder = TopFolder(path);
                if (foldersUsed.Contains(folder)) continue;
                var mi = (ModelImporter)AssetImporter.GetAtPath(path);
                if (!mi.clipAnimations.Any(c => c.lastFrame - c.firstFrame >= MinClipFrames)) continue;

                Rig rig = LoadRig(path, mi);
                if (rig == null) continue;
                foldersUsed.Add(folder);
                rigs.Add(rig);
            }
            return rigs;
        }

        /// <summary>Humanoid model files that import animation, in path order, judged from importer settings only.</summary>
        static IEnumerable<string> Candidates()
        {
            IEnumerable<string> paths = AssetDatabase.FindAssets("t:Model")
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p, StringComparer.Ordinal);
            foreach (string path in paths)
            {
                if (!(AssetImporter.GetAtPath(path) is ModelImporter mi)) continue;
                if (mi.animationType != ModelImporterAnimationType.Human || !mi.importAnimation) continue;
                int clipCount = mi.clipAnimations.Length;
                if (clipCount == 0 || clipCount > MaxClipsPerFile) continue;
                yield return path;
            }
        }

        static Rig LoadRig(string path, ModelImporter mi)
        {
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
            AnimationClip[] clips = assets.OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal) && c.humanMotion)
                .OrderBy(c => c.name, StringComparer.Ordinal)
                .ToArray();
            if (clips.Length == 0) return null;

            Avatar avatar = mi.sourceAvatar ? mi.sourceAvatar : assets.OfType<Avatar>().FirstOrDefault();
            if (!avatar || !avatar.isHuman || !avatar.isValid) return null;
            string avatarPath = AssetDatabase.GetAssetPath(avatar);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(avatarPath);
            if (!model) return null;

            var rig = new Rig { AvatarPath = avatarPath, Model = model, Avatar = avatar, OwnAvatar = !mi.sourceAvatar };
            rig.Clips.AddRange(clips);
            return rig;
        }

        static string TopFolder(string path)
        {
            string[] parts = path.Split('/');
            return parts.Length > 2 ? parts[1] : path;
        }
    }
}
