using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// Host -> Unity: bridge.ready.
public sealed class InterviewBridgeReady
{
    public InterviewBridgeReady(string correlationId, string hostState)
    {
        CorrelationId = correlationId;
        HostState = hostState;
    }

    public string CorrelationId { get; }
    public string HostState { get; }
}

// Host -> Unity: conversation.result, after exact-key and value validation. The fields are
// exactly Stage 6A's flattened contract fields; there is no token, SessionId, or OwnerId.
public sealed class InterviewBridgeResult
{
    public string CorrelationId { get; internal set; }
    public bool Ok { get; internal set; }
    public int HttpStatus { get; internal set; }
    public string ErrorCode { get; internal set; }
    public string ConversationId { get; internal set; }
    public bool HasTurnIndex { get; internal set; }
    public int TurnIndex { get; internal set; }
    public string PromptKind { get; internal set; }
    public string QuestionRef { get; internal set; }

    // Conversation content: displayed only; never logged, persisted, or sent to telemetry.
    public string InterviewerText { get; internal set; }

    public string Action { get; internal set; }
    public string ConversationState { get; internal set; }
    public bool HasExpiry { get; internal set; }
    public long IdleRemainingMs { get; internal set; }
    public long AbsoluteRemainingMs { get; internal set; }

    public bool IsActive =>
        Ok && ConversationState == InterviewBridgeProtocol.States.Active;

    public bool IsTerminal =>
        Ok && InterviewBridgeProtocol.States.IsTerminal(ConversationState);
}

public enum InterviewBridgeInboundKind
{
    Invalid,
    Ready,
    Result,
}

public sealed class InterviewBridgeInbound
{
    public static readonly InterviewBridgeInbound Invalid =
        new InterviewBridgeInbound(InterviewBridgeInboundKind.Invalid, null, null);

    private InterviewBridgeInbound(
        InterviewBridgeInboundKind kind,
        InterviewBridgeReady ready,
        InterviewBridgeResult result)
    {
        Kind = kind;
        Ready = ready;
        Result = result;
    }

    public InterviewBridgeInboundKind Kind { get; }
    public InterviewBridgeReady Ready { get; }
    public InterviewBridgeResult Result { get; }

    internal static InterviewBridgeInbound ForReady(InterviewBridgeReady ready) =>
        new InterviewBridgeInbound(InterviewBridgeInboundKind.Ready, ready, null);

    internal static InterviewBridgeInbound ForResult(InterviewBridgeResult result) =>
        new InterviewBridgeInbound(InterviewBridgeInboundKind.Result, null, result);
}

// Builds Unity -> host envelopes with exactly the Stage 6A keys for each type, and parses
// host -> Unity envelopes, failing closed on anything that is not exactly the Stage 6A shape.
public static class InterviewBridgeMessages
{
    public static string BuildHello(string correlationId, string simVersion)
    {
        if (!InterviewBridgeProtocol.IsValidSimVersion(simVersion))
            throw new ArgumentException("The build version is not a valid bridge simVersion.", nameof(simVersion));

        return Envelope(InterviewBridgeProtocol.MessageTypes.Hello, correlationId, payload =>
        {
            payload.String("scenarioId", InterviewBridgeProtocol.ScenarioId);
            payload.String("simVersion", simVersion);
        });
    }

    public static string BuildStart(string correlationId, string requestId)
    {
        RequireRequestId(requestId);

        return Envelope(InterviewBridgeProtocol.MessageTypes.Start, correlationId, payload =>
        {
            payload.String("requestId", requestId);
        });
    }

    public static string BuildAnswer(
        string correlationId,
        string conversationId,
        string requestId,
        int turnIndex,
        string answerText)
    {
        RequireConversationId(conversationId);
        RequireRequestId(requestId);
        RequireTurnIndex(turnIndex);

        if (!InterviewBridgeProtocol.IsValidAnswerText(answerText))
            throw new ArgumentException("The answer is empty or too long.", nameof(answerText));

        return Envelope(InterviewBridgeProtocol.MessageTypes.Answer, correlationId, payload =>
        {
            payload.String("conversationId", conversationId);
            payload.String("requestId", requestId);
            payload.Integer("turnIndex", turnIndex);
            payload.String("answerText", answerText);
        });
    }

