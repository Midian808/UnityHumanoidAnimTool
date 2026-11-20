# UnityHumanoidAnimTool

Portable Unity Editor tool for fixing Humanoid animation clips and making new ones. The design and decisions are in [docs/HumanoidClipEditorDesign.md](docs/HumanoidClipEditorDesign.md).

An Editor-only tool for fixing **Humanoid** animation clips inside Unity, and for making new ones. Move hands and feet with Unity's own humanoid IK and the arm or leg follows. You can also turn single bones (fingers included), move the hips, or pin a hand or foot to the world, to another bone, or to a prop. Use it where retargeting has made a mess, such as hands that miss a weapon, feet that slide, or twisted fingers, or to pose a new clip key by key.

Copy the `HumanoidClipEditor` folder (with its `.meta` files) anywhere under a project's `Assets` folder; it needs nothing else. It requires Unity 2021.3 or newer (it compiles against 2021.3.26 and 6000.6), works in any render pipeline (it draws nothing of its own but Scene-view handles) and adds nothing to builds.

## How fixes are stored

The clip you fix is **never changed**; it may even be a read-only clip inside a model file. Starting to fix a clip makes two files next to it:

| File | What it is |
|---|---|
| `<clip>_Edit.anim` | **The output.** Use this clip in your Animator Controller. The tool rebuilds it from the original clip and your fixes after every change. |
| `<clip>_Edits.asset` | **Your fixes**: the list of fixes, the character they were made for, and the reference props. |

Because the output is rebuilt, a fix can be changed, muted, reordered or deleted at any time, and deleting every fix gives back the original clip. Curves changed in the output by other means (e.g. in the Animation window) are replaced on the next rebuild. The output's own clip settings (Loop Time, Loop Pose, root motion options) and animation events are kept, and they are what the preview plays.

## Getting started

