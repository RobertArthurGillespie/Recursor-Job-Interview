using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Stage 6B: guards the serialized JobInterview scene wiring (including the End Interview button)
// against silent breakage. It does not replace the live browser test.
public class JobInterviewSceneWiringTests
{
    private const string ScenePath = InterviewWebGLBuildValidator.JobInterviewScenePath;

    private static readonly string[] RequiredUiReferences =
    {
        "session", "roleText", "progressText", "interviewerText", "statusText", "responseInput",
        "startButton", "continueButton", "submitButton", "clarifyButton", "resetButton", "endButton",
    };

    private Scene scene;
    private bool openedHere;

    [OneTimeSetUp]
    public void OpenScene()
    {
        scene = SceneManager.GetSceneByPath(ScenePath);
        if (!scene.IsValid() || !scene.isLoaded)
        {
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            openedHere = true;
        }
    }

    [OneTimeTearDown]
    public void CloseScene()
    {
        if (openedHere && scene.IsValid())
            EditorSceneManager.CloseScene(scene, removeScene: true);
    }

    private IEnumerable<GameObject> AllObjects() =>
        scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject);

    private T Single<T>() where T : Component
    {
        var found = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<T>(true)).ToList();
        Assert.AreEqual(1, found.Count, $"expected exactly one {typeof(T).Name}");
        return found[0];
    }

    [Test]
    public void The_scene_is_the_enabled_startup_scene()
    {
        EditorBuildSettingsScene first = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled);
        Assert.IsNotNull(first);
        Assert.AreEqual(ScenePath, first.path);
    }

    [Test]
    public void No_object_has_a_missing_script_reference()
    {
        foreach (GameObject go in AllObjects())
            Assert.AreEqual(0, GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go), go.name);
    }

    [Test]
    public void The_session_and_ui_controllers_exist_and_are_wired_together()
    {
        var session = Single<InterviewSessionController>();
        var ui = Single<InterviewUIController>();

        var uiSerialized = new SerializedObject(ui);
        foreach (string field in RequiredUiReferences)
            Assert.IsNotNull(uiSerialized.FindProperty(field).objectReferenceValue, $"InterviewUIController.{field}");
        Assert.AreSame(session, uiSerialized.FindProperty("session").objectReferenceValue);

        var sessionSerialized = new SerializedObject(session);
        var scenario = sessionSerialized.FindProperty("scenario").objectReferenceValue as InterviewScenarioDefinition;
        Assert.IsNotNull(scenario, "InterviewSessionController.scenario");
        Assert.AreEqual(InterviewBridgeProtocol.ScenarioId, scenario.ScenarioId);
        Assert.AreEqual(
            (int)InterviewConversationProviderMode.Automatic,
            sessionSerialized.FindProperty("providerMode").enumValueIndex,
            "Automatic: host bridge in WebGL, scripted elsewhere");
    }

    [Test]
    public void Exactly_one_labelled_end_interview_button_is_referenced_by_the_ui_controller()
    {
        var endObjects = AllObjects().Where(go => go.name == "EndInterviewButton").ToList();
        Assert.AreEqual(1, endObjects.Count);

        Button endButton = endObjects[0].GetComponent<Button>();
        Assert.IsNotNull(endButton);

        TMP_Text label = endButton.GetComponentInChildren<TMP_Text>(true);
        Assert.IsNotNull(label);
        Assert.AreEqual("End Interview", label.text);

        var ui = Single<InterviewUIController>();
        var uiSerialized = new SerializedObject(ui);
        Assert.AreSame(endButton, uiSerialized.FindProperty("endButton").objectReferenceValue);

        // Distinct from Reset, beside it, and driven only by the runtime listener (no persistent calls).
        var reset = (Button)uiSerialized.FindProperty("resetButton").objectReferenceValue;
        Assert.AreNotSame(reset, endButton);
        Assert.AreSame(reset.transform.parent, endButton.transform.parent);
        Assert.AreEqual(0, endButton.onClick.GetPersistentEventCount());
    }

    [Test]
    public void Every_ui_button_is_a_distinct_object()
    {
        var uiSerialized = new SerializedObject(Single<InterviewUIController>());
        var buttons = new[] { "startButton", "continueButton", "submitButton", "clarifyButton", "resetButton", "endButton" }
            .Select(f => uiSerialized.FindProperty(f).objectReferenceValue)
            .ToList();

        Assert.AreEqual(buttons.Count, buttons.Distinct().Count());
    }
}
