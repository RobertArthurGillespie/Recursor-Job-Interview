using System;
using System.Threading;
using System.Threading.Tasks;

public sealed class ScriptedInterviewConversationProvider
    : IInterviewConversationProvider
{
    private InterviewScenarioDefinition scenario;
    private int currentQuestionIndex = -1;
    private bool interviewStarted;
    private bool interviewComplete;

    public Task<InterviewTurn> BeginAsync(
        InterviewScenarioDefinition scenarioDefinition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (scenarioDefinition == null)
        {
            throw new ArgumentNullException(nameof(scenarioDefinition));
        }

        if (!scenarioDefinition.IsConfigured)
        {
            throw new InvalidOperationException(
                "The interview scenario is not completely configured.");
        }

        scenario = scenarioDefinition;
        currentQuestionIndex = 0;
        interviewStarted = true;
        interviewComplete = false;

        return Task.FromResult(CreateCurrentTurn(isClarification: false));
    }

    public Task<InterviewTurnResult> SubmitResponseAsync(
        InterviewResponse response,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInterviewIsActive();

        if (response == null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        InterviewQuestionDefinition currentQuestion =
            scenario.Questions[currentQuestionIndex];

        if (response.TurnIndex != currentQuestionIndex)
        {
            throw new InvalidOperationException(
                "The submitted response does not match the active interview turn.");
        }

        if (!string.Equals(
                response.QuestionId,
                currentQuestion.QuestionId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The submitted response does not match the active interview question.");
        }

        if (string.IsNullOrWhiteSpace(response.ResponseText))
        {
            throw new InvalidOperationException(
                "An empty response cannot be submitted.");
        }

        // Deliberately do not retain response.ResponseText. The scripted provider
        // only confirms the active turn and advances to the next question.
        currentQuestionIndex++;

        if (currentQuestionIndex >= scenario.Questions.Count)
        {
            interviewComplete = true;

            return Task.FromResult(
                InterviewTurnResult.Complete(scenario.CompletionMessage));
        }

        return Task.FromResult(
            InterviewTurnResult.ContinueWith(
                CreateCurrentTurn(isClarification: false)));
    }

    public Task<InterviewTurn> RequestClarificationAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureInterviewIsActive();

        return Task.FromResult(
            CreateCurrentTurn(isClarification: true));
    }

    public void Reset()
    {
        scenario = null;
        currentQuestionIndex = -1;
        interviewStarted = false;
        interviewComplete = false;
    }

    private InterviewTurn CreateCurrentTurn(bool isClarification)
    {
        EnsureInterviewIsActive();

        InterviewQuestionDefinition question =
            scenario.Questions[currentQuestionIndex];

        string displayText = question.PromptText;

        if (isClarification &&
            !string.IsNullOrWhiteSpace(question.ClarificationText))
        {
            displayText = question.ClarificationText;
        }

        return new InterviewTurn(
            turnIndex: currentQuestionIndex,
            questionId: question.QuestionId,
            displayText: displayText,
            isClarification: isClarification);
    }

    private void EnsureInterviewIsActive()
    {
        if (!interviewStarted || scenario == null)
        {
            throw new InvalidOperationException(
                "The interview has not been started.");
        }

        if (interviewComplete)
        {
            throw new InvalidOperationException(
                "The interview has already been completed.");
        }

        if (currentQuestionIndex < 0 ||
            currentQuestionIndex >= scenario.Questions.Count)
        {
            throw new InvalidOperationException(
                "The active interview turn is invalid.");
        }
    }
}