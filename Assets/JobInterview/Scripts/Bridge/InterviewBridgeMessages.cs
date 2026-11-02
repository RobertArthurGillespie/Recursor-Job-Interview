using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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

// One behavioral event of a telemetry.batch, exactly as it goes on the wire (contract section 2). Built once
// when the event is recorded and never changed, so every resend of a batch carries identical content.
public sealed class InterviewTelemetryWireEvent
{
    public InterviewTelemetryWireEvent(
        string eventId, long sequenceNumber, string timestampUtc, string eventType, string category, string actor,
        string target, IReadOnlyList<KeyValuePair<string, long>> metrics, IReadOnlyList<KeyValuePair<string, object>> payload)
    {
        EventId = eventId;
        SequenceNumber = sequenceNumber;
        TimestampUtc = timestampUtc;
        EventType = eventType;
        Category = category;
        Actor = actor;
        Target = target;
        Metrics = metrics ?? Array.Empty<KeyValuePair<string, long>>();
        Payload = payload ?? Array.Empty<KeyValuePair<string, object>>();
    }

    public string EventId { get; }
    public long SequenceNumber { get; }
    public string TimestampUtc { get; }
    public string EventType { get; }
    public string Category { get; }
    public string Actor { get; }
    public string Target { get; }

    // Whole numbers only (turn index, whole milliseconds), in wire order.
    public IReadOnlyList<KeyValuePair<string, long>> Metrics { get; }

    // Each value is an enum-member string or a bool, in wire order. Never free text.
    public IReadOnlyList<KeyValuePair<string, object>> Payload { get; }
}

// One telemetry.batch. Immutable once cut: a resend reuses the same ConversationId, BatchId, BatchSequence and
// events. ConversationId is the run's own conversation (Recursor contract section 3.4), for host routing only.
public sealed class InterviewTelemetryBatch
{
    public InterviewTelemetryBatch(string conversationId, string batchId, int batchSequence, string clientTimestampUtc, IReadOnlyList<InterviewTelemetryWireEvent> events)
    {
        ConversationId = conversationId;
        BatchId = batchId;
        BatchSequence = batchSequence;
        ClientTimestampUtc = clientTimestampUtc;
        Events = events ?? Array.Empty<InterviewTelemetryWireEvent>();
    }

    public string ConversationId { get; }
    public string BatchId { get; }
    public int BatchSequence { get; }
    public string ClientTimestampUtc { get; }
    public IReadOnlyList<InterviewTelemetryWireEvent> Events { get; }
}

// Host -> Unity: telemetry.result (BatchId set) or telemetry.finished (BatchId empty), after exact-key and
// value validation. Outcome and ErrorCode are fixed codes, never free text.
public sealed class InterviewTelemetryReply
{
    public InterviewTelemetryReply(string correlationId, string batchId, string outcome, string errorCode)
    {
        CorrelationId = correlationId;
        BatchId = batchId;
        Outcome = outcome;
        ErrorCode = errorCode;
    }

    public string CorrelationId { get; }
    public string BatchId { get; }
    public string Outcome { get; }
    public string ErrorCode { get; }
}

public enum InterviewBridgeInboundKind
{
    Invalid,
    Ready,
    Result,
    TelemetryResult,
    TelemetryFinished,
}

public sealed class InterviewBridgeInbound
{
    public static readonly InterviewBridgeInbound Invalid =
        new InterviewBridgeInbound(InterviewBridgeInboundKind.Invalid, null, null);

    private InterviewBridgeInbound(
        InterviewBridgeInboundKind kind,
        InterviewBridgeReady ready,
        InterviewBridgeResult result,
        InterviewTelemetryReply telemetry = null)
    {
        Kind = kind;
        Ready = ready;
        Result = result;
        Telemetry = telemetry;
    }

    public InterviewBridgeInboundKind Kind { get; }
    public InterviewBridgeReady Ready { get; }
    public InterviewBridgeResult Result { get; }
    public InterviewTelemetryReply Telemetry { get; }

    internal static InterviewBridgeInbound ForReady(InterviewBridgeReady ready) =>
        new InterviewBridgeInbound(InterviewBridgeInboundKind.Ready, ready, null);

    internal static InterviewBridgeInbound ForResult(InterviewBridgeResult result) =>
        new InterviewBridgeInbound(InterviewBridgeInboundKind.Result, null, result);

