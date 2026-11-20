using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// A tool-owned copy of a humanoid character, created at the world origin with no rotation, that is never saved
    /// with the scene (C10). Its <see cref="Root"/> is the object its humanoid Animator is on, which need not be the
    /// copy's top object (a prefab often holds the model as a child): Unity's Animator doesn't animate transforms
    /// that belong to another Animator further down, so one added to the top object would never move the bones.
    /// The root can be moved (the edit session stands it where the clip's root motion puts it); poses are always
    /// applied and read relative to it, through <see cref="HumanPoseHandler"/>. Scripts are removed from the copy
    /// (a script on the Animator's object that has <c>OnAnimatorMove</c> takes root motion over from Unity, so the
    /// character would never travel, even with the script disabled), any other Animator in it is turned off, and its
    /// Animator sends no animation events, so game code doesn't run on it and root motion is Unity's own (C15). Copies left behind by a domain reload or crash are removed on Editor load.
    /// </summary>
    public sealed class PreviewCharacter : IDisposable
    {
        /// <summary>Name prefix that marks preview copies, used to find leftovers.</summary>
        public const string NamePrefix = "__HumanoidClipEditor Preview";

        /// <summary>The whole copy (its top object), at the origin.</summary>
        public GameObject Container { get; private set; }
        /// <summary>The object the humanoid Animator is on: the character's root for poses and travel.</summary>
        public GameObject Root { get; private set; }
        public Animator Animator { get; private set; }
        public Avatar Avatar => Animator ? Animator.avatar : null;
        public float HumanScale => Animator.humanScale;

        HumanPoseHandler handler;

        PreviewCharacter() { }

        /// <summary>
        /// Copies <paramref name="source"/> (a model, prefab asset or scene object). The copy needs a valid humanoid
        /// Avatar: <paramref name="avatar"/> when given, else the avatar of an Animator in it. The Animator used is
        /// the one in the copy that already has <paramref name="avatar"/>, else the first with a valid humanoid
        /// avatar, else the first there is; only when the copy has none is one added to its top object.
        /// </summary>
        public static PreviewCharacter Create(GameObject source, Avatar avatar = null)
        {
            if (!source) throw new ArgumentNullException(nameof(source));

            GameObject copy = UnityEngine.Object.Instantiate(source);
            copy.name = $"{NamePrefix} ({source.name})";
            copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            copy.SetActive(true);

            var pc = new PreviewCharacter { Container = copy, Root = copy };
            try
            {
                Animator[] animators = copy.GetComponentsInChildren<Animator>(true);
                Animator anim = (avatar ? animators.FirstOrDefault(a => a.avatar == avatar) : null)
                                ?? animators.FirstOrDefault(a => a.avatar && a.avatar.isHuman && a.avatar.isValid)
                                ?? animators.FirstOrDefault();
                if (!anim) anim = copy.AddComponent<Animator>();
                if (avatar) anim.avatar = avatar;
                if (!anim.avatar || !anim.avatar.isHuman || !anim.avatar.isValid)
                    throw new ArgumentException(
                        $"'{source.name}' has no valid humanoid Avatar; give one explicitly.", nameof(avatar));
                foreach (Animator other in animators)
                    if (other && other != anim) other.enabled = false;
                anim.gameObject.SetActive(true);
                pc.Root = anim.gameObject;

                anim.runtimeAnimatorController = null;
                anim.applyRootMotion = false;
                anim.fireEvents = false; // clip events would call game scripts, which the copy doesn't run
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate; // other modes skip the humanoid pass when unseen
                anim.enabled = true;
                anim.Rebind();
                RemoveScripts(copy);

                SetHideFlagsRecursive(copy.transform, HideFlags.DontSave);
                pc.Animator = anim;
                pc.handler = new HumanPoseHandler(anim.avatar, anim.transform);
                return pc;
            }
            catch
            {
                pc.Dispose();
                throw;
            }
        }

        /// <summary>Poses the copy's bones. Body position is normalized (divided by human scale), relative to the copy's root.</summary>
        public void ApplyPose(HumanPose pose)
        {
            using (new RootAtOrigin(Root.transform)) handler.SetHumanPose(ref pose);
        }

        /// <summary>
        /// Reads the copy's current bone pose back into muscles and body position/rotation, the body relative to the
        /// copy's root (normalized, like <see cref="ApplyPose"/>).
        /// </summary>
        public HumanPose ReadPose()
        {
            var pose = new HumanPose();
            using (new RootAtOrigin(Root.transform)) handler.GetHumanPose(ref pose);
            return pose;
        }

        /// <summary>
        /// Unity documents GetHumanPose's body as world space but SetHumanPose's as relative to the root; they agree
        /// only while the root sits at the origin unrotated. So the root is put there for the call (its children move
        /// with it, so the pose itself is untouched) and put back afterwards.
        /// </summary>
        readonly struct RootAtOrigin : IDisposable
        {
            readonly Transform root;
            readonly Vector3 position;
            readonly Quaternion rotation;

            public RootAtOrigin(Transform root)
            {
                this.root = root;
                root.GetPositionAndRotation(out position, out rotation);
                root.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            }

            public void Dispose() => root.SetPositionAndRotation(position, rotation);
        }

        public void Dispose()
        {
            handler?.Dispose();
            handler = null;
            if (Container) UnityEngine.Object.DestroyImmediate(Container);
            Container = null;
            Root = null;
            Animator = null;
        }

        /// <summary>
        /// Removes every script from <paramref name="copy"/>. A script another remaining script requires
        /// (<see cref="RequireComponent"/>) waits until that one is gone, since Unity refuses (and logs an error) to
        /// remove a required component; any left over after that (requiring each other) are disabled.
        /// </summary>
        static void RemoveScripts(GameObject copy)
        {
            List<MonoBehaviour> scripts = copy.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m).ToList();
            bool removed = true;
            while (scripts.Count > 0 && removed)
            {
                removed = false;
                foreach (MonoBehaviour m in scripts.ToArray())
                {
                    if (scripts.Any(o => o != m && o.gameObject == m.gameObject && Requires(o, m))) continue;
                    UnityEngine.Object.DestroyImmediate(m);
                    scripts.Remove(m);
                    removed = true;
                }
            }
            foreach (MonoBehaviour m in scripts)
                if (m) m.enabled = false;
        }

        /// <summary>True when <paramref name="dependent"/>'s type requires a component <paramref name="c"/> satisfies.</summary>
        static bool Requires(Component dependent, Component c)
        {
            foreach (RequireComponent r in dependent.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                foreach (Type t in new[] { r.m_Type0, r.m_Type1, r.m_Type2 })
                    if (t != null && t.IsInstanceOfType(c)) return true;
            return false;
        }

        internal static void SetHideFlagsRecursive(Transform t, HideFlags flags)
        {
            t.gameObject.hideFlags = flags;
            foreach (Component c in t.GetComponents<Component>())
                if (c) c.hideFlags = flags;
            for (int i = 0; i < t.childCount; i++) SetHideFlagsRecursive(t.GetChild(i), flags);
        }

        [InitializeOnLoadMethod]
        static void RemoveLeftovers()
        {
            EditorApplication.delayCall += () =>
            {
                foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (!go || go.transform.parent != null) continue;
                    if (EditorUtility.IsPersistent(go)) continue;
                    if (go.name.StartsWith(NamePrefix, StringComparison.Ordinal)
                        && (go.hideFlags & HideFlags.DontSave) == HideFlags.DontSave)
                        UnityEngine.Object.DestroyImmediate(go);
                }
            };
        }
    }
}
