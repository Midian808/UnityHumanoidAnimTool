using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// C4: Unity's own humanoid IK solver (the one the Animator runs for IK Pass / Foot IK), driven for one limb
    /// through an animation job on a hand-evaluated graph. The clip frame is evaluated (which supplies Translation
    /// DoF), the pose to solve from replaces its muscles and body, the limb's goal is set to the target (position and
    /// rotation, weight 1) with the elbow/knee hint, every other goal is left off, and
    /// <see cref="AnimationHumanStream.SolveIK"/> runs. The Animator then writes the solved pose to the bones.
    ///
    /// The body read back from the bones is not exactly the body the stream places the hips from (they can differ
    /// by a centimetre on a rig with Translation DoF), so the stream's hips can land away from the bones' hips. The
    /// limb is solved relative to the hips the stream used, and the caller then puts the hips back. So the goal and
    /// hint are carried along with the stream's hips: after each solve the hips it produced are read, and when they
    /// differ from the ones the goal was carried by, it solves again with the goal carried by those.
    /// </summary>
    public static class LimbIK
    {
        /// <summary>The bone a goal moves (hand or foot).</summary>
        public static HumanBodyBones EndBone(AvatarIKGoal goal)
        {
            switch (goal)
            {
                case AvatarIKGoal.LeftFoot: return HumanBodyBones.LeftFoot;
                case AvatarIKGoal.RightFoot: return HumanBodyBones.RightFoot;
                case AvatarIKGoal.LeftHand: return HumanBodyBones.LeftHand;
                case AvatarIKGoal.RightHand: return HumanBodyBones.RightHand;
                default: throw new ArgumentOutOfRangeException(nameof(goal));
            }
        }

        /// <summary>The bone the hint bends (elbow or knee).</summary>
        public static HumanBodyBones MidBone(AvatarIKGoal goal)
        {
            switch (goal)
            {
                case AvatarIKGoal.LeftFoot: return HumanBodyBones.LeftLowerLeg;
                case AvatarIKGoal.RightFoot: return HumanBodyBones.RightLowerLeg;
                case AvatarIKGoal.LeftHand: return HumanBodyBones.LeftLowerArm;
                case AvatarIKGoal.RightHand: return HumanBodyBones.RightLowerArm;
                default: throw new ArgumentOutOfRangeException(nameof(goal));
            }
        }

        /// <summary>The bone the limb hangs from (upper arm or thigh).</summary>
        public static HumanBodyBones RootBone(AvatarIKGoal goal)
        {
            switch (goal)
            {
                case AvatarIKGoal.LeftFoot: return HumanBodyBones.LeftUpperLeg;
                case AvatarIKGoal.RightFoot: return HumanBodyBones.RightUpperLeg;
                case AvatarIKGoal.LeftHand: return HumanBodyBones.LeftUpperArm;
                case AvatarIKGoal.RightHand: return HumanBodyBones.RightUpperArm;
                default: throw new ArgumentOutOfRangeException(nameof(goal));
            }
        }

        /// <summary>The body part a goal's limb belongs to.</summary>
        public static HumanBodyPart Part(AvatarIKGoal goal) => BodyParts.Of(EndBone(goal));

        /// <summary>
        /// Evaluates <paramref name="clip"/> at <paramref name="time"/> on <paramref name="animator"/>, replaces its
        /// muscles and body with <paramref name="from"/> (muscles indexed like <see cref="HumanTrait.MuscleName"/>;
        /// body relative to the Animator's transform and divided by human scale, as
        /// <see cref="PreviewCharacter.ReadPose"/> gives it), then solves the <paramref name="goal"/> limb so its IK
        /// goal reaches <paramref name="goalPosition"/> / <paramref name="goalRotation"/> (world space), bending
        /// towards <paramref name="hint"/>, relative to the hips as the bones hold them now. The solved pose is left on
        /// the bones; its hips may sit elsewhere (see the class summary), and the limb is right once they are put back.
        /// </summary>
        public static void Solve(Animator animator, AnimationClip clip, float time, HumanPose from, AvatarIKGoal goal,
                                 Vector3 goalPosition, Quaternion goalRotation, Vector3 hint)
        {
            if (!clip) throw new ArgumentNullException(nameof(clip));
            if (from.muscles == null || from.muscles.Length != HumanTrait.MuscleCount)
                throw new ArgumentException("The pose to solve from needs every muscle.", nameof(from));
            using var scope = new AnimatorEvaluationScope(animator);
            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            Vector3 hipsPosition = hips.position;
            Quaternion hipsRotation = hips.rotation;
            Quaternion toHipsSpace = Quaternion.Inverse(hipsRotation);

            MuscleHandle[] handles = HumanStreamProbe.MuscleHandles;
            IReadOnlyList<int> traitIndex = HumanStreamProbe.TraitIndexOfHandle;
            var nHandles = new NativeArray<MuscleHandle>(handles, Allocator.TempJob);
            var nMuscles = new NativeArray<float>(handles.Length, Allocator.TempJob);
            try
            {
                for (int i = 0; i < handles.Length; i++) nMuscles[i] = from.muscles[traitIndex[i]];
                Vector3 carryPosition = hipsPosition;
                Quaternion carryRotation = hipsRotation;
                for (int pass = 0; pass < MaxPasses; pass++)
                {
                    Quaternion carry = carryRotation * toHipsSpace;
                    var job = new SolveJob
                    {
                        Handles = nHandles, Muscles = nMuscles,
                        BodyPosition = from.bodyPosition, BodyRotation = from.bodyRotation,
                        Goal = goal,
                        Position = carryPosition + carry * (goalPosition - hipsPosition),
                        Rotation = carry * goalRotation,
                        Hint = carryPosition + carry * (hint - hipsPosition),
                    };
                    Evaluate(animator, clip, time, job);
                    bool same = (hips.position - carryPosition).magnitude < HipsPositionTolerance &&
                                Quaternion.Angle(hips.rotation, carryRotation) < HipsAngleTolerance;
                    if (same) break;
                    carryPosition = hips.position;
                    carryRotation = hips.rotation;
                }
            }
            finally
            {
                nHandles.Dispose();
                nMuscles.Dispose();
            }
        }

        /// <summary>Passes at most; the stream's hips depend only on the pose fed in, so two passes settle them.</summary>
        const int MaxPasses = 4;
        const float HipsPositionTolerance = 1e-5f;
        const float HipsAngleTolerance = 0.001f;

        static void Evaluate(Animator animator, AnimationClip clip, float time, SolveJob job)
        {
            PlayableGraph graph = PlayableGraph.Create("HumanoidClipEditor IK");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var script = AnimationScriptPlayable.Create(graph, job, 1);
                var cp = AnimationClipPlayable.Create(graph, clip);
                cp.SetApplyFootIK(false);
                cp.SetApplyPlayableIK(false);
                cp.SetTime(time);
                cp.SetTime(time);
                graph.Connect(cp, 0, script, 0);
                script.SetInputWeight(0, 1f);
                var output = AnimationPlayableOutput.Create(graph, "IK", animator);
                output.SetSourcePlayable(script);
                graph.Evaluate(0f);
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
            }
        }

        struct SolveJob : IAnimationJob
        {
            public NativeArray<MuscleHandle> Handles;
            public NativeArray<float> Muscles;
            public Vector3 BodyPosition;
            public Quaternion BodyRotation;
            public AvatarIKGoal Goal;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Hint;

            public void ProcessRootMotion(AnimationStream stream) { }

            public void ProcessAnimation(AnimationStream stream)
            {
                AnimationHumanStream h = stream.AsHuman();
                for (int i = 0; i < Handles.Length; i++) h.SetMuscle(Handles[i], Muscles[i]);
                h.bodyLocalPosition = BodyPosition;
                h.bodyLocalRotation = BodyRotation;
                for (int g = 0; g < 4; g++)
                {
                    h.SetGoalWeightPosition((AvatarIKGoal)g, 0f);
                    h.SetGoalWeightRotation((AvatarIKGoal)g, 0f);
                    h.SetHintWeightPosition((AvatarIKHint)g, 0f);
                }
                h.SetLookAtBodyWeight(0f);
                h.SetLookAtHeadWeight(0f);
                h.SetLookAtEyesWeight(0f);

                // AvatarIKHint is in the same limb order as AvatarIKGoal: left foot/knee, right foot/knee,
                // left hand/elbow, right hand/elbow.
                var hint = (AvatarIKHint)(int)Goal;
                h.SetGoalPosition(Goal, Position);
                h.SetGoalRotation(Goal, Rotation);
                h.SetGoalWeightPosition(Goal, 1f);
                h.SetGoalWeightRotation(Goal, 1f);
                h.SetHintPosition(hint, Hint);
                h.SetHintWeightPosition(hint, 1f);
                h.SolveIK();
            }
        }
    }
}
