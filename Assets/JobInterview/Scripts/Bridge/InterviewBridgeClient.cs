using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public enum InterviewBridgeStatus
{
    // Not connected yet.
    Idle,

    // No validated parent Blazor origin, or Application.version is not a valid simVersion. Nothing is sent.
    Disabled,

    // bridge.hello sent; waiting for bridge.ready.
    Connecting,

    // bridge.hello was retried the maximum number of times without a reply.
    Unresponsive,

    Ready,

    // The host has no usable sign-in (bridge.ready signed_out, or an authentication-required result).
    SignedOut,

    ScenarioMismatch,
    VersionMismatch,
}

public enum InterviewBridgeDisabledReason
{
    None,
    TransportUnavailable,
    InvalidSimVersion,
}

public enum InterviewOperationKind
{
    Start,
    Answer,
    Clarify,
    End,
}

public enum InterviewOperationStatus
{
    Succeeded,

    // An explicit end (or server-side end/expiry) made this operation irrelevant.
    Superseded,

    AuthenticationRequired,

    // Nothing was sent: the bridge was not ready.
    NotReady,

    // Nothing was sent: another operation is outstanding.
    Busy,

    // The addressed conversation no longer accepts operations.
    ConversationEnded,

    // A definitive rejection. ErrorCode holds the sanitized code; never retried.
    Rejected,

    // Automatic retries were exhausted. The RequestId is retained; a new one is never minted
    // to recover this operation.
    OutcomeUnknown,

    // Local reset or disposal.
    Cancelled,
}

public sealed class InterviewOperationOutcome
{
    public InterviewOperationOutcome(
        InterviewOperationStatus status,
        string errorCode,
        InterviewBridgeResult result)
    {
        Status = status;
        ErrorCode = errorCode ?? string.Empty;
        Result = result;
    }

    public InterviewOperationStatus Status { get; }

    // A fixed protocol code (never free text), or empty.
    public string ErrorCode { get; }

    // Present only for Succeeded.
    public InterviewBridgeResult Result { get; }
}

// Stage 6B Unity side of the Stage 6A bridge. Owns the handshake, correlation, lanes, retries,
// conversation tracking, and fail-closed handling of stale, duplicate, and malformed replies.
// It never sees or stores a token, SessionId, OwnerId, or telemetry UserId, and never logs.
//
// Lanes mirror the host: start/answer/clarify share one single-flight lane; end has its own and
// may supersede an outstanding answer/clarify. Every transport attempt has a fresh correlation
// id; every logical operation has one RequestId, reused unchanged by its retries.
public sealed class InterviewBridgeClient : IDisposable
{
    // Longer than the host's 60-second HTTP timeout, so the host lane is free before a retry.
    public const double ReplyTimeoutSeconds = 75.0;

    // The original attempt plus at most two automatic retries.
    public const int MaxTransportAttempts = 3;

    public static readonly IReadOnlyList<double> RetryDelaySeconds = new[] { 2.0, 5.0 };

    public const int MaxHelloAttempts = 30;
    private const double HelloInitialIntervalSeconds = 1.0;
    private const double HelloMaxIntervalSeconds = 5.0;

    private readonly IInterviewBridgeTransport transport;
    private readonly IInterviewBridgeIdSource ids;
    private readonly string simVersion;
    private readonly Func<double> clock;

    private readonly HashSet<string> helloCorrelations = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, Operation> inFlight = new Dictionary<string, Operation>(StringComparer.Ordinal);

    private int helloAttempts;
    private double nextHelloAt;

    private Operation ordinary;
    private Operation end;
    private Operation unresolvedStart;
    private Operation unresolvedEnd;
    private bool unresolvedOrdinary;

    private string activeConversationId;
    private string bestEffortEndSentFor;
    private int currentTurnIndex = -1;
    private double? idleDeadline;
    private double? absoluteDeadline;
    private bool disposed;

    public InterviewBridgeClient(
        IInterviewBridgeTransport transport,
        IInterviewBridgeIdSource ids,
        string simVersion,
        Func<double> clock)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.ids = ids ?? throw new ArgumentNullException(nameof(ids));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.simVersion = simVersion;

        // Validated once here; there is no fallback value.
        if (!InterviewBridgeProtocol.IsValidSimVersion(simVersion))
        {
            Status = InterviewBridgeStatus.Disabled;
            DisabledReason = InterviewBridgeDisabledReason.InvalidSimVersion;
        }
        else if (!transport.IsEnabled)
        {
            Status = InterviewBridgeStatus.Disabled;
            DisabledReason = InterviewBridgeDisabledReason.TransportUnavailable;
        }

