using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>C10: the preview copy is never saved, sits at the origin, runs no game code, and is removed on dispose.</summary>
    public class PreviewCharacterTests
    {
        [OneTimeTearDown]
        public void Release() => TestRigs.Release();

        [Test]
        public void Create_MakesAnUnsavedInertCopyAtTheOrigin()
        {
            TestRigs.Rig rig = TestRigs.RequireAny()[0];
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);

            Assert.IsTrue(pc.Container.name.StartsWith(PreviewCharacter.NamePrefix, StringComparison.Ordinal));
            Assert.AreEqual(Vector3.zero, pc.Container.transform.position);
            Assert.AreEqual(Quaternion.identity, pc.Container.transform.rotation);
            Assert.AreSame(pc.Animator.gameObject, pc.Root, "The root is the Animator's object.");
            foreach (Transform t in pc.Container.GetComponentsInChildren<Transform>(true))
            {
                Assert.IsTrue((t.gameObject.hideFlags & HideFlags.DontSave) == HideFlags.DontSave, t.name);
                foreach (Component c in t.GetComponents<Component>())
                    Assert.IsTrue((c.hideFlags & HideFlags.DontSave) == HideFlags.DontSave, $"{t.name}/{c.GetType().Name}");
            }
            Assert.IsFalse(pc.Container.GetComponentsInChildren<MonoBehaviour>(true).Any(m => m.enabled), "A script is enabled.");
            Assert.IsFalse(pc.Animator.fireEvents);
            Assert.IsFalse(pc.Animator.applyRootMotion);
            Assert.AreEqual(AnimatorCullingMode.AlwaysAnimate, pc.Animator.cullingMode);
            Assert.IsNull(pc.Animator.runtimeAnimatorController);
            Assert.AreSame(rig.Avatar, pc.Avatar);
        }

        [Test]
        public void Dispose_RemovesTheCopy()
        {
            TestRigs.Rig rig = TestRigs.RequireAny()[0];
            PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            GameObject container = pc.Container;
            pc.Dispose();
            Assert.IsTrue(container == null, "The preview copy still exists after Dispose.");
        }

        [Test]
        public void Create_UsesTheAnimatorWhereItIs_WhenTheModelIsAChildOfThePrefab()
        {
            TestRigs.Rig rig = TestRigs.RequireAny()[0];
            TempAssets.Create();
            try
            {
                GameObject prefab = TestEdits.NestedPrefab(rig);
                using PreviewCharacter pc = PreviewCharacter.Create(prefab, rig.Avatar);
                Assert.AreNotSame(pc.Container, pc.Root, "The root should be the child the Animator is on.");
                Assert.AreSame(pc.Animator.gameObject, pc.Root);
                Assert.IsNull(pc.Container.GetComponent<Animator>(), "An Animator was added to the prefab's top object.");
                Assert.AreEqual(1, pc.Container.GetComponentsInChildren<Animator>(true).Length, "The copy should keep its one Animator.");
                Assert.AreSame(rig.Avatar, pc.Avatar);
            }
            finally { TempAssets.Delete(); }
        }

        [Test]
        public void Create_WithoutAHumanoidAvatar_ThrowsAndLeavesNothingBehind()
        {
            var plain = new GameObject("HumanoidClipEditor test source");
            try
            {
                Assert.Throws<ArgumentException>(() => PreviewCharacter.Create(plain));
                bool leftover = Resources.FindObjectsOfTypeAll<GameObject>()
                    .Any(g => g && g.name.StartsWith(PreviewCharacter.NamePrefix, StringComparison.Ordinal));
                Assert.IsFalse(leftover, "A preview copy was left behind.");
            }
            finally { Object.DestroyImmediate(plain); }
        }
    }
}
