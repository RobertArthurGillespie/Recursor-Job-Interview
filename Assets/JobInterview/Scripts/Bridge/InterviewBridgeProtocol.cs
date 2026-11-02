using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

// Stage 6B: Unity-side mirror of the Stage 6A postMessage bridge protocol. The committed
// Stage 6A files in the Recursor repository are authoritative (Client/Recursor/Interview/
// InterviewBridgeProtocol.cs, Client/wwwroot/js/recursor/interview-bridge.js, and
// docs/contracts/job-interview-conversation-v1.md section 14.1). Every value here is copied
// from them. No message defined here has a field that could carry a token, SessionId,
// OwnerId, or telemetry UserId: the Blazor host owns all of those.
public static class InterviewBridgeProtocol
{
    public const string Name = "recursor.interview.bridge";
    public const int Version = 1;

    // The only scenario the host will start. Unity reports it in bridge.hello; it never chooses another.
    public const string ScenarioId = JobInterviewIdentifiers.MedicalSupplyTechnicianScenarioId;

    public const int MaxAnswerTextLength = 2000;
    public const int MinTurnIndex = 0;
    public const int MaxTurnIndex = 11;
    public const int MaxScenarioIdLength = 128;
    public const int MaxSimVersionLength = 64;

    // Unity-side bounds on inbound values (defensive; the host never approaches them).
    public const int MaxInboundMessageLength = 16384;
    public const int MaxInterviewerTextLength = 4000;
    public const int MaxQuestionRefLength = 128;
    public const int MaxHttpStatus = 599;
    public const long MaxRemainingMs = 86400000L;

    // Anchored with \z, not $: in .NET, $ also matches before a trailing "\n".
    private static readonly Regex CorrelationIdPattern =
        new Regex("^[A-Za-z0-9-]{8,64}\\z", RegexOptions.CultureInvariant);

    private static readonly Regex IdentifierPattern =
        new Regex("^[A-Za-z0-9_-]{1,64}\\z", RegexOptions.CultureInvariant);

