using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// C14: a model or prefab the preview character holds while you work (a sword, a cup), attached to a humanoid
    /// bone with a position / rotation / scale offset in that bone's space. Preview only: it never goes into the clip.
    /// A Pin record can anchor to a spot on it.
    /// </summary>
    [Serializable]
    public sealed class ReferenceProp
    {
        /// <summary>Stable id that Pin records refer to.</summary>
        public string id = Guid.NewGuid().ToString("N");
        public GameObject prefab;
        public HumanBodyBones bone = HumanBodyBones.RightHand;
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 scale = Vector3.one;
        public bool visible = true;

        public string Name => prefab ? prefab.name : "(no model)";

        /// <summary>
        /// Where the prop is on the character as it is posed now (the prop's pivot and rotation, as a child of the
        /// bone would be). Works whether or not the prop is shown. False when the bone isn't mapped.
        /// </summary>
        public bool TryGetPose(Animator animator, out Vector3 worldPosition, out Quaternion worldRotation)
        {
            Transform b = animator ? animator.GetBoneTransform(bone) : null;
            if (!b)
            {
                worldPosition = Vector3.zero;
                worldRotation = Quaternion.identity;
                return false;
            }
            worldPosition = b.TransformPoint(position);
            worldRotation = b.rotation * EditRecord.Normalized(rotation);
            return true;
        }
    }

    /// <summary>
    /// The shown copies of a character's reference props: tool-owned, never saved (<c>HideFlags.DontSave</c>, C10),
    /// scripts disabled, each a child of its bone on the preview character so it follows the pose. They go away
    /// with the preview character.
    /// </summary>
    public sealed class PreviewProps : IDisposable
    {
        readonly Dictionary<string, (GameObject prefab, GameObject instance)> shown =
            new Dictionary<string, (GameObject, GameObject)>();

        /// <summary>Makes the shown props match <paramref name="props"/> on <paramref name="preview"/>.</summary>
        public void Sync(IReadOnlyList<ReferenceProp> props, PreviewCharacter preview)
        {
            var keep = new HashSet<string>();
            if (preview?.Animator != null)
            {
                foreach (ReferenceProp p in props)
                {
                    if (p == null || !p.visible || !p.prefab) continue;
                    Transform bone = preview.Animator.GetBoneTransform(p.bone);
                    if (!bone) continue;
                    keep.Add(p.id);

                    if (!shown.TryGetValue(p.id, out (GameObject prefab, GameObject instance) s) || !s.instance || s.prefab != p.prefab)
                    {
                        if (s.instance) Object.DestroyImmediate(s.instance);
                        GameObject made = Object.Instantiate(p.prefab, bone, false);
                        made.name = $"{PreviewCharacter.NamePrefix} Prop ({p.prefab.name})";
                        foreach (MonoBehaviour mb in made.GetComponentsInChildren<MonoBehaviour>(true))
                            if (mb) mb.enabled = false;
                        PreviewCharacter.SetHideFlagsRecursive(made.transform, HideFlags.DontSave);
                        s = (p.prefab, made);
                        shown[p.id] = s;
                    }
                    Transform t = s.instance.transform;
                    if (t.parent != bone) t.SetParent(bone, false);
                    t.localPosition = p.position;
                    t.localRotation = EditRecord.Normalized(p.rotation);
                    t.localScale = p.scale;
                }
            }

            var drop = new List<string>();
            foreach (KeyValuePair<string, (GameObject prefab, GameObject instance)> kv in shown)
                if (!keep.Contains(kv.Key)) drop.Add(kv.Key);
            foreach (string id in drop)
            {
                if (shown[id].instance) Object.DestroyImmediate(shown[id].instance);
                shown.Remove(id);
            }
        }

        /// <summary>The shown copy of prop <paramref name="id"/>, or null.</summary>
        public GameObject InstanceOf(string id) =>
            id != null && shown.TryGetValue(id, out (GameObject prefab, GameObject instance) s) && s.instance ? s.instance : null;

        public void Dispose()
        {
            foreach ((GameObject _, GameObject instance) in shown.Values)
                if (instance) Object.DestroyImmediate(instance);
            shown.Clear();
        }
    }
}