    public static string BuildClarify(
        string correlationId,
        string conversationId,
        string requestId,
        int turnIndex)
    {
        RequireConversationId(conversationId);
        RequireRequestId(requestId);
        RequireTurnIndex(turnIndex);

        // No text field: the host supplies fixed clarification wording.
        return Envelope(InterviewBridgeProtocol.MessageTypes.Clarify, correlationId, payload =>
        {
            payload.String("conversationId", conversationId);
            payload.String("requestId", requestId);
            payload.Integer("turnIndex", turnIndex);
        });
    }

    public static string BuildEnd(
        string correlationId,
        string conversationId,
        string requestId,
        string reason)
    {
        RequireConversationId(conversationId);
        RequireRequestId(requestId);

        if (reason == null || !InterviewBridgeProtocol.EndReasons.All.Contains(reason))
            throw new ArgumentException("The end reason is not recognized.", nameof(reason));

        return Envelope(InterviewBridgeProtocol.MessageTypes.End, correlationId, payload =>
        {
            payload.String("conversationId", conversationId);
            payload.String("requestId", requestId);
            payload.String("reason", reason);
        });
    }

    // Never throws. Anything but an exact Stage 6A host -> Unity envelope is Invalid.
    public static InterviewBridgeInbound Parse(string json)
    {
        if (!InterviewBridgeJson.TryParseObject(json, InterviewBridgeProtocol.MaxInboundMessageLength, out var envelope))
            return InterviewBridgeInbound.Invalid;

        if (!HasExactKeys(envelope, InterviewBridgeProtocol.EnvelopeKeys.All) ||
            !TryString(envelope, "protocol", out string protocol) ||
            protocol != InterviewBridgeProtocol.Name ||
            !TryInteger(envelope, "version", out long version) ||
            version != InterviewBridgeProtocol.Version ||
            !TryString(envelope, "type", out string type) ||
            !TryString(envelope, "correlationId", out string correlationId) ||
            !InterviewBridgeProtocol.IsValidCorrelationId(correlationId) ||
            !envelope.TryGetValue("payload", out BridgeJsonValue payloadValue) ||
            payloadValue.Kind != BridgeJsonKind.Object)
        {
            return InterviewBridgeInbound.Invalid;
        }

        var payload = payloadValue.ObjectValue;

        switch (type)
        {
            case InterviewBridgeProtocol.MessageTypes.Ready:
                return ParseReady(correlationId, payload);
            case InterviewBridgeProtocol.MessageTypes.Result:
                return ParseResult(correlationId, payload);
            default:
                // Includes Unity -> host types echoed back and every unknown type.
                return InterviewBridgeInbound.Invalid;
        }
    }

    private static InterviewBridgeInbound ParseReady(
        string correlationId,
        IReadOnlyDictionary<string, BridgeJsonValue> payload)
    {
        if (!HasExactKeys(payload, InterviewBridgeProtocol.PayloadKeys.Ready) ||
            !TryString(payload, "hostState", out string hostState) ||
            !InterviewBridgeProtocol.HostStates.All.Contains(hostState))
        {
            return InterviewBridgeInbound.Invalid;
        }

        return InterviewBridgeInbound.ForReady(new InterviewBridgeReady(correlationId, hostState));
    }

