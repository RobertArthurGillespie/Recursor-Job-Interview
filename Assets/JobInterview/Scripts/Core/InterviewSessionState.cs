public enum InterviewSessionState
{
    NotStarted,
    Introducing,
    LoadingTurn,
    AwaitingResponse,
    SubmittingResponse,
    RequestingClarification,
    Completed,
    Error,

    // Stage 6B host-backed states. Appended, never inserted: the scene serializes this enum by value.
    WaitingForHost,
    HostUnavailable,
    AuthenticationRequired,
    Ending,
    Abandoned,
    Expired,
    OutcomeUnknown
}