using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public enum InterviewConversationProviderMode
{
    // The Blazor-host bridge in a WebGL player; the scripted provider everywhere else.
    Automatic,
    ScriptedLocal,
    HostBridge
}

public class InterviewSessionController : MonoBehaviour
{
    // Fixed, content-free UI text for host-backed states. Never a server, host, or provider message.
    public const string ConnectingMessage = "Connecting to the interview host…";
    public const string HostNotConfiguredMessage = "This interview is not available on this page.";
    public const string HostUnresponsiveMessage = "The interview host is not responding. Reload the page to try again.";
    public const string HostIncompatibleMessage = "This interview build is not accepted by the host. Reload the page to try again.";
    public const string AuthenticationRequiredMessage = "Your sign-in has expired. Sign in again on the host page, then reload this interview.";
    public const string HostNotReadyMessage = "The interview host is not ready. Please try again in a moment.";
    public const string UserEndedMessage = "You ended the interview.";
    public const string SupersededMessage = "This interview ended before your request could be completed.";
    public const string OutcomeUnknownMessage = "We could not confirm whether your last action was received. Select End Interview or Reset Interview.";
    public const string RejectedMessage = "The request could not be processed. Select End Interview or Reset Interview.";
    public const string EndFailedMessage = "The interview could not be ended. Please try again.";
    public const string CompletedMessage = "The interview is complete.";

    [Header("Configuration")]
    [SerializeField]
    private InterviewScenarioDefinition scenario;

    [SerializeField]
    [Tooltip("Automatic uses the Blazor-host bridge in a WebGL player and the scripted provider elsewhere.")]
    private InterviewConversationProviderMode providerMode =
        InterviewConversationProviderMode.Automatic;

    [Header("Runtime status — do not edit during Play Mode")]
    [SerializeField]
    private InterviewSessionState state =
        InterviewSessionState.NotStarted;

    [SerializeField]
    private int currentQuestionNumber;

    private IInterviewConversationProvider provider;
    private CancellationTokenSource sessionCancellation;
    private int sessionGeneration;

    // Host-backed mode only. The bridge lives for the page; the conversation is per session.
    private HostBridgeInterviewConversationProvider hostProvider;
    private IInterviewConversationLifecycle host;
    private InterviewSessionState stateBeforeEnd;
    private bool contentOperationInFlight;
    private bool lastSubmissionNotSent;
    private string expiryWarning = string.Empty;

    // Runtime-only fields: not serialized into the scene or scenario asset.
    private InterviewTurn currentTurn;
    private string statusMessage = string.Empty;
    private readonly InterviewBehaviorCollector behavior = new InterviewBehaviorCollector();
    public IReadOnlyList<InterviewBehaviorEvent> BehaviorEvents => behavior.Events;
    public int DroppedBehaviorEventCount => behavior.DroppedEventCount;
    public int OmittedBehaviorTimingCount => behavior.OmittedTimingCount;

    public void NotifyResponseStarted(InterviewTurn expectedTurn)
    {
        if (CanSubmit && expectedTurn != null && ReferenceEquals(expectedTurn, currentTurn))
            behavior.ResponseStarted(expectedTurn.TurnIndex);
    }

    public InterviewSessionState State => state;
    public InterviewTurn CurrentTurn => currentTurn;
    public string StatusMessage => statusMessage;
    public string ExpiryWarning => expiryWarning;

    public bool UsesHostBridge => host != null;

    public string DisplayRole =>
        scenario != null ? scenario.DisplayRole : string.Empty;

    public string Introduction =>
        scenario != null ? scenario.Introduction : string.Empty;

    public int CurrentQuestionNumber => currentQuestionNumber;

    // The host decides how many prompts (core questions plus follow-ups) there are, so no
    // fixed total is shown in host-backed mode.
    public int TotalQuestions =>
        !UsesHostBridge && scenario != null && scenario.Questions != null
            ? scenario.Questions.Count
            : 0;

    public bool CanSubmit =>
        isActiveAndEnabled &&
        state == InterviewSessionState.AwaitingResponse;

    public bool CanRequestClarification => CanSubmit;

    public bool CanStart =>
        isActiveAndEnabled &&
        state == InterviewSessionState.NotStarted &&
        (host == null || host.Availability == InterviewHostAvailability.Ready);

