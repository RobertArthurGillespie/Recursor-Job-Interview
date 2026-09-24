using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

// Stage 6B: WebGL project settings, the host-origin rules, the build validator, and static
// checks on the .jslib browser boundary (which cannot execute inside the Editor).
public class InterviewWebGLConfigurationTests
{
    private const string JslibPath = "Assets/JobInterview/Plugins/WebGL/RecursorInterviewBridge.jslib";

    // -- Project settings (committed) ---------------------------------------------

    [Test]
    public void Project_settings_satisfy_the_webgl_build_requirements()
    {
        CollectionAssert.IsEmpty(InterviewWebGLBuildValidator.ValidateProjectSettings(
            PlayerSettings.bundleVersion,
            EditorBuildSettings.scenes,
            PlayerSettings.WebGL.compressionFormat,
            PlayerSettings.WebGL.decompressionFallback));
    }

    [Test]
    public void The_job_interview_scene_is_the_only_enabled_build_scene()
    {
        CollectionAssert.AreEqual(
            new[] { InterviewWebGLBuildValidator.JobInterviewScenePath },
            EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
            "the WebGL player ships only the job-interview application scene");
    }

    [Test]
    public void The_validator_requires_the_job_interview_scene_to_be_the_startup_scene()
    {
        var otherFirst = new[]
        {
            new EditorBuildSettingsScene("Assets/Scenes/SampleScene.unity", true),
            new EditorBuildSettingsScene(InterviewWebGLBuildValidator.JobInterviewScenePath, true),
        };
        Assert.AreEqual(1, InterviewWebGLBuildValidator.ValidateProjectSettings(
            "1.0.0", otherFirst, WebGLCompressionFormat.Gzip, true).Count);
    }

    [Test]
    public void WebGL_uses_gzip_with_decompression_fallback()
    {
        Assert.AreEqual(WebGLCompressionFormat.Gzip, PlayerSettings.WebGL.compressionFormat);
        Assert.IsTrue(PlayerSettings.WebGL.decompressionFallback);
    }

    [Test]
    public void The_application_version_is_a_valid_project_controlled_sim_version()
    {
        Assert.IsTrue(InterviewBridgeProtocol.IsValidSimVersion(PlayerSettings.bundleVersion));
        Assert.AreEqual(PlayerSettings.bundleVersion, Application.version);
    }

    [Test]
    public void The_validator_reports_each_missing_project_requirement_with_fixed_text()
    {
        var problems = InterviewWebGLBuildValidator.ValidateProjectSettings(
            " bad version", new EditorBuildSettingsScene[0], WebGLCompressionFormat.Brotli, false);

        Assert.AreEqual(4, problems.Count);
        Assert.IsFalse(problems.Any(p => p.Contains(" bad version")));

        var disabledScene = new[] { new EditorBuildSettingsScene(InterviewWebGLBuildValidator.JobInterviewScenePath, false) };
        Assert.AreEqual(1, InterviewWebGLBuildValidator.ValidateProjectSettings(
            "1.0.0", disabledScene, WebGLCompressionFormat.Gzip, true).Count);
    }

