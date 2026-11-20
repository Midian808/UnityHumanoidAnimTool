using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// The humanoid curves of one clip, read once, sampled at any time straight from the curves
    /// (no Animator involved). Muscles with no curve read as 0, matching how Unity evaluates them.
    /// Animator-bound curves that aren't humanoid (e.g. Animator parameter curves) are listed in
    /// <see cref="OtherAnimatorBindings"/> and never touched.
    /// </summary>
    public sealed class HumanoidClipCurves
    {
        public AnimationClip Clip { get; }

        readonly AnimationCurve[] muscles = new AnimationCurve[HumanTrait.MuscleCount];
        readonly AnimationCurve[] rootT = new AnimationCurve[3];
        readonly AnimationCurve[] rootQ = new AnimationCurve[4];
        readonly AnimationCurve[,] goalT = new AnimationCurve[HumanoidCurveNames.GoalCount, 3];
        readonly AnimationCurve[,] goalQ = new AnimationCurve[HumanoidCurveNames.GoalCount, 4];
        readonly List<EditorCurveBinding> otherAnimatorBindings = new List<EditorCurveBinding>();

        public IReadOnlyList<EditorCurveBinding> OtherAnimatorBindings => otherAnimatorBindings;

        public HumanoidClipCurves(AnimationClip clip)
        {
            Clip = clip ? clip : throw new ArgumentNullException(nameof(clip));
            foreach (EditorCurveBinding b in AnimationUtility.GetCurveBindings(clip))
            {
                if (b.type != typeof(Animator) || !string.IsNullOrEmpty(b.path)) continue;
                if (!Assign(b.propertyName, AnimationUtility.GetEditorCurve(clip, b)))
                    otherAnimatorBindings.Add(b);
            }
        }

        public bool HasMuscle(int index) => muscles[index] != null;

        public bool HasRoot => rootT[0] != null && rootQ[0] != null;

        public bool HasGoal(AvatarIKGoal goal) => goalT[(int)goal, 0] != null && goalQ[(int)goal, 0] != null;

        /// <summary>Number of muscles that have a curve.</summary>
        public int MuscleCurveCount
        {
            get { int n = 0; foreach (AnimationCurve c in muscles) if (c != null) n++; return n; }
        }

        /// <summary>The clip's pose at <paramref name="time"/>: muscles, RootT (normalized body position) and RootQ.</summary>
        public HumanPose SamplePose(float time)
        {
            var pose = new HumanPose { muscles = new float[muscles.Length] };
            for (int i = 0; i < muscles.Length; i++)
                pose.muscles[i] = muscles[i] != null ? muscles[i].Evaluate(time) : 0f;
            pose.bodyPosition = HasRoot ? Eval3(rootT, time) : Vector3.zero;
            pose.bodyRotation = HasRoot ? EvalQ(rootQ, time) : Quaternion.identity;
            return pose;
        }

        /// <summary>The goal's stored curve values at <paramref name="time"/>.</summary>
        public void SampleGoal(AvatarIKGoal goal, float time, out Vector3 position, out Quaternion rotation)
        {
            int g = (int)goal;
            if (!HasGoal(goal)) throw new InvalidOperationException($"Clip '{Clip.name}' has no {goal} goal curves.");
            position = new Vector3(goalT[g, 0].Evaluate(time), goalT[g, 1].Evaluate(time), goalT[g, 2].Evaluate(time));
            rotation = Normalize(new Quaternion(goalQ[g, 0].Evaluate(time), goalQ[g, 1].Evaluate(time),
                                                goalQ[g, 2].Evaluate(time), goalQ[g, 3].Evaluate(time)));
        }

        bool Assign(string name, AnimationCurve curve)
        {
            if (HumanoidCurveNames.TryGetMuscleIndex(name, out int m)) { muscles[m] = curve; return true; }
            for (int a = 0; a < 3; a++) if (name == HumanoidCurveNames.RootTName(a)) { rootT[a] = curve; return true; }
            for (int a = 0; a < 4; a++) if (name == HumanoidCurveNames.RootQName(a)) { rootQ[a] = curve; return true; }
            for (int g = 0; g < HumanoidCurveNames.GoalCount; g++)
            {
                for (int a = 0; a < 3; a++)
                    if (name == HumanoidCurveNames.GoalTName((AvatarIKGoal)g, a)) { goalT[g, a] = curve; return true; }
                for (int a = 0; a < 4; a++)
                    if (name == HumanoidCurveNames.GoalQName((AvatarIKGoal)g, a)) { goalQ[g, a] = curve; return true; }
            }
            return false;
        }

        static Vector3 Eval3(AnimationCurve[] c, float t) =>
            new Vector3(c[0].Evaluate(t), c[1].Evaluate(t), c[2].Evaluate(t));

        static Quaternion EvalQ(AnimationCurve[] c, float t) =>
            Normalize(new Quaternion(c[0].Evaluate(t), c[1].Evaluate(t), c[2].Evaluate(t), c[3].Evaluate(t)));

        static Quaternion Normalize(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m > 1e-6f ? new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m) : Quaternion.identity;
        }
    }
}