    // End is available while a host conversation exists, including while an answer or
    // clarification is outstanding: the host lets end supersede it.
    public bool CanEnd =>
        isActiveAndEnabled &&
        host != null &&
        host.HasActiveConversation &&
        !host.IsEndPending &&
        (state == InterviewSessionState.AwaitingResponse ||
         state == InterviewSessionState.SubmittingResponse ||
         state == InterviewSessionState.RequestingClarification ||
         state == InterviewSessionState.OutcomeUnknown ||
         state == InterviewSessionState.Error);

    public bool IsEnding => state == InterviewSessionState.Ending;

    // True once a reset has returned the session to its pre-start states.
    public bool IsAtPreStart =>
        state == InterviewSessionState.NotStarted ||
        state == InterviewSessionState.WaitingForHost ||
        state == InterviewSessionState.HostUnavailable ||
        (state == InterviewSessionState.AuthenticationRequired && (host == null || !host.HasActiveConversation));

    // UI notification only. Do not use this to log conversation content.
    public event Action Changed;

    private void Awake()
    {
        bool useHost =
            providerMode == InterviewConversationProviderMode.HostBridge ||
            (providerMode == InterviewConversationProviderMode.Automatic &&
             InterviewBridgeRuntime.IsWebGLPlayer);

        if (useHost && hostProvider == null)
        {
            hostProvider = InterviewBridgeRuntime.CreateProvider();
            host = hostProvider;
            host.Changed += OnHostChanged;
        }

        ClearSession();
    }

    private void OnEnable()
    {
        SyncHostAvailability();
    }

    private void Start()
    {
        // Sends bridge.hello (retried until bridge.ready); a no-op when the bridge is disabled.
        host?.Connect();
        SyncHostAvailability();
    }

    private void Update()
    {
        if (host == null)
            return;

        host.Tick();
        UpdateExpiryWarning();
    }

    private void OnDestroy()
    {
        if (host != null)
        {
            host.Changed -= OnHostChanged;
            host.Dispose();
            host = null;
            hostProvider = null;
        }
    }

    public void StartInterview()
    {
        if (!isActiveAndEnabled ||
            state != InterviewSessionState.NotStarted)
        {
            return;
        }

        if (host != null && host.Availability != InterviewHostAvailability.Ready)
        {
            SyncHostAvailability();
            return;
        }

        if (!IsScenarioValid())
        {
            SetError("The interview scenario needs configuration.");
            return;
        }

        provider = hostProvider != null
            ? hostProvider
            : new ScriptedInterviewConversationProvider();
        sessionCancellation = new CancellationTokenSource();

        statusMessage = string.Empty;
        SetState(InterviewSessionState.Introducing);
    }

