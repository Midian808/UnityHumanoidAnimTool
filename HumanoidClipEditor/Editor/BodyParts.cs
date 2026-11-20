using System;
using UnityEngine;

namespace Midian.HumanoidClipEditor
{
    /// <summary>The parts of a humanoid an edit can touch — the same split as Unity's Avatar Mask.</summary>
    public enum HumanBodyPart
    {
        Body,
        Head,
        LeftArm,
        RightArm,
        LeftLeg,
        RightLeg,
        LeftFingers,
        RightFingers,
    }

    /// <summary>Which <see cref="HumanBodyPart"/> each humanoid bone and muscle belongs to (C17).</summary>
    public static class BodyParts
    {
        public static HumanBodyPart Of(HumanBodyBones bone)
        {
            switch (bone)
            {
                case HumanBodyBones.Hips:
                case HumanBodyBones.Spine:
                case HumanBodyBones.Chest:
                case HumanBodyBones.UpperChest:
                    return HumanBodyPart.Body;
                case HumanBodyBones.Neck:
                case HumanBodyBones.Head:
                case HumanBodyBones.LeftEye:
                case HumanBodyBones.RightEye:
                case HumanBodyBones.Jaw:
                    return HumanBodyPart.Head;
                case HumanBodyBones.LeftShoulder:
                case HumanBodyBones.LeftUpperArm:
                case HumanBodyBones.LeftLowerArm:
                case HumanBodyBones.LeftHand:
                    return HumanBodyPart.LeftArm;
                case HumanBodyBones.RightShoulder:
                case HumanBodyBones.RightUpperArm:
                case HumanBodyBones.RightLowerArm:
                case HumanBodyBones.RightHand:
                    return HumanBodyPart.RightArm;
                case HumanBodyBones.LeftUpperLeg:
                case HumanBodyBones.LeftLowerLeg:
                case HumanBodyBones.LeftFoot:
                case HumanBodyBones.LeftToes:
                    return HumanBodyPart.LeftLeg;
                case HumanBodyBones.RightUpperLeg:
                case HumanBodyBones.RightLowerLeg:
                case HumanBodyBones.RightFoot:
                case HumanBodyBones.RightToes:
                    return HumanBodyPart.RightLeg;
            }
            if (bone >= HumanBodyBones.LeftThumbProximal && bone <= HumanBodyBones.LeftLittleDistal)
                return HumanBodyPart.LeftFingers;
            if (bone >= HumanBodyBones.RightThumbProximal && bone <= HumanBodyBones.RightLittleDistal)
                return HumanBodyPart.RightFingers;
            throw new ArgumentOutOfRangeException(nameof(bone), bone, "Not a humanoid bone.");
        }

        /// <summary>The part of the bone that muscle <paramref name="muscleIndex"/> (a <see cref="HumanTrait.MuscleName"/> index) moves.</summary>
        public static HumanBodyPart OfMuscle(int muscleIndex) => Of((HumanBodyBones)HumanTrait.BoneFromMuscle(muscleIndex));
    }
}
