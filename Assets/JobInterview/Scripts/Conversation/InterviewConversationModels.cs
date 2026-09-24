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

// How a finished interview ended. Completed is natural completion; Abandoned and Expired
// come only from the host-backed provider (contract section 5).
public enum InterviewEndKind
{
    Completed,
    Abandoned,
    Expired
}

public sealed class InterviewTurnResult
{
    public bool InterviewComplete { get; }
    public InterviewTurn NextTurn { get; }
    public string CompletionMessage { get; }
    public InterviewEndKind EndKind { get; }

    private InterviewTurnResult(
        bool interviewComplete,
        InterviewTurn nextTurn,
        string completionMessage,
        InterviewEndKind endKind)
    {
        InterviewComplete = interviewComplete;
        NextTurn = nextTurn;
        CompletionMessage = completionMessage;
        EndKind = endKind;
    }

    public static InterviewTurnResult ContinueWith(InterviewTurn nextTurn)
    {
        return new InterviewTurnResult(
            interviewComplete: false,
            nextTurn: nextTurn,
            completionMessage: string.Empty,
            endKind: InterviewEndKind.Completed);
    }

    public static InterviewTurnResult Complete(string completionMessage)
    {
        return new InterviewTurnResult(
            interviewComplete: true,
            nextTurn: null,
            completionMessage: completionMessage,
            endKind: InterviewEndKind.Completed);
    }

    // The interview is over without natural completion. The message is fixed UI text.
    public static InterviewTurnResult Ended(InterviewEndKind endKind, string message)
    {
        return new InterviewTurnResult(
            interviewComplete: true,
            nextTurn: null,
            completionMessage: message,
            endKind: endKind);
    }
}