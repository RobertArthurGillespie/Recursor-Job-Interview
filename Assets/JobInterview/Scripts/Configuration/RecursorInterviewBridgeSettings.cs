using System;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

// Stage 6B: the single PARENT origin the WebGL bridge may talk to - the origin of the Blazor
// (Recursor) application page that embeds this build in an iframe and exchanges postMessage
// with it (for example https://localhost:7279 for the Server's local https profile).
//
// It is NOT the origin that serves these Unity WebGL files. That iframe/content location is
// Stage 6A's RecursorInterviewHost:UnityOrigin / UnityBuildUrl and is usually a different
// origin; the two are equal only when the applications are intentionally same-origin.
//
// The real asset lives at AssetPath, which is git-ignored so no deployment origin enters source
// control; see Docs/job-interview-webgl-bridge.md. A missing or invalid asset disables the
// bridge and fails a WebGL build (InterviewWebGLBuildValidator). The iframe URL's #parentOrigin
// fragment (set by the Blazor parent) must equal this value exactly before any listener or
// outbound message is enabled, and the .jslib compares every event.origin against it.
public sealed class RecursorInterviewBridgeSettings : ScriptableObject
{
    public const string ResourceName = "RecursorInterviewBridgeSettings";
    public const string AssetPath = "Assets/JobInterview/Resources/RecursorInterviewBridgeSettings.asset";

    [SerializeField]
    [Tooltip("Exact serialized origin of the PARENT Blazor page that embeds this build (not the origin " +
             "serving the Unity files): https://host[:port], lowercase, no path, query, fragment, " +
             "credentials, wildcard, trailing slash, or whitespace.")]
    private string allowedHostOrigin = string.Empty;

    [SerializeField]
    [Tooltip("Explicitly permit a loopback parent origin (https://localhost:<port>, " +
             "https://127.0.0.1:<port>, *.localhost) for local development. Honored only in Development " +
             "builds and the Editor; a non-Development (Release) build rejects loopback even when set.")]
    private bool allowLoopbackDevelopmentOrigin;

    public string AllowedHostOrigin => allowedHostOrigin;
    public bool AllowLoopbackDevelopmentOrigin => allowLoopbackDevelopmentOrigin;

    public static RecursorInterviewBridgeSettings Load() =>
        Resources.Load<RecursorInterviewBridgeSettings>(ResourceName);

    // Editor tooling and tests only.
    public void Configure(string origin, bool allowLoopback)
    {
        allowedHostOrigin = origin ?? string.Empty;
        allowLoopbackDevelopmentOrigin = allowLoopback;
    }
}

public enum HostOriginValidation
{
    Valid,
    Missing,
    NotHttps,
    Malformed,
    LoopbackNotPermitted,
}

public static class RecursorInterviewHostOrigin
{
    // The browser's serialized origin form, which is what event.origin carries: lowercase host,
    // explicit port only when non-default. Anything else could never match exactly.
    private static readonly Regex OriginPattern = new Regex(
        "^https://(?<host>[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*)" +
        "(?::(?<port>[1-9][0-9]{0,4}))?\\z",
        RegexOptions.CultureInvariant);

    // A non-loopback https parent origin is valid in any build. A loopback parent origin is valid
    // only with the explicit allowLoopback opt-in AND a Development build or Editor validation
    // (isDevelopmentBuild); a Release build rejects it even when the flag is set, so a
    // mistakenly configured production build can never trust localhost.
    public static HostOriginValidation Validate(string origin, bool allowLoopback, bool isDevelopmentBuild)
    {
        if (string.IsNullOrEmpty(origin))
            return HostOriginValidation.Missing;

        if (!origin.StartsWith("https://", StringComparison.Ordinal))
        {
            return origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? HostOriginValidation.NotHttps
                : HostOriginValidation.Malformed;
        }

        Match match = OriginPattern.Match(origin);
        if (!match.Success)
            return HostOriginValidation.Malformed;

        string host = match.Groups["host"].Value;
        if (host.Length > 253)
            return HostOriginValidation.Malformed;

        if (match.Groups["port"].Success)
        {
            int port = int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture);
            if (port > 65535 || port == 443)
                return HostOriginValidation.Malformed;
        }

        if (IsLoopback(host) && !(allowLoopback && isDevelopmentBuild))
            return HostOriginValidation.LoopbackNotPermitted;

        return HostOriginValidation.Valid;
    }

    public static bool IsLoopback(string host) =>
        host == "localhost" ||
        host.EndsWith(".localhost", StringComparison.Ordinal) ||
        host == "127.0.0.1";

    // Fixed, value-free text for build errors and UI.
    public static string Describe(HostOriginValidation validation)
    {
        switch (validation)
        {
            case HostOriginValidation.Valid:
                return "The parent Blazor origin (allowedHostOrigin) is valid.";
            case HostOriginValidation.Missing:
                return "No parent Blazor origin (allowedHostOrigin) is configured.";
            case HostOriginValidation.NotHttps:
                return "The parent Blazor origin (allowedHostOrigin) must use https.";
            case HostOriginValidation.LoopbackNotPermitted:
                return "A loopback parent Blazor origin (allowedHostOrigin) requires allowLoopbackDevelopmentOrigin " +
                       "and a Development build; Release builds never accept loopback.";
            default:
                return "The parent Blazor origin (allowedHostOrigin) must be exactly https://host[:port] in lowercase, " +
                       "with no path, query, fragment, credentials, wildcard, trailing slash, whitespace, or default port. " +
                       "It is the embedding Blazor page's origin, not the origin serving the Unity files.";
        }
    }
}
