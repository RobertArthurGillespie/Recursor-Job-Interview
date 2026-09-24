using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Stage 6B: blocks a WebGL build that could not run the job-interview bridge correctly.
// Every message is fixed text: no origin, version, or path value is echoed.
public sealed class InterviewWebGLBuildValidator : IPreprocessBuildWithReport
{
    public const string JobInterviewScenePath = "Assets/Scenes/JobInterview.unity";

    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.WebGL)
            return;

        bool developmentBuild = (report.summary.options & BuildOptions.Development) != 0;
        List<string> problems = ValidateCurrentProject(developmentBuild);

        if (problems.Count > 0)
        {
            throw new BuildFailedException(
                "Job interview WebGL build blocked:\n- " + string.Join("\n- ", problems));
        }
    }

    // developmentBuild: true for a Development build (or Editor-time validation for local
    // development); false for a Release build, which never accepts a loopback parent origin.
    public static List<string> ValidateCurrentProject(bool developmentBuild)
    {
        List<string> problems = ValidateProjectSettings(
            PlayerSettings.bundleVersion,
            EditorBuildSettings.scenes,
            PlayerSettings.WebGL.compressionFormat,
            PlayerSettings.WebGL.decompressionFallback);

        var settings = AssetDatabase.LoadAssetAtPath<RecursorInterviewBridgeSettings>(
            RecursorInterviewBridgeSettings.AssetPath);

        problems.AddRange(ValidateBridgeSettings(
            settings != null,
            settings != null ? settings.AllowedHostOrigin : null,
            settings != null && settings.AllowLoopbackDevelopmentOrigin,
            developmentBuild));

        return problems;
    }

    public static List<string> ValidateProjectSettings(
        string bundleVersion,
        IEnumerable<EditorBuildSettingsScene> scenes,
        WebGLCompressionFormat compressionFormat,
        bool decompressionFallback)
    {
        var problems = new List<string>();

        // Application.version is the bridge simVersion; there is no substitute value.
        if (!InterviewBridgeProtocol.IsValidSimVersion(bundleVersion))
        {
            problems.Add(
                "PlayerSettings version (bundleVersion) must be a nonblank, project-controlled build version: " +
                "1-64 characters, an ASCII letter or digit first, then letters, digits, '.', '_', '+', '-'.");
        }

        // The first enabled scene is the player's startup scene.
        EditorBuildSettingsScene startup = scenes?.FirstOrDefault(s => s.enabled);
        if (startup == null || startup.path != JobInterviewScenePath)
            problems.Add("The JobInterview scene must be the first enabled scene (the startup scene) in Build Settings.");

        if (compressionFormat != WebGLCompressionFormat.Gzip)
            problems.Add("WebGL compression must be Gzip (contract section 14.1).");

        if (!decompressionFallback)
            problems.Add("WebGL Decompression Fallback must be enabled (contract section 14.1).");

        return problems;
    }

    // allowedHostOrigin is the PARENT Blazor page's origin, not the origin serving the Unity files.
    public static List<string> ValidateBridgeSettings(
        bool settingsAssetPresent,
        string allowedHostOrigin,
        bool allowLoopbackDevelopmentOrigin,
        bool developmentBuild)
    {
        var problems = new List<string>();

        if (!settingsAssetPresent)
        {
            problems.Add(
                "The local bridge settings asset is missing. Create it with Job Interview > Create Local Bridge " +
                "Settings Asset and set allowedHostOrigin to the parent Blazor page's origin " +
                "(see Docs/job-interview-webgl-bridge.md).");
            return problems;
        }

        HostOriginValidation validation = RecursorInterviewHostOrigin.Validate(
            allowedHostOrigin,
            allowLoopbackDevelopmentOrigin,
            developmentBuild);

        if (validation != HostOriginValidation.Valid)
            problems.Add(RecursorInterviewHostOrigin.Describe(validation));

        return problems;
    }

    [MenuItem("Job Interview/Create Local Bridge Settings Asset")]
    private static void CreateLocalSettingsAsset()
    {
        var existing = AssetDatabase.LoadAssetAtPath<RecursorInterviewBridgeSettings>(
            RecursorInterviewBridgeSettings.AssetPath);

        if (existing == null)
        {
            string folder = Path.GetDirectoryName(RecursorInterviewBridgeSettings.AssetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder) && !AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(Path.GetDirectoryName(folder)?.Replace('\\', '/'), Path.GetFileName(folder));

            existing = ScriptableObject.CreateInstance<RecursorInterviewBridgeSettings>();
            AssetDatabase.CreateAsset(existing, RecursorInterviewBridgeSettings.AssetPath);
            AssetDatabase.SaveAssets();
        }

        Selection.activeObject = existing;
        EditorGUIUtility.PingObject(existing);
    }
}
