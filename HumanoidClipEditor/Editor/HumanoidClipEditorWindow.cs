using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Midian.HumanoidClipEditor
{
    /// <summary>
    /// Tools → Animation → Humanoid Clip Editor (C11). Fixes a Humanoid clip through an adjustment layer (C13): the
    /// source clip is never changed; fixes are records in an edits asset, and the output clip is rebuilt from the
    /// source and the records after every change. Scrub to a frame, then turn bones (C5), move the hips, move hands
    /// and feet with Unity's IK (C4), or pin a hand or foot to the world, a bone or a reference prop (C14). Letting go
    /// of a handle stores the change as a record (or updates the selected one) and re-bakes. Undo works.
    /// New clips (C16) start from a start pose and are posed with pose keys, made with the same handles; pose keys
    /// can also be added to a fixed clip, and poses copied, pasted and mirrored.
    /// </summary>
    public sealed class HumanoidClipEditorWindow : EditorWindow
    {
        enum EditMode { TurnBones, MoveHandsAndFeet, Props }
        enum LimbTarget { HandOrFoot, ElbowOrKnee }
        enum StartPoseKind { Neutral, ClipFrame }

        static readonly GUIContent[] ModeLabels =
            { new GUIContent("Turn bones"), new GUIContent("Move hands & feet"), new GUIContent("Props") };
        static readonly AvatarIKGoal[] Limbs =
            { AvatarIKGoal.LeftHand, AvatarIKGoal.RightHand, AvatarIKGoal.LeftFoot, AvatarIKGoal.RightFoot };
        static readonly string[] LimbLabels = { "Left hand", "Right hand", "Left foot", "Right foot" };

        /// <summary>A hand or foot that ends up further than this from its handle gets the reach warning (C8).</summary>
        const float ReachWarning = 0.005f;

        [SerializeField] ClipEditsAsset edits;
        [SerializeField] AnimationClip pickedClip;
        [SerializeField] GameObject newCharacter;
        [SerializeField] int frame;
        [SerializeField] EditMode mode;
        [SerializeField] HumanBodyBones selectedBone = HumanBodyBones.LeftLowerArm;
        [SerializeField] AvatarIKGoal selectedLimb = AvatarIKGoal.LeftHand;
        [SerializeField] LimbTarget limbTarget;
        [SerializeField] int newEase = 5;
        [SerializeField] int selectedRecord = -1;
        [SerializeField] int selectedProp = -1;
        [SerializeField] PinAnchor pinAnchor;
        [SerializeField] HumanBodyBones pinBone = HumanBodyBones.RightHand;
        [SerializeField] int pinProp;
        [SerializeField] GameObject newPropModel;
        [SerializeField] HumanBodyBones newPropBone = HumanBodyBones.RightHand;
        [SerializeField] bool showNewClip;
        [SerializeField] int newClipFrames = 30;
        [SerializeField] float newClipRate = 30f;
        [SerializeField] bool newClipLoop;
        [SerializeField] StartPoseKind newClipStart;
        [SerializeField] AnimationClip newClipFrom;
        [SerializeField] int newClipFromFrame;
        [SerializeField] GameObject newClipCharacter;
        [SerializeField] Avatar newClipAvatar;

        PreviewCharacter preview;
        FrameEditSession session;
        string sessionError;
        HumanBodyBones[] mappedBones = Array.Empty<HumanBodyBones>();
        Vector2 scroll;

        bool playing;
        double playStart;
        int playStartFrame;

        bool wasDragging;
        Quaternion globalHandleRotation = Quaternion.identity;

        // IK handles: where the hand / foot is being asked to go, and the elbow / knee hint while it's being dragged.
        Vector3 effectorPosition;
        Quaternion effectorRotation = Quaternion.identity;
        Vector3 activeHint;
        bool hintMoved;

        // Hips move handle and prop handles hold what the user asked for during a drag.
        Vector3 hipsPosition;
        Quaternion hipsRotation = Quaternion.identity;
        bool propDragging;
        Vector3 propPosition;
        Quaternion propRotation = Quaternion.identity;

        [MenuItem("Tools/Animation/Humanoid Clip Editor")]
        static void Open() => GetWindow<HumanoidClipEditorWindow>("Humanoid Clip Editor");

        [MenuItem("Assets/Humanoid Clip Editor/Edit in Humanoid Clip Editor", true)]
        static bool CanEditSelection() =>
            Selection.activeObject is AnimationClip c && c.humanMotion || Selection.activeObject is ClipEditsAsset;

        [MenuItem("Assets/Humanoid Clip Editor/Edit in Humanoid Clip Editor")]
        static void EditSelection()
        {
            ClipEditsAsset e = Selection.activeObject as ClipEditsAsset;
            if (!e && Selection.activeObject is AnimationClip clip)
            {
                e = ClipEditsAsset.FindFor(clip);
                if (!e)
                {
                    e = ClipEditsAsset.Create(clip);
                    Debug.Log($"Humanoid Clip Editor: started fixing '{clip.name}': output {AssetDatabase.GetAssetPath(e.Output)}, " +
                              $"fixes {AssetDatabase.GetAssetPath(e)}", e);
                }
            }
            var window = GetWindow<HumanoidClipEditorWindow>("Humanoid Clip Editor");
            window.edits = e;
            window.frame = 0;
            window.selectedRecord = -1;
            window.selectedProp = -1;
            window.Rebuild();
        }

        void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update += OnUpdate;
            Rebuild();
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.update -= OnUpdate;
            Teardown();
        }

        void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode) Teardown();
            else if (change == PlayModeStateChange.EnteredEditMode) Rebuild();
        }

        void OnFocus()
        {
            // The assets may have been changed elsewhere while this window wasn't focused.
            if (session != null && edits && edits.Problem() == null) { session.Reload(); ResetHandles(); }
        }

        void OnUndoRedo()
        {
            if (session == null) { if (edits) Rebuild(); Repaint(); return; }
            if (!edits || edits.Problem() != null || edits.Character != session.Edits.Character || !preview?.Root)
            {
                Rebuild();
            }
            else
            {
                session.Reload();
                ResetHandles();
            }
            Repaint();
            SceneView.RepaintAll();
        }

        void Teardown()
        {
            playing = false;
            session?.Dispose();
            session = null;
            preview?.Dispose();
            preview = null;
            mappedBones = Array.Empty<HumanBodyBones>();
            ResetHandles();
        }

        /// <summary>(Re)creates the preview and session for the current edits asset and its character.</summary>
        void Rebuild()
        {
            Teardown();
            sessionError = null;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!edits || edits.Problem() != null || !edits.Character) return;
            try
            {
                preview = PreviewCharacter.Create(edits.Character, edits.Avatar);
                session = new FrameEditSession(edits, preview) { NewEditEaseFrames = newEase, SelectedRecord = selectedRecord };
                session.GoToFrame(frame);
                mappedBones = Enumerable.Range(0, (int)HumanBodyBones.LastBone).Cast<HumanBodyBones>()
                    .Where(b => preview.Animator.GetBoneTransform(b)).ToArray();
                if (!mappedBones.Contains(selectedBone) && mappedBones.Length > 0) selectedBone = mappedBones[0];
            }
            catch (Exception e)
            {
                sessionError = e.Message;
                Teardown();
            }
            SceneView.RepaintAll();
        }

        // ---------------------------------------------------------------- window

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            try { DrawWindow(); }
            finally { EditorGUILayout.EndScrollView(); }
        }

        void DrawWindow()
        {
            EditorGUILayout.HelpBox(
                "Fix a Humanoid clip on the character it plays on, or make a new one. A clip you fix is never changed: " +
                "your fixes are records in an edits asset, and the output clip is rebuilt from the clip and the records " +
                "after every change. Scrub to a frame, then turn bones, move hands and feet with Unity's IK (W moves, " +
                "E turns; the elbow / knee dot sets which way the joint bends), pin a hand or foot, or key the whole " +
                "pose. Letting go of a handle stores the change (into the selected record when it does the same thing, " +
                "else as a new one). Esc cancels a move in progress. Undo works.",
                MessageType.None);

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorGUILayout.HelpBox("Not available in Play mode.", MessageType.Info);
                return;
            }

            DrawNewClip();
            if (!DrawAssets()) return;
            if (!string.IsNullOrEmpty(sessionError)) EditorGUILayout.HelpBox(sessionError, MessageType.Error);
            if (session == null) return;

            DrawFrameControls();
            DrawEditControls();
            DrawRecords();
            DrawProps();
            DrawNotes();
        }

        /// <summary>The edits asset, or the clip to start one for, and the character. False when nothing more can be shown.</summary>
        bool DrawAssets()
        {
            EditorGUI.BeginChangeCheck();
            var picked = (ClipEditsAsset)EditorGUILayout.ObjectField(
                new GUIContent("Edits", "The fixes for one clip (an edits asset). Pick a clip below to start or find one."),
                edits, typeof(ClipEditsAsset), false);
            if (EditorGUI.EndChangeCheck()) SelectEdits(picked);

            if (!edits)
            {
                EditorGUI.BeginChangeCheck();
                pickedClip = (AnimationClip)EditorGUILayout.ObjectField("Clip to fix", pickedClip, typeof(AnimationClip), false);
                if (EditorGUI.EndChangeCheck() && pickedClip)
                {
                    ClipEditsAsset found = ClipEditsAsset.FindFor(pickedClip);
                    if (found) { SelectEdits(found); return false; }
                }
                if (!pickedClip) return false;
                if (!pickedClip.humanMotion)
                {
                    EditorGUILayout.HelpBox($"'{pickedClip.name}' isn't a Humanoid clip.", MessageType.Error);
                    return false;
                }
                newCharacter = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent("Character", "The model or prefab the clip will play on. Fixes are made for this character's proportions."),
                    newCharacter, typeof(GameObject), false);
                EditorGUILayout.HelpBox(
                    $"'{pickedClip.name}' has no edits yet. Starting makes '{pickedClip.name}_Edit.anim' (the output: use it in " +
                    $"your Animator) and '{pickedClip.name}_Edits.asset' (your fixes) next to it. '{pickedClip.name}' is left as it is.",
                    MessageType.Info);
                using (new EditorGUI.DisabledScope(!newCharacter))
                {
                    if (GUILayout.Button("Start fixing this clip"))
                    {
                        ClipEditsAsset made = ClipEditsAsset.Create(pickedClip, newCharacter);
                        EditorGUIUtility.PingObject(made);
                        SelectEdits(made);
                    }
                }
                return false;
            }

            string problem = edits.Problem();
            using (new EditorGUI.DisabledScope(true))
            {
                if (edits.IsNewClip)
                {
                    EditorGUILayout.TextField(new GUIContent("Start pose", "The pose held under the pose keys for the whole clip."),
                        edits.StartPose.description);
                    EditorGUILayout.TextField("Length",
                        $"{edits.NewClipFrames} frames (0–{edits.NewClipFrames}) at {edits.NewClipFrameRate:0.##} fps");
                }
                else
                {
                    EditorGUILayout.ObjectField(new GUIContent("Source (never changed)"), edits.Source, typeof(AnimationClip), false);
                }
                EditorGUILayout.ObjectField(new GUIContent("Output (rebuilt)", "Use this clip in your Animator. It is rebuilt from " +
                    "the source and your fixes after every change, so changes made to it elsewhere are replaced."),
                    edits.Output, typeof(AnimationClip), false);
            }
            if (problem != null)
            {
                EditorGUILayout.HelpBox(problem, MessageType.Error);
                return false;
            }

            EditorGUI.BeginChangeCheck();
            var character = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Character", "The model or prefab the clip will play on. Fixes are made for this character's proportions."),
                edits.Character, typeof(GameObject), false);
            var avatar = (Avatar)EditorGUILayout.ObjectField(
                new GUIContent("Avatar (optional)", "Leave empty to use the character's own Animator avatar."),
                edits.Avatar, typeof(Avatar), false);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(edits, "Change Character");
                edits.Character = character;
                edits.Avatar = avatar;
                EditorUtility.SetDirty(edits);
                Rebuild();
                // Fixes are made on the character's proportions, so the output is rebuilt for the new one.
                if (session != null) RunGuarded(() => session.Rebake());
            }
            if (!edits.Character)
            {
                EditorGUILayout.HelpBox("Pick the character the clip plays on.", MessageType.Info);
                return false;
            }
            return true;
        }

        /// <summary>C16a: the New clip section: length, frame rate, Loop, start pose and character, then where to save it.</summary>
        void DrawNewClip()
        {
            showNewClip = EditorGUILayout.Foldout(showNewClip, "New clip", true);
            if (!showNewClip) return;
            using (new EditorGUI.IndentLevelScope())
            {
                newClipFrames = Mathf.Max(1, EditorGUILayout.IntField(
                    new GUIContent("Length (frames)", "The clip runs from frame 0 to this frame: 30 frames at 30 fps is one second."),
                    newClipFrames));
                newClipRate = Mathf.Max(1f, EditorGUILayout.FloatField("Frame rate", newClipRate));
                newClipLoop = EditorGUILayout.Toggle(
                    new GUIContent("Loop", "Turns on Loop Time and keeps the last frame's pose the same as the first's."), newClipLoop);
                newClipStart = (StartPoseKind)EditorGUILayout.Popup(
                    new GUIContent("Start pose", "The pose held for the whole clip under the pose keys."), (int)newClipStart,
                    new[] { new GUIContent("Neutral (every muscle 0)"), new GUIContent("A frame of a clip") });
                bool startOk = true;
                if (newClipStart == StartPoseKind.ClipFrame)
                {
                    newClipFrom = (AnimationClip)EditorGUILayout.ObjectField("Clip", newClipFrom, typeof(AnimationClip), false);
                    if (!newClipFrom) startOk = false;
                    else if (!newClipFrom.humanMotion)
                    {
                        EditorGUILayout.HelpBox($"'{newClipFrom.name}' isn't a Humanoid clip.", MessageType.Error);
                        startOk = false;
                    }
                    else
                    {
                        newClipFromFrame = EditorGUILayout.IntSlider("Frame", newClipFromFrame, 0, ClipWriter.LastFrame(newClipFrom));
                    }
                }
                if (!newClipCharacter && edits) newClipCharacter = edits.Character;
                newClipCharacter = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent("Character", "The model or prefab the clip will play on. Poses are made for its proportions."),
                    newClipCharacter, typeof(GameObject), false);
                newClipAvatar = (Avatar)EditorGUILayout.ObjectField(
                    new GUIContent("Avatar (optional)", "Leave empty to use the character's own Animator avatar."),
                    newClipAvatar, typeof(Avatar), false);
                using (new EditorGUI.DisabledScope(!startOk || !newClipCharacter))
                {
                    if (GUILayout.Button("Create new clip…")) CreateNewClip();
                }
            }
        }

        void CreateNewClip()
        {
            string folder = "Assets";
            if (edits && edits.Output)
                folder = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(edits.Output))?.Replace('\\', '/') ?? folder;
            string path = EditorUtility.SaveFilePanelInProject("New humanoid clip", "NewClip", "anim",
                "Where to save the new clip. Its pose keys and fixes go next to it as <name>_Edits.asset.", folder);
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    StartPose start;
                    if (newClipStart == StartPoseKind.Neutral)
                    {
                        using PreviewCharacter pc = PreviewCharacter.Create(newClipCharacter, newClipAvatar);
                        start = StartPose.Neutral(pc);
                    }
                    else
                    {
                        start = StartPose.FromClipFrame(newClipFrom, newClipFromFrame);
                    }
                    ClipEditsAsset made = ClipEditsAsset.CreateNew(path, newClipFrames, newClipRate, newClipLoop, start,
                        newClipCharacter, newClipAvatar);
                    showNewClip = false;
                    EditorGUIUtility.PingObject(made.Output);
                    SelectEdits(made);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    EditorUtility.DisplayDialog("New clip", e.Message, "OK");
                }
            }
            GUIUtility.ExitGUI();
        }

        void SelectEdits(ClipEditsAsset e)
        {
            edits = e;
            frame = 0;
            selectedRecord = -1;
            selectedProp = -1;
            Rebuild();
        }

        void DrawFrameControls()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Frame", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("|<", GUILayout.Width(30))) SetFrame(0);
                if (GUILayout.Button("<", GUILayout.Width(30))) SetFrame(session.Frame - 1);
                bool play = GUILayout.Toggle(playing, playing ? "Stop" : "Play", "Button", GUILayout.Width(50));
                if (play != playing) TogglePlay(play);
                if (GUILayout.Button(">", GUILayout.Width(30))) SetFrame(session.Frame + 1);
                if (GUILayout.Button(">|", GUILayout.Width(30))) SetFrame(session.LastFrame);
            }
            EditorGUI.BeginChangeCheck();
            int f = EditorGUILayout.IntSlider(session.Frame, 0, session.LastFrame);
            if (EditorGUI.EndChangeCheck()) SetFrame(f);
            EditorGUILayout.LabelField($"Time {session.Time:F3} s of {session.Clip.length:F3} s at {session.Clip.frameRate:0.##} fps");
            if (GUILayout.Button("Frame the character in the Scene view")) FrameInSceneView();
        }

        void DrawEditControls()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Edit", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            mode = (EditMode)GUILayout.Toolbar((int)mode, ModeLabels);
            if (EditorGUI.EndChangeCheck())
            {
                if (session.HasPendingEdit) CommitEdit();
                ResetHandles();
                SceneView.RepaintAll();
            }

            EditorGUI.BeginChangeCheck();
            newEase = Mathf.Max(0, EditorGUILayout.IntField(
                new GUIContent("Ease for new fixes", "Frames over which a new fix blends in before its first frame and out after its last."),
                newEase));
            if (EditorGUI.EndChangeCheck()) session.NewEditEaseFrames = newEase;

            if (mode == EditMode.TurnBones) DrawTurnControls();
            else if (mode == EditMode.MoveHandsAndFeet) DrawLimbControls();
            else EditorGUILayout.HelpBox("Select a prop below (or click its dot), then move (W) or turn (E) it on its bone.",
                MessageType.None);

            using (new EditorGUI.DisabledScope(!session.HasPendingEdit))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Apply")) CommitEdit();
                if (GUILayout.Button("Cancel")) CancelEdit();
            }
            DrawPoseControls();
        }

        /// <summary>C16c, C16g: key the pose on this frame, or copy, paste or mirror it.</summary>
        void DrawPoseControls()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Pose on this frame", EditorStyles.miniBoldLabel);
            if (session.IsNewClip)
                EditorGUILayout.HelpBox("Handle edits pose the character: they go into this frame's pose key (made if there " +
                    "is none). Pose keys blend into each other; offsets and pins apply on top of them.", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Add pose key", "Keys the pose this frame has now, every body part.")))
                    PoseAction(() => session.AddPoseKey());
                if (GUILayout.Button(new GUIContent("Copy pose", "Copies the pose shown, to paste on another frame or into another clip.")))
                    PoseAction(() => { PoseClipboard.Copy(session.CopyPose()); return session.SelectedRecord; });
                using (new EditorGUI.DisabledScope(!PoseClipboard.TryGet(out _)))
                    if (GUILayout.Button(new GUIContent("Paste pose", "Makes this frame's pose the copied one (into its pose key).")))
                        PoseAction(() => PoseClipboard.TryGet(out KeyPose copied) ? session.PastePose(copied) : session.SelectedRecord);
                if (GUILayout.Button(new GUIContent("Mirror pose", "Swaps the pose left ↔ right, the way Unity's Mirror option does (into this frame's pose key).")))
                    PoseAction(() => session.MirrorPose());
            }
        }

        void PoseAction(Func<int> action)
        {
            if (session.HasPendingEdit) CommitEdit();
            RunGuarded(() =>
            {
                int index = action();
                selectedRecord = index;
                session.SelectedRecord = index;
            });
            GUIUtility.ExitGUI();
        }

        void DrawTurnControls()
        {
            int index = Array.IndexOf(mappedBones, selectedBone);
            int picked = EditorGUILayout.Popup("Selected bone", Mathf.Max(0, index),
                mappedBones.Select(b => ObjectNames.NicifyVariableName(b.ToString())).ToArray());
            if (picked >= 0 && picked < mappedBones.Length && mappedBones[picked] != selectedBone)
            {
                if (session.HasPendingEdit) CommitEdit();
                selectedBone = mappedBones[picked];
                globalHandleRotation = Quaternion.identity;
                SceneView.RepaintAll();
            }
            if (selectedBone == HumanBodyBones.Hips)
            {
                EditorGUILayout.HelpBox("The hips carry the whole body: W moves them, E turns them.", MessageType.None);
                DrawRootMotionNote();
            }
        }

        /// <summary>
        /// Where the root follows the hips (Bake Into Pose off), root motion starts from frame 0: a horizontal move or
        /// a turn about the vertical there, or one a new clip's only pose key gives every frame alike, only shifts
        /// where the clip starts, so nothing changes on screen (C15). Says so when it applies.
        /// </summary>
        void DrawRootMotionNote()
        {
            AnimationClipSettings s = AnimationUtility.GetAnimationClipSettings(session.Clip);
            var parts = new List<string>();
            if (!s.loopBlendPositionXZ) parts.Add("moving them sideways or forward");
            if (!s.loopBlendOrientation) parts.Add("turning them about the vertical");
            if (parts.Count == 0) return;
            int keys = session.Edits.Records.Count(r => r is PoseKeyRecord k && !k.muted && k.Covers(HumanBodyPart.Body));
            bool everyFrame = session.IsNewClip && keys <= 1;
            if (session.Frame != 0 && !everyFrame) return;
            string where = session.Frame == 0 ? "On frame 0" : "With one pose key, which gives every frame the same hips,";
            EditorGUILayout.HelpBox(
                $"{where} {string.Join(" or ", parts)} doesn't show: this clip turns those into root motion " +
                "(Bake Into Pose off), which only shows how the hips change from frame to frame, starting from frame 0. " +
                "Key the hips on another frame to make the character travel or turn; raising, lowering and tilting " +
                "them shows everywhere.", MessageType.Info);
        }

        void DrawLimbControls()
        {
            EditorGUI.BeginChangeCheck();
            int limb = EditorGUILayout.Popup("Limb", Array.IndexOf(Limbs, selectedLimb), LimbLabels);
            limbTarget = (LimbTarget)EditorGUILayout.Popup("Handle", (int)limbTarget,
                new[] { "Hand / foot", "Elbow / knee (bend direction)" });
            if (EditorGUI.EndChangeCheck())
            {
                if (session.HasPendingEdit) CommitEdit();
                selectedLimb = Limbs[Mathf.Clamp(limb, 0, Limbs.Length - 1)];
                globalHandleRotation = Quaternion.identity;
                SceneView.RepaintAll();
            }
            string limbName = LimbLabels[Array.IndexOf(Limbs, selectedLimb)];
            if (!session.CanMove(selectedLimb))
            {
                EditorGUILayout.HelpBox("This character's limb isn't fully mapped in its Avatar (upper, lower and end bone are needed).",
                    MessageType.Warning);
                return;
            }

            if (session.ReachShortfall is float shortfall && shortfall > ReachWarning)
                EditorGUILayout.HelpBox(
                    $"The {limbName.ToLowerInvariant()} stops {shortfall * 100f:0.0} cm short of the handle: the limb can't " +
                    "reach further within the Avatar's muscle limits.", MessageType.Warning);
            if (session.TurnShortfall is float turn && turn > 1f)
                EditorGUILayout.HelpBox($"It turns {turn:0.0}° less than the handle asks, held back by the Avatar's muscle limits.",
                    MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Pin the {limbName.ToLowerInvariant()}", EditorStyles.miniBoldLabel);
            pinAnchor = (PinAnchor)EditorGUILayout.EnumPopup(new GUIContent("Hold it to",
                "World: a fixed spot (stops sliding). Bone: moves with another bone. Prop: a spot on a reference prop."), pinAnchor);
            List<ReferenceProp> props = session.Edits.Props;
            if (pinAnchor == PinAnchor.Bone)
            {
                HumanBodyBones[] choices = mappedBones.Where(b => BodyParts.Of(b) != LimbIK.Part(selectedLimb)).ToArray();
                int at = Mathf.Max(0, Array.IndexOf(choices, pinBone));
                at = EditorGUILayout.Popup("Bone", at, choices.Select(b => ObjectNames.NicifyVariableName(b.ToString())).ToArray());
                if (choices.Length > 0) pinBone = choices[Mathf.Clamp(at, 0, choices.Length - 1)];
            }
            else if (pinAnchor == PinAnchor.Prop)
            {
                if (props.Count == 0) EditorGUILayout.HelpBox("Add a prop in the Props section first.", MessageType.Info);
                else pinProp = EditorGUILayout.Popup("Prop", Mathf.Clamp(pinProp, 0, props.Count - 1), props.Select(p => p.Name).ToArray());
            }
            bool canPin = pinAnchor != PinAnchor.Prop || props.Count > 0;
            using (new EditorGUI.DisabledScope(!canPin))
            {
                if (GUILayout.Button($"Pin the {limbName.ToLowerInvariant()} here on this frame"))
                {
                    string propId = pinAnchor == PinAnchor.Prop ? props[Mathf.Clamp(pinProp, 0, props.Count - 1)].id : null;
                    RunGuarded(() =>
                    {
                        selectedRecord = session.AddPin(selectedLimb, pinAnchor, pinBone, propId);
                    });
                }
            }
        }

        void DrawPoseKeys()
        {
            List<EditRecord> records = session.Edits.Records;
            int[] keys = Enumerable.Range(0, records.Count).Where(i => records[i] is PoseKeyRecord)
                .OrderBy(i => ((PoseKeyRecord)records[i]).frame).ToArray();
            if (keys.Length == 0 && !session.IsNewClip) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Pose keys (applied first, blended with each other)", EditorStyles.boldLabel);
            if (keys.Length == 0)
                EditorGUILayout.HelpBox("No pose keys yet: the start pose holds for the whole clip.", MessageType.None);
            foreach (int i in keys)
            {
                var k = (PoseKeyRecord)records[i];
                int index = i;
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool active = EditorGUILayout.Toggle(!k.muted, GUILayout.Width(16));
                    if (active == k.muted)
                        RunGuarded(() => session.ChangeRecords(active ? "Unmute Pose Key" : "Mute Pose Key", l => l[index].muted = !active));
                    bool isSelected = i == selectedRecord;
                    string title = (k.muted ? "(muted) " : "") + k.Title + (k.frame == session.Frame ? "  ◀" : "");
                    if (GUILayout.Toggle(isSelected, title, "Button") != isSelected) SelectRecord(isSelected ? -1 : i);
                    if (GUILayout.Button("✕", GUILayout.Width(24)))
                    {
                        RunGuarded(() => session.ChangeRecords("Delete Pose Key", l => l.RemoveAt(index)));
                        SelectRecord(selectedRecord == index ? -1 : selectedRecord > index ? selectedRecord - 1 : selectedRecord);
                        GUIUtility.ExitGUI();
                    }
                }
                if (i == selectedRecord) DrawPoseKeyDetails(k, i);
            }
        }

        void DrawPoseKeyDetails(PoseKeyRecord k, int index)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUI.BeginChangeCheck();
                string label = EditorGUILayout.DelayedTextField(new GUIContent("Name", "Empty shows the description."), k.label);
                int keyFrame = Mathf.Clamp(EditorGUILayout.DelayedIntField("Frame", k.frame), 0, session.LastFrame);
                PoseEasing easing = (PoseEasing)EditorGUILayout.EnumPopup(new GUIContent("Blend to the next key",
                    "Smooth: a smooth curve through the keys. Linear: a straight blend. Hold: keeps this pose until the next key."), k.easing);
                int easeIn = k.easeIn, easeOut = k.easeOut;
                if (!session.IsNewClip)
                {
                    easeIn = Mathf.Max(0, EditorGUILayout.DelayedIntField(new GUIContent("Ease in (frames)",
                        "Frames over which the clip blends into this pose before the key (when no key comes before within reach)."), k.easeIn));
                    easeOut = Mathf.Max(0, EditorGUILayout.DelayedIntField(new GUIContent("Ease out (frames)",
                        "Frames over which the pose blends back into the clip after the key (when no key comes after within reach)."), k.easeOut));
                }
                EditorGUILayout.LabelField("Body parts it poses", EditorStyles.miniBoldLabel);
                int mask = k.mask;
                var parts = (HumanBodyPart[])Enum.GetValues(typeof(HumanBodyPart));
                for (int row = 0; row < parts.Length; row += 2)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        for (int c = row; c < Mathf.Min(row + 2, parts.Length); c++)
                        {
                            int bit = PoseKeyRecord.Bit(parts[c]);
                            bool on = EditorGUILayout.ToggleLeft(ObjectNames.NicifyVariableName(parts[c].ToString()), (mask & bit) != 0);
                            mask = on ? mask | bit : mask & ~bit;
                        }
                    }
                }
                if (EditorGUI.EndChangeCheck())
                {
                    bool taken = keyFrame != k.frame && session.Edits.Records.Any(r => r is PoseKeyRecord o && o != k && !o.muted && o.frame == keyFrame);
                    if (taken)
                    {
                        EditorUtility.DisplayDialog("Pose key", $"Frame {keyFrame} has a pose key already.", "OK");
                    }
                    else
                    {
                        RunGuarded(() => session.ChangeRecords("Edit Pose Key", l =>
                        {
                            var x = (PoseKeyRecord)l[index];
                            x.label = label;
                            x.frame = keyFrame;
                            x.easing = easing;
                            x.easeIn = easeIn;
                            x.easeOut = easeOut;
                            x.mask = mask;
                        }));
                        if (keyFrame != session.Frame) SetFrame(keyFrame);
                    }
                    GUIUtility.ExitGUI();
                }
                if (k.frame != session.Frame && GUILayout.Button("Go to its frame")) SetFrame(k.frame);
                else if (k.frame == session.Frame)
                    EditorGUILayout.HelpBox("Handle edits on this frame go into this key.", MessageType.None);
                if (RecordApplier.Loops(session.Clip) && k.frame == session.LastFrame)
                    EditorGUILayout.HelpBox("This is the last frame of a looping clip, which plays as frame 0: only the hips' " +
                        "horizontal position and facing of this key are used (how far the loop travels).", MessageType.Info);
            }
        }

        void DrawRecords()
        {
            selectedRecord = session.SelectedRecord;
            DrawPoseKeys();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Fixes (applied top to bottom, on top of the pose keys)", EditorStyles.boldLabel);
            List<EditRecord> records = session.Edits.Records;
            Baker.Result bake = session.LastBake;
            int[] fixes = Enumerable.Range(0, records.Count).Where(i => !(records[i] is PoseKeyRecord)).ToArray();
            if (fixes.Length == 0)
                EditorGUILayout.HelpBox(session.IsNewClip
                    ? "No offsets or pins yet. Select one and use the handles to change it, or pin a hand or foot."
                    : "No fixes yet. Use the handles in the Scene view to make one.", MessageType.None);

            for (int n = 0; n < fixes.Length; n++)
            {
                int i = fixes[n];
                EditRecord r = records[i];
                int index = i;
                int previous = n > 0 ? fixes[n - 1] : -1, next = n < fixes.Length - 1 ? fixes[n + 1] : -1;
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool active = EditorGUILayout.Toggle(!r.muted, GUILayout.Width(16));
                    if (active == r.muted)
                        RunGuarded(() => session.ChangeRecords(active ? "Unmute Fix" : "Mute Fix", l => l[index].muted = !active));
                    bool isSelected = i == selectedRecord;
                    string title = (r.muted ? "(muted) " : "") + r.Title;
                    if (GUILayout.Toggle(isSelected, title, "Button") != isSelected) SelectRecord(isSelected ? -1 : i);
                    using (new EditorGUI.DisabledScope(previous < 0))
                        if (GUILayout.Button("▲", GUILayout.Width(24))) MoveRecord(i, previous);
                    using (new EditorGUI.DisabledScope(next < 0))
                        if (GUILayout.Button("▼", GUILayout.Width(24))) MoveRecord(i, next);
                    if (GUILayout.Button("✕", GUILayout.Width(24)))
                    {
                        RunGuarded(() => session.ChangeRecords("Delete Fix", l => l.RemoveAt(index)));
                        SelectRecord(selectedRecord == index ? -1 : selectedRecord > index ? selectedRecord - 1 : selectedRecord);
                        GUIUtility.ExitGUI();
                    }
                }
                if (bake != null && i < bake.Problems.Length && bake.Problems[i] != null)
                    EditorGUILayout.HelpBox(bake.Problems[i], MessageType.Warning);
                if (bake != null && i < bake.ReachShortfall.Length && bake.ReachShortfall[i] > ReachWarning)
                    EditorGUILayout.HelpBox($"Out of reach on some frames: up to {bake.ReachShortfall[i] * 100f:0.0} cm short.",
                        MessageType.Warning);
                if (i == selectedRecord) DrawRecordDetails(r, i);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(selectedRecord < 0))
                    if (GUILayout.Button(new GUIContent("New fix from the next handle", "Deselects the record, so the next handle edit starts a new one.")))
                        SelectRecord(-1);
                if (GUILayout.Button(new GUIContent("Re-bake", "Rebuilds the output from the source and the fixes (e.g. after the source was re-imported).")))
                    RunGuarded(() => session.Rebake());
            }
            if (bake != null && bake.Passes > 1)
                EditorGUILayout.LabelField($"Last bake: {bake.FramesWritten} frames, {bake.Passes} passes to line up world pins " +
                                           $"with root motion (left {bake.Mismatch * 1000f:0.0} mm).", EditorStyles.miniLabel);
        }

        void DrawRecordDetails(EditRecord r, int index)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUI.BeginChangeCheck();
                string label = EditorGUILayout.DelayedTextField(new GUIContent("Name", "Empty shows the description."), r.label);
                int easeIn = Mathf.Max(0, EditorGUILayout.DelayedIntField("Ease in (frames)", r.easeIn));
                int easeOut = Mathf.Max(0, EditorGUILayout.DelayedIntField("Ease out (frames)", r.easeOut));
                int start = 0, end = 0;
                bool holdRotation = false;
                if (r is PinRecord pin)
                {
                    start = Mathf.Clamp(EditorGUILayout.DelayedIntField("First frame", pin.start), 0, session.LastFrame);
                    end = Mathf.Clamp(EditorGUILayout.DelayedIntField("Last frame", pin.end), 0, session.LastFrame);
                    holdRotation = EditorGUILayout.Toggle(new GUIContent("Hold rotation too", "Off: only the position is pinned."), pin.holdRotation);
                }
                if (EditorGUI.EndChangeCheck())
                {
                    RunGuarded(() => session.ChangeRecords("Edit Fix", l =>
                    {
                        EditRecord x = l[index];
                        x.label = label;
                        x.easeIn = easeIn;
                        x.easeOut = easeOut;
                        if (x is PinRecord p)
                        {
                            p.start = Mathf.Min(start, end);
                            p.end = Mathf.Max(start, end);
                            p.holdRotation = holdRotation;
                        }
                    }));
                    GUIUtility.ExitGUI();
                }

                if (r is PinRecord pr)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("First = this frame"))
                            RunGuarded(() => session.ChangeRecords("Edit Pin Range", l =>
                            {
                                var p = (PinRecord)l[index];
                                p.start = session.Frame;
                                if (p.end < p.start) p.end = p.start;
                            }));
                        if (GUILayout.Button("Last = this frame"))
                            RunGuarded(() => session.ChangeRecords("Edit Pin Range", l =>
                            {
                                var p = (PinRecord)l[index];
                                p.end = session.Frame;
                                if (p.start > p.end) p.start = p.end;
                            }));
                    }
                    if (session.Frame < pr.FirstFrame || session.Frame > pr.LastFrame)
                        EditorGUILayout.HelpBox("This frame is outside the pin's range: dragging the hand / foot here makes a new fix.",
                            MessageType.None);
                    else
                        EditorGUILayout.HelpBox("Drag the hand / foot (Move hands & feet) to move the pinned spot.", MessageType.None);
                }
                else if (r is OffsetRecord o)
                {
                    EditorGUILayout.LabelField("Keys", EditorStyles.miniBoldLabel);
                    foreach (OffsetKey k in o.keys.ToArray())
                    {
                        int keyFrame = k.frame;
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField($"Frame {keyFrame}" + (keyFrame == session.Frame ? "  (this frame)" : ""));
                            if (GUILayout.Button("Go to", GUILayout.Width(60))) SetFrame(keyFrame);
                            using (new EditorGUI.DisabledScope(o.keys.Count <= 1))
                                if (GUILayout.Button(new GUIContent("Remove", "A fix needs at least one key; delete the fix instead."), GUILayout.Width(60)))
                                {
                                    RunGuarded(() => session.ChangeRecords("Remove Fix Key",
                                        l => ((OffsetRecord)l[index]).keys.RemoveAll(x => x.frame == keyFrame)));
                                    GUIUtility.ExitGUI();
                                }
                        }
                    }
                    EditorGUILayout.HelpBox("Dragging its handle on another frame adds a key there.", MessageType.None);
                }
            }
        }

        void SelectRecord(int index)
        {
            if (session.HasPendingEdit) CommitEdit();
            selectedRecord = index;
            session.SelectedRecord = index;
            if (index >= 0 && index < session.Edits.Records.Count)
            {
                switch (session.Edits.Records[index])
                {
                    case PoseKeyRecord k:
                        if (k.frame != session.Frame) { session.GoToFrame(k.frame); frame = session.Frame; }
                        break;
                    case OffsetRecord o when o.target == OffsetTarget.Limb:
                        mode = EditMode.MoveHandsAndFeet; selectedLimb = o.limb; limbTarget = LimbTarget.HandOrFoot; break;
                    case OffsetRecord o:
                        mode = EditMode.TurnBones; selectedBone = o.bone; break;
                    case PinRecord p:
                        mode = EditMode.MoveHandsAndFeet; selectedLimb = p.limb; limbTarget = LimbTarget.HandOrFoot; break;
                }
            }
            ResetHandles();
            Repaint();
            SceneView.RepaintAll();
        }

        /// <summary>Swaps fix <paramref name="from"/> with fix <paramref name="to"/>; the selection stays on the same record.</summary>
        void MoveRecord(int from, int to)
        {
            List<EditRecord> records = session.Edits.Records;
            EditRecord selected = selectedRecord >= 0 && selectedRecord < records.Count ? records[selectedRecord] : null;
            RunGuarded(() => session.ChangeRecords("Reorder Fixes", l =>
            {
                EditRecord a = l[from];
                l[from] = l[to];
                l[to] = a;
            }));
            selectedRecord = selected != null ? session.Edits.Records.IndexOf(selected) : -1;
            session.SelectedRecord = selectedRecord;
            GUIUtility.ExitGUI();
        }

        void DrawProps()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Reference props (preview only, never in the clip)", EditorStyles.boldLabel);
            List<ReferenceProp> props = session.Edits.Props;
            for (int i = 0; i < props.Count; i++)
            {
                ReferenceProp p = props[i];
                int index = i;
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool visible = EditorGUILayout.Toggle(p.visible, GUILayout.Width(16));
                    if (visible != p.visible)
                        RunGuarded(() => session.ChangeProps(visible ? "Show Prop" : "Hide Prop", l => l[index].visible = visible));
                    bool isSelected = i == selectedProp;
                    if (GUILayout.Toggle(isSelected, $"{p.Name} on {ObjectNames.NicifyVariableName(p.bone.ToString())}", "Button") != isSelected)
                    {
                        selectedProp = isSelected ? -1 : i;
                        if (selectedProp >= 0) mode = EditMode.Props;
                        SceneView.RepaintAll();
                    }
                    if (GUILayout.Button("✕", GUILayout.Width(24)))
                    {
                        bool pinned = session.Edits.Records.Any(r => r is PinRecord pin && pin.anchor == PinAnchor.Prop && pin.propId == p.id);
                        if (!pinned || EditorUtility.DisplayDialog("Remove prop",
                                $"A pin holds on to '{p.Name}'. Removing the prop leaves that pin without an anchor (it stops applying).",
                                "Remove", "Cancel"))
                        {
                            RunGuarded(() => session.ChangeProps("Remove Prop", l => l.RemoveAt(index)));
                            selectedProp = -1;
                            GUIUtility.ExitGUI();
                        }
                    }
                }
                if (i == selectedProp) DrawPropDetails(p, index);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                newPropModel = (GameObject)EditorGUILayout.ObjectField(newPropModel, typeof(GameObject), false);
                int at = Mathf.Max(0, Array.IndexOf(mappedBones, newPropBone));
                at = EditorGUILayout.Popup(at, mappedBones.Select(b => ObjectNames.NicifyVariableName(b.ToString())).ToArray());
                if (mappedBones.Length > 0) newPropBone = mappedBones[Mathf.Clamp(at, 0, mappedBones.Length - 1)];
                using (new EditorGUI.DisabledScope(!newPropModel))
                {
                    if (GUILayout.Button("Add prop", GUILayout.Width(80)))
                    {
                        var prop = new ReferenceProp { prefab = newPropModel, bone = newPropBone };
                        RunGuarded(() => session.ChangeProps("Add Prop", l => l.Add(prop)));
                        selectedProp = session.Edits.Props.Count - 1;
                        mode = EditMode.Props;
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        void DrawPropDetails(ReferenceProp p, int index)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUI.BeginChangeCheck();
                var model = (GameObject)EditorGUILayout.ObjectField("Model", p.prefab, typeof(GameObject), false);
                int at = Mathf.Max(0, Array.IndexOf(mappedBones, p.bone));
                at = EditorGUILayout.Popup("Bone", at, mappedBones.Select(b => ObjectNames.NicifyVariableName(b.ToString())).ToArray());
                HumanBodyBones bone = mappedBones.Length > 0 ? mappedBones[Mathf.Clamp(at, 0, mappedBones.Length - 1)] : p.bone;
                Vector3 position = EditorGUILayout.Vector3Field("Position", p.position);
                Vector3 euler = EditorGUILayout.Vector3Field("Rotation", EditRecord.Normalized(p.rotation).eulerAngles);
                Vector3 scale = EditorGUILayout.Vector3Field("Scale", p.scale);
                if (EditorGUI.EndChangeCheck())
                {
                    RunGuarded(() => session.ChangeProps("Edit Prop", l =>
                    {
                        ReferenceProp x = l[index];
                        x.prefab = model;
                        x.bone = bone;
                        x.position = position;
                        x.rotation = Quaternion.Euler(euler);
                        x.scale = scale;
                    }));
                }
            }
        }

        void DrawNotes()
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                $"Fixes are made for '{session.Edits.Character.name}': the clip's muscles are shared by every humanoid, but the " +
                "same values put hands and feet in different places on differently proportioned characters.",
                MessageType.None);
            if (session.ClipIsMirrored)
                EditorGUILayout.HelpBox("The output has Mirror on. The preview shows it unmirrored — the sides its curves are written for.",
                    MessageType.Info);
            if (RecordApplier.Loops(session.Clip) && session.Frame == session.LastFrame && session.Edits.Records.Any(r => r is PoseKeyRecord))
                EditorGUILayout.HelpBox("This is the last frame of a looping clip: it plays as frame 0, so pose frame 0 instead. " +
                    "A pose key here only sets how far the hips have travelled by the end of the loop (horizontal position and facing).",
                    MessageType.Info);
            if (session.EditShiftsWholeLoop)
                EditorGUILayout.HelpBox("The output has Loop Pose on and this is its first or last frame. Unity works out the loop " +
                    "correction from these two frames, so a fix here shifts every frame of the clip slightly.", MessageType.Warning);
        }

        // ---------------------------------------------------------------- actions

        void SetFrame(int f)
        {
            if (session == null) return;
            if (session.HasPendingEdit) CommitEdit();
            session.GoToFrame(f);
            frame = session.Frame;
            ResetHandles();
            Repaint();
            SceneView.RepaintAll();
        }

        void TogglePlay(bool on)
        {
            playing = on;
            if (!on || session == null) return;
            if (session.HasPendingEdit) session.Cancel();
            playStart = EditorApplication.timeSinceStartup;
            playStartFrame = session.Frame;
        }

        void OnUpdate()
        {
            if (!playing || session == null) return;
            int count = session.LastFrame + 1;
            int f = (playStartFrame + (int)((EditorApplication.timeSinceStartup - playStart) * session.Clip.frameRate)) % count;
            if (f != session.Frame) SetFrame(f);
        }

        void CommitEdit()
        {
            RunGuarded(() => session.Commit());
            selectedRecord = session?.SelectedRecord ?? -1;
            ResetHandles();
            Repaint();
            SceneView.RepaintAll();
        }

        void CancelEdit()
        {
            session.Cancel();
            ResetHandles();
            Repaint();
            SceneView.RepaintAll();
        }

        /// <summary>Runs a change; a failure is logged and the shown frame goes back to the output's.</summary>
        void RunGuarded(Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                Debug.LogException(e);
                session?.Cancel();
            }
            Repaint();
            SceneView.RepaintAll();
        }

        /// <summary>Handles go back to the bones and the hint to the shown bend, e.g. on a new frame.</summary>
        void ResetHandles()
        {
            globalHandleRotation = Quaternion.identity;
            hintMoved = false;
            propDragging = false;
        }

        void FrameInSceneView()
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (!view || preview?.Root == null) return;
            Renderer[] renderers = preview.Container.GetComponentsInChildren<Renderer>();
            var bounds = new Bounds(preview.Root.transform.position, Vector3.one);
            foreach (Renderer r in renderers) bounds.Encapsulate(r.bounds);
            view.Frame(bounds, false);
        }

        // ---------------------------------------------------------------- scene view

        void OnSceneGUI(SceneView view)
        {
            if (session == null || preview?.Animator == null) return;
            Event e = Event.current;

            // Releasing a handle ends the drag: store the change.
            bool dragging = GUIUtility.hotControl != 0;
            if (wasDragging && !dragging)
            {
                if (session.HasPendingEdit) CommitEdit();
                if (propDragging) CommitPropDrag();
            }
            wasDragging = dragging;

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && (session.HasPendingEdit || propDragging))
            {
                if (propDragging) { propDragging = false; session.Reload(); }
                CancelEdit();
                e.Use();
                return;
            }
            if (playing) return;

            DrawSelectedPin();
            if (mode == EditMode.TurnBones) TurnBoneHandles();
            else if (mode == EditMode.MoveHandsAndFeet) LimbHandles();
            else PropHandles();
        }

        void TurnBoneHandles()
        {
            DrawBoneDots();
            Transform bone = preview.Animator.GetBoneTransform(selectedBone);
            if (!bone) return;

            bool local = Tools.pivotRotation == PivotRotation.Local;
            if (selectedBone == HumanBodyBones.Hips && Tools.current == Tool.Move)
            {
                if (!session.HasPendingEdit) { hipsPosition = bone.position; hipsRotation = bone.rotation; }
                EditorGUI.BeginChangeCheck();
                Vector3 p = Handles.PositionHandle(hipsPosition, local ? hipsRotation : Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    hipsPosition = p;
                    RunGuarded(() => session.MoveHips(hipsPosition, hipsRotation));
                }
                return;
            }

            Quaternion handleRotation = local ? bone.rotation : globalHandleRotation;
            EditorGUI.BeginChangeCheck();
            Quaternion turned = Handles.RotationHandle(handleRotation, bone.position);
            if (EditorGUI.EndChangeCheck())
            {
                Quaternion delta = turned * Quaternion.Inverse(handleRotation);
                RunGuarded(() => session.RotateBone(selectedBone, delta * bone.rotation));
                if (!local) globalHandleRotation = turned;
            }
        }

        void LimbHandles()
        {
            DrawLimbDots();
            if (!session.CanMove(selectedLimb)) return;
            Transform end = preview.Animator.GetBoneTransform(LimbIK.EndBone(selectedLimb));
            Transform mid = preview.Animator.GetBoneTransform(LimbIK.MidBone(selectedLimb));

            // Between drags the handles sit on the bones; during a drag they hold what the user asked for.
            if (!session.HasPendingEdit)
            {
                effectorPosition = end.position;
                effectorRotation = end.rotation;
                activeHint = session.DefaultHint(selectedLimb);
                hintMoved = false;
            }

            Color old = Handles.color;
            Handles.color = new Color(1f, 0.6f, 0.2f, 0.9f);
            Handles.DrawDottedLine(mid.position, activeHint, 3f);
            float hintSize = HandleUtility.GetHandleSize(activeHint) * 0.05f;
            Handles.SphereHandleCap(0, activeHint, Quaternion.identity, hintSize, EventType.Repaint);
            Handles.color = old;

            bool local = Tools.pivotRotation == PivotRotation.Local;
            bool moved = false;
            if (limbTarget == LimbTarget.ElbowOrKnee)
            {
                EditorGUI.BeginChangeCheck();
                Vector3 hint = Handles.PositionHandle(activeHint, Quaternion.identity);
                if (EditorGUI.EndChangeCheck()) { activeHint = hint; hintMoved = true; moved = true; }
            }
            else if (Tools.current == Tool.Rotate)
            {
                Quaternion handleRotation = local ? effectorRotation : globalHandleRotation;
                EditorGUI.BeginChangeCheck();
                Quaternion turned = Handles.RotationHandle(handleRotation, effectorPosition);
                if (EditorGUI.EndChangeCheck())
                {
                    effectorRotation = turned * Quaternion.Inverse(handleRotation) * effectorRotation;
                    if (!local) globalHandleRotation = turned;
                    moved = true;
                }
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                Vector3 p = Handles.PositionHandle(effectorPosition, local ? effectorRotation : Quaternion.identity);
                if (EditorGUI.EndChangeCheck()) { effectorPosition = p; moved = true; }
            }

            if (moved)
            {
                Vector3? hint = hintMoved ? activeHint : (Vector3?)null;
                RunGuarded(() => session.MoveEffector(selectedLimb, effectorPosition, effectorRotation, hint));
            }

            if (session.ReachShortfall is float shortfall && shortfall > ReachWarning)
            {
                Handles.color = Color.red;
                Handles.DrawLine(end.position, effectorPosition);
                Handles.Label(effectorPosition, $"  {shortfall * 100f:0.0} cm short");
                Handles.color = old;
            }
        }

        void PropHandles()
        {
            List<ReferenceProp> props = session.Edits.Props;
            Color old = Handles.color;
            for (int i = 0; i < props.Count; i++)
            {
                if (!props[i].TryGetPose(preview.Animator, out Vector3 pos, out Quaternion _)) continue;
                float size = HandleUtility.GetHandleSize(pos) * 0.06f;
                Handles.color = i == selectedProp ? Color.yellow : new Color(0.6f, 1f, 0.4f, 0.8f);
                if (Handles.Button(pos, Quaternion.identity, size, size, Handles.SphereHandleCap) && i != selectedProp)
                {
                    selectedProp = i;
                    Repaint();
                }
            }
            Handles.color = old;
            if (selectedProp < 0 || selectedProp >= props.Count) return;
            ReferenceProp prop = props[selectedProp];
            if (!prop.TryGetPose(preview.Animator, out Vector3 at, out Quaternion rot)) return;
            if (!propDragging) { propPosition = at; propRotation = rot; }

            bool local = Tools.pivotRotation == PivotRotation.Local;
            bool moved = false;
            if (Tools.current == Tool.Rotate)
            {
                EditorGUI.BeginChangeCheck();
                Quaternion turned = Handles.RotationHandle(propRotation, propPosition);
                if (EditorGUI.EndChangeCheck()) { propRotation = turned; moved = true; }
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                Vector3 p = Handles.PositionHandle(propPosition, local ? propRotation : Quaternion.identity);
                if (EditorGUI.EndChangeCheck()) { propPosition = p; moved = true; }
            }
            if (moved)
            {
                propDragging = true;
                GameObject shown = session.Props.InstanceOf(prop.id);
                if (shown) shown.transform.SetPositionAndRotation(propPosition, propRotation);
            }
        }

        /// <summary>Stores a dragged prop's new place on its bone (one Undo step; pins on it are re-baked).</summary>
        void CommitPropDrag()
        {
            propDragging = false;
            List<ReferenceProp> props = session.Edits.Props;
            if (selectedProp < 0 || selectedProp >= props.Count) return;
            Transform bone = preview.Animator.GetBoneTransform(props[selectedProp].bone);
            if (!bone) return;
            int index = selectedProp;
            Vector3 localPosition = bone.InverseTransformPoint(propPosition);
            Quaternion localRotation = Quaternion.Inverse(bone.rotation) * propRotation;
            RunGuarded(() => session.ChangeProps("Move Prop", l =>
            {
                l[index].position = localPosition;
                l[index].rotation = localRotation;
            }));
        }

        /// <summary>Marks the selected pin's spot on this frame.</summary>
        void DrawSelectedPin()
        {
            int s = session.SelectedRecord;
            if (s < 0 || !(session.Edits.Records[s] is PinRecord pin)) return;
            if (!RecordApplier.AnchorPose(preview, session.Edits, pin, out Vector3 ap, out Quaternion ar, out _)) return;
            Vector3 spot = ap + ar * pin.position;
            Color old = Handles.color;
            Handles.color = new Color(1f, 0.3f, 0.8f, 0.9f);
            float size = HandleUtility.GetHandleSize(spot) * 0.08f;
            Handles.DrawWireDisc(spot, Vector3.up, size);
            Handles.DrawWireDisc(spot, Vector3.right, size);
            Handles.DrawWireDisc(spot, Vector3.forward, size);
            Handles.color = old;
        }

        void DrawBoneDots()
        {
            Color old = Handles.color;
            foreach (HumanBodyBones b in mappedBones)
            {
                Transform t = preview.Animator.GetBoneTransform(b);
                if (!t) continue;
                float size = HandleUtility.GetHandleSize(t.position) * (b >= HumanBodyBones.LeftThumbProximal &&
                                                                          b != HumanBodyBones.UpperChest ? 0.03f : 0.06f);
                Handles.color = b == selectedBone ? Color.yellow : new Color(0.3f, 0.8f, 1f, 0.8f);
                if (Handles.Button(t.position, Quaternion.identity, size, size, Handles.SphereHandleCap) && b != selectedBone)
                {
                    if (session.HasPendingEdit) CommitEdit();
                    selectedBone = b;
                    globalHandleRotation = Quaternion.identity;
                    Repaint();
                }
            }
            Handles.color = old;
        }

        /// <summary>Hand / foot dots select a limb's hand or foot handle; elbow / knee dots select its bend handle.</summary>
        void DrawLimbDots()
        {
            Color old = Handles.color;
            foreach (AvatarIKGoal limb in Limbs)
            {
                if (!session.CanMove(limb)) continue;
                LimbDot(limb, LimbIK.EndBone(limb), LimbTarget.HandOrFoot);
                LimbDot(limb, LimbIK.MidBone(limb), LimbTarget.ElbowOrKnee);
            }
            Handles.color = old;
        }

        void LimbDot(AvatarIKGoal limb, HumanBodyBones bone, LimbTarget target)
        {
            Transform t = preview.Animator.GetBoneTransform(bone);
            bool selected = limb == selectedLimb && target == limbTarget;
            float size = HandleUtility.GetHandleSize(t.position) * 0.06f;
            Handles.color = selected ? Color.yellow
                : target == LimbTarget.HandOrFoot ? new Color(0.3f, 0.8f, 1f, 0.8f) : new Color(1f, 0.6f, 0.2f, 0.8f);
            if (Handles.Button(t.position, Quaternion.identity, size, size, Handles.SphereHandleCap) && !selected)
            {
                if (session.HasPendingEdit) CommitEdit();
                selectedLimb = limb;
                limbTarget = target;
                globalHandleRotation = Quaternion.identity;
                Repaint();
            }
        }
    }
}
