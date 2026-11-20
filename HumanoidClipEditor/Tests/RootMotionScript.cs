using UnityEngine;

namespace Midian.HumanoidClipEditor.Tests
{
    /// <summary>
    /// Stands in for a game script that handles root motion itself (as a player controller does): having
    /// <c>OnAnimatorMove</c> on the Animator's object takes root motion over from Unity. Used by <see cref="TravelTests"/>.
    /// </summary>
    public sealed class RootMotionScript : MonoBehaviour
    {
        void OnAnimatorMove() { }
    }
}