    // Called when the user finishes reading the introduction.
    public async Task ContinueFromIntroductionAsync()
    {
        if (!isActiveAndEnabled ||
            state != InterviewSessionState.Introducing)
        {
            return;
        }

        int generation = sessionGeneration;
        var activeProvider = provider;
        CancellationToken token = sessionCancellation.Token;

        statusMessage = string.Empty;
        SetState(InterviewSessionState.LoadingTurn);
        contentOperationInFlight = true;

        try
        {
            InterviewTurn turn =
                await activeProvider.BeginAsync(scenario, token);

            if (!IsCurrentOperation(generation, InterviewSessionState.LoadingTurn))
                return;

            PresentTurn(turn);
        }
        catch (InterviewConversationException ex)
        {
            if (IsCurrentOperation(generation, InterviewSessionState.LoadingTurn))
                HandleConversationFailure(ex.Failure, InterviewSessionState.Introducing);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentOperation(generation, InterviewSessionState.LoadingTurn))
                SetError("The interview operation was cancelled.");
        }
        catch (Exception)
        {
            if (IsCurrentOperation(generation, InterviewSessionState.LoadingTurn))
                SetError("The interview could not be started.");
        }
        finally
        {
            if (generation == sessionGeneration)
                contentOperationInFlight = false;
        }
    }

    public async Task SubmitResponseAsync(
        InterviewTurn expectedTurn,
        string responseText)
    {
        // The caller must submit against the exact turn it displayed.
        // A delayed second click carrying the old turn cannot answer
        // the next question, even when the provider completes immediately.
        if (!CanSubmit ||
            expectedTurn == null ||
            !ReferenceEquals(expectedTurn, currentTurn))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            statusMessage = "Please enter a response before submitting.";
            Changed?.Invoke();
            return;
        }

        if (UsesHostBridge && responseText.Length > InterviewBridgeProtocol.MaxAnswerTextLength)
        {
            statusMessage = "Please shorten your response to 2,000 characters or fewer.";
            Changed?.Invoke();
            return;
        }

        int generation = sessionGeneration;
        var activeProvider = provider;
        CancellationToken token = sessionCancellation.Token;

        InterviewResponse response = new InterviewResponse(
            currentTurn.TurnIndex,
            currentTurn.QuestionId,
            responseText);

        // No controller field retains the applicant's answer.
        int submittedTurnIndex = currentTurn.TurnIndex;
        behavior.Submit(submittedTurnIndex, responseText.Length);
        responseText = null;
        statusMessage = string.Empty;
        lastSubmissionNotSent = false;

        SetState(InterviewSessionState.SubmittingResponse);
        contentOperationInFlight = true;

        try
        {
            // Automatic retries of an unknown outcome happen inside the provider with the same
            // RequestId: this method, its telemetry, and its UI update run exactly once.
            InterviewTurnResult result =
                await activeProvider.SubmitResponseAsync(response, token);

            if (!IsCurrentOperation(generation, InterviewSessionState.SubmittingResponse))
                return;

            if (result == null)
                throw new InvalidOperationException();

            if (!result.InterviewComplete && result.NextTurn == null)
                throw new InvalidOperationException();

            if (!result.InterviewComplete || result.EndKind == InterviewEndKind.Completed)
                behavior.CompleteTurn(submittedTurnIndex);

            if (result.InterviewComplete)
            {
                currentTurn = null;
                statusMessage = result.CompletionMessage ?? string.Empty;

                activeProvider.Reset();
                SetState(ToSessionState(result.EndKind));
            }
            else
            {
                PresentTurn(result.NextTurn);
            }
        }
        catch (InterviewConversationException ex)
        {
            if (IsCurrentOperation(generation, InterviewSessionState.SubmittingResponse))
            {
                lastSubmissionNotSent =
                    ex.Failure == InterviewConversationFailure.HostNotReady ||
                    ex.Failure == InterviewConversationFailure.Busy;

                HandleConversationFailure(ex.Failure, InterviewSessionState.AwaitingResponse);
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentOperation(generation, InterviewSessionState.SubmittingResponse))
                SetError("The interview operation was cancelled.");
        }
        catch (Exception)
        {
            if (IsCurrentOperation(generation, InterviewSessionState.SubmittingResponse))
                SetError("The response could not be processed.");
        }
        finally
        {
            // Release this reference; this is not secure memory erasure.
            response = null;

            if (generation == sessionGeneration)
                contentOperationInFlight = false;
        }
    }

    public async Task RequestClarificationAsync(
        InterviewTurn expectedTurn)
    {
        if (!CanRequestClarification ||
            expectedTurn == null ||
            !ReferenceEquals(expectedTurn, currentTurn))
        {
            return;
        }

        int generation = sessionGeneration;
        var activeProvider = provider;
        CancellationToken token = sessionCancellation.Token;

        statusMessage = string.Empty;
        SetState(InterviewSessionState.RequestingClarification);
        behavior.ClarificationRequested(expectedTurn.TurnIndex);
        contentOperationInFlight = true;

        try
        {
            InterviewTurn turn =
                await activeProvider.RequestClarificationAsync(token);

            if (!IsCurrentOperation(generation, InterviewSessionState.RequestingClarification))
                return;

            PresentTurn(turn);
        }
        catch (InterviewConversationException ex)
        {
            if (IsCurrentOperation(generation, InterviewSessionState.RequestingClarification))
                HandleConversationFailure(ex.Failure, InterviewSessionState.AwaitingResponse);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentOperation(generation, InterviewSessionState.RequestingClarification))
                SetError("The interview operation was cancelled.");
        }
        catch (Exception)
        {
            if (IsCurrentOperation(generation, InterviewSessionState.RequestingClarification))
                SetError("Clarification could not be loaded.");
        }
        finally
        {
            if (generation == sessionGeneration)
                contentOperationInFlight = false;
        }
    }

    // End Interview button: conversation.end with user_ended. Repeated calls while an end is
    // outstanding do nothing; the bridge client also returns the same logical operation.
    public Task EndInterviewAsync() =>
        EndHostConversationAsync(InterviewBridgeProtocol.EndReasons.UserEnded, resetAfterEnd: false);

    // Reset Interview button. With an active host conversation it first sends conversation.end
    // with restart_requested and resets only after a definitive terminal acknowledgment; it
    // never starts a new conversation by itself. Otherwise it is the local reset.
    public async Task RestartInterviewAsync()
    {
        if (state == InterviewSessionState.Ending)
            return;

        // No conversation to end, or no way to reach the host (for example after sign-in
        // expired): a local reset. It never starts a new conversation by itself.
        if (host == null ||
            !host.HasActiveConversation ||
            host.Availability != InterviewHostAvailability.Ready)
        {
            ResetInterview();
            return;
        }

        await EndHostConversationAsync(InterviewBridgeProtocol.EndReasons.RestartRequested, resetAfterEnd: true);
    }

    public bool ConsumeSubmissionNotSent()
    {
        bool notSent = lastSubmissionNotSent;
        lastSubmissionNotSent = false;
        return notSent;
    }

    // Local reset (also used when the screen is disabled). A host conversation that has had no
    // end at all gets one best-effort user_ended; this is cleanup, not the End Interview control.
    public void ResetInterview()
    {
        ClearSession();
        Changed?.Invoke();
    }

    private async Task EndHostConversationAsync(string reason, bool resetAfterEnd)
    {
        if (host == null ||
            state == InterviewSessionState.Ending ||
            host.IsEndPending ||
            !host.HasActiveConversation)
        {
            return;
        }

        int generation = sessionGeneration;
        stateBeforeEnd = state;
        statusMessage = string.Empty;
        SetState(InterviewSessionState.Ending);

        InterviewEndResult result = await host.EndAsync(reason);

        if (generation != sessionGeneration || state != InterviewSessionState.Ending)
            return;

        if (result.IsEnded)
        {
            if (resetAfterEnd)
            {
                ResetInterview();
                return;
            }

            currentTurn = null;
            statusMessage = result.EndKind == InterviewEndKind.Abandoned
                ? UserEndedMessage
                : result.EndKind == InterviewEndKind.Expired
                    ? HostBridgeInterviewConversationProvider.ExpiredMessage
                    : CompletedMessage;
            SetState(ToSessionState(result.EndKind));
            return;
        }

        switch (result.Failure)
        {
            case InterviewConversationFailure.Cancelled:
                return;
            case InterviewConversationFailure.AuthenticationRequired:
                SetAuthenticationRequired();
                return;
            case InterviewConversationFailure.OutcomeUnknown:
                SetOutcomeUnknown();
                return;
        }

        // Not ended, and definitively so. Return to the prior state if it is still accurate:
        // an outstanding answer/clarification completes normally; one whose result was
        // withheld while ending cannot be recovered, so its outcome is reported as unknown.
        bool priorWasPending =
            stateBeforeEnd == InterviewSessionState.SubmittingResponse ||
            stateBeforeEnd == InterviewSessionState.RequestingClarification;

        if (priorWasPending && !contentOperationInFlight)
        {
            SetOutcomeUnknown();
            return;
        }

        statusMessage = EndFailedMessage;
        SetState(stateBeforeEnd);
    }

    private void HandleConversationFailure(
        InterviewConversationFailure failure,
        InterviewSessionState recoverTo)
    {
        switch (failure)
        {
            case InterviewConversationFailure.Cancelled:
                return;

            case InterviewConversationFailure.HostNotReady:
            case InterviewConversationFailure.Busy:
                // Nothing was sent: the applicant may try again on the same prompt.
                statusMessage = HostNotReadyMessage;
                SetState(recoverTo);
                return;

            case InterviewConversationFailure.AuthenticationRequired:
                SetAuthenticationRequired();
                return;

            case InterviewConversationFailure.Superseded:
                SetTerminal(InterviewSessionState.Abandoned, SupersededMessage);
                return;

            case InterviewConversationFailure.ConversationEnded:
                SetTerminal(InterviewSessionState.Abandoned, HostBridgeInterviewConversationProvider.AbandonedMessage);
                return;

            case InterviewConversationFailure.OutcomeUnknown:
                SetOutcomeUnknown();
                return;

            default:
                SetError(RejectedMessage);
                return;
        }
    }

    private void OnHostChanged()
    {
        SyncHostAvailability();
        Changed?.Invoke();
    }

    private void SyncHostAvailability()
    {
        if (host == null)
            return;

        bool preStart =
            state == InterviewSessionState.NotStarted ||
            state == InterviewSessionState.WaitingForHost ||
            state == InterviewSessionState.HostUnavailable ||
            (state == InterviewSessionState.AuthenticationRequired && !host.HasActiveConversation);

        if (!preStart)
            return;

        switch (host.Availability)
        {
            case InterviewHostAvailability.Ready:
                SetPreStart(InterviewSessionState.NotStarted, string.Empty);
                break;
            case InterviewHostAvailability.Connecting:
                SetPreStart(InterviewSessionState.WaitingForHost, ConnectingMessage);
                break;
            case InterviewHostAvailability.AuthenticationRequired:
                SetPreStart(InterviewSessionState.AuthenticationRequired, AuthenticationRequiredMessage);
                break;
            case InterviewHostAvailability.Unresponsive:
                SetPreStart(InterviewSessionState.HostUnavailable, HostUnresponsiveMessage);
                break;
            case InterviewHostAvailability.NotConfigured:
                SetPreStart(InterviewSessionState.HostUnavailable, HostNotConfiguredMessage);
                break;
            default:
                SetPreStart(InterviewSessionState.HostUnavailable, HostIncompatibleMessage);
                break;
        }
    }

    private void SetPreStart(InterviewSessionState nextState, string message)
    {
        if (state == nextState && statusMessage == message)
            return;

        statusMessage = message;
        SetState(nextState);
    }

    private void UpdateExpiryWarning()
    {
        string warning = string.Empty;

        if (state == InterviewSessionState.AwaitingResponse &&
            host.TryGetRemainingSeconds(out double idleSeconds, out double absoluteSeconds))
        {
            double remaining = Math.Min(idleSeconds, absoluteSeconds);

            if (remaining <= 0)
            {
                warning = "This interview may have timed out. Your next response may not be accepted.";
            }
            else if (remaining <= 120)
            {
                int minutes = (int)Math.Ceiling(remaining / 60.0);
                warning = absoluteSeconds <= idleSeconds
                    ? $"This interview will end in about {minutes} minute(s)."
                    : $"Please respond within about {minutes} minute(s) to keep this interview active.";
            }
        }

        if (warning != expiryWarning)
        {
            expiryWarning = warning;
            Changed?.Invoke();
        }
    }

    private void PresentTurn(InterviewTurn turn)
    {
        if (turn == null)
            throw new InvalidOperationException();

        currentTurn = turn;
        behavior.Present(turn.TurnIndex);
        currentQuestionNumber = turn.TurnIndex + 1;
        statusMessage = string.Empty;

        SetState(InterviewSessionState.AwaitingResponse);
    }

    private bool IsCurrentSession(int generation)
    {
        return generation == sessionGeneration && isActiveAndEnabled;
    }

    // A late result is applied only while the session is still waiting for exactly that
    // operation; after an end, reset, or supersession it is discarded.
    private bool IsCurrentOperation(int generation, InterviewSessionState expected)
    {
        return IsCurrentSession(generation) && state == expected;
    }

    private bool IsScenarioValid()
    {
        if (scenario == null || !scenario.IsConfigured)
            return false;

        // IsConfigured currently checks nonempty values, but not duplicates.
        var questionIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (InterviewQuestionDefinition question in scenario.Questions)
        {
            if (!questionIds.Add(question.QuestionId))
                return false;
        }

        return true;
    }

    private static InterviewSessionState ToSessionState(InterviewEndKind endKind)
    {
        switch (endKind)
        {
            case InterviewEndKind.Abandoned: return InterviewSessionState.Abandoned;
            case InterviewEndKind.Expired: return InterviewSessionState.Expired;
            default: return InterviewSessionState.Completed;
        }
    }

    private void SetState(InterviewSessionState nextState)
    {
        state = nextState;
        Changed?.Invoke();
    }

    private void SetError(string safeMessage)
    {
        currentTurn = null;
        statusMessage = safeMessage;
        SetState(InterviewSessionState.Error);
    }

    private void SetTerminal(InterviewSessionState terminalState, string safeMessage)
    {
        currentTurn = null;
        statusMessage = safeMessage;
        SetState(terminalState);
    }

    private void SetAuthenticationRequired()
    {
        currentTurn = null;
        statusMessage = AuthenticationRequiredMessage;
        SetState(InterviewSessionState.AuthenticationRequired);
    }

    private void SetOutcomeUnknown()
    {
        currentTurn = null;
        statusMessage = OutcomeUnknownMessage;
        SetState(InterviewSessionState.OutcomeUnknown);
    }

    private void ClearSession()
    {
        // Invalidates results from any operation belonging to the old session.
        sessionGeneration++;
        behavior.Clear();

        if (sessionCancellation != null)
        {
            sessionCancellation.Cancel();
            sessionCancellation.Dispose();
            sessionCancellation = null;
        }

        if (hostProvider != null)
        {
            hostProvider.SendBestEffortEnd();
            hostProvider.Reset();
        }

        // Drop the old provider. An outstanding operation, if any, keeps
        // its own reference until it finishes, but cannot update this session.
        provider = null;
        currentTurn = null;
        currentQuestionNumber = 0;
        statusMessage = string.Empty;
        expiryWarning = string.Empty;
        contentOperationInFlight = false;
        lastSubmissionNotSent = false;
        state = InterviewSessionState.NotStarted;
        SyncHostAvailability();
    }

    private void OnDisable()
    {
        ClearSession();
        Changed?.Invoke();
    }

