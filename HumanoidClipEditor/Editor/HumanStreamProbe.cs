using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Midian.HumanoidClipEditor
{
    /// <summary>What Unity's own humanoid evaluation produced for one frame on one character.</summary>
    public sealed class HumanStreamSample
    {
        /// <summary>Muscle values, indexed like <see cref="HumanTrait.MuscleName"/>.</summary>
        public float[] Muscles = new float[HumanTrait.MuscleCount];
        public float HumanScale;
        public Vector3 BodyLocalPosition, BodyPosition;
        public Quaternion BodyLocalRotation, BodyRotation;
        /// <summary>Goals as evaluated (from the clip's goal curves), relative to the root and in world space.</summary>
        public Vector3[] GoalLocalPosition = new Vector3[HumanoidCurveNames.GoalCount];
        public Quaternion[] GoalLocalRotation = new Quaternion[HumanoidCurveNames.GoalCount];
        public Vector3[] GoalPosition = new Vector3[HumanoidCurveNames.GoalCount];
        public Quaternion[] GoalRotation = new Quaternion[HumanoidCurveNames.GoalCount];
        /// <summary>Goals Unity computes from the evaluated pose (world space).</summary>
        public Vector3[] GoalPositionFromPose = new Vector3[HumanoidCurveNames.GoalCount];
        public Quaternion[] GoalRotationFromPose = new Quaternion[HumanoidCurveNames.GoalCount];
        public Vector3 Velocity, AngularVelocity;
        /// <summary>The stream's left and right foot heights (ankle above sole).</summary>
        public float LeftFootHeight, RightFootHeight;
    }

    /// <summary>
    /// Runs Unity's humanoid evaluation (a PlayableGraph with an animation job, evaluated by hand, so it works in
    /// Edit mode) on a preview character and reports what the stream holds. Animation events never fire during it,
    /// and the Animator always animates (no culling) while it runs. This is the reference the tool's own curve
    /// reading is checked against, and the source of IK goals computed from a pose (C7) and of the travel path (C15).
    /// </summary>
    public static class HumanStreamProbe
    {
        const int VecCount = 1 + 1 + 4 * 3 + 2; // bodyLocal, body, goals local/world/fromPose, velocity, angular
        const int ScalarCount = 3;

        static MuscleHandle[] handles;
        static int[] traitIndexOfHandle;

        /// <summary>
        /// Unity's evaluation of <paramref name="clip"/> at <paramref name="time"/> (no foot IK, no IK pass). With
        /// <paramref name="goalsFromPose"/>, the goals are then set to the goals Unity computes from the evaluated pose
        /// (which includes Translation DoF), so the goal fields hold Unity's conversion of the pose's own goals instead
        /// of the clip's stored goal curves.
        /// </summary>
        public static HumanStreamSample EvaluateClip(Animator animator, AnimationClip clip, float time,
                                                     bool goalsFromPose = false)
        {
            return Run(animator, clip, time, goalsFromPose);
        }

        /// <summary>
        /// The character's travel (root motion) through one play of <paramref name="clip"/>, as Unity applies it:
        /// the Animator's root position and rotation after each frame step, starting at the origin. Entry 0 is the
        /// start; entry i is after i steps of 1 / <paramref name="frameRate"/> seconds. The Animator's transform is
        /// restored afterwards.
        /// </summary>
        public static List<Pose> TravelPath(Animator animator, AnimationClip clip, float frameRate)
        {
            if (!clip) throw new ArgumentNullException(nameof(clip));
            using var scope = new AnimatorEvaluationScope(animator);
            Transform t = animator.transform;
            t.GetPositionAndRotation(out Vector3 oldPos, out Quaternion oldRot);
            bool oldRootMotion = animator.applyRootMotion;
            PlayableGraph graph = PlayableGraph.Create("HumanoidClipEditor Travel");
            var path = new List<Pose>();
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var cp = AnimationClipPlayable.Create(graph, clip);
                cp.SetApplyFootIK(false);
                cp.SetTime(0); cp.SetTime(0);
                var output = AnimationPlayableOutput.Create(graph, "Travel", animator);
                output.SetSourcePlayable(cp);
                animator.applyRootMotion = true;
                t.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                graph.Evaluate(0f);
                t.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                path.Add(new Pose(t.position, t.rotation));
                int steps = Mathf.Max(1, Mathf.RoundToInt(clip.length * frameRate));
                float dt = clip.length / steps;
                for (int i = 0; i < steps; i++)
                {
                    graph.Evaluate(dt);
                    path.Add(new Pose(t.position, t.rotation));
                }
                return path;
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                animator.applyRootMotion = oldRootMotion;
                t.SetPositionAndRotation(oldPos, oldRot);
            }
        }

        /// <summary>Every muscle handle, in Unity's order (see <see cref="TraitIndexOfHandle"/>).</summary>
        internal static MuscleHandle[] MuscleHandles
        {
            get { BuildHandles(); return handles; }
        }

        /// <summary>Maps <see cref="MuscleHandle"/> order to <see cref="HumanTrait.MuscleName"/> indices (by name).</summary>
        public static IReadOnlyList<int> TraitIndexOfHandle
        {
            get { BuildHandles(); return traitIndexOfHandle; }
        }

        static void BuildHandles()
        {
            if (handles != null) return;
            var h = new MuscleHandle[MuscleHandle.muscleHandleCount];
            MuscleHandle.GetMuscleHandles(h);
            var map = new int[h.Length];
            for (int i = 0; i < h.Length; i++)
            {
                // Muscle handles are named like clip curves ("LeftHand.Thumb.1 Stretched"), not like HumanTrait.
                if (!HumanoidCurveNames.TryGetMuscleIndex(h[i].name, out map[i]))
                    throw new InvalidOperationException($"Muscle handle '{h[i].name}' has no matching muscle curve name.");
            }
            traitIndexOfHandle = map;
            handles = h;
        }

        static HumanStreamSample Run(Animator animator, AnimationClip clip, float time, bool goalsFromPose)
        {
            if (!clip) throw new ArgumentNullException(nameof(clip));
            using var scope = new AnimatorEvaluationScope(animator);
            BuildHandles();

            var nHandles = new NativeArray<MuscleHandle>(handles, Allocator.TempJob);
            var nMuscles = new NativeArray<float>(handles.Length, Allocator.TempJob);
            var nVecs = new NativeArray<Vector3>(VecCount, Allocator.TempJob);
            var nRots = new NativeArray<Quaternion>(VecCount, Allocator.TempJob);
            var nScalars = new NativeArray<float>(ScalarCount, Allocator.TempJob);
            PlayableGraph graph = PlayableGraph.Create("HumanoidClipEditor Probe");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var job = new CaptureJob
                {
                    Handles = nHandles, Muscles = nMuscles, Vecs = nVecs, Rots = nRots, Scalars = nScalars,
                    GoalsFromPose = goalsFromPose,
                };
                var script = AnimationScriptPlayable.Create(graph, job, 1);
                var cp = AnimationClipPlayable.Create(graph, clip);
                cp.SetApplyFootIK(false);
                cp.SetApplyPlayableIK(false);
                cp.SetTime(time);
                cp.SetTime(time);
                graph.Connect(cp, 0, script, 0);
                script.SetInputWeight(0, 1f);
                var output = AnimationPlayableOutput.Create(graph, "Probe", animator);
                output.SetSourcePlayable(script);
                graph.Evaluate(0f);

                var s = new HumanStreamSample();
                for (int i = 0; i < handles.Length; i++) s.Muscles[traitIndexOfHandle[i]] = nMuscles[i];
                s.HumanScale = nScalars[0];
                s.LeftFootHeight = nScalars[1];
                s.RightFootHeight = nScalars[2];
                s.BodyLocalPosition = nVecs[0]; s.BodyLocalRotation = nRots[0];
                s.BodyPosition = nVecs[1]; s.BodyRotation = nRots[1];
                for (int g = 0; g < HumanoidCurveNames.GoalCount; g++)
                {
                    s.GoalLocalPosition[g] = nVecs[2 + g]; s.GoalLocalRotation[g] = nRots[2 + g];
                    s.GoalPosition[g] = nVecs[6 + g]; s.GoalRotation[g] = nRots[6 + g];
                    s.GoalPositionFromPose[g] = nVecs[10 + g]; s.GoalRotationFromPose[g] = nRots[10 + g];
                }
                s.Velocity = nVecs[14]; s.AngularVelocity = nVecs[15];
                if (!(s.HumanScale > 0f))
                    throw new InvalidOperationException(
                        $"Unity did not run the humanoid evaluation for '{animator.name}' (the stream came back empty).");
                return s;
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                nHandles.Dispose(); nMuscles.Dispose(); nVecs.Dispose(); nRots.Dispose();
                nScalars.Dispose();
            }
        }

        struct CaptureJob : IAnimationJob
        {
            public NativeArray<MuscleHandle> Handles;
            public NativeArray<float> Muscles;
            public NativeArray<Vector3> Vecs;
            public NativeArray<Quaternion> Rots;
            public NativeArray<float> Scalars;
            public bool GoalsFromPose;

            public void ProcessRootMotion(AnimationStream stream) { }

            public void ProcessAnimation(AnimationStream stream)
            {
                AnimationHumanStream h = stream.AsHuman();
                if (GoalsFromPose)
                {
                    for (int g = 0; g < 4; g++)
                    {
                        var goal = (AvatarIKGoal)g;
                        Vector3 p = h.GetGoalPositionFromPose(goal);
                        Quaternion q = h.GetGoalRotationFromPose(goal);
                        h.SetGoalPosition(goal, p);
                        h.SetGoalRotation(goal, q);
                    }
                }
                for (int i = 0; i < Handles.Length; i++) Muscles[i] = h.GetMuscle(Handles[i]);
                Scalars[0] = h.humanScale;
                Scalars[1] = h.leftFootHeight;
                Scalars[2] = h.rightFootHeight;
                Vecs[0] = h.bodyLocalPosition; Rots[0] = h.bodyLocalRotation;
                Vecs[1] = h.bodyPosition; Rots[1] = h.bodyRotation;
                for (int g = 0; g < 4; g++)
                {
                    var goal = (AvatarIKGoal)g;
                    Vecs[2 + g] = h.GetGoalLocalPosition(goal); Rots[2 + g] = h.GetGoalLocalRotation(goal);
                    Vecs[6 + g] = h.GetGoalPosition(goal); Rots[6 + g] = h.GetGoalRotation(goal);
                    Vecs[10 + g] = h.GetGoalPositionFromPose(goal); Rots[10 + g] = h.GetGoalRotationFromPose(goal);
                }
                Vecs[14] = stream.velocity; Rots[14] = Quaternion.identity;
                Vecs[15] = stream.angularVelocity; Rots[15] = Quaternion.identity;
            }
        }
    }
}