    [Test]
    public void The_validator_fails_a_missing_or_invalid_bridge_settings_asset()
    {
        Assert.AreEqual(1, InterviewWebGLBuildValidator.ValidateBridgeSettings(false, null, false, false).Count);
        Assert.AreEqual(1, InterviewWebGLBuildValidator.ValidateBridgeSettings(true, "", false, false).Count);
        Assert.AreEqual(1, InterviewWebGLBuildValidator.ValidateBridgeSettings(true, "http://host.example", false, false).Count);
        Assert.AreEqual(1, InterviewWebGLBuildValidator.ValidateBridgeSettings(true, "https://localhost:7279", false, true).Count, "loopback without the flag");
        Assert.AreEqual(1, InterviewWebGLBuildValidator.ValidateBridgeSettings(true, "https://localhost:7279", true, false).Count, "loopback in a Release build");
        CollectionAssert.IsEmpty(InterviewWebGLBuildValidator.ValidateBridgeSettings(true, "https://localhost:7279", true, true), "loopback with flag in Development");
        CollectionAssert.IsEmpty(InterviewWebGLBuildValidator.ValidateBridgeSettings(true, "https://parent.example", false, false), "non-loopback in Release");
        CollectionAssert.IsEmpty(InterviewWebGLBuildValidator.ValidateBridgeSettings(true, "https://parent.example", false, true), "non-loopback in Development");

        var problems = InterviewWebGLBuildValidator.ValidateBridgeSettings(true, "https://evil.example/path", false, false);
        Assert.IsFalse(problems.Any(p => p.Contains("evil.example")), "origin values are never echoed");
    }

    // -- Host-origin rules ---------------------------------------------------------

    [TestCase("https://interview-host.example", HostOriginValidation.Valid)]
    [TestCase("https://recursor.azurewebsites.net", HostOriginValidation.Valid)]
    [TestCase("https://host.example:8443", HostOriginValidation.Valid)]
    [TestCase("", HostOriginValidation.Missing)]
    [TestCase(null, HostOriginValidation.Missing)]
    [TestCase("http://interview-host.example", HostOriginValidation.NotHttps)]
    [TestCase("HTTP://interview-host.example", HostOriginValidation.NotHttps)]
    [TestCase("https://interview-host.example/", HostOriginValidation.Malformed)]
    [TestCase("https://interview-host.example/app", HostOriginValidation.Malformed)]
    [TestCase("https://interview-host.example?x=1", HostOriginValidation.Malformed)]
    [TestCase("https://interview-host.example#frag", HostOriginValidation.Malformed)]
    [TestCase("https://user:pass@interview-host.example", HostOriginValidation.Malformed)]
    [TestCase("https://*.example", HostOriginValidation.Malformed)]
    [TestCase("*", HostOriginValidation.Malformed)]
    [TestCase(" https://interview-host.example", HostOriginValidation.Malformed)]
    [TestCase("https://interview-host.example ", HostOriginValidation.Malformed)]
    [TestCase("https://Interview-Host.example", HostOriginValidation.Malformed)]
    [TestCase("https://interview-host.example:443", HostOriginValidation.Malformed)]
    [TestCase("https://interview-host.example:0", HostOriginValidation.Malformed)]
    [TestCase("https://interview-host.example:70000", HostOriginValidation.Malformed)]
    [TestCase("https://interview-host.example\n", HostOriginValidation.Malformed)]
    [TestCase("null", HostOriginValidation.Malformed)]
    [TestCase("https://", HostOriginValidation.Malformed)]
    public void Host_origin_must_be_an_exact_https_serialized_origin(string origin, HostOriginValidation expected) =>
        Assert.AreEqual(expected, RecursorInterviewHostOrigin.Validate(origin, allowLoopback: false, isDevelopmentBuild: false));

    [TestCase("https://parent.example")]
    [TestCase("https://interview-host.example:8443")]
    public void Non_loopback_https_parent_origins_are_valid_in_development_and_release_builds(string origin)
    {
        Assert.AreEqual(HostOriginValidation.Valid, RecursorInterviewHostOrigin.Validate(origin, allowLoopback: false, isDevelopmentBuild: false));
        Assert.AreEqual(HostOriginValidation.Valid, RecursorInterviewHostOrigin.Validate(origin, allowLoopback: false, isDevelopmentBuild: true));
    }

    [TestCase("https://localhost:7279")]
    [TestCase("https://127.0.0.1:7279")]
    [TestCase("https://app.localhost:7279")]
    public void Loopback_without_the_flag_is_rejected_in_every_build(string origin)
    {
        Assert.AreEqual(HostOriginValidation.LoopbackNotPermitted, RecursorInterviewHostOrigin.Validate(origin, allowLoopback: false, isDevelopmentBuild: false));
        Assert.AreEqual(HostOriginValidation.LoopbackNotPermitted, RecursorInterviewHostOrigin.Validate(origin, allowLoopback: false, isDevelopmentBuild: true));
    }

