using System;
using System.Collections.Generic;
using System.Text;

// Test doubles and exact Stage 6A host reply builders for the Stage 6B bridge tests.
internal sealed class FakeBridgeTransport : IInterviewBridgeTransport
{
    public FakeBridgeTransport(bool enabled = true) => IsEnabled = enabled;

    public bool IsEnabled { get; set; }
    public bool PostSucceeds { get; set; } = true;
    public bool Disposed { get; private set; }
    public List<string> Posted { get; } = new List<string>();

    public event Action<string> Received;

    public bool Post(string envelopeJson)
    {
        if (!IsEnabled)
            return false;

        Posted.Add(envelopeJson);
        return PostSucceeds;
    }

    public void Deliver(string json) => Received?.Invoke(json);

    public string Last => Posted.Count > 0 ? Posted[Posted.Count - 1] : null;
}

internal sealed class SequentialBridgeIds : IInterviewBridgeIdSource
{
    private int correlation;
    private int request;

    public List<string> IssuedCorrelations { get; } = new List<string>();
    public List<string> IssuedRequests { get; } = new List<string>();

    public string NewCorrelationId()
    {
        string id = $"corr-{++correlation:D4}";
        IssuedCorrelations.Add(id);
        return id;
    }

    public string NewRequestId()
    {
        string id = $"req-{++request:D4}";
        IssuedRequests.Add(id);
        return id;
    }
}

internal sealed class ManualBridgeClock
{
    public double Now { get; set; }

    public double Read() => Now;
}

// A decoded Unity -> host envelope, for assertions.
internal sealed class PostedMessage
{
    public PostedMessage(string json)
    {
        if (!InterviewBridgeJson.TryParseObject(json, 1 << 20, out var envelope))
            throw new InvalidOperationException("Posted message is not a JSON object.");

        Envelope = envelope;
        Payload = envelope["payload"].ObjectValue;
    }

    public IReadOnlyDictionary<string, BridgeJsonValue> Envelope { get; }
    public IReadOnlyDictionary<string, BridgeJsonValue> Payload { get; }

    public string Type => Envelope["type"].StringValue;
    public string CorrelationId => Envelope["correlationId"].StringValue;
    public string RequestId => Payload.TryGetValue("requestId", out var v) ? v.StringValue : null;

    public string Str(string key) => Payload[key].StringValue;
    public long Int(string key) => Payload[key].IntegerValue;
}

internal static class HostReply
{
    public const string ConversationId = "3f1c9a7d4e2b4c6f8a9d1e2f3a4b5c6d";
    public const string InterviewerText = "INTERVIEWER-TEXT-SENTINEL Tell me about inventory tracking.";

    public static string Ready(string correlationId, string hostState = "ready") =>
        Envelope("bridge.ready", correlationId, "{\"hostState\":" + Q(hostState) + "}");

    public static string Active(
        string correlationId,
        int turnIndex,
        string action = "question_presented",
        string promptKind = "core_question",
        string conversationId = ConversationId,
        string interviewerText = InterviewerText,
        string questionRef = "mst-q1") =>
        Result(correlationId, ok: true, httpStatus: 200, errorCode: "", conversationId: conversationId,
            hasTurnIndex: true, turnIndex: turnIndex, promptKind: promptKind, questionRef: questionRef,
            interviewerText: interviewerText, action: action, conversationState: "active",
            hasExpiry: true, idleMs: 720000, absoluteMs: 1800000);

    public static string Terminal(
        string correlationId,
        string state,
        string interviewerText = "",
        string conversationId = ConversationId) =>
        Result(correlationId, ok: true, httpStatus: 200, errorCode: "", conversationId: conversationId,
            hasTurnIndex: false, turnIndex: 0, promptKind: "", questionRef: "",
            interviewerText: interviewerText, action: InterviewBridgeProtocol.States.TerminalActionFor(state),
            conversationState: state, hasExpiry: false, idleMs: 0, absoluteMs: 0);

    public static string Error(string correlationId, string errorCode, int httpStatus = 0) =>
        Result(correlationId, ok: false, httpStatus: httpStatus, errorCode: errorCode, conversationId: "",
            hasTurnIndex: false, turnIndex: 0, promptKind: "", questionRef: "", interviewerText: "",
            action: "", conversationState: "", hasExpiry: false, idleMs: 0, absoluteMs: 0);

    public static string Result(
        string correlationId, bool ok, int httpStatus, string errorCode, string conversationId,
        bool hasTurnIndex, int turnIndex, string promptKind, string questionRef, string interviewerText,
        string action, string conversationState, bool hasExpiry, long idleMs, long absoluteMs)
    {
        var p = new StringBuilder();
        p.Append("{\"ok\":").Append(ok ? "true" : "false");
        p.Append(",\"httpStatus\":").Append(httpStatus);
        p.Append(",\"errorCode\":").Append(Q(errorCode));
        p.Append(",\"conversationId\":").Append(Q(conversationId));
        p.Append(",\"hasTurnIndex\":").Append(hasTurnIndex ? "true" : "false");
        p.Append(",\"turnIndex\":").Append(turnIndex);
        p.Append(",\"promptKind\":").Append(Q(promptKind));
        p.Append(",\"questionRef\":").Append(Q(questionRef));
        p.Append(",\"interviewerText\":").Append(Q(interviewerText));
        p.Append(",\"action\":").Append(Q(action));
        p.Append(",\"conversationState\":").Append(Q(conversationState));
        p.Append(",\"hasExpiry\":").Append(hasExpiry ? "true" : "false");
        p.Append(",\"idleRemainingMs\":").Append(idleMs);
        p.Append(",\"absoluteRemainingMs\":").Append(absoluteMs);
        p.Append('}');
        return Envelope("conversation.result", correlationId, p.ToString());
    }

    public static string Envelope(string type, string correlationId, string payloadJson) =>
        "{\"protocol\":\"recursor.interview.bridge\",\"version\":1,\"type\":" + Q(type) +
        ",\"correlationId\":" + Q(correlationId) + ",\"payload\":" + payloadJson + "}";

    public static string Q(string value)
    {
        var builder = new StringBuilder();
        InterviewBridgeJson.AppendString(builder, value);
        return builder.ToString();
    }
}

// A client already handshaken (Ready) with helpers for common flows.
internal sealed class BridgeHarness
{
    public const string BuildVersion = "7.3.1-rc_2+webgl.415";

    public BridgeHarness(string simVersion = BuildVersion, bool transportEnabled = true)
    {
        Transport = new FakeBridgeTransport(transportEnabled);
        Client = new InterviewBridgeClient(Transport, Ids, simVersion, Clock.Read);
    }

    public FakeBridgeTransport Transport { get; }
    public SequentialBridgeIds Ids { get; } = new SequentialBridgeIds();
    public ManualBridgeClock Clock { get; } = new ManualBridgeClock();
    public InterviewBridgeClient Client { get; }

    public PostedMessage LastPosted => new PostedMessage(Transport.Last);

    public BridgeHarness Handshake()
    {
        Client.Connect();
        Transport.Deliver(HostReply.Ready(LastPosted.CorrelationId));
        return this;
    }

    // hello + start -> active conversation at turn 0.
    public BridgeHarness Started()
    {
        Handshake();
        var start = Client.StartAsync();
        Transport.Deliver(HostReply.Active(LastPosted.CorrelationId, 0));
        if (!start.IsCompleted || start.Result.Status != InterviewOperationStatus.Succeeded)
            throw new InvalidOperationException("Harness start did not succeed.");
        return this;
    }
}
