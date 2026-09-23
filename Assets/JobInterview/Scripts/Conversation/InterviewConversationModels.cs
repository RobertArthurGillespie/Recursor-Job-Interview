public sealed class InterviewTurn
{
    public int TurnIndex { get; }
    public string QuestionId { get; }
    public string DisplayText { get; }
    public bool IsClarification { get; }

    public InterviewTurn(
        int turnIndex,
        string questionId,
        string displayText,
        bool isClarification)
    {
        TurnIndex = turnIndex;
        QuestionId = questionId;
        DisplayText = displayText;
        IsClarification = isClarification;
    }
}

public sealed class InterviewResponse
{
    public int TurnIndex { get; }
    public string QuestionId { get; }

    // Ephemeral conversation content. This must never be copied into
    // ordinary Recursor telemetry, logs, PlayerPrefs, or a ScriptableObject.
    public string ResponseText { get; }

    public InterviewResponse(
        int turnIndex,
        string questionId,
        string responseText)
    {
        TurnIndex = turnIndex;
        QuestionId = questionId;
        ResponseText = responseText;
    }
}

public sealed class InterviewTurnResult
{
    public bool InterviewComplete { get; }
    public InterviewTurn NextTurn { get; }
    public string CompletionMessage { get; }

    private InterviewTurnResult(
        bool interviewComplete,
        InterviewTurn nextTurn,
        string completionMessage)
    {
        InterviewComplete = interviewComplete;
        NextTurn = nextTurn;
        CompletionMessage = completionMessage;
    }

    public static InterviewTurnResult ContinueWith(InterviewTurn nextTurn)
    {
        return new InterviewTurnResult(
            interviewComplete: false,
            nextTurn: nextTurn,
            completionMessage: string.Empty);
    }

    public static InterviewTurnResult Complete(string completionMessage)
    {
        return new InterviewTurnResult(
            interviewComplete: true,
            nextTurn: null,
            completionMessage: completionMessage);
    }
}