1. Right-click a Humanoid clip in the Project window → **Humanoid Clip Editor → Edit in Humanoid Clip Editor**. (To make a clip from scratch instead, see [New clips](#new-clips).) Alternatively, open **Tools → Animation → Humanoid Clip Editor**, pick the clip, then pick a character and click **Start fixing this clip**. Opening a clip that already has fixes (or its `_Edit` output) opens those fixes.
2. Pick the **character** the clip will play on: a model, or a prefab holding one (its Animator may be on a child object). Give an **Avatar** only if its Animator has none. Fixes are made for that character's proportions: every humanoid shares the clip's muscle values, but the same values put hands and feet in different places on differently proportioned characters.
3. The character appears at the scene origin as a preview copy, which is never saved with the scene. Scrub to a frame. The character stands where the clip's root motion puts it.

## Making fixes

| Mode | Handles in the Scene view |
|---|---|
| **Turn bones** | Click a bone's dot, then turn it with the rotation handle. Select the **hips** to carry the whole body: **W** moves them, **E** turns them. |
| **Move hands & feet** | Blue dots are hands and feet: **W** moves, **E** turns, and the arm or leg follows (Unity's humanoid IK). Orange dots are elbows and knees: drag one to choose which way the joint bends. |
| **Props** | Click a prop's dot, then **W** moves or **E** turns it on its bone. |

Letting go of a handle stores the change and rebuilds the output; **Esc** cancels a drag, and **Undo** works for everything. Each change goes **into the selected fix** when that fix does the same thing (the same bone or hand / foot), adding or changing its key on this frame. Otherwise it becomes a **new fix**. Use **New fix from the next handle** to deselect.

Joints stay within the Avatar's muscle limits. When a hand or foot can't reach the handle, a red line shows how far short it stops. A fix that falls short on some of its frames gets a warning in the fix list.

### Kinds of fix

- **Offset**: moves a hand or foot, turns a bone, or moves and turns the hips by a keyed amount. Drag on several frames to key it on each; between keys the amount changes smoothly. Movements are in the character's own space and turns are in the bone's own space, so the fix follows the motion.
- **Pose key**: the whole pose on one frame (see [Pose keys](#pose-keys)).
- **Pin**: holds a hand or foot on one spot across a range of frames. Under **Move hands & feet**, pick what to hold it to and click **Pin … here on this frame**:
  - **World**: a fixed spot the character moves through (stops foot sliding).
  - **Bone**: moves with another bone (keeps a hand on the other hand).
  - **Prop**: a spot on a reference prop (the second grip of a two-handed sword).

  Then set its **First / Last frame**. To move the pinned spot, drag the hand or foot on a frame inside the range.

Every fix has **Ease in / Ease out**: the number of frames over which it blends in before its first frame and out after its last. New fixes get the **Ease for new fixes** value. On a clip with **Loop Time** on, an ease that runs past either end continues from the other end.

Fixes apply **top to bottom**, each to the pose the ones above it leave. For example, move the hips down first, then pin the feet below it so they stay put. Pose keys always come first, underneath every offset and pin.

## Pose keys

A pose key holds the whole pose on one frame: every muscle and where the hips are. Under **Pose on this frame**:

| Button | What it does |
|---|---|
| **Add pose key** | Keys the pose this frame has now. Handle edits on this frame then go into the key while it is selected. |
| **Copy pose** | Copies the pose shown, to paste on another frame or into another clip (it stays copied after a script reload). |
| **Paste pose** | Makes this frame's pose the copied one. |
| **Mirror pose** | Swaps the pose left ↔ right, the way Unity's clip **Mirror** option does. |

Paste and Mirror put the pose into the frame's pose key, making one if there is none.

Pose keys are listed apart from the other fixes, by frame. Each one has:

- **Body parts it poses**: Body (spine and the hips' place), Head, Left / Right Arm, Left / Right Leg, Left / Right Fingers, the parts of Unity's Avatar Mask. A key that poses only the arms leaves everything else, the hips included, to the clip and the other keys. Editing a part the key doesn't pose adds that part.
- **Blend to the next key**: **Smooth** (a smooth curve through the keys that doesn't overshoot them), **Linear**, or **Hold** (keeps this pose until the next key).
- **Ease in / Ease out** (on a clip you fix): the clip blends into the key's pose before it and back out after it. Two keys whose eases meet blend into each other instead.

Pose keys apply first; offsets and pins apply on top of them in list order, so you can pin the feet while posing the upper body.

## New clips

Open **New clip** at the top of the window. Set the length in frames (the clip runs from frame 0 to that frame), the frame rate, **Loop**, the start pose and the character. Click **Create new clip…** and choose a name and folder. That makes `<name>.anim` (the clip) and `<name>_Edits.asset` (its pose keys and fixes).

- **Start pose**: **Neutral** (every muscle 0, standing on the ground) or **a frame of a clip** (its pose moved over the origin). It holds for the whole clip under the pose keys.
- **Posing**: scrub to a frame and pose the character with the handles. Each release keys that frame's pose. Keys blend into each other, and the first and last keys hold to the clip's ends. Offsets and pins work as on a clip you fix: select one to change it with the handles.
- **Travel**: move the hips to make the character travel. A new clip starts with Bake Into Pose off, so that travel becomes root motion.
- **Loop**: turns on Loop Time and makes the last frame play as frame 0, so pose frame 0, not the last frame. The hips' forward travel and facing carry on through the loop instead (a walk ends ahead of where it started), so a pose key on the last frame sets only how far the loop travels.

## Reference props

Under **Reference props**, pick a model or prefab and a bone, then click **Add prop**. Then place it with its handles or its fields. Props are only shown in the preview and never go into the clip. They are saved with your fixes, so they come back next time. Moving a prop moves any hand pinned to it.

## Good to know

- **Mirror**: a clip with Mirror on is shown unmirrored, the sides its curves are written for. A fix to the left hand is a fix to the left-hand curves.
- **Loop Pose**: Unity works out the loop correction from the first and last frames, so a fix on either of those frames shifts every frame slightly. The window warns you on those frames.
- **Root motion**: the preview plays root motion the way Unity does (scripts on the character, such as a player controller that handles root motion itself, are removed from the preview copy). Where Bake Into Pose is off, the root follows the hips, so moving the hips sideways or forward, or turning them about the vertical, shows only as a change from frame to frame: on frame 0, or with a single pose key that gives every frame the same hips, it doesn't show. Raising, lowering and tilting the hips always show.
- **IK goals**: the output's hand and foot IK goal curves are recomputed for every fixed frame, so Foot IK and IK Pass in your Animator follow the fixed pose.
- **Re-bake**: if the original clip is re-imported or changed, click **Re-bake** to rebuild the output from it.

## Tests

EditMode tests live in `Tests/` (assembly `Midian.HumanoidClipEditor.Tests`, compiled only with the Test Framework present). They run on a small sample of the project's own imported humanoid clips and are ignored in a project that has none. Run them from **Window → General → Test Runner**.