    internal static InterviewBridgeInbound ForTelemetry(InterviewBridgeInboundKind kind, InterviewTelemetryReply reply) =>
        new InterviewBridgeInbound(kind, null, null, reply);
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

    // telemetry.batch: exactly {batchId, batchSequence, clientTimestampUtc, events}, each event exactly the nine
    // contract keys. Every value is checked against the host's limits so nothing the host would refuse is posted.
    // The payload depends only on the batch, so a resend is byte-identical apart from its correlation id.
    public static string BuildTelemetryBatch(string correlationId, InterviewTelemetryBatch batch)
    {
        if (batch == null)
            throw new ArgumentNullException(nameof(batch));
        RequireConversationId(batch.ConversationId);
        if (!InterviewBridgeProtocol.Telemetry.IsValidBatchId(batch.BatchId))
            throw new ArgumentException("The batch identifier is not valid.", nameof(batch));
        if (batch.BatchSequence < 0 || batch.BatchSequence > InterviewBridgeProtocol.Telemetry.MaxBatchSequence)
            throw new ArgumentException("The batch sequence is out of range.", nameof(batch));
        if (!IsTelemetryTimestamp(batch.ClientTimestampUtc))
            throw new ArgumentException("The batch timestamp is not valid.", nameof(batch));
        if (batch.Events.Count < 1 || batch.Events.Count > InterviewBridgeProtocol.Telemetry.MaxEventsPerBatch)
            throw new ArgumentException("The batch has no events or too many.", nameof(batch));

        var events = new StringBuilder(256 * batch.Events.Count);
        events.Append('[');
        for (int i = 0; i < batch.Events.Count; i++)
        {
            if (i > 0)
                events.Append(',');
            AppendTelemetryEvent(events, batch.Events[i]);
        }
        events.Append(']');

        return Envelope(InterviewBridgeProtocol.MessageTypes.TelemetryBatch, correlationId, payload =>
        {
            payload.String("conversationId", batch.ConversationId);
            payload.String("batchId", batch.BatchId);
            payload.Long("batchSequence", batch.BatchSequence);
            payload.String("clientTimestampUtc", batch.ClientTimestampUtc);
            payload.Raw("events", events.ToString());
        });
    }

