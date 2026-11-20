using System;
using UnityEngine;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// The Animator settings every hand-evaluated graph in the tool needs, set for the duration of a <c>using</c> and
    /// put back afterwards: bound to its avatar, animation events off (clip events call game scripts the preview
    /// doesn't have), and Always Animate (other culling modes skip the humanoid pass when no renderer is visible,
    /// leaving the stream empty).
    /// </summary>
    internal readonly struct AnimatorEvaluationScope : IDisposable
    {
        readonly Animator animator;
        readonly bool fireEvents;
        readonly AnimatorCullingMode culling;

        public AnimatorEvaluationScope(Animator animator)
        {
            if (!animator || !animator.avatar || !animator.avatar.isHuman)
                throw new ArgumentException("Needs an Animator with a humanoid Avatar.", nameof(animator));
            this.animator = animator;
            fireEvents = animator.fireEvents;
            culling = animator.cullingMode;
            if (!animator.isInitialized) animator.Rebind();
            animator.fireEvents = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        public void Dispose()
        {
            if (!animator) return;
            animator.fireEvents = fireEvents;
            animator.cullingMode = culling;
        }
    }
}
