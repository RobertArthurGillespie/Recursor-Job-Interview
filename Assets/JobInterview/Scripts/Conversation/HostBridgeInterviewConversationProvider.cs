using System;
using System.Threading;
using System.Threading.Tasks;

public enum InterviewHostAvailability
{
    Connecting,
    Ready,

    // No validated parent Blazor origin, invalid build version, or not framed by the Blazor parent.
    NotConfigured,

    Unresponsive,
    AuthenticationRequired,
    ScenarioMismatch,
    VersionMismatch,
}

public enum InterviewConversationFailure
{
    HostNotReady,
    Busy,
    AuthenticationRequired,
    ConversationEnded,
    Superseded,
    Rejected,
    OutcomeUnknown,
    Cancelled,
}

// Carries only a failure category. Its message is fixed: it never contains a server, host,
// or provider message, an identifier, or conversation content.
public sealed class InterviewConversationException : Exception
{
    public const string FixedMessage = "The interview conversation operation did not complete.";

    public InterviewConversationException(InterviewConversationFailure failure)
        : base(FixedMessage)
    {
        Failure = failure;
    }

    public InterviewConversationFailure Failure { get; }
}

public sealed class InterviewEndResult
{
    private InterviewEndResult(bool ended, InterviewEndKind endKind, InterviewConversationFailure failure)
    {
        IsEnded = ended;
        EndKind = endKind;
        Failure = failure;
    }

    // True after a definitive terminal acknowledgment (or when the conversation had already ended).
    public bool IsEnded { get; }
    public InterviewEndKind EndKind { get; }
    public InterviewConversationFailure Failure { get; }

    public static InterviewEndResult Ended(InterviewEndKind kind) =>
        new InterviewEndResult(true, kind, InterviewConversationFailure.ConversationEnded);

    public static InterviewEndResult NotEnded(InterviewConversationFailure failure) =>
        new InterviewEndResult(false, InterviewEndKind.Abandoned, failure);
}

// Lifecycle operations the scripted provider does not need: readiness, explicit end, and expiry.
public interface IInterviewConversationLifecycle : IDisposable
{
    InterviewHostAvailability Availability { get; }
    bool HasActiveConversation { get; }
    bool IsEndPending { get; }
    bool HasUnresolvedOperation { get; }

    // UI notification only; carries no content.
    event Action Changed;

    void Connect();
    void Tick();
    Task<InterviewEndResult> EndAsync(string reason);
    void SendBestEffortEnd();
    bool TryGetRemainingSeconds(out double idleSeconds, out double absoluteSeconds);
}

