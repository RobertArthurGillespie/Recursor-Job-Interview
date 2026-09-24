using System;
using System.Runtime.InteropServices;
using UnityEngine;

// Stage 6B: the C# side of Plugins/WebGL/RecursorInterviewBridge.jslib. "Parent origin" below is
// the configured allowedHostOrigin: the origin of the Blazor page that embeds this build, not
// the origin serving the Unity files (Stage 6A's UnityOrigin). The .jslib enables its window
// listener only when the page is framed, the #parentOrigin fragment equals the configured parent
// origin exactly, and bridgeVersion matches; it accepts only messages whose source is
// window.parent and whose event.origin is that parent origin, and posts only to that parent
// origin (never "*"). Outside a WebGL player this transport is disabled and sends nothing.
public sealed class WebGLInterviewBridgeTransport : IInterviewBridgeTransport, IDisposable
{
    public delegate void MessageCallback(string json);

    private static WebGLInterviewBridgeTransport current;

    private WebGLInterviewBridgeTransport(bool enabled) => IsEnabled = enabled;

    public event Action<string> Received;

    public bool IsEnabled { get; private set; }

    // validatedParentOrigin must already have passed RecursorInterviewHostOrigin.Validate.
    public static WebGLInterviewBridgeTransport Create(string validatedParentOrigin)
    {
        current?.Dispose();

        bool enabled = false;
#if UNITY_WEBGL && !UNITY_EDITOR
        enabled = RecursorInterviewBridge_Enable(validatedParentOrigin, InterviewBridgeProtocol.Version, OnMessage) == 1;
#endif
        current = new WebGLInterviewBridgeTransport(enabled);
        return current;
    }

    public bool Post(string envelopeJson)
    {
        if (!IsEnabled || envelopeJson == null)
            return false;
#if UNITY_WEBGL && !UNITY_EDITOR
        return RecursorInterviewBridge_Post(envelopeJson) == 1;
#else
        return false;
#endif
    }

    public void Dispose()
    {
        if (!IsEnabled)
            return;

        IsEnabled = false;
#if UNITY_WEBGL && !UNITY_EDITOR
        RecursorInterviewBridge_Disable();
#endif
        if (ReferenceEquals(current, this))
            current = null;
    }

    [AOT.MonoPInvokeCallback(typeof(MessageCallback))]
    private static void OnMessage(string json)
    {
        WebGLInterviewBridgeTransport transport = current;
        if (transport != null && transport.IsEnabled)
            transport.Received?.Invoke(json);
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern int RecursorInterviewBridge_Enable(string expectedParentOrigin, int bridgeVersion, MessageCallback onMessage);

    [DllImport("__Internal")]
    private static extern int RecursorInterviewBridge_Post(string envelopeJson);

    [DllImport("__Internal")]
    private static extern void RecursorInterviewBridge_Disable();
#endif
}

// Used when no validated parent origin exists: nothing can be sent or received.
public sealed class DisabledInterviewBridgeTransport : IInterviewBridgeTransport
{
    public static readonly DisabledInterviewBridgeTransport Instance = new DisabledInterviewBridgeTransport();

    private DisabledInterviewBridgeTransport() { }

    public bool IsEnabled => false;

    public bool Post(string envelopeJson) => false;

    public event Action<string> Received
    {
        add { }
        remove { }
    }
}

public static class InterviewBridgeRuntime
{
    public static bool IsWebGLPlayer => Application.platform == RuntimePlatform.WebGLPlayer;

    // Application.version is the only build-version source; it is validated by the client and
    // never replaced with a fallback. A missing/invalid parent origin leaves the bridge disabled.
    public static HostBridgeInterviewConversationProvider CreateProvider()
    {
        RecursorInterviewBridgeSettings settings = RecursorInterviewBridgeSettings.Load();

        bool originValid =
            settings != null &&
            RecursorInterviewHostOrigin.Validate(
                settings.AllowedHostOrigin,
                settings.AllowLoopbackDevelopmentOrigin,
                Debug.isDebugBuild) == HostOriginValidation.Valid;

        IInterviewBridgeTransport transport = originValid
            ? (IInterviewBridgeTransport)WebGLInterviewBridgeTransport.Create(settings.AllowedHostOrigin)
            : DisabledInterviewBridgeTransport.Instance;

        if (!originValid)
            Debug.LogWarning("Interview host bridge is not configured; the host-backed interview is disabled.");

        var client = new InterviewBridgeClient(
            transport,
            new InterviewBridgeIdSource(),
            Application.version,
            () => Time.realtimeSinceStartupAsDouble);

        return new HostBridgeInterviewConversationProvider(client);
    }
}
