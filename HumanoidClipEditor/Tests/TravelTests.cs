using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>
    /// C15: <see cref="HumanStreamProbe.TravelPath"/> is Unity's own root motion for a clip. Checked against the
    /// clip's body curves: with XZ not baked into the pose the character travels as far as the body moves; with it
    /// baked the character stays put.
    /// </summary>
    public class TravelTests
    {
        const int MaxCases = 3;

        [OneTimeTearDown]
        public void Release() => TestRigs.Release();

        [Test]
        public void UnbakedXZ_TravelsAsFarAsTheBody()
        {
            var cases = Pick(bakedXZ: false, minMove: 0.5f);
            if (cases.Count == 0) Assert.Ignore("No sample clip that moves with XZ not baked into the pose.");
            foreach ((TestRigs.Rig rig, AnimationClip clip, float bodyMove) in cases)
            {
                float travel = Travel(rig, clip);
                Assert.AreEqual(bodyMove, travel, bodyMove * 0.03f, TestUtil.Describe(rig, clip));
            }
        }

        [Test]
        public void BakedXZ_DoesNotTravel()
        {
            var cases = Pick(bakedXZ: true, minMove: 0.3f);
            if (cases.Count == 0) Assert.Ignore("No sample clip whose body moves with XZ baked into the pose.");
            foreach ((TestRigs.Rig rig, AnimationClip clip, float _) in cases)
                Assert.Less(Travel(rig, clip), 0.01f, TestUtil.Describe(rig, clip));
        }

        [Test]
        public void AScriptThatHandlesRootMotion_DoesNotStopTheCharacterTravelling()
        {
            var cases = Pick(bakedXZ: false, minMove: 0.5f);
            if (cases.Count == 0) Assert.Ignore("No sample clip that moves with XZ not baked into the pose.");
            (TestRigs.Rig rig, AnimationClip clip, float bodyMove) = cases[0];
            GameObject character = Object.Instantiate(rig.Model);
            try
            {
                character.GetComponentInChildren<Animator>().gameObject.AddComponent<RootMotionScript>();
                using PreviewCharacter pc = PreviewCharacter.Create(character, rig.Avatar);
                Assert.AreEqual(0, pc.Container.GetComponentsInChildren<MonoBehaviour>(true).Count(m => m),
                    "The preview copy kept a script.");
                Assert.AreEqual(bodyMove, Travel(pc, clip), bodyMove * 0.03f, TestUtil.Describe(rig, clip));
            }
            finally { Object.DestroyImmediate(character); }
        }

        static float Travel(TestRigs.Rig rig, AnimationClip clip)
        {
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            return Travel(pc, clip);
        }

        static float Travel(PreviewCharacter pc, AnimationClip clip)
        {
            List<Pose> path = HumanStreamProbe.TravelPath(pc.Animator, clip, clip.frameRate);
            Vector3 end = path[path.Count - 1].position - path[0].position;
            return new Vector2(end.x, end.z).magnitude;
        }

        /// <summary>
        /// Clips whose body moves at least <paramref name="minMove"/> metres horizontally from start to end without
        /// turning (under 5°), with the given XZ bake setting; the horizontal body move is returned in metres.
        /// </summary>
        static List<(TestRigs.Rig rig, AnimationClip clip, float bodyMove)> Pick(bool bakedXZ, float minMove)
        {
            var result = new List<(TestRigs.Rig, AnimationClip, float)>();
            foreach ((TestRigs.Rig rig, AnimationClip clip) in TestRigs.SampleClips())
            {
                if (clip.length < TestUtil.MinLength) continue;
                if (AnimationUtility.GetAnimationClipSettings(clip).loopBlendPositionXZ != bakedXZ) continue;
                var curves = new HumanoidClipCurves(clip);
                if (!curves.HasRoot) continue;
                HumanPose a = curves.SamplePose(0f), b = curves.SamplePose(clip.length);
                float yaw = Mathf.Abs(Mathf.DeltaAngle(a.bodyRotation.eulerAngles.y, b.bodyRotation.eulerAngles.y));
                if (yaw > 5f) continue;
                Vector3 d = b.bodyPosition - a.bodyPosition;
                float move = new Vector2(d.x, d.z).magnitude * HumanScale(rig);
                if (move < minMove) continue;
                result.Add((rig, clip, move));
                if (result.Count >= MaxCases) break;
            }
            return result;
        }

        static readonly Dictionary<Avatar, float> scales = new Dictionary<Avatar, float>();

        static float HumanScale(TestRigs.Rig rig)
        {
            if (scales.TryGetValue(rig.Avatar, out float s)) return s;
            using PreviewCharacter pc = PreviewCharacter.Create(rig.Model, rig.Avatar);
            s = pc.HumanScale;
            scales[rig.Avatar] = s;
            return s;
        }
    }
}