    // 1..64 chars: an ASCII letter or digit first, then letters, digits, '.', '_', '+', '-'.
    private static readonly Regex SimVersionPattern =
        new Regex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}\\z", RegexOptions.CultureInvariant);

    public static bool IsValidCorrelationId(string value) =>
        value != null && CorrelationIdPattern.IsMatch(value);

    public static bool IsValidRequestId(string value) =>
        value != null && IdentifierPattern.IsMatch(value);

    public static bool IsValidConversationId(string value) =>
        value != null && IdentifierPattern.IsMatch(value);

    // Unity's Application.version, sent as bridge.hello simVersion. A build label only:
    // not ParameterContractVersion, and never identity or authorization data.
    public static bool IsValidSimVersion(string value) =>
        value != null &&
        value.Length > 0 &&
        value.Length <= MaxSimVersionLength &&
        SimVersionPattern.IsMatch(value);

    public static bool IsValidTurnIndex(long value) =>
        value >= MinTurnIndex && value <= MaxTurnIndex;

    public static bool IsValidAnswerText(string value) =>
        value != null &&
        value.Length > 0 &&
        value.Length <= MaxAnswerTextLength &&
        !string.IsNullOrWhiteSpace(value);

    public static class MessageTypes
    {
        // Unity -> host.
        public const string Hello = "bridge.hello";
        public const string Start = "conversation.start";
        public const string Answer = "conversation.answer";
        public const string Clarify = "conversation.clarify";
        public const string End = "conversation.end";

        // Unity -> host: the telemetry lane (job-interview-telemetry-v1 section 9).
        public const string TelemetryBatch = "telemetry.batch";
        public const string TelemetryFinish = "telemetry.finish";

        // Host -> Unity.
        public const string Ready = "bridge.ready";
        public const string Result = "conversation.result";
        public const string TelemetryResult = "telemetry.result";
        public const string TelemetryFinished = "telemetry.finished";
    }

    public static class EnvelopeKeys
    {
        public const string Protocol = "protocol";
        public const string Version = "version";
        public const string Type = "type";
        public const string CorrelationId = "correlationId";
        public const string Payload = "payload";

        public static readonly IReadOnlyList<string> All = new[]
        {
            Protocol, Version, Type, CorrelationId, Payload,
        };
    }

    // Exact payload keys per message type, in wire order.
    public static class PayloadKeys
    {
        public static readonly IReadOnlyList<string> Hello = new[] { "scenarioId", "simVersion" };
        public static readonly IReadOnlyList<string> Start = new[] { "requestId" };
        public static readonly IReadOnlyList<string> Answer = new[] { "conversationId", "requestId", "turnIndex", "answerText" };
        public static readonly IReadOnlyList<string> Clarify = new[] { "conversationId", "requestId", "turnIndex" };
        public static readonly IReadOnlyList<string> End = new[] { "conversationId", "requestId", "reason" };

        public static readonly IReadOnlyList<string> Ready = new[] { "hostState" };

        public static readonly IReadOnlyList<string> Result = new[]
        {
            "ok", "httpStatus", "errorCode", "conversationId", "hasTurnIndex", "turnIndex", "promptKind",
            "questionRef", "interviewerText", "action", "conversationState", "hasExpiry",
            "idleRemainingMs", "absoluteRemainingMs",
        };

        // conversationId (Recursor contract section 3.4) names the conversation whose run the message belongs to.
        // The host routes by it and never forwards it; it is never part of an event.
        public static readonly IReadOnlyList<string> TelemetryBatch = new[] { "conversationId", "batchId", "batchSequence", "clientTimestampUtc", "events" };
        public static readonly IReadOnlyList<string> TelemetryFinish = new[] { "conversationId", "lastBatchSequence" };
        public static readonly IReadOnlyList<string> TelemetryEvent = new[]
        {
            "eventId", "sequenceNumber", "timestampUtc", "eventType", "category", "actor", "target", "metrics", "payload",
        };

        public static readonly IReadOnlyList<string> TelemetryResult = new[] { "batchId", "outcome", "errorCode" };
        public static readonly IReadOnlyList<string> TelemetryFinished = new[] { "outcome", "errorCode" };
    }

    // The telemetry lane of bridge v1 (Recursor docs/contracts/job-interview-telemetry-v1.md sections 3 and 9,
    // and the host's InterviewTelemetryProtocol). Behavioral events only: no field can carry answer,
    // clarification or interviewer text, and Unity never sends SessionId, SimId, ScenarioId, UserId or Context.
    public static class Telemetry
    {
        public const int MaxEventsPerBatch = 200;
        public const int MaxBatchSequence = 100000;
        public const long MaxEventSequence = 1000000000L;
        public const int MaxTimingMs = 600000;

        // Host-produced codes are short snake_case identifiers; anything else is dropped.
        private static readonly Regex ErrorCodePattern =
            new Regex("^[a-z][a-z0-9_]{0,63}\\z", RegexOptions.CultureInvariant);

        // eventId is wider than a bridge identifier (host: ^[A-Za-z0-9_-]{1,128}$).
        private static readonly Regex EventIdPattern =
            new Regex("^[A-Za-z0-9_-]{1,128}\\z", RegexOptions.CultureInvariant);

        public static bool IsValidBatchId(string value) => value != null && IdentifierPattern.IsMatch(value);

        public static bool IsValidEventId(string value) => value != null && EventIdPattern.IsMatch(value);

        public static bool IsValidErrorCode(string value) => value != null && (value.Length == 0 || ErrorCodePattern.IsMatch(value));

        // telemetry.result outcomes.
        public static class Outcomes
        {
            public const string Succeeded = "succeeded";
            public const string Rejected = "rejected";
            public const string Forbidden = "forbidden";
            public const string AuthenticationRequired = "authentication_required";
            public const string TransportFailure = "transport_failure";
            public const string Busy = "busy";
            public const string NotPermitted = "not_permitted";

            public static readonly IReadOnlyCollection<string> All = Set(
                Succeeded, Rejected, Forbidden, AuthenticationRequired, TransportFailure, Busy, NotPermitted);
        }

        // telemetry.finished outcomes. Only Ended means every batch was acknowledged and the end succeeded;
        // it never means the rows are stored or queryable in ADX.
        public static class FinishOutcomes
        {
            public const string Ended = "ended";
            public const string Incomplete = "incomplete";
            public const string Busy = "busy";
            public const string NotPermitted = "not_permitted";

            public static readonly IReadOnlyCollection<string> All = Set(Ended, Incomplete, Busy, NotPermitted);
        }

        // Host codes that make an incomplete finish final: retrying the same finish cannot succeed.
        public static class Codes
        {
            public const string BatchNotAcknowledged = "bridge_batch_not_acknowledged";
            public const string BatchRejected = "bridge_batch_rejected";
        }

        // The Checkpoint 5 vocabulary as Unity emits it (contract section 2). Unity never sends
        // interview_response_abandoned or interview_interruption_recorded and never infers abandonment.
        public static class EventTypes
        {
            public const string TurnPresented = "interview_turn_presented";
            public const string ResponseStarted = "interview_response_started";
            public const string ResponseSubmitted = "interview_response_submitted";
            public const string ClarificationRequested = "interview_clarification_requested";
            public const string TurnCompleted = "interview_turn_completed";
        }

        public static class Categories
        {
            public const string TaskAction = "task_action";
            public const string System = "system";
        }

        public static class Actors
        {
            public const string Applicant = "applicant";
            public const string System = "system";
        }

        public static class Targets
        {
            public const string Turn = "turn";
            public const string Response = "response";
        }

        public static class MetricKeys
        {
            public const string TurnIndex = "turnIndex";
            public const string ResponseLatencyMs = "responseLatencyMs";
            public const string ResponseDurationMs = "responseDurationMs";
        }

        public static class PayloadKeys
        {
            public const string ResponseLengthBand = "responseLengthBand";
            public const string TopicCompleted = "topicCompleted";
            public const string CompletionStatus = "completionStatus";
        }

        public static class LengthBands
        {
            public const string VeryShort = "very-short";
            public const string Short = "short";
            public const string Medium = "medium";
            public const string Long = "long";
        }

        // P3: only "completed", and only on the last turn of a genuinely completed interview.
        public const string CompletionStatusCompleted = "completed";
    }

    public static class HostStates
    {
        public const string Ready = "ready";
        public const string SignedOut = "signed_out";
        public const string ScenarioMismatch = "scenario_mismatch";
        public const string VersionMismatch = "version_mismatch";

        public static readonly IReadOnlyCollection<string> All = Set(Ready, SignedOut, ScenarioMismatch, VersionMismatch);
    }

    // Host-produced result codes. Every value is fixed text.
    public static class HostErrorCodes
    {
        public const string TransportFailure = "bridge_transport_failure";
        public const string AuthenticationRequired = "bridge_authentication_required";
        public const string Busy = "bridge_busy";
        public const string ConversationMismatch = "bridge_conversation_mismatch";
        public const string InvalidMessage = "bridge_invalid_message";
        public const string NotReady = "bridge_not_ready";
        public const string SessionStartFailed = "bridge_session_start_failed";
        public const string Superseded = "bridge_superseded";

        public static readonly IReadOnlyCollection<string> All = Set(
            TransportFailure, AuthenticationRequired, Busy, ConversationMismatch,
            InvalidMessage, NotReady, SessionStartFailed, Superseded);
    }

    // The server's contract section 20 codes the host forwards verbatim (code only, never text).
    public static class ServerErrorCodes
    {
        public const string InvalidRequest = "interview_conversation_invalid_request";
        public const string NotFoundOrForbidden = "interview_conversation_not_found_or_forbidden";
        public const string ScenarioNotAllowed = "interview_conversation_scenario_not_allowed";
        public const string ScenarioMismatch = "interview_conversation_scenario_mismatch";
        public const string SessionNotEligible = "interview_conversation_session_not_eligible";
        public const string TurnMismatch = "interview_conversation_turn_mismatch";
        public const string AnswerTooLong = "interview_conversation_answer_too_long";
        public const string ClarificationTooLong = "interview_conversation_clarification_too_long";
        public const string InvalidEndReason = "interview_conversation_invalid_end_reason";
        public const string PayloadTooLarge = "interview_conversation_payload_too_large";
        public const string AlreadyActive = "interview_conversation_already_active";
        public const string AlreadyTerminal = "interview_conversation_already_terminal";
        public const string RequestConflict = "interview_conversation_request_conflict";
        public const string OperationInProgress = "interview_conversation_operation_in_progress";
        public const string OperationSuperseded = "interview_conversation_operation_superseded";
        public const string RequestBudgetExceeded = "interview_conversation_request_budget_exceeded";
        public const string InternalError = "interview_conversation_internal_error";

        public static readonly IReadOnlyCollection<string> All = Set(
            InvalidRequest, NotFoundOrForbidden, ScenarioNotAllowed, ScenarioMismatch,
            SessionNotEligible, TurnMismatch, AnswerTooLong, ClarificationTooLong,
            InvalidEndReason, PayloadTooLarge, AlreadyActive, AlreadyTerminal,
            RequestConflict, OperationInProgress, OperationSuperseded,
            RequestBudgetExceeded, InternalError);
    }

    public static class Actions
    {
        public const string QuestionPresented = "question_presented";
        public const string FollowUpPresented = "follow_up_presented";
        public const string ClarificationProvided = "clarification_provided";
        public const string InterviewCompleted = "interview_completed";
        public const string InterviewAbandoned = "interview_abandoned";
        public const string InterviewExpired = "interview_expired";
    }

    public static class States
    {
        public const string Active = "active";
        public const string Completed = "completed";
        public const string Abandoned = "abandoned";
        public const string Expired = "expired";

        public static bool IsTerminal(string state) =>
            state == Completed || state == Abandoned || state == Expired;

        // Deterministic terminal action per state (contract sections 4.4 and 8).
        public static string TerminalActionFor(string state)
        {
            switch (state)
            {
                case Completed: return Actions.InterviewCompleted;
                case Abandoned: return Actions.InterviewAbandoned;
                case Expired: return Actions.InterviewExpired;
                default: return null;
            }
        }
    }

    public static class PromptKinds
    {
        public const string CoreQuestion = "core_question";
        public const string FollowUp = "follow_up";
    }

    public static class EndReasons
    {
        public const string UserEnded = "user_ended";
        public const string ClientError = "client_error";
        public const string RestartRequested = "restart_requested";

        public static readonly IReadOnlyCollection<string> All = Set(UserEnded, ClientError, RestartRequested);
    }

    private static IReadOnlyCollection<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);
}