// Stage 6B: the Blazor-host-backed provider. All HTTP and authentication stay in the host;
// this class only speaks the postMessage bridge through InterviewBridgeClient.
public sealed class HostBridgeInterviewConversationProvider
    : IInterviewConversationProvider, IInterviewConversationLifecycle
{
    // Used only if a natural completion arrives without the server's fixed closing text.
    public const string CompletionFallbackMessage =
        "That covers everything for today. Thank you for practicing this interview.";

    public const string AbandonedMessage = "This interview has ended.";
    public const string ExpiredMessage = "This interview has timed out.";

    private readonly InterviewBridgeClient client;
    private InterviewTurn currentTurn;

    public HostBridgeInterviewConversationProvider(InterviewBridgeClient client)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        client.Changed += OnClientChanged;
    }

    public event Action Changed;

    public InterviewHostAvailability Availability
    {
        get
        {
            switch (client.Status)
            {
                case InterviewBridgeStatus.Ready: return InterviewHostAvailability.Ready;
                case InterviewBridgeStatus.Disabled: return InterviewHostAvailability.NotConfigured;
                case InterviewBridgeStatus.Unresponsive: return InterviewHostAvailability.Unresponsive;
                case InterviewBridgeStatus.SignedOut: return InterviewHostAvailability.AuthenticationRequired;
                case InterviewBridgeStatus.ScenarioMismatch: return InterviewHostAvailability.ScenarioMismatch;
                case InterviewBridgeStatus.VersionMismatch: return InterviewHostAvailability.VersionMismatch;
                default: return InterviewHostAvailability.Connecting;
            }
        }
    }

    public bool HasActiveConversation => client.HasActiveConversation;
    public bool IsEndPending => client.IsEndPending;

    public bool HasUnresolvedOperation =>
        client.HasUnresolvedOrdinaryOperation || client.HasUnresolvedEnd;

    public void Connect() => client.Connect();

    public void Tick() => client.Tick();

    public bool TryGetRemainingSeconds(out double idleSeconds, out double absoluteSeconds) =>
        client.TryGetRemainingSeconds(out idleSeconds, out absoluteSeconds);

    public async Task<InterviewTurn> BeginAsync(
        InterviewScenarioDefinition scenario,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (scenario == null)
            throw new ArgumentNullException(nameof(scenario));

        // The host starts only the fixed scenario; a differently configured asset fails closed.
        if (!string.Equals(scenario.ScenarioId, InterviewBridgeProtocol.ScenarioId, StringComparison.Ordinal))
            throw new InterviewConversationException(InterviewConversationFailure.Rejected);

        InterviewOperationOutcome outcome = await client.StartAsync();
        InterviewBridgeResult result = RequireSuccess(outcome);

        if (!result.IsActive)
            throw new InterviewConversationException(InterviewConversationFailure.ConversationEnded);

        currentTurn = ToTurn(result, isClarification: false);
        return currentTurn;
    }

    public async Task<InterviewTurnResult> SubmitResponseAsync(
        InterviewResponse response,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (response == null)
            throw new ArgumentNullException(nameof(response));

        if (currentTurn == null ||
            response.TurnIndex != currentTurn.TurnIndex ||
            !string.Equals(response.QuestionId, currentTurn.QuestionId, StringComparison.Ordinal))
        {
            throw new InterviewConversationException(InterviewConversationFailure.Rejected);
        }

        // The answer text goes to the bridge client only; it is not retained here.
        InterviewOperationOutcome outcome =
            await client.AnswerAsync(response.TurnIndex, response.ResponseText);

        InterviewBridgeResult result = RequireSuccess(outcome);

        if (result.IsActive)
        {
            currentTurn = ToTurn(result, isClarification: false);
            return InterviewTurnResult.ContinueWith(currentTurn);
        }

        currentTurn = null;
        return ToEndedResult(result);
    }

    public async Task<InterviewTurn> RequestClarificationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (currentTurn == null)
            throw new InterviewConversationException(InterviewConversationFailure.Rejected);

        InterviewOperationOutcome outcome = await client.ClarifyAsync(currentTurn.TurnIndex);
        InterviewBridgeResult result = RequireSuccess(outcome);

        currentTurn = ToTurn(result, isClarification: true);
        return currentTurn;
    }

    public async Task<InterviewEndResult> EndAsync(string reason)
    {
        InterviewOperationOutcome outcome = await client.EndAsync(reason);

        switch (outcome.Status)
        {
            case InterviewOperationStatus.Succeeded:
                currentTurn = null;
                return InterviewEndResult.Ended(ToEndKind(outcome.Result.ConversationState));

            case InterviewOperationStatus.ConversationEnded:
                // Already over (terminal, unknown to the server, or not active in the host).
                currentTurn = null;
                return InterviewEndResult.Ended(InterviewEndKind.Abandoned);

            default:
                return InterviewEndResult.NotEnded(ToFailure(outcome.Status));
        }
    }

    public void SendBestEffortEnd() => client.SendBestEffortEnd();

    // Local reset only; outstanding replies are ignored afterwards.
    public void Reset()
    {
        currentTurn = null;
        client.ResetConversation();
    }

    public void Dispose()
    {
        client.Changed -= OnClientChanged;
        client.Dispose();
        currentTurn = null;
    }

    private void OnClientChanged() => Changed?.Invoke();

    private static InterviewBridgeResult RequireSuccess(InterviewOperationOutcome outcome)
    {
        if (outcome.Status == InterviewOperationStatus.Succeeded && outcome.Result != null)
            return outcome.Result;

        throw new InterviewConversationException(ToFailure(outcome.Status));
    }

    private static InterviewConversationFailure ToFailure(InterviewOperationStatus status)
    {
        switch (status)
        {
            case InterviewOperationStatus.Superseded: return InterviewConversationFailure.Superseded;
            case InterviewOperationStatus.AuthenticationRequired: return InterviewConversationFailure.AuthenticationRequired;
            case InterviewOperationStatus.NotReady: return InterviewConversationFailure.HostNotReady;
            case InterviewOperationStatus.Busy: return InterviewConversationFailure.Busy;
            case InterviewOperationStatus.ConversationEnded: return InterviewConversationFailure.ConversationEnded;
            case InterviewOperationStatus.OutcomeUnknown: return InterviewConversationFailure.OutcomeUnknown;
            case InterviewOperationStatus.Cancelled: return InterviewConversationFailure.Cancelled;
            default: return InterviewConversationFailure.Rejected;
        }
    }

    private static InterviewTurn ToTurn(InterviewBridgeResult result, bool isClarification) =>
        new InterviewTurn(result.TurnIndex, result.QuestionRef, result.InterviewerText, isClarification);

    private static InterviewTurnResult ToEndedResult(InterviewBridgeResult result)
    {
        switch (ToEndKind(result.ConversationState))
        {
            case InterviewEndKind.Completed:
                return InterviewTurnResult.Complete(
                    string.IsNullOrWhiteSpace(result.InterviewerText)
                        ? CompletionFallbackMessage
                        : result.InterviewerText);
            case InterviewEndKind.Expired:
                return InterviewTurnResult.Ended(InterviewEndKind.Expired, ExpiredMessage);
            default:
                return InterviewTurnResult.Ended(InterviewEndKind.Abandoned, AbandonedMessage);
        }
    }

    private static InterviewEndKind ToEndKind(string conversationState)
    {
        switch (conversationState)
        {
            case InterviewBridgeProtocol.States.Completed: return InterviewEndKind.Completed;
            case InterviewBridgeProtocol.States.Expired: return InterviewEndKind.Expired;
            default: return InterviewEndKind.Abandoned;
        }
    }
}
