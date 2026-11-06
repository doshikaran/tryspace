using System;
using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction;
using Oculus.Interaction.Editor.QuickActions;
using Tryscape.Unity;
using Tryscape.Unity.Experience;
using Tryscape.Unity.MixedReality;
using Tryscape.Unity.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Tryscape.Editor
{
    /// <summary>
    /// Builds the Phase 1 experience into the generated bootstrap scene: greybox catalog prefabs (collision proxy =
    /// footprint), prefab registry, tabletop presenter, placement ghost and a hand-selectable panel. Interaction SDK
    /// wiring uses Meta's public QuickActionsAPI against the rig already in the scene.
    /// </summary>
    internal static class ExperienceBuilder
    {
        const string ContentRoot = "Assets/Tryscape/Content/MovingDay";
        const string MaterialsDir = ContentRoot + "/Materials";
        const string PrefabsDir = ContentRoot + "/Prefabs";
        const string RegistryPath = ContentRoot + "/MovingDayPrefabRegistry.asset";

        static Font _font;

        public static void Build(OVRPassthroughLayer passthroughLayer, Camera camera, GameObject virtualEnvironment)
        {
            EnsureFolder(ContentRoot + "/Materials");
            EnsureFolder(ContentRoot + "/Prefabs");
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var m = new Materials();
            var sofa = BuildStaticPrefab("sofa_standard_greybox", new Vector3(1.8f, 0.9f, 0.8f), m.Sofa, m.Selected, "Delivery sofa");
            var console = BuildBlockerPrefab("console_standard_greybox", new Vector3(0.6f, 0.8f, 1.0f), m.Console, m.Selected);

            var registry = AssetDatabase.LoadAssetAtPath<PrefabRegistry>(RegistryPath);
            if (registry == null)
            {
                registry = ScriptableObject.CreateInstance<PrefabRegistry>();
                AssetDatabase.CreateAsset(registry, RegistryPath);
            }
            registry.SetEntries(new[]
            {
                new PrefabRegistry.Entry { key = "sofa_standard_greybox", prefab = sofa },
                new PrefabRegistry.Entry { key = "console_standard_greybox", prefab = console },
            });
            EditorUtility.SetDirty(registry);

            // Tabletop: room presentation root; scale is presentation only.
            var tabletop = new GameObject("Tabletop");
            tabletop.transform.position = new Vector3(0f, 0.72f, 0.55f);
            var tabletopController = tabletop.AddComponent<TabletopController>();
            var presenter = tabletop.AddComponent<ScenePresenter>();
            presenter.Configure(registry, m.Floor, m.Wall, m.Route, m.RouteInactive);
            var ghostGo = new GameObject("Placement Ghost");
            ghostGo.transform.SetParent(tabletop.transform, false);
            var ghost = ghostGo.AddComponent<PlacementGhost>();
            ghost.Configure(m.GhostValid, m.GhostInvalid);
            var highlightGo = new GameObject("Result Highlight");
            highlightGo.transform.SetParent(tabletop.transform, false);
            var highlight = highlightGo.AddComponent<ResultHighlight>();
            highlight.Configure(m.Contact);
            tabletop.AddComponent<PlaybackController>();

            var panel = BuildPanel();

            var appGo = new GameObject("Tryscape");
            var app = appGo.AddComponent<TryscapeApp>();
            app.Configure(presenter, ghost, panel, tabletopController, highlight);

            var mixedReality = appGo.AddComponent<MixedRealityController>();
            mixedReality.Configure(passthroughLayer, camera, virtualEnvironment, tabletopController);
            var gazeController = appGo.AddComponent<GazeController>();
            var eyeGaze = UnityEngine.Object.FindObjectsByType<Oculus.Interaction.Input.EyeGaze>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(g => g.GetType() == typeof(Oculus.Interaction.Input.EyeGaze))
                ?? throw new InvalidOperationException("No EyeGaze in scene after gaze quick actions");
            gazeController.Configure(eyeGaze);
            app.ConfigureMixedReality(mixedReality, gazeController);

            // S05.3: the rig's top-level hands (left and right) feed the hand-loss replay pause.
            var hands = UnityEngine.Object.FindObjectsByType<Oculus.Interaction.Input.Hand>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(h => h.GetType() == typeof(Oculus.Interaction.Input.Hand)).ToList();
            if (hands.Count == 0)
                throw new InvalidOperationException("No Interaction SDK hands in the rig");
            var watcher = appGo.AddComponent<HandTrackingWatcher>();
            watcher.Configure(hands);
            app.ConfigureHandTracking(watcher);
            Debug.Log($"[Tryscape] Hand-loss watcher wired to {hands.Count} hand(s)");

            AssetDatabase.SaveAssets();
        }

        // ---------------------------------------------------------------- prefabs

        static GameObject BuildStaticPrefab(string key, Vector3 size, Material body, Material selected, string label)
        {
            var root = new GameObject(key);
            var view = root.AddComponent<EntityView>();
            var bodyRenderer = Body(root.transform, size, body);
            view.Configure(Label(root.transform, size.y + 0.25f, label), bodyRenderer, selected);
            return SavePrefab(root, key);
        }

        static GameObject BuildBlockerPrefab(string key, Vector3 size, Material body, Material selected)
        {
            var root = new GameObject(key);
            var view = root.AddComponent<EntityView>();
            var bodyRenderer = Body(root.transform, size, body);
            view.Configure(Label(root.transform, size.y + 0.25f, "Console table"), bodyRenderer, selected);

            // Collision proxy = exact footprint x rendered height (S06.5).
            var collider = root.AddComponent<BoxCollider>();
            collider.size = size;
            collider.center = new Vector3(0, size.y / 2f, 0);

            var created = new List<GameObject>();
            created.AddRange(QuickActionsAPI.AddGrabInteraction(root));
            created.AddRange(QuickActionsAPI.AddRayGrabInteraction(root));
            // Gaze grab with ray fallback: look at the console and pinch; the hand's motion moves it (AC35).
            var gazeObjects = QuickActionsAPI.AddGazeInteraction(root, enableRayFallback: true);
            if (gazeObjects.Count == 0)
                throw new InvalidOperationException("Gaze quick action created nothing");
            created.AddRange(gazeObjects);
            var rigidbody = root.GetComponent<Rigidbody>();
            if (rigidbody == null)
                throw new InvalidOperationException("Grab quick action did not add a Rigidbody");
            rigidbody.isKinematic = true; // physics never decides placement or results
            rigidbody.useGravity = false;

            var grabbable = root.GetComponent<Grabbable>() ?? throw new InvalidOperationException("No Grabbable added");
            var adapter = root.AddComponent<BlockerDragAdapter>();
            adapter.Configure(grabbable);

            // Quick actions may also add interactors to the scene rig; those stay in the scene, not in the prefab.
            var prefab = SavePrefab(root, key);
            Debug.Log($"[Tryscape] Built {key}: grab objects created={created.Count}");
            return prefab;
        }

        static Renderer Body(Transform parent, Vector3 size, Material material)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            UnityEngine.Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(parent, false);
            body.transform.localScale = size;
            body.transform.localPosition = new Vector3(0, size.y / 2f, 0);
            var renderer = body.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            return renderer;
        }

        static TextMesh Label(Transform parent, float height, string text)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0, height, 0);
            var mesh = go.AddComponent<TextMesh>();
            mesh.text = text;
            mesh.anchor = TextAnchor.LowerCenter;
            mesh.characterSize = 0.05f;
            mesh.fontSize = 64;
            mesh.color = Color.white;
            return mesh;
        }

        static GameObject SavePrefab(GameObject root, string key)
        {
            var path = $"{PrefabsDir}/{key}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path, out var success);
            UnityEngine.Object.DestroyImmediate(root);
            if (!success || prefab == null)
                throw new InvalidOperationException($"Could not save prefab {path}");
            return prefab;
        }

        // ---------------------------------------------------------------- panel

        static PanelController BuildPanel()
        {
            var canvasGo = new GameObject("Tryscape Panel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rect = canvasGo.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(560, 900);
            // Initial pose for an eye-level origin (head at y = 0); the app re-places it relative to the real head at start.
            canvasGo.transform.position = new Vector3(-0.32f, -0.12f, 0.55f);
            canvasGo.transform.rotation = Quaternion.LookRotation(new Vector3(-0.32f, 0f, 0.55f).normalized, Vector3.up);
            canvasGo.transform.localScale = Vector3.one * 0.001f; // 1 px = 1 mm

            Image(canvasGo.transform, "Background", new Color(0.08f, 0.09f, 0.11f, 0.92f), stretch: true);
            var title = Text(canvasGo.transform, "Title", "Tryscape", 40, FontStyle.Bold, new Vector2(0, 405), new Vector2(520, 60));
            title.alignment = TextAnchor.MiddleCenter;
            Text(canvasGo.transform, "Tagline", "Understand the situation. Rehearse your options. Act prepared.", 18,
                FontStyle.Italic, new Vector2(0, 365), new Vector2(520, 30)).alignment = TextAnchor.MiddleCenter;
            var status = Text(canvasGo.transform, "Status", "", 20, FontStyle.Normal, new Vector2(0, -395), new Vector2(520, 100));
            status.alignment = TextAnchor.UpperLeft;

            var buttons = new List<Button>();
            var welcome = Section(canvasGo.transform, "Welcome");
            buttons.Add(Button(welcome, "CreateWithHands", "Create with hands", 0, 200, 440));
            buttons.Add(Button(welcome, "DescribeTyped", "Describe (typed, dev input)", 0, 130, 440));
            buttons.Add(Button(welcome, "SpeakSituation", "Describe with voice", 0, 60, 440));
            buttons.Add(Button(welcome, "OpenSaved", "Open saved project", 0, -10, 440));
            buttons.Add(Button(welcome, "Help", "Help", 0, -80, 440));
            buttons.Add(Button(welcome, "ResumeEditing", "Resume editing", 0, -150, 440));
            buttons.Add(Button(welcome, "ResumeLast", "Resume saved project", 0, -220, 440));
            buttons.Add(Button(welcome, "OnboardingOk", "Got it", 0, 265, 200));

            // Always available (D05): Settings / Help / Pause / Reload route, top-right of the panel in every mode.
            buttons.Add(Button(canvasGo.transform, "Settings", "Settings", 205, 405, 130));
            var settings = Section(canvasGo.transform, "Settings Section");
            var settingsCard = Text(settings, "SettingsCard", "", 15, FontStyle.Normal, new Vector2(0, 215), new Vector2(520, 190));
            settingsCard.alignment = TextAnchor.UpperLeft;
            // Mixed reality and gaze (S11): presentation only.
            buttons.Add(Button(settings, "TogglePassthrough", "Passthrough off", -130, 95, 250));
            buttons.Add(Button(settings, "PlaceOnSurface", "Place on table", 130, 95, 250));
            buttons.Add(Button(settings, "SetUpSpace", "Set up my space", -130, 35, 250));
            buttons.Add(Button(settings, "PlaceFloating", "Float in front of me", 130, 35, 250));
            buttons.Add(Button(settings, "ToggleGaze", "Use head gaze", -130, -25, 250));
            buttons.Add(Button(settings, "PauseAll", "Pause replay", 130, -25, 250));
            buttons.Add(Button(settings, "RotateViewLeft", "Turn view left", -130, -85, 250));
            buttons.Add(Button(settings, "RotateViewRight", "Turn view right", 130, -85, 250));
            buttons.Add(Button(settings, "ResetView", "Reset view", -130, -145, 250));
            buttons.Add(Button(settings, "ReloadSaved", "Reload last save", 130, -145, 250));
            buttons.Add(Button(settings, "SettingsHelp", "Help", -130, -205, 250));
            buttons.Add(Button(settings, "SettingsBack", "Close settings", 130, -205, 250));

            // Typed development transcript entry. Labelled as development input; this is not voice recognition.
            var describe = Section(canvasGo.transform, "Describe");
            var banner = Text(describe, "DevBanner", "DEVELOPMENT INPUT - typed transcript, not voice recognition", 18,
                FontStyle.Bold, new Vector2(0, 225), new Vector2(520, 30));
            banner.color = new Color(1f, 0.8f, 0.3f);
            banner.alignment = TextAnchor.MiddleCenter;
            var input = TranscriptInput(describe, new Vector2(0, 130), new Vector2(520, 150));
            buttons.Add(Button(describe, "Preset0", "Example: 6 x 5 m", -130, 20, 250));
            buttons.Add(Button(describe, "Preset1", "7 x 6 m, parked south", 130, 20, 250));
            buttons.Add(Button(describe, "Preset2", "No size given", -130, -45, 250));
            buttons.Add(Button(describe, "Preset3", "Unsupported request", 130, -45, 250));
            buttons.Add(Button(describe, "SubmitTranscript", "Interpret", -130, -120, 250));
            buttons.Add(Button(describe, "CancelProposal", "Cancel", 130, -120, 250));
            buttons.Add(Button(describe, "DescribeBack", "Back", 0, -185, 250));

            // Voice: processing notice before the first recording, then status/elapsed time, Stop and Cancel.
            var voice = Section(canvasGo.transform, "Voice");
            var voiceTitle = Text(voice, "VoiceTitle", "Describe with voice", 24, FontStyle.Bold, new Vector2(0, 225), new Vector2(520, 36));
            voiceTitle.alignment = TextAnchor.MiddleCenter;
            var voiceCard = Text(voice, "VoiceCard", "", 20, FontStyle.Normal, new Vector2(0, 120), new Vector2(520, 170));
            voiceCard.alignment = TextAnchor.UpperLeft;
            voiceCard.supportRichText = false; // may contain the untrusted transcript
            buttons.Add(Button(voice, "VoiceAllow", "Allow and record", 0, 0, 440));
            buttons.Add(Button(voice, "VoiceStop", "Stop", -130, 0, 250));
            buttons.Add(Button(voice, "VoiceCancel", "Cancel", 130, 0, 250));
            buttons.Add(Button(voice, "VoiceRetry", "Record again", -130, -65, 250));
            buttons.Add(Button(voice, "VoiceTypeInstead", "Type instead", 130, -65, 250));
            buttons.Add(Button(voice, "VoiceHands", "Create with hands", -130, -130, 250));
            buttons.Add(Button(voice, "VoiceBack", "Menu", 130, -130, 250));

            // Proposal review: interpretation, assumptions, clarification choices; confirmation is a hand press.
            var review = Section(canvasGo.transform, "Review");
            var reviewCard = Text(review, "ReviewCard", "", 18, FontStyle.Normal, new Vector2(0, 140), new Vector2(520, 210));
            reviewCard.alignment = TextAnchor.UpperLeft;
            reviewCard.supportRichText = false; // contains untrusted transcript text
            buttons.Add(Button(review, "Choice0", "Choice", -130, 0, 250));
            buttons.Add(Button(review, "Choice1", "Choice", 130, 0, 250));
            buttons.Add(Button(review, "Choice2", "Choice", -130, -65, 250));
            buttons.Add(Button(review, "Choice3", "Choice", 130, -65, 250));
            buttons.Add(Button(review, "ConfirmProposal", "Confirm and build", -130, -130, 250));
            buttons.Add(Button(review, "AdjustProposal", "Adjust with hands", 130, -130, 250));
            buttons.Add(Button(review, "ReviewHands", "Create with hands", 130, -130, 250));
            buttons.Add(Button(review, "DescribeAgain", "Describe again", -130, -195, 250));
            buttons.Add(Button(review, "ReviewMenu", "Menu", 130, -195, 250));
            buttons.Add(Button(review, "EditTranscript", "Edit transcript", 0, -260, 250));

            var create = Section(canvasGo.transform, "Create");
            var draft = Text(create, "Draft", "", 20, FontStyle.Normal, new Vector2(0, 175), new Vector2(520, 90));
            draft.alignment = TextAnchor.UpperLeft;
            Text(create, "WidthLabel", "Width", 20, FontStyle.Bold, new Vector2(-215, 95), new Vector2(90, 50));
            buttons.Add(Button(create, "WidthMinus", "-0.5", -105, 95, 90));
            buttons.Add(Button(create, "WidthMinusFine", "-0.1", -5, 95, 90));
            buttons.Add(Button(create, "WidthPlusFine", "+0.1", 95, 95, 90));
            buttons.Add(Button(create, "WidthPlus", "+0.5", 195, 95, 90));
            Text(create, "DepthLabel", "Depth", 20, FontStyle.Bold, new Vector2(-215, 30), new Vector2(90, 50));
            buttons.Add(Button(create, "DepthMinus", "-0.5", -105, 30, 90));
            buttons.Add(Button(create, "DepthMinusFine", "-0.1", -5, 30, 90));
            buttons.Add(Button(create, "DepthPlusFine", "+0.1", 95, 30, 90));
            buttons.Add(Button(create, "DepthPlus", "+0.5", 195, 30, 90));
            buttons.Add(Button(create, "ObstructionCenter", "Console in the way", -130, -40, 250));
            buttons.Add(Button(create, "ObstructionSouth", "Console parked south", 130, -40, 250));
            buttons.Add(Button(create, "RouteDirect", "Direct approach", -130, -105, 250));
            buttons.Add(Button(create, "RouteNorth", "Northern approach", 130, -105, 250));
            buttons.Add(Button(create, "ConfirmCreate", "Create example room", 70, -175, 300));
            buttons.Add(Button(create, "BackToWelcome", "Back", -175, -175, 150));

            var edit = Section(canvasGo.transform, "Edit");
            var scene = Text(edit, "SceneSummary", "", 20, FontStyle.Normal, new Vector2(0, 190), new Vector2(520, 50));
            scene.alignment = TextAnchor.MiddleLeft;
            buttons.Add(Button(edit, "Rotate", "Rotate console 90°", -130, 120, 250));
            buttons.Add(Button(edit, "ToggleRoute", "Switch route", 130, 120, 250));
            buttons.Add(Button(edit, "Undo", "Undo", -170, 50, 160));
            buttons.Add(Button(edit, "Redo", "Redo", 0, 50, 160));
            buttons.Add(Button(edit, "Reset", "Reset", 170, 50, 160));
            buttons.Add(Button(edit, "ZoomOut", "Smaller", -170, -20, 160));
            buttons.Add(Button(edit, "ZoomIn", "Larger", 0, -20, 160));
            buttons.Add(Button(edit, "Recenter", "Recenter", 170, -20, 160));
            buttons.Add(Button(edit, "Menu", "Menu", -195, -90, 120));
            buttons.Add(Button(edit, "Save", "Save", -65, -90, 120));
            buttons.Add(Button(edit, "KeepA", "Keep as A", 65, -90, 120));
            buttons.Add(Button(edit, "KeepB", "Keep as B", 195, -90, 120));
            buttons.Add(Button(edit, "Rehearse", "Rehearse", -195, -160, 120));
            buttons.Add(Button(edit, "PlayPause", "Replay", -65, -160, 120));
            buttons.Add(Button(edit, "Details", "Details", 65, -160, 120));
            buttons.Add(Button(edit, "Compare", "Compare", 195, -160, 120));
            var result = Text(edit, "ResultCard", "", 18, FontStyle.Normal, new Vector2(0, -275), new Vector2(520, 150));
            result.alignment = TextAnchor.UpperLeft;

            // Comparison: both retained attempts, changes and evidence-based explanation; inspect either one read-only.
            var compare = Section(canvasGo.transform, "Compare");
            var compareCard = Text(compare, "CompareCard", "", 17, FontStyle.Normal, new Vector2(0, 110), new Vector2(520, 300));
            compareCard.alignment = TextAnchor.UpperLeft;
            buttons.Add(Button(compare, "InspectA", "Inspect A", -175, -80, 160));
            buttons.Add(Button(compare, "InspectB", "Inspect B", 0, -80, 160));
            buttons.Add(Button(compare, "InspectLive", "Live scene", 175, -80, 160));
            buttons.Add(Button(compare, "CompareReplay", "Replay attempt", -130, -145, 250));
            buttons.Add(Button(compare, "CompareBack", "Back to editing", 130, -145, 250));

            // Saved projects on this device: open or delete (two presses).
            var projects = Section(canvasGo.transform, "Projects");
            var projectsCard = Text(projects, "ProjectsCard", "", 20, FontStyle.Normal, new Vector2(0, 140), new Vector2(520, 200));
            projectsCard.alignment = TextAnchor.UpperLeft;
            buttons.Add(Button(projects, "ProjectPrev", "Previous", -130, 0, 250));
            buttons.Add(Button(projects, "ProjectNext", "Next", 130, 0, 250));
            buttons.Add(Button(projects, "ProjectOpen", "Open", -130, -65, 250));
            buttons.Add(Button(projects, "ProjectDelete", "Delete", 130, -65, 250));
            buttons.Add(Button(projects, "ProjectsBack", "Menu", 0, -130, 250));

            var controller = canvasGo.AddComponent<PanelController>();
            controller.Configure(welcome.gameObject, create.gameObject, edit.gameObject, status, draft, scene, buttons, result,
                describe.gameObject, review.gameObject, reviewCard, input, voice.gameObject, voiceCard, compare.gameObject, compareCard,
                projects.gameObject, projectsCard, settings.gameObject, settingsCard);

            var created = QuickActionsAPI.AddRayCanvasInteraction(canvasGo).Concat(QuickActionsAPI.AddPokeCanvasInteraction(canvasGo)).ToList();
            // Gaze (S11, AC35): look and pinch; the ray stays the pointer whenever eye gaze is not tracked.
            var gaze = QuickActionsAPI.AddGazeCanvasInteraction(canvasGo, enableRayFallback: true);
            if (gaze.Count == 0)
                throw new InvalidOperationException("Gaze canvas quick action created nothing");
            created.AddRange(gaze);
            Debug.Log($"[Tryscape] Panel canvas interaction objects created={created.Count} (gaze {gaze.Count})");
            return controller;
        }

        static InputField TranscriptInput(Transform parent, Vector2 position, Vector2 extent)
        {
            var go = new GameObject("TranscriptInput", typeof(RectTransform), typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<RectTransform>();
            r.sizeDelta = extent;
            r.anchoredPosition = position;
            go.GetComponent<Image>().color = new Color(0.16f, 0.17f, 0.2f, 1f);
            var text = Text(go.transform, "Text", "", 18, FontStyle.Normal, Vector2.zero, extent - new Vector2(20, 16));
            text.alignment = TextAnchor.UpperLeft;
            text.supportRichText = false;
            var placeholder = Text(go.transform, "Placeholder", "Type a description, or pick an example below.", 18,
                FontStyle.Italic, Vector2.zero, extent - new Vector2(20, 16));
            placeholder.alignment = TextAnchor.UpperLeft;
            placeholder.color = new Color(1f, 1f, 1f, 0.45f);
            var field = go.GetComponent<InputField>();
            field.textComponent = text;
            field.placeholder = placeholder;
            field.characterLimit = 2000;
            field.lineType = InputField.LineType.MultiLineNewline;
            return field;
        }

        static Transform Section(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<RectTransform>();
            r.sizeDelta = new Vector2(560, 480);
            r.anchoredPosition = new Vector2(0, 20);
            return go.transform;
        }

        static Button Button(Transform parent, string id, string label, float x, float y, float width)
        {
            var go = new GameObject(id, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<RectTransform>();
            r.sizeDelta = new Vector2(width, 56);
            r.anchoredPosition = new Vector2(x, y);
            go.GetComponent<Image>().color = new Color(0.22f, 0.36f, 0.62f, 1f);
            var button = go.GetComponent<Button>();
            var colors = button.colors;
            colors.disabledColor = new Color(0.35f, 0.35f, 0.35f, 0.6f);
            colors.highlightedColor = new Color(0.45f, 0.6f, 0.95f, 1f);
            button.colors = colors;
            var text = Text(go.transform, "Text", label, 20, FontStyle.Bold, Vector2.zero, new Vector2(width - 10, 50));
            text.alignment = TextAnchor.MiddleCenter;
            return button;
        }

        static Text Text(Transform parent, string name, string value, int size, FontStyle style, Vector2 position, Vector2 extent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<RectTransform>();
            r.sizeDelta = extent;
            r.anchoredPosition = position;
            var text = go.GetComponent<Text>();
            text.font = _font;
            text.text = value;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            return text;
        }

        static void Image(Transform parent, string name, Color color, bool stretch)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<RectTransform>();
            if (stretch)
            {
                r.anchorMin = Vector2.zero;
                r.anchorMax = Vector2.one;
                r.offsetMin = Vector2.zero;
                r.offsetMax = Vector2.zero;
            }
            go.GetComponent<Image>().color = color;
        }

        // ---------------------------------------------------------------- materials

        sealed class Materials
        {
            public readonly Material Floor = Get("Floor", new Color(0.55f, 0.55f, 0.52f));
            public readonly Material Wall = Get("Wall", new Color(0.85f, 0.84f, 0.8f));
            public readonly Material Route = Get("RouteActive", new Color(1f, 0.72f, 0.15f));
            public readonly Material RouteInactive = Get("RouteInactive", new Color(0.3f, 0.3f, 0.33f));
            public readonly Material Sofa = Get("Sofa", new Color(0.25f, 0.42f, 0.75f));
            public readonly Material Console = Get("Console", new Color(0.62f, 0.42f, 0.25f));
            public readonly Material Selected = Get("Selected", new Color(0.2f, 0.9f, 0.95f));
            public readonly Material GhostValid = Get("GhostValid", new Color(0.3f, 0.85f, 0.35f));
            public readonly Material GhostInvalid = Get("GhostInvalid", new Color(0.9f, 0.25f, 0.2f));
            public readonly Material Contact = Get("ResultContact", new Color(1f, 0.35f, 0.25f));

            static Material Get(string name, Color color)
            {
                var path = $"{MaterialsDir}/{name}.mat";
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    var shader = Shader.Find("Universal Render Pipeline/Lit") ?? throw new InvalidOperationException("URP Lit shader not found");
                    material = new Material(shader) { name = name };
                    AssetDatabase.CreateAsset(material, path);
                }
                material.SetColor("_BaseColor", color);
                EditorUtility.SetDirty(material);
                return material;
            }
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            var parent = System.IO.Path.GetDirectoryName(path)!.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }
    }
}
