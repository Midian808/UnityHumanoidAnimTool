using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// Names of the curves a Humanoid clip stores (all bound to the <see cref="Animator"/> on the clip's root path),
    /// and the mapping between muscle curve names and <see cref="HumanTrait.MuscleName"/> indices.
    /// Body muscles use the trait name as the curve name; finger muscles don't
    /// (trait "Left Index 1 Stretched" is stored as curve "LeftHand.Index.1 Stretched").
    /// </summary>
    public static class HumanoidCurveNames
    {
        /// <summary>Body (centre of mass) position curve prefix: RootT.x / .y / .z.</summary>
        public const string RootT = "RootT";

        /// <summary>Body (centre of mass) rotation curve prefix: RootQ.x / .y / .z / .w.</summary>
        public const string RootQ = "RootQ";

        /// <summary>Goal curve prefixes indexed by <see cref="AvatarIKGoal"/> (LeftFoot, RightFoot, LeftHand, RightHand).</summary>
        public static readonly string[] GoalPrefixes = { "LeftFoot", "RightFoot", "LeftHand", "RightHand" };

        /// <summary>Number of IK goals stored in a Humanoid clip.</summary>
        public const int GoalCount = 4;

        static readonly string[] Axes3 = { "x", "y", "z" };
        static readonly string[] Axes4 = { "x", "y", "z", "w" };

        static readonly Regex FingerTrait =
            new Regex(@"^(Left|Right) (Thumb|Index|Middle|Ring|Little) (\d Stretched|Spread)$");

        static string[] muscleCurveNames;
        static Dictionary<string, int> muscleIndexByCurveName;

        /// <summary>Curve name of each muscle, indexed like <see cref="HumanTrait.MuscleName"/>.</summary>
        public static IReadOnlyList<string> MuscleCurveNames
        {
            get { Build(); return muscleCurveNames; }
        }

        /// <summary>Converts a <see cref="HumanTrait.MuscleName"/> entry to the curve name clips store it under.</summary>
        public static string TraitNameToCurveName(string traitName)
        {
            Match m = FingerTrait.Match(traitName);
            return m.Success ? $"{m.Groups[1].Value}Hand.{m.Groups[2].Value}.{m.Groups[3].Value}" : traitName;
        }

        /// <summary>Muscle index for a muscle curve name, or false if the name isn't a muscle curve.</summary>
        public static bool TryGetMuscleIndex(string curveName, out int index)
        {
            Build();
            return muscleIndexByCurveName.TryGetValue(curveName, out index);
        }

        /// <summary>RootT component curve name, axis 0..2.</summary>
        public static string RootTName(int axis) => $"{RootT}.{Axes3[axis]}";

        /// <summary>RootQ component curve name, component 0..3 (x, y, z, w).</summary>
        public static string RootQName(int component) => $"{RootQ}.{Axes4[component]}";

        /// <summary>Goal position component curve name, e.g. LeftFootT.x.</summary>
        public static string GoalTName(AvatarIKGoal goal, int axis) => $"{GoalPrefixes[(int)goal]}T.{Axes3[axis]}";

        /// <summary>Goal rotation component curve name, e.g. LeftFootQ.w.</summary>
        public static string GoalQName(AvatarIKGoal goal, int component) => $"{GoalPrefixes[(int)goal]}Q.{Axes4[component]}";

        static void Build()
        {
            if (muscleCurveNames != null) return;
            string[] traits = HumanTrait.MuscleName;
            var names = new string[traits.Length];
            var map = new Dictionary<string, int>(traits.Length);
            for (int i = 0; i < traits.Length; i++)
            {
                names[i] = TraitNameToCurveName(traits[i]);
                map[names[i]] = i;
            }
            muscleIndexByCurveName = map;
            muscleCurveNames = names;
        }
    }
}
