using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class InterviewSessionController : MonoBehaviour
{
    [Header("Configuration")]
    [SerializeField]
    private InterviewScenarioDefinition scenario;

    [Header("Runtime status — do not edit during Play Mode")]
    [SerializeField]
    private InterviewSessionState state =
        InterviewSessionState.NotStarted;

    [SerializeField]
    private int currentQuestionNumber;

    private IInterviewConversationProvider provider;
    private CancellationTokenSource sessionCancellation;
    private int sessionGeneration;

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

    public string DisplayRole =>
        scenario != null ? scenario.DisplayRole : string.Empty;

    public string Introduction =>
        scenario != null ? scenario.Introduction : string.Empty;

    public int CurrentQuestionNumber => currentQuestionNumber;

    public int TotalQuestions =>
        scenario != null && scenario.Questions != null
            ? scenario.Questions.Count
            : 0;

    public bool CanSubmit =>
        isActiveAndEnabled &&
        state == InterviewSessionState.AwaitingResponse;

    public bool CanRequestClarification => CanSubmit;

    // UI notification only. Do not use this to log conversation content.
    public event Action Changed;

    private void Awake()
    {
        ClearSession();
    }

    public void StartInterview()
    {
        if (!isActiveAndEnabled ||
            state != InterviewSessionState.NotStarted)
        {
            return;
        }

        if (!IsScenarioValid())
        {
            SetError("The interview scenario needs configuration.");
            return;
        }

        provider = new ScriptedInterviewConversationProvider();
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

        SetState(InterviewSessionState.LoadingTurn);

        try
        {
            InterviewTurn turn =
                await activeProvider.BeginAsync(scenario, token);

            if (!IsCurrentSession(generation))
                return;

            PresentTurn(turn);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentSession(generation))
                SetError("The interview operation was cancelled.");
        }
        catch (Exception)
        {
            if (IsCurrentSession(generation))
                SetError("The interview could not be started.");
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

        SetState(InterviewSessionState.SubmittingResponse);

        try
        {
            InterviewTurnResult result =
                await activeProvider.SubmitResponseAsync(response, token);

            if (!IsCurrentSession(generation))
                return;

            if (result == null)
                throw new InvalidOperationException();

            if (!result.InterviewComplete && result.NextTurn == null)
                throw new InvalidOperationException();
            behavior.CompleteTurn(submittedTurnIndex);

            if (result.InterviewComplete)
            {
                currentTurn = null;
                statusMessage = result.CompletionMessage ?? string.Empty;

                activeProvider.Reset();
                SetState(InterviewSessionState.Completed);
            }
            else
            {
                PresentTurn(result.NextTurn);
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentSession(generation))
                SetError("The interview operation was cancelled.");
        }
        catch (Exception)
        {
            if (IsCurrentSession(generation))
                SetError("The response could not be processed.");
        }
        finally
        {
            // Release this reference; this is not secure memory erasure.
            response = null;
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

        try
        {
            InterviewTurn turn =
                await activeProvider.RequestClarificationAsync(token);

            if (!IsCurrentSession(generation))
                return;

            PresentTurn(turn);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentSession(generation))
                SetError("The interview operation was cancelled.");
        }
        catch (Exception)
        {
            if (IsCurrentSession(generation))
                SetError("Clarification could not be loaded.");
        }
    }

    public void ResetInterview()
    {
        ClearSession();
        Changed?.Invoke();
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

        // Drop the old provider. An outstanding operation, if any, keeps
        // its own reference until it finishes, but cannot update this session.
        provider = null;
        currentTurn = null;
        currentQuestionNumber = 0;
        statusMessage = string.Empty;
        state = InterviewSessionState.NotStarted;
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
#endif
}