    [TestCase("https://localhost:7279")]
    [TestCase("https://127.0.0.1:7279")]
    [TestCase("https://app.localhost:7279")]
    public void Loopback_with_the_flag_is_rejected_in_a_release_build(string origin) =>
        Assert.AreEqual(HostOriginValidation.LoopbackNotPermitted, RecursorInterviewHostOrigin.Validate(origin, allowLoopback: true, isDevelopmentBuild: false));

    [TestCase("https://localhost:7279")]
    [TestCase("https://127.0.0.1:7279")]
    [TestCase("https://app.localhost:7279")]
    public void Loopback_with_the_flag_is_accepted_in_a_development_build_or_the_editor(string origin) =>
        Assert.AreEqual(HostOriginValidation.Valid, RecursorInterviewHostOrigin.Validate(origin, allowLoopback: true, isDevelopmentBuild: true));

    [Test]
    public void Http_loopback_is_never_accepted()
    {
        Assert.AreEqual(HostOriginValidation.NotHttps, RecursorInterviewHostOrigin.Validate("http://localhost:5010", allowLoopback: true, isDevelopmentBuild: true));
    }

    // -- .jslib boundary (static checks) ------------------------------------------

    private static string Jslib() => File.ReadAllText(JslibPath);

    [Test]
    public void The_jslib_posts_only_to_the_validated_origin_and_never_to_a_wildcard()
    {
        string js = Jslib();

        StringAssert.Contains("window.parent.postMessage(message, state.origin);", js);
        Assert.AreEqual(1, CountOf(js, "postMessage("), "exactly one postMessage call");
        StringAssert.DoesNotContain("\"*\"", js);
        StringAssert.DoesNotContain("'*'", js);
    }

    [Test]
    public void The_jslib_accepts_only_the_parent_window_and_exact_origin_after_matching_the_fragment()
    {
        string js = Jslib();

        StringAssert.Contains("event.source !== window.parent || event.origin !== state.origin", js);
        StringAssert.Contains("fragment.parentOrigin !== expectedOrigin", js);
        StringAssert.Contains("fragment.bridgeVersion !== String(bridgeVersion)", js);
        Assert.Less(js.IndexOf("fragment.parentOrigin !== expectedOrigin", System.StringComparison.Ordinal),
                    js.IndexOf("window.addEventListener(\"message\"", System.StringComparison.Ordinal),
                    "the listener is registered only after the fragment check");
    }

    [Test]
    public void The_jslib_never_logs_stores_or_makes_network_requests()
    {
        string js = Jslib();

        foreach (string forbidden in new[]
        {
            "console.", "localStorage", "sessionStorage", "document.cookie", "fetch(", "XMLHttpRequest",
            "WebSocket", "sendBeacon", "Authorization", "Bearer", "token", "eval(",
        })
        {
            StringAssert.DoesNotContain(forbidden, js, forbidden);
        }
    }

    [Test]
    public void The_bridge_settings_asset_path_is_git_ignored_exactly_and_nothing_broader()
    {
        string ignore = File.ReadAllText(".gitignore");

        StringAssert.Contains("/" + RecursorInterviewBridgeSettings.AssetPath + "\n", ignore.Replace("\r\n", "\n") + "\n");
        StringAssert.Contains("/" + RecursorInterviewBridgeSettings.AssetPath + ".meta", ignore);
        StringAssert.DoesNotContain("Assets/JobInterview/Resources/\n", ignore.Replace("\r\n", "\n"));
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, System.StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, System.StringComparison.Ordinal))
            count++;
        return count;
    }
}
