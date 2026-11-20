using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// Editing one clip's adjustment layer (C13) on the preview character: go to a frame, then turn a bone, move the
    /// hips, move a hand or foot with IK, or pin one; <see cref="Commit"/> stores the change as a record and re-bakes
    /// the output, <see cref="Cancel"/> drops it. The window drives this; so do the tests, so they exercise the same
    /// path.
    ///
    /// Between edits the preview shows the output clip as the game plays it (C18). While an edit is in progress it
    /// shows the frame the bake would write: the source frame with the pose keys applied as a layer, then every other
    /// record in list order, the one being edited included. Where a handle edit goes:
    /// <list type="number">
    /// <item>into the selected record when it already does the same thing: a pose key on this frame, an offset of
    /// the same bone or limb, or a pin of the same limb on a frame inside its range;</item>
    /// <item>on a new clip (C16), into the pose key on this frame, made if there is none;</item>
    /// <item>otherwise into a new offset added at the end of the list.</item>
    /// </list>
    /// Offsets are relative to the pose the layer and the earlier records leave, so the edit's amount is worked out
    /// against that pose. A pose key holds a whole pose: the edit is made on the layer's pose for this frame (the
    /// Offsets and Pins on top don't take part) and the result is what the key stores.
    /// </summary>
    public sealed class FrameEditSession : IDisposable
    {
        public ClipEditsAsset Edits { get; }
        /// <summary>The output clip (what the game plays).</summary>
        public AnimationClip Clip => Edits.Output;
        /// <summary>True when the clip is a new one made from a start pose and pose keys (C16).</summary>
        public bool IsNewClip => Edits.IsNewClip;
        public PreviewCharacter Preview { get; }
        public int Frame { get; private set; }
        public int LastFrame => ClipWriter.LastFrame(Clip);
        public float Time => CurveKeys.FrameTime(Frame, Clip.frameRate);

        /// <summary>The record handle edits go into, or -1 (they then make a new record).</summary>
        public int SelectedRecord
        {
            get => selected >= 0 && selected < Edits.Records.Count ? selected : -1;
            set => selected = value;
        }

        /// <summary>Ease in and out (frames) given to records the session creates.</summary>
        public int NewEditEaseFrames { get; set; } = 5;

        /// <summary>True while an edit is in progress (not yet committed or cancelled).</summary>
        public bool HasPendingEdit => pending != null;

        /// <summary>Body parts the shown frame has changed, while an edit is in progress.</summary>
        public IReadOnlyCollection<HumanBodyPart> EditedParts =>
            pending != null ? chain.EditedParts : (IReadOnlyCollection<HumanBodyPart>)Array.Empty<HumanBodyPart>();

        /// <summary>While moving a hand or foot: how far (metres) it ended up from the handle (C8).</summary>
        public float? ReachShortfall { get; private set; }

        /// <summary>While moving a hand or foot: how far (degrees) its rotation ended up from the handle's.</summary>
        public float? TurnShortfall { get; private set; }

        /// <summary>The last bake's report, or null before the first bake of this session.</summary>
        public Baker.Result LastBake { get; private set; }

        /// <summary>
        /// True when the output has Loop Pose on and the current frame is its first or last: Loop Pose's correction is
        /// worked out from those two frames, so an edit here shifts every frame of the clip slightly (C18).
        /// </summary>
        public bool EditShiftsWholeLoop =>
            AnimationUtility.GetAnimationClipSettings(Clip).loopBlend && (Frame == 0 || Frame == LastFrame);

        /// <summary>True when the output has Mirror on; the preview then shows it unmirrored (the sides its curves are written for).</summary>
        public bool ClipIsMirrored => AnimationUtility.GetAnimationClipSettings(Clip).mirror;

        /// <summary>The shown reference props (C14).</summary>
        public PreviewProps Props { get; } = new PreviewProps();

        FramePoser output;   // shows the output clip
        FramePoser chain;    // shows the source with records applied, standing on the chain travel
        List<Pose> chainTravel;
        ClipEditsAsset.BaseClip baseClip; // what the chain starts from: the source, or a new clip's start pose
        PoseLayer layer;     // the committed pose keys
        EditRecord pending;
        int pendingIndex = -1;
        int selected = -1;

        /// <summary>Edits <paramref name="edits"/> on <paramref name="preview"/> (the character the fixes are made for, C6).</summary>
        public FrameEditSession(ClipEditsAsset edits, PreviewCharacter preview)
        {
            Edits = edits ? edits : throw new ArgumentNullException(nameof(edits));
            string problem = edits.Problem();
            if (problem != null) throw new ArgumentException(problem, nameof(edits));
            Preview = preview ?? throw new ArgumentNullException(nameof(preview));
            Reload();
        }

        /// <summary>Shows <paramref name="frame"/> (clamped to the clip) as the output plays it. An edit in progress is cancelled.</summary>
        public void GoToFrame(int frame)
        {
            ClearPending();
            Frame = Mathf.Clamp(frame, 0, LastFrame);
            output.Show(Frame);
        }

        /// <summary>Re-reads the edits asset and the output (after an outside change such as Undo) and shows the current frame again.</summary>
        public void Reload() => Reload(Baker.ChainTravel(Edits, Preview, chainTravel));

        /// <summary>
        /// Shows the output again with the source standing on <paramref name="travel"/> while edits are posed (see
        /// <see cref="Baker"/>: where it has to stand so an edit lands where the output will show it).
        /// </summary>
        void Reload(List<Pose> travel)
        {
            ClearPending();
            chainTravel = travel;
            output?.Dispose();
            chain?.Dispose();
            baseClip?.Dispose();
            baseClip = Edits.OpenBase(Preview.Animator);
            layer = PoseLayer.For(Edits, Edits.Records, Edits.Output);
            output = new FramePoser(Edits.Output, Preview);
            chain = new FramePoser(baseClip.Clip, Preview, chainTravel, settingsFrom: Edits.Output);
            Props.Sync(Edits.Props, Preview);
            Frame = Mathf.Clamp(Frame, 0, LastFrame);
            output.Show(Frame);
        }

        /// <summary>Rebuilds the output from the source and the records, then shows the current frame.</summary>
        public Baker.Result Rebake()
        {
            ClearPending();
            LastBake = Baker.Bake(Edits, Preview, chainTravel);
            Reload(LastBake.ChainTravel);
            return LastBake;
        }

        /// <summary>True when the character has the bones a goal's IK needs (upper limb, lower limb, hand / foot).</summary>
        public bool CanMove(AvatarIKGoal goal) => FramePoser.CanMove(Preview, goal);

        /// <summary>Where the elbow / knee hint sits by default for the pose shown now (keeps the shown bend).</summary>
        public Vector3 DefaultHint(AvatarIKGoal goal) => FramePoser.DefaultHint(Preview, goal);

        /// <summary>
        /// Turns <paramref name="bone"/> to <paramref name="worldRotation"/> on this frame (C5). The pose then snaps to
        /// the nearest one a humanoid can hold (muscle limits), which is what a commit writes. The hips go into a
        /// move / turn hips offset.
        /// </summary>
        public void RotateBone(HumanBodyBones bone, Quaternion worldRotation)
        {
            if (!Preview.Animator.GetBoneTransform(bone))
                throw new ArgumentException($"{bone} isn't mapped on this character.", nameof(bone));
            Func<EditRecord, bool> fits = r => r is OffsetRecord o && o.target == OffsetTarget.Bone && o.bone == bone;
            if (PoseKeyFor(fits, out int key)) { EditPoseKey(key, poser => poser.RotateBone(bone, worldRotation)); return; }
            int index = Matching(fits);
            Edit(index, () => OffsetRecord.ForBone(bone), (record, poser) =>
            {
                var o = (OffsetRecord)record;
                Transform t = poser.Bone(bone);
                OffsetKey key = o.Sample(Frame);
                key.frame = Frame;
                key.rotation = Quaternion.Inverse(t.rotation) * worldRotation;
                o.SetKey(key);
            });
        }

        /// <summary>Moves and turns the hips (the whole body) to <paramref name="worldPosition"/> / <paramref name="worldRotation"/> on this frame.</summary>
        public void MoveHips(Vector3 worldPosition, Quaternion worldRotation)
        {
            if (!Preview.Animator.GetBoneTransform(HumanBodyBones.Hips)) throw new InvalidOperationException("The hips aren't mapped.");
            Func<EditRecord, bool> fits = r => r is OffsetRecord o && o.IsHips;
            if (PoseKeyFor(fits, out int key)) { EditPoseKey(key, poser => poser.MoveHips(worldPosition, worldRotation)); return; }
            int index = Matching(fits);
            Edit(index, () => OffsetRecord.ForBone(HumanBodyBones.Hips), (record, poser) =>
            {
                var o = (OffsetRecord)record;
                Transform t = poser.Bone(HumanBodyBones.Hips);
                Quaternion root = poser.Preview.Root.transform.rotation;
                OffsetKey key = o.Sample(Frame);
                key.frame = Frame;
                key.position = Quaternion.Inverse(root) * (worldPosition - t.position);
                key.rotation = Quaternion.Inverse(t.rotation) * worldRotation;
                o.SetKey(key);
            });
        }

        /// <summary>
        /// Asks for the hand or foot of <paramref name="goal"/> to sit at <paramref name="position"/> with
        /// <paramref name="rotation"/> (world space) on this frame, solved with Unity's IK (C4); only that limb
        /// changes (C17). With <paramref name="hint"/> the elbow / knee bends towards that point; without it the
        /// record's own bend is kept. <see cref="ReachShortfall"/> says how close it got (C8). The edit goes into the
        /// selected pin of this limb when this frame is inside its range (moving the pinned spot), or the selected
        /// offset of this limb, or a new offset.
        /// </summary>
        public void MoveEffector(AvatarIKGoal goal, Vector3 position, Quaternion rotation, Vector3? hint = null)
        {
            if (!CanMove(goal)) throw new ArgumentException($"This character's {goal} limb isn't fully mapped.", nameof(goal));
            Func<EditRecord, bool> fits = r =>
                r is OffsetRecord o && o.target == OffsetTarget.Limb && o.limb == goal ||
                r is PinRecord p && p.limb == goal && Frame >= p.FirstFrame && Frame <= p.LastFrame;
            if (PoseKeyFor(fits, out int key))
            {
                EditPoseKey(key, poser =>
                {
                    ReachShortfall = poser.MoveEffector(goal, position, rotation, hint ?? poser.DefaultHint(goal), out float turn);
                    TurnShortfall = turn;
                });
                return;
            }
            int index = Matching(fits);
            Edit(index, () => OffsetRecord.ForLimb(goal), (record, poser) =>
            {
                Quaternion root = poser.Preview.Root.transform.rotation;
                if (record is PinRecord pin)
                {
                    if (!RecordApplier.AnchorPose(poser.Preview, Edits, pin, out Vector3 ap, out Quaternion ar, out string problem))
                        throw new InvalidOperationException(problem);
                    Quaternion inv = Quaternion.Inverse(ar);
                    pin.position = inv * (position - ap);
                    pin.rotation = inv * rotation;
                    if (hint.HasValue) pin.hint = Quaternion.Inverse(root) * (hint.Value - poser.DefaultHint(goal));
                    return;
                }
                var o = (OffsetRecord)record;
                Transform end = poser.Bone(LimbIK.EndBone(goal));
                OffsetKey key = o.Sample(Frame);
                key.frame = Frame;
                key.position = Quaternion.Inverse(root) * (position - end.position);
                key.rotation = Quaternion.Inverse(end.rotation) * rotation;
                if (hint.HasValue) key.hint = Quaternion.Inverse(root) * (hint.Value - poser.DefaultHint(goal));
                o.SetKey(key);
            });
        }

        /// <summary>The unmuted pose key on this frame, or -1.</summary>
        public int PoseKeyAtFrame()
        {
            List<EditRecord> records = Edits.Records;
            for (int i = records.Count - 1; i >= 0; i--)
                if (records[i] is PoseKeyRecord k && !k.muted && k.frame == Frame) return i;
            return -1;
        }

        /// <summary>
        /// Adds a pose key (C16c) on this frame holding the pose the pose keys already give it (on a fixed clip with
        /// no key nearby, the clip's own pose), covering every body part; Offsets and Pins on top aren't part of it.
        /// When there is a key on this frame already it is selected instead. Commits and re-bakes; returns the key's
        /// index (also selected).
        /// </summary>
        public int AddPoseKey()
        {
            ClearPending();
            int existing = PoseKeyAtFrame();
            if (existing >= 0)
            {
                ClearPending();
                SelectedRecord = existing;
                return existing;
            }
            EditPoseKey(-1, null);
            Commit();
            return SelectedRecord;
        }

        /// <summary>
        /// The pose shown on this frame (C16g), as a pose key would hold it: the muscles as the output plays them and
        /// the hips relative to where the character stands while records are posed (the chain travel), so pasting it
        /// on another frame or clip puts the hips in the same place relative to the character. An edit in progress
        /// is cancelled first.
        /// </summary>
        public KeyPose CopyPose()
        {
            GoToFrame(Frame);
            Pose stand = StandingPose();
            Transform hips = Preview.Animator.GetBoneTransform(HumanBodyBones.Hips);
            Quaternion inv = Quaternion.Inverse(stand.rotation);
            return new KeyPose
            {
                muscles = (float[])output.Played.muscles.Clone(),
                hipsPosition = inv * (hips.position - stand.position),
                hipsRotation = EditRecord.Normalized(inv * hips.rotation),
            };
        }

        /// <summary>
        /// Makes this frame's pose <paramref name="pose"/> (C16g): it goes into the pose key on this frame (made if
        /// there is none), which then covers every body part. Commits and re-bakes; returns the key's index (also
        /// selected).
        /// </summary>
        public int PastePose(KeyPose pose, string undoName = "Paste Pose")
        {
            if (pose == null || !pose.IsValid) throw new ArgumentException("Not a complete pose.", nameof(pose));
            int index = -1;
            int existing = PoseKeyAtFrame();
            KeyPose copy = pose.Clone();
            copy.hipsRotation = EditRecord.Normalized(copy.hipsRotation);
            ChangeRecords(undoName, list =>
            {
                if (existing >= 0 && existing < list.Count && list[existing] is PoseKeyRecord k)
                {
                    k.pose = copy;
                    k.mask = PoseKeyRecord.AllParts;
                    index = existing;
                }
                else
                {
                    list.Add(NewPoseKey(copy));
                    index = list.Count - 1;
                }
            });
            SelectedRecord = index;
            return index;
        }

        /// <summary>
        /// Mirrors this frame's pose left ↔ right (C16g) and makes it the frame's pose (as <see cref="PastePose"/>).
        /// The muscles are mirrored the way Unity's clip Mirror option does it (<see cref="PoseMirror"/>), and the
        /// body is reflected across the centre plane of where the character stands while records are posed (the
        /// frame a pose key is stored in). That plane doesn't move with the pose (the output's own root can: where it
        /// follows the body, it would move with the mirrored body), so mirroring twice gives back the pose.
        /// </summary>
        public int MirrorPose()
        {
            GoToFrame(Frame);
            HumanPose shown = Preview.ReadPose();
            Transform root = Preview.Root.transform;
            Pose stand = StandingPose();
            float scale = Preview.HumanScale;
            Quaternion invStand = Quaternion.Inverse(stand.rotation), invRoot = Quaternion.Inverse(root.rotation);
            Vector3 body = root.position + root.rotation * (shown.bodyPosition * scale);
            Vector3 bodyInStand = PoseMirror.MirrorPosition(invStand * (body - stand.position));
            Quaternion turnInStand = PoseMirror.MirrorRotation(invStand * root.rotation * shown.bodyRotation);
            body = stand.position + stand.rotation * bodyInStand;
            var mirrored = new HumanPose
            {
                muscles = PoseMirror.MirrorMuscles(output.Played.muscles),
                bodyPosition = invRoot * (body - root.position) / scale,
                bodyRotation = EditRecord.Normalized(invRoot * stand.rotation * turnInStand),
            };
            Preview.ApplyPose(mirrored);
            Transform hips = Preview.Animator.GetBoneTransform(HumanBodyBones.Hips);
            Quaternion inv = Quaternion.Inverse(stand.rotation);
            var pose = new KeyPose
            {
                muscles = mirrored.muscles,
                hipsPosition = inv * (hips.position - stand.position),
                hipsRotation = EditRecord.Normalized(inv * hips.rotation),
            };
            output.Show(Frame);
            return PastePose(pose, "Mirror Pose");
        }

        /// <summary>Where the character stands on this frame while records are posed (the chain travel, see <see cref="Baker"/>).</summary>
        Pose StandingPose() =>
            chainTravel != null && chainTravel.Count > 0 ? chainTravel[Mathf.Clamp(Frame, 0, chainTravel.Count - 1)] : chain.Travel[Mathf.Clamp(Frame, 0, chain.Travel.Count - 1)];

        PoseKeyRecord NewPoseKey(KeyPose pose) => new PoseKeyRecord
        {
            frame = Frame, mask = PoseKeyRecord.AllParts, easing = PoseEasing.Smooth,
            easeIn = Mathf.Max(0, NewEditEaseFrames), easeOut = Mathf.Max(0, NewEditEaseFrames),
            pose = pose,
        };

        /// <summary>
        /// Whether a handle edit goes into a pose key, and which (-1: a new one on this frame): the selected record
        /// when it is an unmuted pose key on this frame; else, on a new clip, unless the selected record is an Offset
        /// or Pin that <paramref name="otherFits"/>, the pose key on this frame.
        /// </summary>
        bool PoseKeyFor(Func<EditRecord, bool> otherFits, out int index)
        {
            index = -1;
            int s = SelectedRecord;
            if (s >= 0 && Edits.Records[s] is PoseKeyRecord k && !k.muted && k.frame == Frame) { index = s; return true; }
            if (!IsNewClip || Matching(otherFits) >= 0) return false;
            index = PoseKeyAtFrame();
            return true;
        }

        /// <summary>
        /// A handle edit (or, with <paramref name="change"/> null, none) on the pose key <paramref name="index"/>
        /// (-1: a new key on this frame). The pose the key starts from is the layer's for this frame with the key in
        /// it (a new key: without it, so it starts as what is there); <paramref name="change"/> is made on that pose
        /// and the result is what the key holds. A key covering only some parts takes in the part the change moved.
        /// Then the frame is shown as the bake will write it.
        /// </summary>
        void EditPoseKey(int index, Action<FramePoser> change)
        {
            List<EditRecord> records = Edits.Records;
            PoseKeyRecord candidate = index >= 0 ? (PoseKeyRecord)records[index].Clone() : NewPoseKey(new KeyPose());
            if (pending != null && (pendingIndex != index || index < 0 && !SameTarget(pending, candidate)))
                throw new InvalidOperationException("Another edit is in progress; commit or cancel it first.");

            ReachShortfall = null;
            TurnShortfall = null;
            chain.Show(Frame);
            chain.ApplyPoseLayer(LayerWith(index, index >= 0 ? candidate : null).Evaluate(Frame));
            if (change != null)
            {
                change(chain);
                if (chain.LastStepPart is HumanBodyPart part) candidate.mask |= PoseKeyRecord.Bit(part);
                if (chain.LastStepHips) candidate.mask |= PoseKeyRecord.Bit(HumanBodyPart.Body);
            }
            candidate.pose = chain.CapturePose();

            chain.Show(Frame);
            chain.ApplyPoseLayer(LayerWith(index, candidate).Evaluate(Frame));
            foreach (EditRecord r in records)
                if (!r.muted && !(r is PoseKeyRecord)) RecordApplier.Apply(chain, Edits, r, Clip);

            pending = candidate;
            pendingIndex = index;
        }

        /// <summary>The committed pose keys with record <paramref name="index"/> replaced by (or, at -1, joined by) <paramref name="key"/>.</summary>
        PoseLayer LayerWith(int index, PoseKeyRecord key)
        {
            if (key == null) return layer;
            List<EditRecord> records = Edits.Records.ToList();
            if (index >= 0 && index < records.Count) records[index] = key;
            else records.Add(key);
            return PoseLayer.For(Edits, records, Clip);
        }

        /// <summary>
        /// Adds a pin (C13b) holding <paramref name="goal"/>'s hand or foot where the output shows it on this frame,
        /// relative to <paramref name="anchor"/> (the world, <paramref name="anchorBone"/>, or the prop
        /// <paramref name="propId"/>), covering this frame; its range and ease can then be changed. Commits and
        /// re-bakes; returns the new record's index (also selected).
        /// </summary>
        public int AddPin(AvatarIKGoal goal, PinAnchor anchor, HumanBodyBones anchorBone = HumanBodyBones.Hips, string propId = null)
        {
            if (!CanMove(goal)) throw new ArgumentException($"This character's {goal} limb isn't fully mapped.", nameof(goal));
            if (anchor == PinAnchor.Bone && BodyParts.Of(anchorBone) == LimbIK.Part(goal))
                throw new ArgumentException("A hand or foot can't be pinned to a bone of its own limb.", nameof(anchorBone));
            GoToFrame(Frame);
            var pin = new PinRecord
            {
                limb = goal, start = Frame, end = Frame, anchor = anchor, anchorBone = anchorBone, propId = propId ?? "",
                easeIn = NewEditEaseFrames, easeOut = NewEditEaseFrames,
            };
            if (!RecordApplier.AnchorPose(Preview, Edits, pin, out Vector3 ap, out Quaternion ar, out string problem))
                throw new InvalidOperationException(problem);
            Transform end = Preview.Animator.GetBoneTransform(LimbIK.EndBone(goal));
            Quaternion inv = Quaternion.Inverse(ar);
            pin.position = inv * (end.position - ap);
            pin.rotation = inv * end.rotation;

            int index = -1;
            ChangeRecords("Add Pin", list => { list.Add(pin); index = list.Count - 1; });
            SelectedRecord = index;
            return index;
        }

        /// <summary>Stores the edit in progress in the edits asset and re-bakes the output (one Undo step).</summary>
        public void Commit()
        {
            if (pending == null) return;
            EditRecord record = pending;
            int index = pendingIndex;
            ClearPending();
            ChangeRecords(record is PinRecord ? "Move Pin" : record is PoseKeyRecord ? "Edit Pose Key" : "Edit Humanoid Clip", list =>
            {
                if (index >= 0 && index < list.Count) list[index] = record;
                else { list.Add(record); index = list.Count - 1; }
            });
            SelectedRecord = index;
        }

        /// <summary>Drops the edit in progress and shows the frame as the output plays it.</summary>
        public void Cancel()
        {
            ClearPending();
            output.Show(Frame);
        }

        /// <summary>
        /// Changes the record list (add, remove, reorder, mute, edit a record's fields) as one Undo step together with
        /// the re-bake it needs. An edit in progress is dropped first.
        /// </summary>
        public void ChangeRecords(string undoName, Action<List<EditRecord>> change)
        {
            ClearPending();
            Undo.RecordObject(Edits, undoName);
            change(Edits.Records);
            EditorUtility.SetDirty(Edits);
            Undo.FlushUndoRecordObjects();
            Rebake();
        }

        /// <summary>
        /// Changes the reference props (C14) as one Undo step; re-bakes when a pin holds on to a prop (moving a prop
        /// moves what is pinned to it). An edit in progress is dropped first.
        /// </summary>
        public void ChangeProps(string undoName, Action<List<ReferenceProp>> change)
        {
            ClearPending();
            Undo.RecordObject(Edits, undoName);
            change(Edits.Props);
            EditorUtility.SetDirty(Edits);
            Undo.FlushUndoRecordObjects();
            if (Edits.Records.Any(r => r is PinRecord p && !p.muted && p.anchor == PinAnchor.Prop)) Rebake();
            else Reload(chainTravel);
        }

        public void Dispose()
        {
            output?.Dispose();
            chain?.Dispose();
            output = chain = null;
            baseClip?.Dispose();
            baseClip = null;
            Props.Dispose();
        }

        /// <summary>The selected record when it is unmuted and <paramref name="fits"/>; else -1 (a new record).</summary>
        int Matching(Func<EditRecord, bool> fits)
        {
            int s = SelectedRecord;
            return s >= 0 && !Edits.Records[s].muted && fits(Edits.Records[s]) ? s : -1;
        }

        /// <summary>
        /// Shows the frame with the edit in progress: the source frame, the records before the one being edited,
        /// then that record as <paramref name="set"/> makes it from the pose they leave, then the records after it.
        /// </summary>
        void Edit(int index, Func<EditRecord> create, Action<EditRecord, FramePoser> set)
        {
            List<EditRecord> records = Edits.Records;
            EditRecord candidate;
            if (index >= 0) candidate = records[index].Clone();
            else
            {
                candidate = create();
                candidate.easeIn = candidate.easeOut = Mathf.Max(0, NewEditEaseFrames);
            }
            if (pending != null && (pendingIndex != index || index < 0 && !SameTarget(pending, candidate)))
                throw new InvalidOperationException("Another edit is in progress; commit or cancel it first.");

            chain.Show(Frame);
            chain.ApplyPoseLayer(layer.Evaluate(Frame));
            int at = index >= 0 ? index : records.Count;
            for (int i = 0; i < at; i++)
                if (!records[i].muted) RecordApplier.Apply(chain, Edits, records[i], Clip);
            set(candidate, chain);
            RecordApplier.Outcome outcome = RecordApplier.Apply(chain, Edits, candidate, Clip);
            ReachShortfall = outcome.ReachShortfall;
            TurnShortfall = outcome.TurnShortfall;
            if (index >= 0)
                for (int i = index + 1; i < records.Count; i++)
                    if (!records[i].muted) RecordApplier.Apply(chain, Edits, records[i], Clip);

            pending = candidate;
            pendingIndex = index;
        }

        static bool SameTarget(EditRecord a, EditRecord b) =>
            a is OffsetRecord x && b is OffsetRecord y && x.target == y.target &&
            (x.target == OffsetTarget.Limb ? x.limb == y.limb : x.bone == y.bone) ||
            a is PoseKeyRecord pa && b is PoseKeyRecord pb && pa.frame == pb.frame;

        void ClearPending()
        {
            pending = null;
            pendingIndex = -1;
            ReachShortfall = null;
            TurnShortfall = null;
        }
    }
}
