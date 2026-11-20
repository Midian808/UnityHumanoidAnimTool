using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// Poses the preview character for one frame of a clip and applies edits on top, each starting from the pose the
    /// previous one left: turn a bone (C5), move and turn the hips, move a hand or foot with Unity's IK (C4).
    /// It keeps the two poses <see cref="ClipWriter"/> needs: the frame as Unity played it, and the edited pose.
    ///
    /// The frame is shown as the game plays it, apart from Mirror (C18): the clip is evaluated through Unity
    /// (<see cref="RawClipCopy.CreateForPreview"/>), and the character stands where the clip's root motion puts it on
    /// that frame (or on a given travel path). That matters for edits: on a clip whose horizontal root motion isn't
    /// baked into the pose, the root follows the body's centre of mass, so a preview pinned to the origin shows the
    /// whole body slide when a limb moves (8 mm on a lifted foot, measured).
    /// </summary>
    public sealed class FramePoser : IDisposable
    {
        public PreviewCharacter Preview { get; }
        /// <summary>The clip whose frames are shown.</summary>
        public AnimationClip Clip { get; }
        public int Frame { get; private set; }
        public int LastFrame => ClipWriter.LastFrame(Clip);
        public float Time => CurveKeys.FrameTime(Frame, Clip.frameRate);

        /// <summary>Where the character stands on each frame (index = frame).</summary>
        public IReadOnlyList<Pose> Travel => travel;

        /// <summary>The frame as Unity played it: muscles exactly as the stream held them, body read back.</summary>
        public HumanPose Played => played;

        /// <summary>The edited pose (muscles exact for the edited parts, body read back); the played pose before any edit.</summary>
        public HumanPose Current => pending ?? played;

        /// <summary>True once an edit has changed the frame.</summary>
        public bool HasEdit => pending.HasValue;

        /// <summary>Body parts whose bones the edits turned.</summary>
        public IReadOnlyCollection<HumanBodyPart> EditedParts => editedParts;

        /// <summary>True when an edit turned or moved the hips themselves.</summary>
        public bool HipsEdited { get; private set; }

        /// <summary>The body part the last single edit step (turn a bone, move a hand or foot) turned, if any.</summary>
        public HumanBodyPart? LastStepPart { get; private set; }

        /// <summary>True when the last single edit step turned or moved the hips.</summary>
        public bool LastStepHips { get; private set; }

        AnimationClip previewCopy;
        List<Pose> travel;
        HumanPose played;
        HumanPose? pending;
        float[] currentMuscles;
        readonly HashSet<HumanBodyPart> editedParts = new HashSet<HumanBodyPart>();
        readonly (Vector3 position, Quaternion rotation)[] goalOffsets =
            new (Vector3, Quaternion)[HumanoidCurveNames.GoalCount];

        /// <summary>
        /// Shows frames of <paramref name="clip"/> on <paramref name="preview"/>, played with the clip settings of
        /// <paramref name="settingsFrom"/> (default: its own; an edits asset's source plays with the output's). The
        /// character stands on <paramref name="travelPath"/> (one pose per frame) when given, else on the clip's own
        /// root-motion path.
        /// </summary>
        public FramePoser(AnimationClip clip, PreviewCharacter preview, IReadOnlyList<Pose> travelPath = null,
                          AnimationClip settingsFrom = null)
        {
            if (!clip) throw new ArgumentNullException(nameof(clip));
            if (!clip.humanMotion) throw new ArgumentException($"'{clip.name}' isn't a humanoid clip.", nameof(clip));
            Clip = clip;
            Preview = preview ?? throw new ArgumentNullException(nameof(preview));
            previewCopy = RawClipCopy.CreateForPreview(clip, settingsFrom);
            travel = travelPath != null
                ? new List<Pose>(travelPath)
                : HumanStreamProbe.TravelPath(preview.Animator, previewCopy, clip.frameRate);
        }

        /// <summary>Stands the character on <paramref name="travelPath"/> from the next <see cref="Show"/> on.</summary>
        public void SetTravel(IReadOnlyList<Pose> travelPath) => travel = new List<Pose>(travelPath);

        /// <summary>Shows <paramref name="frame"/> (clamped to the clip) as Unity plays it; any edit is dropped.</summary>
        public void Show(int frame)
        {
            Frame = Mathf.Clamp(frame, 0, LastFrame);
            pending = null;
            editedParts.Clear();
            HipsEdited = false;
            LastStepPart = null;
            LastStepHips = false;

            Pose root = travel.Count > 0 ? travel[Mathf.Clamp(Frame, 0, travel.Count - 1)] : new Pose(Vector3.zero, Quaternion.identity);
            Preview.Root.transform.SetPositionAndRotation(root.position, root.rotation);
            HumanStreamSample s = HumanStreamProbe.EvaluateClip(Preview.Animator, previewCopy, Time);
            // Muscles as Unity played them, straight from the stream: the stored values plus anything playback adds
            // (Loop Pose). A read-back of the bones would differ from them (twist re-split, clamping), and that
            // difference would end up in the clip. The body has no such problem, and has to be in the preview's
            // space, so it is read back.
            played = Preview.ReadPose();
            played.muscles = (float[])s.Muscles.Clone();
            currentMuscles = (float[])s.Muscles.Clone();

            // Where each IK goal sits relative to its hand / foot bone (a foot's goal isn't at the bone's pivot), so a
            // target for the bone can be turned into a goal for the solver.
            for (int g = 0; g < goalOffsets.Length; g++)
            {
                Transform end = Bone(LimbIK.EndBone((AvatarIKGoal)g));
                if (!end) continue;
                Quaternion inv = Quaternion.Inverse(end.rotation);
                goalOffsets[g] = (inv * (s.GoalPositionFromPose[g] - end.position), inv * s.GoalRotationFromPose[g]);
            }
        }

        public Transform Bone(HumanBodyBones bone) => Preview.Animator.GetBoneTransform(bone);

        /// <summary>True when the character has the bones a goal's IK needs (upper limb, lower limb, hand / foot).</summary>
        public bool CanMove(AvatarIKGoal goal) => CanMove(Preview, goal);

        public static bool CanMove(PreviewCharacter preview, AvatarIKGoal goal) =>
            preview.Animator.GetBoneTransform(LimbIK.RootBone(goal)) &&
            preview.Animator.GetBoneTransform(LimbIK.MidBone(goal)) &&
            preview.Animator.GetBoneTransform(LimbIK.EndBone(goal));

        /// <summary>
        /// Where the elbow / knee hint sits by default for the pose on the bones now: out from the joint, in the
        /// direction the limb is already bent, so solving with it keeps the bend. A straight limb bends forward (knee)
        /// or backward (elbow) relative to the character.
        /// </summary>
        public Vector3 DefaultHint(AvatarIKGoal goal) => DefaultHint(Preview, goal);

        public static Vector3 DefaultHint(PreviewCharacter preview, AvatarIKGoal goal)
        {
            Animator a = preview.Animator;
            Vector3 root = a.GetBoneTransform(LimbIK.RootBone(goal)).position;
            Vector3 mid = a.GetBoneTransform(LimbIK.MidBone(goal)).position;
            Vector3 end = a.GetBoneTransform(LimbIK.EndBone(goal)).position;
            Vector3 axis = end - root;
            Vector3 onAxis = root + Vector3.Project(mid - root, axis.sqrMagnitude > 1e-8f ? axis : Vector3.down);
            Vector3 bend = mid - onAxis;
            if (bend.sqrMagnitude < 1e-6f)
            {
                bool knee = goal == AvatarIKGoal.LeftFoot || goal == AvatarIKGoal.RightFoot;
                bend = preview.Root.transform.forward * (knee ? 1f : -1f);
            }
            return mid + bend.normalized * Mathf.Max(0.05f, (mid - root).magnitude * 0.5f);
        }

        /// <summary>
        /// Turns <paramref name="bone"/> to <paramref name="worldRotation"/>. The pose then snaps to the nearest one a
        /// humanoid can hold (muscle limits), which is what gets written.
        /// </summary>
        public void RotateBone(HumanBodyBones bone, Quaternion worldRotation)
        {
            Transform t = Bone(bone);
            if (!t) throw new ArgumentException($"{bone} isn't mapped on this character.", nameof(bone));
            var before = Snapshot();
            t.rotation = worldRotation;
            if (bone == HumanBodyBones.Hips) Settle(before, null, hipsTurned: true, hipsMoved: false);
            else Settle(before, BodyParts.Of(bone), hipsTurned: false, hipsMoved: false);
        }

        /// <summary>Moves and turns the hips (the whole body) to <paramref name="worldPosition"/> / <paramref name="worldRotation"/>.</summary>
        public void MoveHips(Vector3 worldPosition, Quaternion worldRotation)
        {
            Transform t = Bone(HumanBodyBones.Hips);
            var before = Snapshot();
            t.SetPositionAndRotation(worldPosition, worldRotation);
            Settle(before, null, hipsTurned: true, hipsMoved: true);
        }

        /// <summary>
        /// Moves the hand or foot of <paramref name="goal"/> so its bone sits at <paramref name="position"/> with
        /// <paramref name="rotation"/> (world space), solved with Unity's humanoid IK (C4) from the current pose and
        /// bending the elbow / knee towards <paramref name="hint"/>. Only that limb changes (C17). Returns how far
        /// (metres) the bone ended up from <paramref name="position"/> once held to the muscle limits (C8), and in
        /// <paramref name="turnShortfall"/> how far (degrees) from <paramref name="rotation"/>.
        /// </summary>
        public float MoveEffector(AvatarIKGoal goal, Vector3 position, Quaternion rotation, Vector3 hint,
                                  out float turnShortfall)
        {
            if (!CanMove(goal)) throw new ArgumentException($"This character's {goal} limb isn't fully mapped.", nameof(goal));
            var before = Snapshot();
            (Vector3 offset, Quaternion turn) = goalOffsets[(int)goal];
            LimbIK.Solve(Preview.Animator, previewCopy, Time, Current, goal, position + rotation * offset, rotation * turn, hint);
            Settle(before, LimbIK.Part(goal), hipsTurned: false, hipsMoved: false);

            Transform end = Bone(LimbIK.EndBone(goal));
            turnShortfall = Quaternion.Angle(end.rotation, rotation);
            return (end.position - position).magnitude;
        }

        /// <summary>
        /// Applies a pose-key layer sample (C16c–d): each muscle with a weight blends from its current value towards
        /// the sample's, and the hips blend towards the sample's place (height and tilt by the body weight,
        /// horizontal position and facing by the travel weight), in the character's root space. The hips stay where
        /// they are when neither weight is set, so a layer posing only the arms doesn't move them. As with every
        /// edit step, bones outside the blended parts keep their local rotations, every bone keeps its local position
        /// (Translation DoF), and the blended muscles are kept exactly as blended.
        /// </summary>
        public void ApplyPoseLayer(PoseLayer.Sample sample)
        {
            if (sample == null || !sample.Any) return;
            var before = Snapshot();
            float[] target = (float[])currentMuscles.Clone();
            var parts = new HashSet<HumanBodyPart>();
            for (int m = 0; m < target.Length; m++)
            {
                float w = sample.MuscleWeights[m];
                if (w <= 0f) continue;
                target[m] = Mathf.Lerp(currentMuscles[m], sample.Muscles[m], w);
                parts.Add(BodyParts.OfMuscle(m));
            }

            bool hipsMoved = sample.BodyWeight > 0f || sample.TravelWeight > 0f;
            Vector3 hipsPosition = Vector3.zero;
            Quaternion hipsRotation = Quaternion.identity;
            if (hipsMoved)
            {
                HipsInRoot(out Vector3 position, out Quaternion rotation);
                PoseLayer.Split(rotation, out float yaw, out Quaternion tilt);
                float wb = sample.BodyWeight, wt = sample.TravelWeight;
                var local = new Vector3(
                    Mathf.Lerp(position.x, sample.HipsHorizontal.x, wt),
                    Mathf.Lerp(position.y, sample.HipsHeight, wb),
                    Mathf.Lerp(position.z, sample.HipsHorizontal.y, wt));
                Quaternion turned = PoseLayer.Join(
                    Mathf.LerpAngle(yaw, sample.HipsYaw, wt),
                    wb > 0f ? Quaternion.Slerp(tilt, sample.HipsTilt, wb) : tilt);
                Transform root = Preview.Root.transform;
                hipsPosition = root.position + root.rotation * local;
                hipsRotation = root.rotation * turned;
            }

            HumanPose pose = Current;
            pose.muscles = target;
            Preview.ApplyPose(pose);
            for (int i = 0; i < before.Length; i++)
            {
                if (!before[i].HasValue) continue;
                var bone = (HumanBodyBones)i;
                Transform t = Bone(bone);
                if (bone == HumanBodyBones.Hips)
                {
                    if (hipsMoved) t.SetPositionAndRotation(hipsPosition, hipsRotation);
                    else { t.localPosition = before[i].Value.position; t.localRotation = before[i].Value.rotation; }
                    continue;
                }
                t.localPosition = before[i].Value.position;
                if (!parts.Contains(BodyParts.Of(bone))) t.localRotation = before[i].Value.rotation;
            }
            foreach (HumanBodyPart part in parts)
                for (int m = 0; m < currentMuscles.Length; m++)
                    if (BodyParts.OfMuscle(m) == part) currentMuscles[m] = target[m];

            HumanPose settled = Preview.ReadPose();
            settled.muscles = (float[])currentMuscles.Clone();
            pending = settled;
            editedParts.UnionWith(parts);
            if (hipsMoved) HipsEdited = true;
        }

        /// <summary>Where the hips are now, in the character's root space.</summary>
        public void HipsInRoot(out Vector3 position, out Quaternion rotation)
        {
            Transform hips = Bone(HumanBodyBones.Hips);
            Transform root = Preview.Root.transform;
            Quaternion inv = Quaternion.Inverse(root.rotation);
            position = inv * (hips.position - root.position);
            rotation = EditRecord.Normalized(inv * hips.rotation);
        }

        /// <summary>The pose shown now, as a pose key holds it: the exact muscles and the hips in the root's space.</summary>
        public KeyPose CapturePose()
        {
            HipsInRoot(out Vector3 position, out Quaternion rotation);
            return new KeyPose { muscles = (float[])currentMuscles.Clone(), hipsPosition = position, hipsRotation = rotation };
        }

        public void Dispose()
        {
            if (previewCopy) Object.DestroyImmediate(previewCopy);
            previewCopy = null;
        }

        (Vector3 position, Quaternion rotation)?[] Snapshot()
        {
            var s = new (Vector3, Quaternion)?[(int)HumanBodyBones.LastBone];
            for (int i = 0; i < s.Length; i++)
            {
                Transform t = Bone((HumanBodyBones)i);
                if (t) s[i] = (t.localPosition, t.localRotation);
            }
            return s;
        }

        /// <summary>
        /// Holds the preview to what a write will produce after one edit step. First the pose snaps to the nearest one
        /// a humanoid can hold (muscle limits). Applying a read-back pose also moves bones the step didn't touch (Unity
        /// re-splits twist between neighbours: measured over 2° on an upper leg) and drops Translation DoF offsets, so
        /// then every bone gets back its local position from before the step (edits never change Translation DoF
        /// curves; the hips keep a new position when the step moved them), and every bone outside the part the step
        /// turned gets back its local rotation from before the step.
        /// The muscles of the turned part are the ones applied, which is what the preview shows of it (reading the
        /// bones back again would re-split twist and drift from it); every other muscle keeps its exact value from
        /// before the step. The body is read back from the result.
        /// </summary>
        void Settle((Vector3 position, Quaternion rotation)?[] before, HumanBodyPart? part, bool hipsTurned, bool hipsMoved)
        {
            HumanPose applied = Preview.ReadPose();
            Preview.ApplyPose(applied);
            for (int i = 0; i < before.Length; i++)
            {
                if (!before[i].HasValue) continue;
                var bone = (HumanBodyBones)i;
                Transform t = Bone(bone);
                bool hips = bone == HumanBodyBones.Hips;
                if (!(hips && hipsMoved)) t.localPosition = before[i].Value.position;
                bool turned = hips ? hipsTurned : part.HasValue && BodyParts.Of(bone) == part.Value;
                if (!turned) t.localRotation = before[i].Value.rotation;
            }

            if (part.HasValue)
                for (int m = 0; m < currentMuscles.Length; m++)
                    if (BodyParts.OfMuscle(m) == part.Value) currentMuscles[m] = applied.muscles[m];

            HumanPose settled = Preview.ReadPose();
            settled.muscles = (float[])currentMuscles.Clone();
            pending = settled;
            if (part.HasValue) editedParts.Add(part.Value);
            if (hipsTurned) HipsEdited = true;
            LastStepPart = part;
            LastStepHips = hipsTurned || hipsMoved;
        }
    }
}
