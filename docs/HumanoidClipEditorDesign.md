# Humanoid Clip Editor — Design

A portable Editor tool for fixing **Humanoid** animation clips inside Unity: duplicate a clip, then adjust the copy by dragging hands, feet, elbows, knees and hips in the Scene view (in-Editor inverse kinematics) or by rotating individual bones, and write the result back into the clip's humanoid curves. It also creates **new** humanoid clips the same way, by posing the character at key frames (C16).

**Status:** Done (2026-11-20): all six phases are built, pass all their tests (75) and were tried by hand. Phase 5 (new clips: start pose, pose keys with masks and easing, copy / paste / mirror pose, loop; pose keys on fixed clips) was tried on PlayerRigEmerald; what that found (a prefab whose Animator is on a child object, a script that handles root motion, loading pose keys) is in §6. Phase 6 checked it on this project's real assets and, copied into an empty project on Unity 2021.3.26f1, it compiles cleanly, passes its tests and works by hand (§7, *Phase 6 checks*). All decisions (C1–C18) are settled. No questions are open.

---

## 1. Why Unity can't do this today

- A Humanoid clip doesn't store bone transforms. It stores **muscle curves** (about 95 values such as `Left Arm Down-Up`, `Spine Front-Back`, `LeftHand.Index.1 Stretched`), the body's position and rotation (`RootT` / `RootQ`), and **IK goal curves** for hands and feet (`LeftHandT` / `LeftHandQ`, `LeftFootT` / `LeftFootQ`, …). At runtime the Avatar turns muscles into bone rotations for whichever character plays the clip ("retargeting").
- Clips inside an FBX are read-only. Duplicating one (Ctrl+D) gives an editable `.anim`, but the Animation window only lets you key raw muscle numbers; dragging a bone in the Scene view while recording doesn't produce muscle keys.
- The Animation Rigging package can bake constraints into clips, but its bake commands refuse humanoid characters (`BakeUtils` returns false when `animator.isHuman`), and its constraints only solve while the rig is evaluated.

The missing piece is a converter between **bone pose ↔ muscle values**. Unity provides it as a public API: `HumanPoseHandler` turns a character's current bone pose into a `HumanPose` (body position/rotation + muscle array) and back, and it works in Edit mode. The tool is built around that.

---

## 2. How it works (core loop)

1. **Sample**: for the current frame, read the clip's muscle and root curves into a `HumanPose` and apply it to a preview character (`HumanPoseHandler.SetHumanPose`), so the bones show the clip's pose for that character.
2. **Edit**: the user drags handles in the Scene view. Hand/foot handles run Unity's own humanoid IK solver (C4) with the elbow/knee handle as its hint; bone-rotation handles rotate one bone directly.
3. **Read back**: `HumanPoseHandler.GetHumanPose` turns the edited bone pose into muscle values.
4. **Store and bake**: the edit is stored as a record in the clip's edits asset (C13), and the output clip is rebuilt from the untouched source with every record applied: muscle curves (and the root curves when the body moved) get the new values, and the **IK goal curves are recomputed** from the new pose (C7) so runtime Foot IK / IK Pass doesn't pull the limbs back to the old positions.

---

## 3. Decisions