        transport.Received += Receive;
    }

    public event Action Changed;

    public InterviewBridgeStatus Status { get; private set; } = InterviewBridgeStatus.Idle;
    public InterviewBridgeDisabledReason DisabledReason { get; private set; }

    // Diagnostics only: counts, never content.
    public int IgnoredMessageCount { get; private set; }

    public string ActiveConversationId => activeConversationId;
    public bool HasActiveConversation => activeConversationId != null;
    public int CurrentTurnIndex => currentTurnIndex;
    public bool IsOrdinaryPending => ordinary != null;
    public bool IsEndPending => end != null;
    public bool HasUnresolvedOrdinaryOperation => unresolvedOrdinary;
    public bool HasUnresolvedStart => unresolvedStart != null;
    public bool HasUnresolvedEnd => unresolvedEnd != null;

    public bool TryGetRemainingSeconds(out double idleSeconds, out double absoluteSeconds)
    {
        idleSeconds = absoluteSeconds = 0;
        if (activeConversationId == null || !idleDeadline.HasValue || !absoluteDeadline.HasValue)
            return false;

        double now = clock();
        idleSeconds = Math.Max(0, idleDeadline.Value - now);
        absoluteSeconds = Math.Max(0, absoluteDeadline.Value - now);
        return true;
    }

    public void Connect()
    {
        if (disposed ||
            Status == InterviewBridgeStatus.Disabled ||
            Status == InterviewBridgeStatus.Ready ||
            Status == InterviewBridgeStatus.Connecting)
        {
            return;
        }

        helloAttempts = 0;
        SetStatus(InterviewBridgeStatus.Connecting);
        SendHello();
    }

    // Drives hello retries, reply timeouts, and scheduled retries. Call once per frame.
    public void Tick()
    {
        if (disposed)
            return;

        double now = clock();

        if (Status == InterviewBridgeStatus.Connecting && now >= nextHelloAt)
        {
            if (helloAttempts >= MaxHelloAttempts)
                SetStatus(InterviewBridgeStatus.Unresponsive);
            else
                SendHello();
        }

        foreach (Operation op in inFlight.Values.Where(o => now >= o.Deadline).ToList())
        {
            // A reply that arrives later carries a retired correlation id and is ignored.
            inFlight.Remove(op.Correlation);
            op.Correlation = null;
            RetryOrGiveUp(op);
        }

        foreach (Operation op in new[] { ordinary, end })
        {
            if (op != null && op.RetryAt.HasValue && now >= op.RetryAt.Value)
            {
                op.RetryAt = null;
                Send(op);
            }
        }
    }

    public Task<InterviewOperationOutcome> StartAsync()
    {
        if (disposed)
            return Done(InterviewOperationStatus.Cancelled);
        if (Status != InterviewBridgeStatus.Ready)
            return Done(InterviewOperationStatus.NotReady);
        if (ordinary != null || end != null)
            return Done(InterviewOperationStatus.Busy);
        if (activeConversationId != null)
            return Done(InterviewOperationStatus.Rejected, InterviewBridgeProtocol.ServerErrorCodes.AlreadyActive);

        // A start whose outcome is unknown is re-sent with its original RequestId, so an
        // explicit new attempt replays the original conversation instead of creating another.
        Operation op = unresolvedStart ?? new Operation(InterviewOperationKind.Start, ids.NewRequestId());
        unresolvedStart = null;
        op.Renew();

        ordinary = op;
        Send(op);
        return op.Completion.Task;
    }

    public Task<InterviewOperationOutcome> AnswerAsync(int turnIndex, string answerText)
    {
        Task<InterviewOperationOutcome> rejection = CheckOrdinaryAdmission(turnIndex);
        if (rejection != null)
            return rejection;

        if (!InterviewBridgeProtocol.IsValidAnswerText(answerText))
        {
            return Done(
                InterviewOperationStatus.Rejected,
                answerText != null && answerText.Length > InterviewBridgeProtocol.MaxAnswerTextLength
                    ? InterviewBridgeProtocol.ServerErrorCodes.AnswerTooLong
                    : InterviewBridgeProtocol.HostErrorCodes.InvalidMessage);
        }

        var op = new Operation(InterviewOperationKind.Answer, ids.NewRequestId())
        {
            ConversationId = activeConversationId,
            TurnIndex = turnIndex,
            AnswerText = answerText,
        };

        ordinary = op;
        Send(op);
        return op.Completion.Task;
    }

    public Task<InterviewOperationOutcome> ClarifyAsync(int turnIndex)
    {
        Task<InterviewOperationOutcome> rejection = CheckOrdinaryAdmission(turnIndex);
        if (rejection != null)
            return rejection;

        var op = new Operation(InterviewOperationKind.Clarify, ids.NewRequestId())
        {
            ConversationId = activeConversationId,
            TurnIndex = turnIndex,
        };

        ordinary = op;
        Send(op);
        return op.Completion.Task;
    }

    // Repeated calls while an end is outstanding return that same logical operation. An end
    // whose outcome is unknown is re-sent with its original RequestId and payload.
    public Task<InterviewOperationOutcome> EndAsync(string reason)
    {
        if (disposed)
            return Done(InterviewOperationStatus.Cancelled);
        if (end != null)
            return end.Completion.Task;
        if (reason == null || !InterviewBridgeProtocol.EndReasons.All.Contains(reason))
            return Done(InterviewOperationStatus.Rejected, InterviewBridgeProtocol.ServerErrorCodes.InvalidEndReason);
        if (activeConversationId == null)
            return Done(InterviewOperationStatus.ConversationEnded);
        if (Status != InterviewBridgeStatus.Ready)
            return Done(InterviewOperationStatus.NotReady);

        Operation op;
        if (unresolvedEnd != null && unresolvedEnd.ConversationId == activeConversationId)
        {
            op = unresolvedEnd;
            op.Renew();
        }
        else
        {
            op = new Operation(InterviewOperationKind.End, ids.NewRequestId())
            {
                ConversationId = activeConversationId,
                Reason = reason,
            };
        }

        unresolvedEnd = null;
        end = op;
        Send(op);
        return op.Completion.Task;
    }

    // Cleanup only (screen disabled or unloaded): one fire-and-forget user_ended, sent only if
    // the active conversation has had no end at all. Its reply is not awaited and is ignored.
    public void SendBestEffortEnd()
    {
        if (disposed ||
            Status != InterviewBridgeStatus.Ready ||
            activeConversationId == null ||
            end != null ||
            unresolvedEnd != null ||
            bestEffortEndSentFor == activeConversationId)
        {
            return;
        }

        bestEffortEndSentFor = activeConversationId;

        try
        {
            transport.Post(InterviewBridgeMessages.BuildEnd(
                ids.NewCorrelationId(),
                activeConversationId,
                ids.NewRequestId(),
                InterviewBridgeProtocol.EndReasons.UserEnded));
        }
        catch (ArgumentException)
        {
            // Best effort only.
        }
    }

    // Local reset: cancels outstanding operations (their late replies are ignored), clears the
    // tracked conversation, and drops any retained answer text. An unresolved start is kept so
    // the next start reuses its RequestId rather than silently creating a second conversation.
    public void ResetConversation()
    {
        CancelOutstanding();
        ClearConversation();
        unresolvedOrdinary = false;
        unresolvedEnd = null;
        RaiseChanged();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        CancelOutstanding();
        ClearConversation();
        unresolvedStart = null;
        unresolvedEnd = null;
        transport.Received -= Receive;
        (transport as IDisposable)?.Dispose();
        disposed = true;
        Status = InterviewBridgeStatus.Disabled;
    }

    // Raw JSON from the transport (already window.parent / parent-origin checked there). Fails closed.
    public void Receive(string json)
    {
        if (disposed)
            return;

        InterviewBridgeInbound inbound = InterviewBridgeMessages.Parse(json);

        switch (inbound.Kind)
        {
            case InterviewBridgeInboundKind.Ready:
                HandleReady(inbound.Ready);
                return;

            case InterviewBridgeInboundKind.Result:
                if (!inFlight.TryGetValue(inbound.Result.CorrelationId, out Operation op))
                {
                    // Stale (timed out or superseded), duplicate, or unsolicited.
                    IgnoredMessageCount++;
                    return;
                }

                inFlight.Remove(op.Correlation);
                op.Correlation = null;
                HandleResult(op, inbound.Result);
                return;

            default:
                IgnoredMessageCount++;
                return;
        }
    }

    private Task<InterviewOperationOutcome> CheckOrdinaryAdmission(int turnIndex)
    {
        if (disposed)
            return Done(InterviewOperationStatus.Cancelled);
        if (Status != InterviewBridgeStatus.Ready)
            return Done(InterviewOperationStatus.NotReady);
        if (ordinary != null || end != null)
            return Done(InterviewOperationStatus.Busy);
        if (activeConversationId == null || unresolvedOrdinary)
            return Done(InterviewOperationStatus.Rejected, InterviewBridgeProtocol.HostErrorCodes.ConversationMismatch);
        if (turnIndex != currentTurnIndex)
            return Done(InterviewOperationStatus.Rejected, InterviewBridgeProtocol.ServerErrorCodes.TurnMismatch);
        return null;
    }

    private void HandleReady(InterviewBridgeReady ready)
    {
        if (!helloCorrelations.Contains(ready.CorrelationId))
        {
            IgnoredMessageCount++;
            return;
        }

        helloCorrelations.Clear();

        switch (ready.HostState)
        {
            case InterviewBridgeProtocol.HostStates.Ready:
                SetStatus(InterviewBridgeStatus.Ready);
                ResumeParked();
                return;
            case InterviewBridgeProtocol.HostStates.SignedOut:
                SetStatus(InterviewBridgeStatus.SignedOut);
                return;
            case InterviewBridgeProtocol.HostStates.ScenarioMismatch:
                SetStatus(InterviewBridgeStatus.ScenarioMismatch);
                return;
            default:
                SetStatus(InterviewBridgeStatus.VersionMismatch);
                return;
        }
    }

    private void HandleResult(Operation op, InterviewBridgeResult r)
    {
        if (r.Ok)
        {
            if (!IsConsistent(op, r))
            {
                // A well-formed result that does not fit this operation changes nothing. Further
                // answers/clarifications are blocked; End and Reset remain available.
                if (op.Kind == InterviewOperationKind.Answer || op.Kind == InterviewOperationKind.Clarify)
                    unresolvedOrdinary = true;

                Complete(op, InterviewOperationStatus.Rejected, InterviewBridgeProtocol.HostErrorCodes.InvalidMessage);
                return;
            }

            Apply(op, r);
            Complete(op, InterviewOperationStatus.Succeeded, string.Empty, r);
            return;
        }

        string code = r.ErrorCode;

        switch (code)
        {
            case InterviewBridgeProtocol.HostErrorCodes.TransportFailure:
            case InterviewBridgeProtocol.ServerErrorCodes.InternalError:
                RetryOrGiveUp(op);
                return;

            case InterviewBridgeProtocol.HostErrorCodes.NotReady:
                // The host did not send this attempt. Re-handshake; an earlier attempt of a retried
                // operation may still have reached the server, so resume it only once ready.
                SetStatus(InterviewBridgeStatus.Idle);
                Connect();
                if (op.Attempts <= 1)
                    Complete(op, InterviewOperationStatus.NotReady, code);
                else
                    op.WaitingForReady = true;
                return;

            case InterviewBridgeProtocol.HostErrorCodes.Busy:
            case InterviewBridgeProtocol.ServerErrorCodes.OperationInProgress:
                if (op.Attempts <= 1)
                    Complete(op, code == InterviewBridgeProtocol.HostErrorCodes.Busy
                        ? InterviewOperationStatus.Busy
                        : InterviewOperationStatus.Rejected, code);
                else
                    GiveUp(op);
                return;

            case InterviewBridgeProtocol.HostErrorCodes.AuthenticationRequired:
                SetStatus(InterviewBridgeStatus.SignedOut);
                Complete(op, InterviewOperationStatus.AuthenticationRequired, code);
                return;

            case InterviewBridgeProtocol.HostErrorCodes.Superseded:
            case InterviewBridgeProtocol.ServerErrorCodes.OperationSuperseded:
                if (op.ConversationId != null && op.ConversationId == activeConversationId)
                    EndConversationLocally(except: op);
                Complete(op, InterviewOperationStatus.Superseded, code);
                return;

            case InterviewBridgeProtocol.HostErrorCodes.ConversationMismatch:
            case InterviewBridgeProtocol.ServerErrorCodes.NotFoundOrForbidden:
            case InterviewBridgeProtocol.ServerErrorCodes.AlreadyTerminal:
                if (op.Kind == InterviewOperationKind.Start)
                {
                    Complete(op, InterviewOperationStatus.Rejected, code);
                    return;
                }

                if (op.ConversationId == activeConversationId)
                    EndConversationLocally(except: op);
                Complete(op, InterviewOperationStatus.ConversationEnded, code);
                return;

            default:
                // Every other code is a definitive rejection: never retried.
                Complete(op, InterviewOperationStatus.Rejected, code);
                return;
        }
    }

    private bool IsConsistent(Operation op, InterviewBridgeResult r)
    {
        switch (op.Kind)
        {
            case InterviewOperationKind.Start:
                return r.IsTerminal ||
                       (r.IsActive &&
                        r.TurnIndex == InterviewBridgeProtocol.MinTurnIndex &&
                        r.Action == InterviewBridgeProtocol.Actions.QuestionPresented);

            case InterviewOperationKind.Answer:
                return r.ConversationId == op.ConversationId &&
                       (r.IsTerminal ||
                        (r.IsActive &&
                         r.TurnIndex == op.TurnIndex + 1 &&
                         (r.Action == InterviewBridgeProtocol.Actions.QuestionPresented ||
                          r.Action == InterviewBridgeProtocol.Actions.FollowUpPresented)));

            case InterviewOperationKind.Clarify:
                return r.ConversationId == op.ConversationId &&
                       r.IsActive &&
                       r.TurnIndex == op.TurnIndex &&
                       r.Action == InterviewBridgeProtocol.Actions.ClarificationProvided;

            default:
                return r.ConversationId == op.ConversationId && r.IsTerminal;
        }
    }

    private void Apply(Operation op, InterviewBridgeResult r)
    {
        if (r.IsActive)
        {
            activeConversationId = r.ConversationId;
            currentTurnIndex = r.TurnIndex;
            unresolvedOrdinary = false;

            double now = clock();
            idleDeadline = now + r.IdleRemainingMs / 1000.0;
            absoluteDeadline = now + r.AbsoluteRemainingMs / 1000.0;
            return;
        }

        // Terminal. A start replay may report an earlier conversation that already ended.
        if (op.Kind == InterviewOperationKind.Start || r.ConversationId == activeConversationId)
            EndConversationLocally(except: op);
    }

    // The conversation can no longer accept operations. An outstanding answer/clarify is told
    // Superseded now and its eventual reply (superseded or late content) is ignored.
    private void EndConversationLocally(Operation except = null)
    {
        ClearConversation();
        unresolvedOrdinary = false;
        unresolvedEnd = null;

        if (ordinary != null &&
            !ReferenceEquals(ordinary, except) &&
            ordinary.Kind != InterviewOperationKind.Start)
        {
            Complete(ordinary, InterviewOperationStatus.Superseded, InterviewBridgeProtocol.HostErrorCodes.Superseded);
        }
    }

    private void RetryOrGiveUp(Operation op)
    {
        if (!IsStillEligible(op))
        {
            Complete(op, InterviewOperationStatus.ConversationEnded);
            return;
        }

        if (op.Attempts < MaxTransportAttempts)
            op.RetryAt = clock() + RetryDelaySeconds[Math.Min(op.Attempts - 1, RetryDelaySeconds.Count - 1)];
        else
            GiveUp(op);
    }

    private bool IsStillEligible(Operation op) =>
        op.Kind == InterviewOperationKind.Start
            ? activeConversationId == null
            : op.ConversationId != null && op.ConversationId == activeConversationId;

    private void GiveUp(Operation op)
    {
        switch (op.Kind)
        {
            case InterviewOperationKind.Start:
                unresolvedStart = op;
                break;
            case InterviewOperationKind.End:
                unresolvedEnd = op;
                break;
            default:
                unresolvedOrdinary = true;
                break;
        }

        Complete(op, InterviewOperationStatus.OutcomeUnknown, string.Empty);
    }

    private void Send(Operation op)
    {
        op.RetryAt = null;
        op.WaitingForReady = false;

        if (Status != InterviewBridgeStatus.Ready)
        {
            // Only a retry can reach here; it resumes when readiness is restored.
            op.WaitingForReady = true;
            return;
        }

        string correlationId = ids.NewCorrelationId();
        string json;

        try
        {
            json = Build(op, correlationId);
        }
        catch (ArgumentException)
        {
            Complete(op, InterviewOperationStatus.Rejected, InterviewBridgeProtocol.HostErrorCodes.InvalidMessage);
            return;
        }

        op.Attempts++;
        op.Correlation = correlationId;
        op.Deadline = clock() + ReplyTimeoutSeconds;
        inFlight[correlationId] = op;

        if (!transport.Post(json))
        {
            inFlight.Remove(correlationId);
            op.Correlation = null;

            if (op.Attempts <= 1)
                Complete(op, InterviewOperationStatus.NotReady);
            else
                GiveUp(op);
        }
    }

    private static string Build(Operation op, string correlationId)
    {
        switch (op.Kind)
        {
            case InterviewOperationKind.Start:
                return InterviewBridgeMessages.BuildStart(correlationId, op.RequestId);
            case InterviewOperationKind.Answer:
                return InterviewBridgeMessages.BuildAnswer(correlationId, op.ConversationId, op.RequestId, op.TurnIndex, op.AnswerText);
            case InterviewOperationKind.Clarify:
                return InterviewBridgeMessages.BuildClarify(correlationId, op.ConversationId, op.RequestId, op.TurnIndex);
            default:
                return InterviewBridgeMessages.BuildEnd(correlationId, op.ConversationId, op.RequestId, op.Reason);
        }
    }

    private void SendHello()
    {
        string correlationId = ids.NewCorrelationId();
        string json = InterviewBridgeMessages.BuildHello(correlationId, simVersion);

        helloAttempts++;
        helloCorrelations.Add(correlationId);
        nextHelloAt = clock() + Math.Min(
            HelloMaxIntervalSeconds,
            HelloInitialIntervalSeconds * Math.Pow(2, helloAttempts - 1));

        if (!transport.Post(json))
        {
            Status = InterviewBridgeStatus.Disabled;
            DisabledReason = InterviewBridgeDisabledReason.TransportUnavailable;
            RaiseChanged();
        }
    }

    private void ResumeParked()
    {
        foreach (Operation op in new[] { ordinary, end })
        {
            if (op != null && op.WaitingForReady)
                Send(op);
        }
    }

    private void CancelOutstanding()
    {
        foreach (Operation op in new[] { ordinary, end })
        {
            if (op == null)
                continue;

            // A start that was already sent may have created a conversation: keep its RequestId
            // so the next start replays it instead of creating a second one.
            if (op.Kind == InterviewOperationKind.Start && op.Attempts > 0 && !disposed)
                unresolvedStart = op;

            Complete(op, InterviewOperationStatus.Cancelled);
        }
    }

    private void ClearConversation()
    {
        activeConversationId = null;
        currentTurnIndex = -1;
        idleDeadline = null;
        absoluteDeadline = null;
    }

    private void Complete(
        Operation op,
        InterviewOperationStatus status,
        string errorCode = null,
        InterviewBridgeResult result = null)
    {
        if (op.Correlation != null)
        {
            inFlight.Remove(op.Correlation);
            op.Correlation = null;
        }

        op.RetryAt = null;
        op.WaitingForReady = false;

        // Release retained answer text for every outcome; not secure memory erasure.
        op.AnswerText = null;

        if (ReferenceEquals(ordinary, op))
            ordinary = null;
        if (ReferenceEquals(end, op))
            end = null;

        op.Completion.TrySetResult(new InterviewOperationOutcome(status, errorCode, result));
        RaiseChanged();
    }

    private void SetStatus(InterviewBridgeStatus status)
    {
        if (Status == status)
            return;

        Status = status;
        if (status != InterviewBridgeStatus.Connecting)
            helloCorrelations.Clear();

        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    private static Task<InterviewOperationOutcome> Done(InterviewOperationStatus status, string errorCode = null) =>
        Task.FromResult(new InterviewOperationOutcome(status, errorCode, null));

    private sealed class Operation
    {
        public Operation(InterviewOperationKind kind, string requestId)
        {
            Kind = kind;
            RequestId = requestId;
            Renew();
        }

        public InterviewOperationKind Kind { get; }
        public string RequestId { get; }
        public string ConversationId { get; set; }
        public int TurnIndex { get; set; }
        public string Reason { get; set; }

        // Volatile retention for identical retries only; cleared on every completion.
        public string AnswerText { get; set; }

        public int Attempts { get; set; }
        public string Correlation { get; set; }
        public double Deadline { get; set; }
        public double? RetryAt { get; set; }
        public bool WaitingForReady { get; set; }
        public TaskCompletionSource<InterviewOperationOutcome> Completion { get; private set; }

        // A fresh completion and attempt budget for an explicit re-send of the same RequestId.
        public void Renew()
        {
            Attempts = 0;
            Completion = new TaskCompletionSource<InterviewOperationOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