#if UNITY_EDITOR
    [ContextMenu("Interview Debug/7 - Print Safe Behavior Events")]
    private void DebugBehaviorEvents()
    {
        if (!Application.isPlaying) return;
        foreach (var item in behavior.Events)
            Debug.Log($"[Interview behavior] {item.EventType}; turn={item.TurnIndex}; latencyMs={item.ResponseLatencyMs}; durationMs={item.ResponseDurationMs}; band={item.ResponseLengthBand}; completion={item.CompletionStatus}");
        Debug.Log($"[Interview behavior] count={behavior.Events.Count}; dropped={behavior.DroppedEventCount}; omittedTimings={behavior.OmittedTimingCount}");
    }
    // Temporary manual controls. These use synthetic text only and
    // are excluded from player builds.

    [ContextMenu("Interview Debug/1 - Start")]
    private void DebugStart()
    {
        if (Application.isPlaying)
            StartInterview();
    }

    [ContextMenu("Interview Debug/2 - Continue Introduction")]
    private async void DebugContinue()
    {
        if (Application.isPlaying)
            await ContinueFromIntroductionAsync();
    }

    [ContextMenu("Interview Debug/3 - Request Clarification")]
    private async void DebugClarify()
    {
        if (Application.isPlaying)
            await RequestClarificationAsync(currentTurn);
    }

    [ContextMenu("Interview Debug/4 - Submit Synthetic Response")]
    private async void DebugSubmit()
    {
        if (Application.isPlaying)
        {
            await SubmitResponseAsync(
                currentTurn,
                "Synthetic response for local development.");
        }
    }

    [ContextMenu("Interview Debug/5 - Test Duplicate Submission")]
    private async void DebugDuplicateSubmission()
    {
        if (!Application.isPlaying)
            return;

        InterviewTurn displayedTurn = currentTurn;

        await SubmitResponseAsync(
            displayedTurn,
            "Synthetic response for local development.");

        // Deliberately reuse the OLD turn. This must be ignored.
        await SubmitResponseAsync(
            displayedTurn,
            "Synthetic duplicate for local development.");
    }

    [ContextMenu("Interview Debug/6 - Reset")]
    private void DebugReset()
    {
        if (Application.isPlaying)
            ResetInterview();
    }

    [ContextMenu("Interview Debug/8 - End Interview")]
    private async void DebugEnd()
    {
        if (Application.isPlaying)
            await EndInterviewAsync();
    }
#endif
}
