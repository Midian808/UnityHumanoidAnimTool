using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// Applies edit records (C13b) to one frame on a <see cref="FramePoser"/>, in list order, each from the pose the
    /// previous one left. The baker and the edit session both go through here, so the preview of an edit in
    /// progress is what the bake writes.
    /// </summary>
    public static class RecordApplier
    {
        const float PositionEpsilon = 1e-6f;

        /// <summary>What one record did on one frame.</summary>
        public struct Outcome
        {
            /// <summary>True when the record changed the pose.</summary>
            public bool Applied;
            /// <summary>For a hand / foot: how far (metres) it ended up from where the record asked (C8).</summary>
            public float? ReachShortfall;
            /// <summary>For a hand / foot: how far (degrees) its rotation ended up from the asked rotation.</summary>
            public float? TurnShortfall;
            /// <summary>Why the record couldn't apply (a missing bone or prop), or null.</summary>
            public string Problem;
        }

        public static bool Loops(AnimationClip clip) => AnimationUtility.GetAnimationClipSettings(clip).loopTime;

        /// <summary>
        /// Every frame of <paramref name="clip"/> that an unmuted Offset or Pin record in <paramref name="records"/>
        /// changes (pose keys reach frames as a layer: see the overload taking the edits asset).
        /// </summary>
        public static SortedSet<int> TouchedFrames(IEnumerable<EditRecord> records, AnimationClip clip)
        {
            var frames = new SortedSet<int>();
            int last = ClipWriter.LastFrame(clip);
            bool loop = Loops(clip);
            foreach (EditRecord r in records)
                if (r != null && !r.muted && !(r is PoseKeyRecord)) r.AddTouchedFrames(frames, last, loop);
            return frames;
        }

        /// <summary>
        /// Every frame of <paramref name="timingClip"/> that <paramref name="edits"/>' records change: the Offset and
        /// Pin records' frames and every frame the pose-key layer reaches (<paramref name="layer"/>).
        /// </summary>
        public static SortedSet<int> TouchedFrames(ClipEditsAsset edits, PoseLayer layer, AnimationClip timingClip)
        {
            SortedSet<int> frames = TouchedFrames(edits.Records, timingClip);
            layer.AddTouchedFrames(frames);
            return frames;
        }

        /// <summary>
        /// Applies <paramref name="record"/> (muted or not) to the frame <paramref name="poser"/> shows, at the
        /// strength its ease gives that frame. <paramref name="timingClip"/> supplies the frame count and Loop Time
        /// (the output clip: loop settings are the output's).
        /// </summary>
        public static Outcome Apply(FramePoser poser, ClipEditsAsset edits, EditRecord record, AnimationClip timingClip)
        {
            var outcome = new Outcome();
            if (record is PoseKeyRecord) return outcome; // pose keys apply first, as a layer (FramePoser.ApplyPoseLayer)
            float w = record.Weight(poser.Frame, ClipWriter.LastFrame(timingClip), Loops(timingClip), out int at);
            if (w <= 0f) return outcome;
            Quaternion rootRotation = poser.Preview.Root.transform.rotation;

            switch (record)
            {
                case OffsetRecord o when o.target == OffsetTarget.Limb:
                {
                    if (!poser.CanMove(o.limb)) { outcome.Problem = "The limb isn't fully mapped on this character."; return outcome; }
                    OffsetKey k = o.Scaled(at, w);
                    if (IsZero(k))
                    {
                        // Nothing to move: the hand or foot is exactly where this key asks for it.
                        outcome.ReachShortfall = 0f;
                        outcome.TurnShortfall = 0f;
                        return outcome;
                    }
                    Transform end = poser.Bone(LimbIK.EndBone(o.limb));
                    Vector3 target = end.position + rootRotation * k.position;
                    Quaternion turn = end.rotation * k.rotation;
                    Vector3 hint = poser.DefaultHint(o.limb) + rootRotation * k.hint;
                    outcome.ReachShortfall = poser.MoveEffector(o.limb, target, turn, hint, out float turnShort);
                    outcome.TurnShortfall = turnShort;
                    outcome.Applied = true;
                    return outcome;
                }
                case OffsetRecord o:
                {
                    Transform t = poser.Bone(o.bone);
                    if (!t) { outcome.Problem = "The bone isn't mapped on this character."; return outcome; }
                    OffsetKey k = o.Scaled(at, w);
                    if (o.IsHips)
                    {
                        if (k.position.sqrMagnitude < PositionEpsilon * PositionEpsilon && IsIdentity(k.rotation))
                            return outcome;
                        if (k.position.sqrMagnitude < PositionEpsilon * PositionEpsilon)
                            poser.RotateBone(HumanBodyBones.Hips, t.rotation * k.rotation);
                        else
                            poser.MoveHips(t.position + rootRotation * k.position, t.rotation * k.rotation);
                    }
                    else
                    {
                        if (IsIdentity(k.rotation)) return outcome;
                        poser.RotateBone(o.bone, t.rotation * k.rotation);
                    }
                    outcome.Applied = true;
                    return outcome;
                }
                case PinRecord p:
                {
                    if (!poser.CanMove(p.limb)) { outcome.Problem = "The limb isn't fully mapped on this character."; return outcome; }
                    if (!AnchorPose(poser.Preview, edits, p, out Vector3 anchorPosition, out Quaternion anchorRotation, out string problem))
                    {
                        outcome.Problem = problem;
                        return outcome;
                    }
                    Transform end = poser.Bone(LimbIK.EndBone(p.limb));
                    Vector3 spot = anchorPosition + anchorRotation * p.position;
                    Quaternion spotRotation = anchorRotation * EditRecord.Normalized(p.rotation);
                    Vector3 target = Vector3.Lerp(end.position, spot, w);
                    Quaternion turn = p.holdRotation ? Quaternion.Slerp(end.rotation, spotRotation, w) : end.rotation;
                    Vector3 hint = poser.DefaultHint(p.limb) + rootRotation * (p.hint * w);
                    outcome.ReachShortfall = poser.MoveEffector(p.limb, target, turn, hint, out float turnShort);
                    outcome.TurnShortfall = turnShort;
                    outcome.Applied = true;
                    return outcome;
                }
            }
            return outcome;
        }

        /// <summary>
        /// Where a pin's anchor is on the character as posed now: the world origin, a bone, or a prop (C14).
        /// False (with the reason) when the bone isn't mapped or the prop is gone.
        /// </summary>
        public static bool AnchorPose(PreviewCharacter preview, ClipEditsAsset edits, PinRecord pin,
                                      out Vector3 position, out Quaternion rotation, out string problem)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            problem = null;
            switch (pin.anchor)
            {
                case PinAnchor.World:
                    return true;
                case PinAnchor.Bone:
                {
                    Transform b = preview.Animator.GetBoneTransform(pin.anchorBone);
                    if (!b) { problem = $"{pin.anchorBone} isn't mapped on this character."; return false; }
                    position = b.position;
                    rotation = b.rotation;
                    return true;
                }
                default:
                {
                    ReferenceProp prop = edits ? edits.FindProp(pin.propId) : null;
                    if (prop == null) { problem = "The prop this pin holds on to has been removed."; return false; }
                    if (!prop.TryGetPose(preview.Animator, out position, out rotation))
                    {
                        problem = $"The prop's bone ({prop.bone}) isn't mapped on this character.";
                        return false;
                    }
                    return true;
                }
            }
        }

        static bool IsZero(OffsetKey k) =>
            k.position.sqrMagnitude < PositionEpsilon * PositionEpsilon &&
            k.hint.sqrMagnitude < PositionEpsilon * PositionEpsilon &&
            IsIdentity(k.rotation);

        /// <summary>
        /// True for a turn too small to matter (under about 0.0001°). Measured on the quaternion's vector part:
        /// Quaternion.Angle reads anything under about 0.16° as 0.
        /// </summary>
        static bool IsIdentity(Quaternion q) => new Vector3(q.x, q.y, q.z).magnitude < 1e-6f;
    }
}