| # | Decision |
|---|---|
| C1 | **Settled (user, 2026-11-20).** **Portable, Editor-only**, same rules as the Texture Tools (TextureToolsDesign D15): one folder `Assets/_MyFiles/Tools/HumanoidClipEditor/` with its own assemblies `Midian.HumanoidClipEditor.Editor` / `.Tests`, references only `UnityEngine` and `UnityEditor` (no Animation Rigging, no Mathematics), no hard-coded paths, test assembly behind `UNITY_INCLUDE_TESTS`, Unity 2021.3+ API only, own `README.md`. Nothing ships in a build. |
| C2 | **Settled (user, 2026-11-20).** **Humanoid clips only.** Generic clips already store bone transforms and are editable in the Animation window; they are not part of this tool. |
| C3 | **Settled (user, 2026-11-20).** **The source clip is never modified.** The tool works on a copy. For a read-only FBX clip, **Duplicate as editable** makes `<name>_Edit.anim` next to the FBX (or in a chosen folder), copying curves, events and clip settings (loop, root-motion bake options, mirror). |
| C4 | **Settled (user, 2026-11-06): Unity's own humanoid IK solver.** Hand and foot handles drive the solver the Animator runs in game (IK Pass / Foot IK), reached through an animation job on the preview character like the rest of the tool (`HumanStreamProbe`): the frame's pose goes into the stream, the dragged limb's goal position and rotation are set (weights 1) along with its elbow/knee hint, `AnimationHumanStream.SolveIK` runs, and the solved muscles come back out. Unity solves in muscle space, so results always respect the Avatar's muscle limits (C8 reports how far short a target fell). The elbow/knee handle sets the hint. Not Animation Rigging (C1, and Rigging can't write humanoid curves anyway). |
| C5 | **Settled (user, 2026-11-20).** **FK editing too**: any humanoid bone (spine, chest, neck, head, shoulders, fingers, toes) can be rotated with a rotation handle; the result goes through the same `GetHumanPose` read-back. Fingers are covered by this, since retargeting often mangles them. |
| C6 | **Settled (user, 2026-11-20).** **Edits are made on the character the clip will play on.** Muscles retarget, so the same muscle values put the hands in different places on differently-proportioned characters. The window says which character the fix was made for. |
| C7 | **Settled (user, 2026-11-20).** **IK goal curves are recomputed for every frame the tool writes**, by Unity's own goal-from-pose evaluation of that frame (`GoalCurves.FromClipFrame`, an animation job on the preview character). The frame is evaluated from a clip, not from a `HumanPose`: a HumanPose has no Translation DoF, and on an Avatar with Translation DoF the clip's `*TDOF` curves move the leg bones, which put foot goals about 1 cm off when left out (measured on the zombie set). The clip must play its curves as stored (`RawClipCopy`: Loop Pose and Mirror off), since goal curves hold the pose before those are applied. Convention, measured on real clips: **rotation** `Q = inverse(bodyLocalRotation) * goalLocalRotation`; **position** `T = inverse(bodyRotation) * (goalFromPose + rootRotation * goalLocalRotation * (footHeight, 0, 0) - bodyPosition) / humanScale`. Foot goals sit one foot height (the stream's `leftFootHeight` / `rightFootHeight`) below the ankle along the goal's x axis; hands have no offset. `GoalConventionTests` checks this reproduces the goal curves stored in imported clips (within 0.005 normalized units and 0.5°). |
| C8 | **Settled (user, 2026-11-20).** **Muscle limits are respected and reported.** A humanoid pose can't bend a joint past the Avatar's muscle range, so an IK target can be out of reach after read-back. The tool re-applies the read-back pose, measures how far the hand/foot ended up from its target, and shows that distance on the handle (not silently clamped). |
| C9 | **Settled (user, 2026-11-20).** **Muscle-curve naming map**: clip curve names differ from `HumanTrait.MuscleName` for fingers (curve `LeftHand.Index.1 Stretched` vs trait `Left Index 1 Stretched`); Unity's `MuscleHandle.name` uses the curve spelling. The mapping lives in `HumanoidCurveNames`; tests check it against Unity's muscle handles and the bindings of real imported clips. Other Animator-bound curves in a clip (`MotionT`/`MotionQ`, `*TDOF`, Animator parameter curves) are left untouched. |
| C10 | **Settled (user, 2026-11-20).** **Undo** works for every write (Unity's Undo on the clip asset), and the preview character never dirties the open scene (it's a tool-owned instance with `HideFlags.DontSave`, removed when the window closes or on domain reload). |
| C11 | **Settled (user, 2026-11-20).** **Window**: **Tools → Animation → Humanoid Clip Editor** (IMGUI, like the other tools): edits asset field, or a clip to start one for (C13e) + New clip (C16); source and output shown read-only; character field; frame scrubber with play/step; modes Turn bones / Move hands & feet / Props; reach warning (C8) on the handle and per record after a bake; pin controls; the record list (C13d: mute, select, reorder, delete, name, ease, keys or pin range); the prop list (C14). |
| C12 | **Settled (user, 2026-11-20).** **Clip frame rate is kept**; written keys land on the clip's frame times. Keys outside the edited frames are left exactly as they were. |
| C13 | **Settled (user, 2026-11-06): non-destructive adjustment layer.** Fixes are stored records, not hand-placed keys. (a) Records live in an **edits asset** (`<clip>_Edits.asset`, `ClipEditsAsset`) that references the untouched **source clip** and the **output clip** (`<clip>_Edit.anim`), plus the preview character used (C6). (b) Record kinds (plus **Pose key**, C16c), each covering a run of full-strength frames with **ease in / ease out** (the number of frames it blends over before and after, smoothstep): **Offset** — moves a hand or foot (with an elbow/knee hint shift), turns one bone, or moves and turns the hips, by keyed amounts; between keys the amount follows clamped-auto curves (no overshoot), outside them it holds the nearest key. Movements are in the character's own space (they travel with root motion), turns in the bone's own space (they follow the bone). An offset's amount is relative to the pose the records above it leave. **Pin** — holds a hand or foot (position, and rotation unless turned off) at one spot across a range: a world spot (stops foot sliding), a spot relative to another bone (keeps a hand on the other hand for a two-handed weapon), or a spot on a reference prop (C14); during the ease it blends from where the records above leave it. (c) **Bake** (`Baker`): the output's curves are reset to the source's, then every frame an unmuted record touches is posed from the source frame with the records applied in list order and written to the output (C7, C12); frames no record touches are the source's exactly. The source is played with the output's clip settings (Loop Pose, root motion), which are what the game plays; the output's settings and events are its own and are kept. While a frame is posed, the source stands where the posed frame lands where the output will show it (the chain travel, C15): on frames no record touches that is the output's own root-motion path, but on a touched frame, where the root follows the body, the output's path already carries that frame's edit (posing on it would count a hips drop twice), and the source's own path misses a shift of the whole path from an edit near the first frame. So after writing, the bake compares the hips it posed on each touched frame with the hips the output shows there and moves that frame's travel by the difference; with world pins (the only records that depend on it) it poses and writes again on the corrected travel until the two agree within 0.5 mm. The session poses its live edits on the same chain travel, so a drag and its commit match. (d) Records can be edited, muted, reordered or deleted, and the output re-baked at any time; every change re-bakes, and the change plus its bake are one Undo step. Handle edits go into the selected record when it does the same thing (same bone or limb; a pin only on a frame inside its range), otherwise into a new record at the end. (e) Any humanoid clip with no edits asset can be adopted: the clip becomes the source and the tool makes the output copy (C3). (f) On a clip with Loop Time on, an ease running past either end continues from the other end, and the first and last frames get the same fix. |
| C14 | **Settled (user, 2026-11-06): reference props.** The preview character can hold any number of props: each is a model or prefab, a humanoid bone to attach to, and a position/rotation/scale offset in that bone's space, adjusted with Scene-view handles or fields. Props are preview-only (tool-owned `DontSave` instances parented to the bone, scripts off, C10) and never go into the clip. The prop list is saved in the edits asset (C13) so it comes back next session. A Pin record (C13b) can anchor to a point on a prop as well as to a bone (e.g. the left hand pinned to the sword's second grip); the prop's pose is worked out from its bone and offset, so a hidden prop still anchors its pins, and changing a prop re-bakes the pins on it. |
| C15 | **Settled (user, 2026-11-20).** **Moving the hips edits the body curves (`RootT`/`RootQ`) and leaves the clip's root-motion settings alone.** In a humanoid clip the character's travel (root motion) is worked out by Unity from the body curves according to the clip's *Root Transform* settings, so there is no separate travel curve to hold still: with *Bake Into Pose* on for an axis, moving the hips on that axis only moves the body; with it off, travel changes wherever the hips offset changes (the ease-in/out frames of a record). The window draws the travel path before and after the edit and warns when an edit changes it. The path isn't derived by the tool: it comes from playing the clip through Unity's own Animator with root motion on (`HumanStreamProbe.TravelPath`), so it is what the game does by construction. `TravelTests` checks it against the body curves (unbaked XZ travels as far as the body moves; baked XZ stays put). |
| C17 | **Settled (user, 2026-11-20).** **The bake rewrites only the muscles of the body parts an edit touches**; every other muscle is copied from the source pose. Reading a pose back (`GetHumanPose`) isn't lossless for muscle values: Unity re-splits twist between neighbouring bones (up to 3° on a leg twist, measured) and clamps values outside ±1 (imported clips can hold e.g. 3.93). Bone positions stay within 5 mm, but rewriting untouched limbs from a read-back would still nudge them, so they aren't rewritten. Body position/rotation (RootT/RootQ) are rewritten whenever the edit moved the body: the body is the centre of mass, so a limb edit shifts it slightly; goals of untouched limbs are re-expressed against the new body so they stay in place. The preview follows the same rule: while an edit is pending, every bone of an untouched part stays exactly as it was before the edit step (`FramePoser` restores them after snapping the edited bone to its muscle limits); without that, the zombie set's upper leg visibly turned 2.3° whenever an arm was edited. Every bone's local position is restored too, edited parts included: edits never change Translation DoF curves, and applying a read-back pose drops those offsets. |
| C18 | **Settled (user, 2026-11-20).** **The preview shows what the game plays; the curves are what get edited.** The preview frame is posed by evaluating the output clip through Unity (`HumanStreamProbe`, on `RawClipCopy.CreateForPreview`), so Loop Pose and Translation DoF look as they do in game, and the character stands where the clip's root motion puts it on that frame (`HumanStreamProbe.TravelPath`, starting from the origin on the first frame). Standing in place isn't the same: on a clip whose horizontal root motion isn't baked into the pose, Unity's root follows the body's centre of mass, so moving a limb moves the root while the hips stay put in the world; a preview pinned to the origin showed the whole body slide instead (8 mm on a lifted foot of the zombie set, measured in Phase 3). Pose maths stays relative to the root (`PreviewCharacter` reads and applies poses with the root briefly at the origin, because Unity documents GetHumanPose's body as world space and SetHumanPose's as root-relative). The one exception is **Mirror**: a mirrored clip is previewed unmirrored, showing the sides its curves are written for, so an edit to the left hand is an edit to the left-hand curves (the window says so). An edit's change is added to the curves (`ClipWriter`), so whatever playback adds on top (Loop Pose's correction, root-motion extraction) stays out of them. The change is taken between exact muscle values, never read-backs: "before" is what Unity's stream played, "after" is what was applied to the preview. A read-back of the bones re-splits twist and clamps at the limits, and with read-backs on both sides a fully stretched arm played back 1.5° off the preview and a zombie-set foot 1.9 cm off its target (measured in Phase 3). Loop Pose's correction depends only on the first and last frames (it grows linearly through the clip so the last frame meets the first), so an edit touching the first or last frame of a Loop Pose clip shifts every frame slightly; the window warns on those frames. |
| C16 | **Settled (user, 2026-11-06): new clips, built after the fixing workflow.** (a) **New clip** in the window asks for name and folder, length in frames, frame rate (30 by default) and Loop. (b) **Start pose**: the Avatar's neutral pose (all muscles 0) or any frame of any humanoid clip. The edits asset (C13) stores this start pose in place of a source clip; it is held for the whole length as the base, and the bake writes every frame. (c) **Pose key**, a third record kind (with Offset and Pin): the pose at one frame (muscles, body position and rotation), made with the same IK / FK handles (C4, C5). Each pose key has a **body-part mask** (Body, Head, Left/Right Arm, Left/Right Leg, Left/Right Fingers — the parts of Unity's Avatar Mask), so a key can pose only the arms and leave the rest to the base or other keys. (d) **Blending between pose keys** is per muscle, with an easing choice on each key: Smooth (default, auto tangents), Linear or Hold. Body position blends the same way; body rotation blends as a rotation (no component-wise flips). (e) Offset and Pin records apply on top of pose keys, in list order (e.g. pin the feet while posing the upper body). (f) Pose keys also work on fix-mode clips (C13): the source pose eases into the key pose and back over the key's ease frames. (g) **Copy / paste pose** between frames and between clips, and **Mirror pose** (left ↔ right); mirroring is checked by test against Unity's own clip *Mirror* option. (h) **Loop**: sets the clip's Loop Time and keeps the last frame's pose equal to the first. Travel in a new clip is keyed through the hips (C15); new clips start with Bake Into Pose off, so that travel becomes root motion. **How it is built:** (i) The start pose is stored in the edits asset (muscles, RootT, RootQ; a clip frame's body is moved over the origin, keeping its height and facing; the neutral pose is raised so the lower sole rests on y = 0); every bake and preview builds an in-memory clip holding it for the whole length and starts from that as a fixed clip starts from its source, so the bake, root-motion and writer code is shared. (ii) A pose key stores every muscle and the **hips' place** in the root's space (where the character stands while records are posed), not the centre of mass, so a key posing only some parts doesn't move the hips (the centre of mass moves whenever a limb does). The Body part of the mask includes the hips' place. (iii) Pose keys apply first as one layer (`PoseLayer`), per body part; Offsets and Pins apply on top in list order, so the order of pose keys in the list doesn't matter and the window lists them apart, by frame. (iv) On a new clip a part's keys always blend into each other and the first and last keys hold to the ends; on a fixed clip two neighbouring keys blend into each other only when their ease windows meet, otherwise the clip eases into and back out of each group of keys. (v) Each segment's tangents are set directly, not through Unity's tangent modes (setting those per side gave a Linear segment flat ends): Linear uses the segment's slope at both ends, Hold a step, Smooth a monotone cubic (PCHIP tangents: flat at the first and last key, never past a key). The hips blend as height, horizontal position, facing (an unwrapped angle about the vertical) and tilt (a rotation on one hemisphere). (vi) Loop: the last frame takes frame 0's muscles and hips height and tilt; the hips' horizontal position and facing are travel and carry on through the loop (a walk ends ahead of where it started; Unity's looping root motion carries it on), so a key on a looping clip's last frame sets only those. (vii) Handle edits go into the selected record when it does the same thing (a pose key on this frame, or as in C13d); otherwise, on a new clip, into the pose key on this frame (made if missing); otherwise into a new offset. A pose-key edit is made on the layer's pose for the frame (the Offsets and Pins on top don't take part), and a key covering only some parts takes in the part the edit moved. (viii) Copy pose copies the shown pose (muscles as played, hips relative to where the character stands while records are posed), to the system copy buffer so it works between clips and windows; Paste and Mirror put the pose into the frame's pose key (made if missing, then covering every part). Mirror reflects the body across the centre plane of that same standing place, which doesn't move with the pose, so mirroring twice gives the pose back. |

---

## 4. What the user does (target workflow)

1. Select a humanoid clip in the Project window → right-click → **Humanoid Clip Editor → Edit in Humanoid Clip Editor** (or open the window and pick it). The first time, this makes the output `<clip>_Edit.anim` and the edits asset `<clip>_Edits.asset`; the clip itself is never changed (C3, C13).
2. Pick the character model/prefab the clip will play on. A preview instance appears in the Scene view.
3. Scrub to the bad frame. Drag the left hand to where it should be; the elbow follows. Rotate a finger that's bent wrong. Each release stores a fix (with the chosen ease) and rebuilds the output. Pin a sliding foot to the world over the frames it should be planted.
4. Play the output back in the window and in the real game; adjust, mute or delete fixes as needed.

**New clip (C16):** Tools → Animation → Humanoid Clip Editor → **New clip** → length, frame rate, Loop, start pose, character → **Create new clip…** (name and folder) → scrub to a frame and pose the character with the handles (each release keys that frame's pose) → repeat at other frames → play it back; set each key's body parts and blend; add pins / offsets on top as needed. **Copy / Paste / Mirror pose** work on any frame of any clip.

---

## 5. Files

Built (Phase 1):

```
HumanoidClipEditor/
  Editor/
    Midian.HumanoidClipEditor.Editor.asmdef
    HumanoidCurveNames.cs    curve names: muscles (C9 map), RootT/RootQ, goal T/Q
    HumanoidClipCurves.cs    reads a clip's humanoid curves; samples a HumanPose and goal values at any time
    PreviewCharacter.cs      DontSave copy (created at the origin), scripts and events off, always animates; HumanPoseHandler, poses relative to the root (C10)
    HumanStreamProbe.cs      Unity's own evaluation via a hand-evaluated PlayableGraph + animation job: clip frame -> stream sample (optionally goals from the pose); travel path (C15)
    RawClipCopy.cs           in-memory uncompressed copies: as stored (Loop Pose and Mirror off) and for the preview (Mirror off only, C18)
    GoalCurves.cs            goal curve values from a clip frame (C7)
  Tests/
    Midian.HumanoidClipEditor.Tests.asmdef
    TestRigs.cs              picks a small sample of real imported clips + their characters (at most 8 files, one per top-level folder)
    TestUtil.cs              sample frame times, bone readers
    CurveNameTests.cs, PoseTests.cs, GoalConventionTests.cs, TravelTests.cs, PreviewCharacterTests.cs, RawClipCopyTests.cs
```

Built (Phase 2):

```
HumanoidClipEditor/
  Editor/
    BodyParts.cs             body part of each bone and muscle (C17; the Avatar Mask parts of C16c)
    CurveKeys.cs             sets one frame's value without changing any other frame (pins the frames either side)
    ClipDuplicator.cs        Duplicate as Editable (C3): every curve, events, settings; never overwrites; Assets context menu
    ClipWriter.cs            writes one edited frame: change of the edited parts' muscles, body, goals (C7, C12, C17, C18); Undo; batched curve writes
    FrameEditSession.cs      frame + preview + pending edit; commit / cancel; used by the window and the tests
    HumanoidClipEditorWindow.cs  C11: clip (+ Duplicate as Editable), character, frame scrubber / play, Esc to cancel; Turn bones mode (bone dots, rotation handle); Move hands & feet mode added in Phase 3
  Tests/
    TempAssets.cs            scratch folder under Assets for clips the tests create
    BodyPartsTests.cs, CurveKeysTests.cs, ClipDuplicatorTests.cs, FrameEditTests.cs
```

Built (Phase 3):

```
HumanoidClipEditor/
  Editor/
    AnimatorEvaluationScope.cs   Animator settings every hand-evaluated graph needs (bound, events off, Always Animate), put back afterwards
    LimbIK.cs                    Unity's humanoid IK solver for one limb through an animation job: goal + rotation + hint in, solved pose on the bones (C4)
    FrameEditSession.cs          + preview stands on the clip's root-motion path; MoveEffector (solves from the frame as played, so drags don't accumulate), DefaultHint (keeps the clip's bend), reach / turn shortfall (C8)
    HumanoidClipEditorWindow.cs  + Move hands & feet mode: hand / foot dots (W moves, E turns), elbow / knee dots move the bend hint, reach warning in the window and the Scene view
  Tests/
    LimbIKTests.cs
```

Built (Phase 4):

```
HumanoidClipEditor/
  README.md                    user guide
  Editor/
    EditRecords.cs             EditRecord (ease, loop wrap), OffsetRecord + OffsetKey, PinRecord (C13b)
    ClipEditsAsset.cs          the edits asset: source, output, character, records, props; Create / FindFor (C13a, C13e)
    ReferenceProps.cs          ReferenceProp (data, pose on its bone) and PreviewProps (shown DontSave copies) (C14)
    FramePoser.cs              one frame of a clip on the preview + edit steps (turn bone, move hips, IK move), each from the pose the last left; played / current poses for the writer (C17, C18)
    RecordApplier.cs           applies records to a frame (weights, anchors, shortfalls); touched frames
    Baker.cs                   reset output to source, pose and write every touched frame, measure the chain travel (repeat for world pins) (C13c)
    ClipWriter.cs              now writes many frames in one batch (WriteFrames); undo is the baker's
    LimbIK.cs                  now solves from a given pose (muscles and body fed into the stream), so records chain
    FrameEditSession.cs        now edits records: handle edit → candidate record previewed through the chain → Commit stores it and re-bakes
    HumanoidClipEditorWindow.cs  edits asset / start fixing, record list, pins, props mode and list
  Tests/
    TestEdits.cs               edits assets and sessions for tests; curve values per frame
    RecordTests.cs, EditsAssetTests.cs; FrameEditTests and LimbIKTests now go through records and the bake
```

Built (Phase 5):

```
HumanoidClipEditor/
  Editor/
    PoseKeys.cs                PoseEasing, KeyPose, PoseKeyRecord (C16c), StartPose (neutral / clip frame, base clip; C16b), PoseClipboard (C16g)
    PoseBlend.cs               PoseLayer: per-part tracks, groups, easing curves, eases, loop wrap with travel kept apart (C16c–d, f, h)
    PoseMirror.cs              mirror muscles and body the way Unity's Mirror option does (C16g)
    ClipEditsAsset.cs          now also new clips: start pose, length, frame rate; CreateNew; OpenBase (source or generated start-pose clip)
    FramePoser.cs              now ApplyPoseLayer (exact muscles, hips placed), CapturePose, last step's part
    RecordApplier.cs, Baker.cs now start from OpenBase and apply the pose layer before the other records
    FrameEditSession.cs        now routes handle edits into pose keys; AddPoseKey, CopyPose, PastePose, MirrorPose
    HumanoidClipEditorWindow.cs  New clip section; pose buttons; Pose keys list with frame, blend, eases, body parts
  Tests/
    PoseKeyTests.cs            new clip playback, easing, start poses, loop, masks and eases on a fixed clip, copy / paste, records on top
    MirrorTests.cs             mirror convention against Unity's Mirror option; Mirror pose twice gives the pose back
```

---

## 6. Risks and Phase 1 findings

- **IK goal convention (C7)** — measured in Phase 1; the convention is in C7.
- **Pose round-trip drift** — measured in Phase 1: muscle values don't round-trip exactly, bones stay within 5 mm; handled by C17.
- **Root-motion derivation (C15)** — not derived: the travel path is Unity's own playback (C15).
- **Imported clips may be compressed** — Unity plays an imported clip from its compressed data, which can differ slightly from the editor curves; how much wasn't measured on its own (the large difference first seen on the FluXY crouch idle was Loop Pose, below). The tool reads and writes editor curves and evaluates through `RawClipCopy`, which is uncompressed; `PoseTests` checks that a RawClipCopy plays exactly its curves.
- **Preview Animator settings** — the probe only works with the Animator set to Always Animate (other culling modes skip the humanoid pass when no renderer is visible, leaving the stream empty) and with animation events off (clip events call game scripts the preview doesn't have). `PreviewCharacter` sets both, and the probe throws if the stream comes back empty.
- **Clip settings that change playback** — two settings make Unity play something other than a clip's curves: **Loop Pose** adds a correction that grows linearly through the clip so the last frame meets the first (measured 0.123 muscle units per second on the FluXY crouch idle, up to 9° on a finger), and **Mirror** swaps sides. `RawClipCopy` turns both off when a clip must play exactly its curves (pose checks, goal computation); C18 covers what the preview shows.
- **Translation DoF** — Avatars with Translation DoF on (the zombie set) let the clip's `*TDOF` curves move leg, spine and shoulder bones. `HumanPose` has no slot for them, so anything computed from a HumanPose alone misses them; goals are computed from a clip frame instead (C7). The bake copies TDOF curves from the source unchanged (C17).
- **Project size** — this project holds about 4,100 humanoid model files; loading them all exhausted memory and crashed the Editor. Tests load a small sample (`TestRigs`) and unload it afterwards.
- **Mirror convention (C16g)** — which muscles swap sides and which change sign. `PoseMirror` swaps every "Left …" muscle with its "Right …" twin unchanged, negates the centre muscles named "Left-Right" (spine, chest, upper chest, neck, head, jaw) and keeps the other centre muscles; the body is reflected across the root's YZ plane. `MirrorTests` compares this with Unity's own evaluation of real clips played with *Mirror* on, and passes.
- **Animator not on the prefab's top object** — found on PlayerRigEmerald, whose Animator (and Avatar) is on a child object, `Player`. The preview used to add a second Animator to the copy's top object, but Unity's Animator doesn't animate transforms that belong to another Animator further down, so every evaluation through it (showing a frame, the IK solve) wrote nothing to the bones while `HumanPoseHandler` still wrote them directly: no clip pose was shown, a dragged foot didn't move (the shortfall line just grew) and the frame fell back to a stale pose. `PreviewCharacter` now uses the humanoid Animator wherever it is in the copy (one with the given Avatar, else the first with a valid humanoid Avatar) and makes its object the `Root`; the whole copy is the `Container`; other Animators in the copy are turned off; one is added to the top object only when the copy has none. Checked by `PreviewCharacterTests` and `PoseKeyTests` on a prefab built that way.
- **Scripts that handle root motion** — found on PlayerRigEmerald: `PlayerStateMachine`, on the Animator's object, has `OnAnimatorMove`, and a script with it takes root motion over from Unity even while disabled, so the preview's travel path came back all zeros. Moving the hips up, down, sideways or forward, or turning them about the vertical, was stored and written but never showed (that is root motion); tilting did. `HipsEditTests` pinned it by checking each stage: preview, stored key and body curves were right, playback wasn't. The preview copy now removes its scripts (in an order `RequireComponent` allows; any left are disabled), so root motion is Unity's own; `TravelTests` checks a character carrying such a script travels as far as without it.
- **No Unity API in serialized field initializers** — Unity runs a serialized class's field initializers while it loads an asset, where its own API may not be called. `KeyPose` sized its muscle array from `HumanTrait.MuscleCount` there, so loading any edits asset holding a pose key logged an exception (found when `EditsAssetTests` loaded every edits asset in the project); it now starts empty, which counts as no pose until a key is captured or pasted whole.
- **Keyframe reduction** — imported clips may have reduced keys; writing every frame in an edited range adds keys, which is expected (file grows), not a defect.
- **Chaining IK records (Phase 4)** — a record after another must solve from the pose the earlier one left, not from the clip frame. `LimbIK` feeds that pose into the stream (every muscle through its handle, and the body as `bodyLocalPosition` / `bodyLocalRotation`) before solving, keeping the clip's Translation DoF. The body read back from the bones isn't exactly the body the stream places the hips from (about 1 cm apart on the DarkSorcerer rig, which made hands and feet land short), so `LimbIK` doesn't rely on them matching: after each solve it reads the hips the stream produced and, when they differ from the ones the goal was carried by, solves again with the goal and hint carried along with those hips; the limb is then right once `FramePoser.Settle` puts the hips back. `RecordTests` (hips then foot pin) and the Phase 3 tests check it.

---

## 7. Build phases

| Phase | Content | Done when |
|---|---|---|
| 1 | `HumanoidCurveNames`, `HumanoidClipCurves`, `PreviewCharacter`, `HumanStreamProbe`, `RawClipCopy`, `GoalCurves`: read a frame into a `HumanPose`, apply it, read it back; name map (C9); goal convention (C7); travel path (C15). **Done: all 20 tests pass.** | All Phase 1 tests (§5) pass in the Test Runner on real imported clips of this project. |
| 2 | `BodyParts`, `CurveKeys`, `ClipDuplicator`, `ClipWriter`, `FrameEditSession`, window with bone-rotation handles. **Done: all tests pass; window checked by hand (bone dots enlarged 50% after that check).** | Rotating a bone at a frame writes curves that play back as edited in a real Animator (`FrameEditTests`, within 0.2°), with no other frame and no other body part's muscle curves changed; Undo restores the clip. |
| 3 | `LimbIK` (Unity's solver, C4) + hand/foot and elbow/knee handles, reach display (C8). **Done: all tests pass; tried by hand.** | Moving a hand or foot to a target writes a frame that plays back with it at the target within 5 mm (`LimbIKTests`, every sample character), or, out of reach, reports the shortfall and writes what the preview shows; the elbow / knee hint changes the bend; the default hint keeps the clip's bend; nothing outside the limb changes. |
| 4 | Adjustment layer (C13): edits asset, Offset and Pin records, bake, record list in the window; reference props and prop-anchored pins (C14); README. **Done: all 59 tests pass and the window was tried by hand (2026-11-06).** | Re-baking after editing, muting or deleting a record reproduces the expected output; untouched frames match the source exactly (`RecordTests`, `EditsAssetTests`); the Phase 2–3 checks still pass through records. |
| 5 | New clips (C16): New clip dialog, start pose, Pose key records with masks and easing, copy/paste/mirror pose, Loop; pose keys in fix mode. **Done: all 75 tests pass and the window was tried by hand on PlayerRigEmerald (2026-11-20).** | A new clip made from pose keys plays back in a real Animator matching the keyed poses at key frames; mirror test passes; a looping clip's last frame matches its first. |
| 6 | Real-asset check here, then copy the folder into an empty project and check it there (portability). **Done (2026-11-20): every check below passes.** | The checks in *Phase 6 checks* below all pass. |

**Phase 6 checks**

Without the Editor:

- Every file and folder has its `.meta`; the editor assembly (`Midian.HumanoidClipEditor.Editor`) references nothing; the test assembly references only the Test Framework and compiles only when it is present (`UNITY_INCLUDE_TESTS`). No path, type or asset of this project is referenced (the only `Assets/` strings are Unity's menu paths and the rule that new clips go under Assets).
- The tool and its tests compile with no errors or warnings against the Unity assemblies of 2021.3.26f1 (the oldest version the README claims) and 6000.6.6f1 (the newest installed; no obsolete API).

In this project (real assets):

1. Fix a clip the game uses on PlayerRigEmerald (hands off a weapon, a sliding foot or the like) and point its Animator state at the `_Edit` output; in Play mode the fix shows.
2. Put a new clip made with the tool in an Animator state; in Play mode it plays as previewed (root motion as the player's own `OnAnimatorMove` applies it, where that state uses root motion).

In an empty project (portability), on Unity 2021.3.26f1:

3. New project from the 3D (Built-in) template. Copy `Assets/_MyFiles/Tools/HumanoidClipEditor` with its `.meta` files anywhere under the new project's `Assets`. It compiles with no Console errors or warnings.
4. Copy the folder `Assets/DarkSorcerer_AnimSet/Animations/Humanoid` with its `.meta` files (about 310 MB; its clips take their Avatar from `00_T-Pose.FBX` in it) so the tests have humanoids to run on (without any, they are all ignored). Window → General → Test Runner → EditMode → Run All: no test fails. A test may be ignored only for want of a kind of sample clip, and its message says so (a clip that travels, one on its model's own Avatar, one long enough or moving a hand far enough); any other ignore is a problem.
5. Tools → Animation → Humanoid Clip Editor: fix a frame of `attack01` on `00_T-Pose`, make a new clip from the neutral pose with two pose keys; both play back as previewed.


---

## 8. Open questions (asked one at a time)

1. ~~Edit model~~ — settled as C13.
2. ~~Reference props~~ — settled as C14.
3. ~~Moving the hips and root motion~~ — not a free choice (travel is derived from the body curves); resolved as C15.
4. ~~Creating new clips~~ — settled as C16.
5. ~~IK solver~~ — settled as C4 (Unity's own solver).