    // telemetry.finish: the run's own conversation, and the last BatchSequence Unity sent or -1 when it sent none.
    public static string BuildTelemetryFinish(string correlationId, string conversationId, int lastBatchSequence)
    {
        RequireConversationId(conversationId);
        if (lastBatchSequence < -1 || lastBatchSequence > InterviewBridgeProtocol.Telemetry.MaxBatchSequence)
            throw new ArgumentException("The last batch sequence is out of range.", nameof(lastBatchSequence));

        return Envelope(InterviewBridgeProtocol.MessageTypes.TelemetryFinish, correlationId, payload =>
        {
            payload.String("conversationId", conversationId);
            payload.Long("lastBatchSequence", lastBatchSequence);
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
            case InterviewBridgeProtocol.MessageTypes.TelemetryResult:
                return ParseTelemetryResult(correlationId, payload);
            case InterviewBridgeProtocol.MessageTypes.TelemetryFinished:
                return ParseTelemetryFinished(correlationId, payload);
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

    private static InterviewBridgeInbound ParseTelemetryResult(
        string correlationId,
        IReadOnlyDictionary<string, BridgeJsonValue> payload)
    {
        // The host echoes an empty batchId only when the batch it received was not valid.
        if (!HasExactKeys(payload, InterviewBridgeProtocol.PayloadKeys.TelemetryResult) ||
            !TryString(payload, "batchId", out string batchId) ||
            (batchId.Length != 0 && !InterviewBridgeProtocol.Telemetry.IsValidBatchId(batchId)) ||
            !TryString(payload, "outcome", out string outcome) ||
            !InterviewBridgeProtocol.Telemetry.Outcomes.All.Contains(outcome) ||
            !TryString(payload, "errorCode", out string errorCode) ||
            !InterviewBridgeProtocol.Telemetry.IsValidErrorCode(errorCode))
        {
            return InterviewBridgeInbound.Invalid;
        }

        return InterviewBridgeInbound.ForTelemetry(
            InterviewBridgeInboundKind.TelemetryResult,
            new InterviewTelemetryReply(correlationId, batchId, outcome, errorCode));
    }

    private static InterviewBridgeInbound ParseTelemetryFinished(
        string correlationId,
        IReadOnlyDictionary<string, BridgeJsonValue> payload)
    {
        if (!HasExactKeys(payload, InterviewBridgeProtocol.PayloadKeys.TelemetryFinished) ||
            !TryString(payload, "outcome", out string outcome) ||
            !InterviewBridgeProtocol.Telemetry.FinishOutcomes.All.Contains(outcome) ||
            !TryString(payload, "errorCode", out string errorCode) ||
            !InterviewBridgeProtocol.Telemetry.IsValidErrorCode(errorCode))
        {
            return InterviewBridgeInbound.Invalid;
        }

        return InterviewBridgeInbound.ForTelemetry(
            InterviewBridgeInboundKind.TelemetryFinished,
            new InterviewTelemetryReply(correlationId, string.Empty, outcome, errorCode));
    }

    private static void AppendTelemetryEvent(StringBuilder builder, InterviewTelemetryWireEvent e)
    {
        if (e == null ||
            !InterviewBridgeProtocol.Telemetry.IsValidEventId(e.EventId) ||
            e.SequenceNumber < 0 || e.SequenceNumber > InterviewBridgeProtocol.Telemetry.MaxEventSequence ||
            !IsTelemetryTimestamp(e.TimestampUtc) ||
            !IsTelemetryToken(e.EventType) || !IsTelemetryToken(e.Category) || !IsTelemetryToken(e.Actor) ||
            (e.Target.Length != 0 && !IsTelemetryToken(e.Target)) ||
            e.Metrics.Count > 8 || e.Payload.Count > 8)
        {
            throw new ArgumentException("A telemetry event is not valid.");
        }

        builder.Append('{');
        var fields = new PayloadWriter(builder);
        fields.String("eventId", e.EventId);
        fields.Long("sequenceNumber", e.SequenceNumber);
        fields.String("timestampUtc", e.TimestampUtc);
        fields.String("eventType", e.EventType);
        fields.String("category", e.Category);
        fields.String("actor", e.Actor);
        fields.String("target", e.Target);

        var metrics = new StringBuilder("{");
        var metricWriter = new PayloadWriter(metrics);
        foreach (KeyValuePair<string, long> metric in e.Metrics)
        {
            if (!IsTelemetryKey(metric.Key) || metric.Value < 0)
                throw new ArgumentException("A telemetry metric is not valid.");
            metricWriter.Long(metric.Key, metric.Value);
        }
        metrics.Append('}');
        fields.Raw("metrics", metrics.ToString());

        var payload = new StringBuilder("{");
        var payloadWriter = new PayloadWriter(payload);
        foreach (KeyValuePair<string, object> item in e.Payload)
        {
            if (!IsTelemetryKey(item.Key))
                throw new ArgumentException("A telemetry payload key is not valid.");

            if (item.Value is bool flag)
                payloadWriter.Boolean(item.Key, flag);
            else if (item.Value is string member && TelemetryEnumMember.IsMatch(member))
                payloadWriter.String(item.Key, member);
            else
                throw new ArgumentException("A telemetry payload value is not an enum member or a boolean.");
        }
        payload.Append('}');
        fields.Raw("payload", payload.ToString());
        builder.Append('}');
    }

    // The host bridge's structural checks (interview-bridge.js), mirrored so an invalid batch is never posted.
    private static readonly Regex TelemetryTokenPattern =
        new Regex("^[a-z][a-z_]{0,63}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex TelemetryKeyPattern =
        new Regex("^[A-Za-z]{1,32}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex TelemetryEnumMember =
        new Regex("^[a-z][a-z_-]{0,31}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex TelemetryTimestampPattern =
        new Regex("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(\\.\\d{1,7})?Z\\z", RegexOptions.CultureInvariant);

    private static bool IsTelemetryToken(string value) => value != null && TelemetryTokenPattern.IsMatch(value);

    private static bool IsTelemetryKey(string value) => value != null && TelemetryKeyPattern.IsMatch(value);

    private static bool IsTelemetryTimestamp(string value) =>
        value != null && value.Length <= 40 && TelemetryTimestampPattern.IsMatch(value);

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

        public void Long(string key, long value)
        {
            Key(key);
            builder.Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public void Boolean(string key, bool value)
        {
            Key(key);
            builder.Append(value ? "true" : "false");
        }

        // An already serialized JSON value (an array or object built by this class).
        public void Raw(string key, string json)
        {
            Key(key);
            builder.Append(json);
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
