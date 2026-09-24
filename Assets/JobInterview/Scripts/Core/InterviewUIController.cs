using System;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class InterviewUIController : MonoBehaviour
{
    [Header("Session")]
    [SerializeField] private InterviewSessionController session;

    [Header("Text")]
    [SerializeField] private TMP_Text roleText;
    [SerializeField] private TMP_Text progressText;
    [SerializeField] private TMP_Text interviewerText;
    [SerializeField] private TMP_Text statusText;

    [Header("Response")]
    [SerializeField] private TMP_InputField responseInput;

    [Header("Buttons")]
    [SerializeField] private Button startButton;
    [SerializeField] private Button continueButton;
    [SerializeField] private Button submitButton;
    [SerializeField] private Button clarifyButton;
    [SerializeField] private Button resetButton;

    [Tooltip("Stage 6B: ends a host-backed interview (conversation.end, user_ended). Optional; hidden in scripted mode.")]
    [SerializeField] private Button endButton;

    private InterviewTurn displayedTurn;
    private bool operationInProgress;
    private bool endInProgress;
    private bool listenersAttached;
    private int viewGeneration;
    private string localError = string.Empty;

    private void OnEnable()
    {
        if (!ReferencesAssigned())
        {
            // Static configuration message: no conversation content.
            Debug.LogError("Interview UI has missing Inspector references.");
            enabled = false;
            return;
        }

        viewGeneration++;
        operationInProgress = false;
        endInProgress = false;
        localError = string.Empty;
        displayedTurn = null;

        responseInput.SetTextWithoutNotify(string.Empty);

        session.Changed += Refresh;
        responseInput.onValueChanged.AddListener(OnResponseChanged);

        startButton.onClick.AddListener(OnStartClicked);
        continueButton.onClick.AddListener(OnContinueClicked);
        submitButton.onClick.AddListener(OnSubmitClicked);
        clarifyButton.onClick.AddListener(OnClarifyClicked);
        resetButton.onClick.AddListener(OnResetClicked);

        if (endButton != null)
            endButton.onClick.AddListener(OnEndClicked);

        listenersAttached = true;
        Refresh();
    }

    private void Start()
    {
        // Refresh after all scene components have run Awake.
        Refresh();
    }

    private void OnDisable()
    {
        viewGeneration++;

        if (listenersAttached)
        {
            session.Changed -= Refresh;
            responseInput.onValueChanged.RemoveListener(OnResponseChanged);

            startButton.onClick.RemoveListener(OnStartClicked);
            continueButton.onClick.RemoveListener(OnContinueClicked);
            submitButton.onClick.RemoveListener(OnSubmitClicked);
            clarifyButton.onClick.RemoveListener(OnClarifyClicked);
            resetButton.onClick.RemoveListener(OnResetClicked);

            if (endButton != null)
                endButton.onClick.RemoveListener(OnEndClicked);

            listenersAttached = false;

            // Hiding/disabling this interview screen ends its local session.
            if (session != null)
                session.ResetInterview();
        }

        if (responseInput != null)
            responseInput.SetTextWithoutNotify(string.Empty);

        displayedTurn = null;
        operationInProgress = false;
        endInProgress = false;
        localError = string.Empty;
    }

    private bool ReferencesAssigned()
    {
        return session != null &&
               roleText != null &&
               progressText != null &&
               interviewerText != null &&
               statusText != null &&
               responseInput != null &&
               startButton != null &&
               continueButton != null &&
               submitButton != null &&
               clarifyButton != null &&
               resetButton != null;
    }

    private void OnStartClicked()
    {
        if (operationInProgress || !session.CanStart)
            return;

        localError = string.Empty;
        session.StartInterview();
    }

    private async void OnContinueClicked()
    {
        if (operationInProgress ||
            session.State != InterviewSessionState.Introducing)
        {
            return;
        }

        await RunOperationAsync(session.ContinueFromIntroductionAsync);
    }

    private async void OnSubmitClicked()
    {
        if (operationInProgress ||
            !session.CanSubmit ||
            displayedTurn == null ||
            !ReferenceEquals(displayedTurn, session.CurrentTurn))
        {
            return;
        }

        string answer = responseInput.text;

        if (string.IsNullOrWhiteSpace(answer))
            return;

        InterviewTurn submittedTurn = displayedTurn;

        // Clear before invoking the provider, which may finish immediately.
        // A second click therefore cannot reuse this answer for the next turn.
        responseInput.SetTextWithoutNotify(string.Empty);

        try
        {
            await RunOperationAsync(
                () => session.SubmitResponseAsync(submittedTurn, answer));

            // The host was not ready, so nothing was sent: give the draft back on the same prompt.
            if (this != null &&
                isActiveAndEnabled &&
                session.ConsumeSubmissionNotSent() &&
                ReferenceEquals(session.CurrentTurn, submittedTurn) &&
                string.IsNullOrEmpty(responseInput.text))
            {
                responseInput.SetTextWithoutNotify(answer);
                Refresh();
            }
        }
        finally
        {
            // Release the local reference; not secure memory erasure.
            answer = null;
        }
    }

    private async void OnClarifyClicked()
    {
        if (operationInProgress ||
            !session.CanRequestClarification ||
            displayedTurn == null ||
            !ReferenceEquals(displayedTurn, session.CurrentTurn))
        {
            return;
        }

        InterviewTurn turn = displayedTurn;

        // Existing draft text is preserved when the same question is clarified.
        await RunOperationAsync(
            () => session.RequestClarificationAsync(turn));
    }

    private async void OnResetClicked()
    {
        if (endInProgress || session.IsEnding)
            return;

        if (session.UsesHostBridge)
        {
            // Host-backed: conversation.end (restart_requested) first; the session resets only
            // after a definitive terminal acknowledgment and never starts a new attempt itself.
            await RunEndAsync(session.RestartInterviewAsync);

            if (this == null || !isActiveAndEnabled || !session.IsAtPreStart)
                return;
        }
        else
        {
            session.ResetInterview();
        }

        ClearLocalView();
    }

    private async void OnEndClicked()
    {
        // Deliberately not gated by operationInProgress: end may supersede an outstanding answer.
        if (endInProgress || !session.CanEnd)
            return;

        await RunEndAsync(session.EndInterviewAsync);
    }

    private async Task RunEndAsync(Func<Task> endOperation)
    {
        int generation = viewGeneration;
        endInProgress = true;
        localError = string.Empty;
        Refresh();

        try
        {
            await endOperation();
        }
        catch (Exception)
        {
            // Do not expose provider exceptions or conversation content.
            if (generation == viewGeneration && this != null)
                localError = "The interview could not be ended. Please try again.";
        }
        finally
        {
            if (this != null && generation == viewGeneration)
            {
                endInProgress = false;
                if (isActiveAndEnabled)
                    Refresh();
            }
        }
    }

    private void ClearLocalView()
    {
        if (this == null || !isActiveAndEnabled)
            return;

        // Invalidates UI completion callbacks belonging to the old session.
        viewGeneration++;
        operationInProgress = false;
        localError = string.Empty;
        displayedTurn = null;

        endInProgress = false;
        responseInput.SetTextWithoutNotify(string.Empty);
        Refresh();
    }

    private void OnResponseChanged(string unusedText)
    {
        // Never retain or log the event's text argument.
        if (!operationInProgress && !string.IsNullOrWhiteSpace(unusedText))
            session.NotifyResponseStarted(displayedTurn);
        Refresh();
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        if (operationInProgress)
            return;

        int generation = viewGeneration;

        operationInProgress = true;
        localError = string.Empty;
        Refresh();

        try
        {
            await operation();
        }
        catch (Exception)
        {
            // Do not expose provider exceptions or conversation content.
            if (generation == viewGeneration && this != null)
            {
                localError =
                    "The action could not be completed. Please reset the interview.";
            }
        }
        finally
        {
            if (this != null &&
                isActiveAndEnabled &&
                generation == viewGeneration)
            {
                operationInProgress = false;
                Refresh();
            }
        }
    }

    private void Refresh()
    {
        if (!isActiveAndEnabled || !ReferencesAssigned())
            return;

        InterviewSessionState state = session.State;
        InterviewTurn nextTurn = session.CurrentTurn;

        bool sameQuestion =
            displayedTurn != null &&
            nextTurn != null &&
            displayedTurn.TurnIndex == nextTurn.TurnIndex &&
            string.Equals(
                displayedTurn.QuestionId,
                nextTurn.QuestionId,
                StringComparison.Ordinal);

        // Preserve a draft only while staying on the same question,
        // including when displaying its clarification.
        if (!sameQuestion)
            responseInput.SetTextWithoutNotify(string.Empty);

        displayedTurn = nextTurn;

        bool hasLocalError = !string.IsNullOrEmpty(localError);
        bool awaiting =
            state == InterviewSessionState.AwaitingResponse;
        bool ending = endInProgress || session.IsEnding;

        bool canAnswer =
            awaiting &&
            session.CanSubmit &&
            !operationInProgress &&
            !ending &&
            !hasLocalError;

        roleText.text = session.DisplayRole;

        progressText.text =
            state == InterviewSessionState.Completed
                ? "Interview complete"
                : nextTurn != null
                    ? session.TotalQuestions > 0
                        ? $"Question {session.CurrentQuestionNumber} of {session.TotalQuestions}"
                        : $"Question {session.CurrentQuestionNumber}"
                    : string.Empty;

        switch (state)
        {
            case InterviewSessionState.NotStarted:
                interviewerText.text =
                    "Select Start Interview when you are ready.";
                break;

            case InterviewSessionState.Introducing:
                interviewerText.text = session.Introduction;
                break;

            case InterviewSessionState.Completed:
                interviewerText.text = session.StatusMessage;
                break;

            case InterviewSessionState.Error:
                interviewerText.text =
                    "The interview has stopped. Select Reset Interview to try again.";
                break;

            case InterviewSessionState.WaitingForHost:
            case InterviewSessionState.HostUnavailable:
            case InterviewSessionState.AuthenticationRequired:
            case InterviewSessionState.OutcomeUnknown:
                // Fixed, sanitized session text only.
                interviewerText.text = session.StatusMessage;
                break;

            case InterviewSessionState.Ending:
                interviewerText.text = "Ending the interview…";
                break;

            case InterviewSessionState.Abandoned:
                interviewerText.text = string.IsNullOrEmpty(session.StatusMessage)
                    ? "The interview has ended."
                    : session.StatusMessage;
                break;

            case InterviewSessionState.Expired:
                interviewerText.text = string.IsNullOrEmpty(session.StatusMessage)
                    ? "This interview has timed out."
                    : session.StatusMessage;
                break;

            default:
                interviewerText.text =
                    nextTurn != null
                        ? nextTurn.DisplayText
                        : "Preparing the next question…";
                break;
        }

        bool statusShownAsMain =
            state == InterviewSessionState.Completed ||
            state == InterviewSessionState.Abandoned ||
            state == InterviewSessionState.Expired ||
            state == InterviewSessionState.WaitingForHost ||
            state == InterviewSessionState.HostUnavailable ||
            state == InterviewSessionState.AuthenticationRequired ||
            state == InterviewSessionState.OutcomeUnknown;

        statusText.text = hasLocalError
            ? localError
            : statusShownAsMain
                ? string.Empty
                : !string.IsNullOrEmpty(session.StatusMessage)
                    ? session.StatusMessage
                    : awaiting
                        ? session.ExpiryWarning
                        : string.Empty;

        if (!hasLocalError && operationInProgress)
            statusText.text = "Please wait…";

        responseInput.interactable = canAnswer;

        startButton.interactable =
            session.CanStart &&
            !operationInProgress &&
            !hasLocalError;

        continueButton.interactable =
            state == InterviewSessionState.Introducing &&
            !operationInProgress &&
            !hasLocalError;

        submitButton.interactable =
            canAnswer &&
            !string.IsNullOrWhiteSpace(responseInput.text);

        clarifyButton.interactable =
            canAnswer && session.CanRequestClarification;

        resetButton.interactable =
            !ending &&
            (state != InterviewSessionState.NotStarted ||
             operationInProgress ||
             hasLocalError);

        if (endButton != null)
        {
            if (endButton.gameObject.activeSelf != session.UsesHostBridge)
                endButton.gameObject.SetActive(session.UsesHostBridge);

            endButton.interactable = session.CanEnd && !ending;
        }
    }
}