    private static InterviewBridgeInbound ParseResult(
        string correlationId,
        IReadOnlyDictionary<string, BridgeJsonValue> payload)
    {
        if (!HasExactKeys(payload, InterviewBridgeProtocol.PayloadKeys.Result) ||
            !TryBoolean(payload, "ok", out bool ok) ||
            !TryInteger(payload, "httpStatus", out long httpStatus) ||
            !TryString(payload, "errorCode", out string errorCode) ||
            !TryString(payload, "conversationId", out string conversationId) ||
            !TryBoolean(payload, "hasTurnIndex", out bool hasTurnIndex) ||
            !TryInteger(payload, "turnIndex", out long turnIndex) ||
            !TryString(payload, "promptKind", out string promptKind) ||
            !TryString(payload, "questionRef", out string questionRef) ||
            !TryString(payload, "interviewerText", out string interviewerText) ||
            !TryString(payload, "action", out string action) ||
            !TryString(payload, "conversationState", out string conversationState) ||
            !TryBoolean(payload, "hasExpiry", out bool hasExpiry) ||
            !TryInteger(payload, "idleRemainingMs", out long idleRemainingMs) ||
            !TryInteger(payload, "absoluteRemainingMs", out long absoluteRemainingMs))
        {
            return InterviewBridgeInbound.Invalid;
        }

        if (httpStatus < 0 || httpStatus > InterviewBridgeProtocol.MaxHttpStatus)
            return InterviewBridgeInbound.Invalid;

        var result = new InterviewBridgeResult
        {
            CorrelationId = correlationId,
            Ok = ok,
            HttpStatus = (int)httpStatus,
            ErrorCode = errorCode,
            ConversationId = conversationId,
            HasTurnIndex = hasTurnIndex,
            TurnIndex = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, turnIndex)),
            PromptKind = promptKind,
            QuestionRef = questionRef,
            InterviewerText = interviewerText,
            Action = action,
            ConversationState = conversationState,
            HasExpiry = hasExpiry,
            IdleRemainingMs = idleRemainingMs,
            AbsoluteRemainingMs = absoluteRemainingMs,
        };

        bool valid = ok
            ? IsValidSuccess(result, httpStatus, turnIndex)
            : IsValidError(result, turnIndex);

        return valid ? InterviewBridgeInbound.ForResult(result) : InterviewBridgeInbound.Invalid;
    }

    private static bool IsValidError(InterviewBridgeResult r, long turnIndex)
    {
        // The host's error reply carries the code and status only; every content field is empty.
        bool knownCode =
            InterviewBridgeProtocol.HostErrorCodes.All.Contains(r.ErrorCode) ||
            InterviewBridgeProtocol.ServerErrorCodes.All.Contains(r.ErrorCode);

        return knownCode &&
               r.ConversationId.Length == 0 &&
               !r.HasTurnIndex &&
               turnIndex == 0 &&
               r.PromptKind.Length == 0 &&
               r.QuestionRef.Length == 0 &&
               r.InterviewerText.Length == 0 &&
               r.Action.Length == 0 &&
               r.ConversationState.Length == 0 &&
               !r.HasExpiry &&
               r.IdleRemainingMs == 0 &&
               r.AbsoluteRemainingMs == 0;
    }

    private static bool IsValidSuccess(InterviewBridgeResult r, long httpStatus, long turnIndex)
    {
        if (httpStatus != 200 ||
            r.ErrorCode.Length != 0 ||
            !InterviewBridgeProtocol.IsValidConversationId(r.ConversationId))
        {
            return false;
        }

        if (r.ConversationState == InterviewBridgeProtocol.States.Active)
        {
            bool actionMatchesKind =
                (r.Action == InterviewBridgeProtocol.Actions.QuestionPresented &&
                 r.PromptKind == InterviewBridgeProtocol.PromptKinds.CoreQuestion) ||
                (r.Action == InterviewBridgeProtocol.Actions.FollowUpPresented &&
                 r.PromptKind == InterviewBridgeProtocol.PromptKinds.FollowUp) ||
                (r.Action == InterviewBridgeProtocol.Actions.ClarificationProvided &&
                 (r.PromptKind == InterviewBridgeProtocol.PromptKinds.CoreQuestion ||
                  r.PromptKind == InterviewBridgeProtocol.PromptKinds.FollowUp));

            return actionMatchesKind &&
                   r.HasTurnIndex &&
                   InterviewBridgeProtocol.IsValidTurnIndex(turnIndex) &&
                   r.QuestionRef.Length > 0 &&
                   r.QuestionRef.Length <= InterviewBridgeProtocol.MaxQuestionRefLength &&
                   !string.IsNullOrWhiteSpace(r.InterviewerText) &&
                   r.InterviewerText.Length <= InterviewBridgeProtocol.MaxInterviewerTextLength &&
                   r.HasExpiry &&
                   IsValidRemaining(r.IdleRemainingMs) &&
                   IsValidRemaining(r.AbsoluteRemainingMs);
        }

        if (!InterviewBridgeProtocol.States.IsTerminal(r.ConversationState) ||
            r.Action != InterviewBridgeProtocol.States.TerminalActionFor(r.ConversationState))
        {
            return false;
        }

        // A terminal response has no active prompt and no expiry (contract section 8). Only
        // natural completion may carry the fixed closing acknowledgment.
        bool textAllowed =
            r.ConversationState == InterviewBridgeProtocol.States.Completed
                ? r.InterviewerText.Length <= InterviewBridgeProtocol.MaxInterviewerTextLength
                : r.InterviewerText.Length == 0;

        return textAllowed &&
               !r.HasTurnIndex &&
               turnIndex == 0 &&
               r.PromptKind.Length == 0 &&
               r.QuestionRef.Length == 0 &&
               !r.HasExpiry &&
               r.IdleRemainingMs == 0 &&
               r.AbsoluteRemainingMs == 0;
    }

    private static bool IsValidRemaining(long value) =>
        value >= 0 && value <= InterviewBridgeProtocol.MaxRemainingMs;

    private static bool HasExactKeys(IReadOnlyDictionary<string, BridgeJsonValue> obj, IReadOnlyList<string> keys) =>
        obj.Count == keys.Count && keys.All(obj.ContainsKey);

    private static bool TryString(IReadOnlyDictionary<string, BridgeJsonValue> obj, string key, out string value)
    {
        value = null;
        if (!obj.TryGetValue(key, out BridgeJsonValue v) || v.Kind != BridgeJsonKind.String)
            return false;
        value = v.StringValue;
        return true;
    }

    private static bool TryInteger(IReadOnlyDictionary<string, BridgeJsonValue> obj, string key, out long value)
    {
        value = 0;
        if (!obj.TryGetValue(key, out BridgeJsonValue v) || v.Kind != BridgeJsonKind.Integer)
            return false;
        value = v.IntegerValue;
        return true;
    }

    private static bool TryBoolean(IReadOnlyDictionary<string, BridgeJsonValue> obj, string key, out bool value)
    {
        value = false;
        if (!obj.TryGetValue(key, out BridgeJsonValue v) || v.Kind != BridgeJsonKind.Boolean)
            return false;
        value = v.BooleanValue;
        return true;
    }

    private static void RequireConversationId(string conversationId)
    {
        if (!InterviewBridgeProtocol.IsValidConversationId(conversationId))
            throw new ArgumentException("The conversation identifier is not valid.", nameof(conversationId));
    }

    private static void RequireRequestId(string requestId)
    {
        if (!InterviewBridgeProtocol.IsValidRequestId(requestId))
            throw new ArgumentException("The request identifier is not valid.", nameof(requestId));
    }

    private static void RequireTurnIndex(int turnIndex)
    {
        if (!InterviewBridgeProtocol.IsValidTurnIndex(turnIndex))
            throw new ArgumentException("The turn index is out of range.", nameof(turnIndex));
    }

    private static string Envelope(string type, string correlationId, Action<PayloadWriter> writePayload)
    {
        if (!InterviewBridgeProtocol.IsValidCorrelationId(correlationId))
            throw new ArgumentException("The correlation identifier is not valid.", nameof(correlationId));

        var builder = new StringBuilder(256);
        builder.Append("{\"protocol\":");
        InterviewBridgeJson.AppendString(builder, InterviewBridgeProtocol.Name);
        builder.Append(",\"version\":");
        builder.Append(InterviewBridgeProtocol.Version);
        builder.Append(",\"type\":");
        InterviewBridgeJson.AppendString(builder, type);
        builder.Append(",\"correlationId\":");
        InterviewBridgeJson.AppendString(builder, correlationId);
        builder.Append(",\"payload\":{");
        writePayload(new PayloadWriter(builder));
        builder.Append("}}");
        return builder.ToString();
    }

    private sealed class PayloadWriter
    {
        private readonly StringBuilder builder;
        private bool first = true;

        public PayloadWriter(StringBuilder builder) => this.builder = builder;

        public void String(string key, string value)
        {
            Key(key);
            InterviewBridgeJson.AppendString(builder, value);
        }

        public void Integer(string key, int value)
        {
            Key(key);
            builder.Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private void Key(string key)
        {
            if (!first)
                builder.Append(',');
            first = false;
            InterviewBridgeJson.AppendString(builder, key);
            builder.Append(':');
        }
    }
}
