
using System.Threading.Tasks;
using System.Threading;


public interface IInterviewConversationProvider
{
    Task<InterviewTurn> BeginAsync(
           InterviewScenarioDefinition scenario,
           CancellationToken cancellationToken);

    Task<InterviewTurnResult> SubmitResponseAsync(
        InterviewResponse response,
        CancellationToken cancellationToken);

    Task<InterviewTurn> RequestClarificationAsync(
        CancellationToken cancellationToken);

    void Reset();
}
