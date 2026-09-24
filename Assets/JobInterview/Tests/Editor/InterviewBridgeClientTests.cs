using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

// Stage 6B: the Unity bridge client's handshake, correlation, lanes, turn tracking, retries,
// supersession, and fail-closed handling, against exact Stage 6A host replies.
public class InterviewBridgeClientTests
{
    private const string Answer = "APPLICANT-ANSWER-SENTINEL I count stock twice a week.";

    private static void AssertPending(Task<InterviewOperationOutcome> task) =>
        Assert.IsFalse(task.IsCompleted, "operation should still be pending");

    private static InterviewOperationOutcome Done(Task<InterviewOperationOutcome> task)
    {
        Assert.IsTrue(task.IsCompleted, "operation should have completed");
        return task.Result;
    }

    // -- Handshake -----------------------------------------------------------------

    [Test]
    public void Connect_sends_hello_with_the_fixed_scenario_and_the_supplied_application_version()
    {
        var h = new BridgeHarness("0.1.0");
        h.Client.Connect();

        PostedMessage hello = h.LastPosted;
        Assert.AreEqual("bridge.hello", hello.Type);
        Assert.AreEqual("medical-supply-technician-interview-v1", hello.Str("scenarioId"));
        Assert.AreEqual("0.1.0", hello.Str("simVersion"));
        Assert.AreEqual(2, hello.Payload.Count);
        Assert.AreEqual(InterviewBridgeStatus.Connecting, h.Client.Status);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" 1.0")]
    [TestCase("1.0.0\n")]
    [TestCase("not/a/version")]
    public void An_invalid_application_version_disables_the_bridge_with_no_fallback(string version)
    {
        var h = new BridgeHarness(version);
        h.Client.Connect();
        h.Client.Tick();

        Assert.AreEqual(InterviewBridgeStatus.Disabled, h.Client.Status);
        Assert.AreEqual(InterviewBridgeDisabledReason.InvalidSimVersion, h.Client.DisabledReason);
        Assert.IsEmpty(h.Transport.Posted);
        Assert.AreEqual(InterviewOperationStatus.NotReady, Done(h.Client.StartAsync()).Status);
        Assert.IsEmpty(h.Transport.Posted);
    }

    [Test]
    public void A_disabled_transport_sends_nothing()
    {
        var h = new BridgeHarness(transportEnabled: false);
        h.Client.Connect();

        Assert.AreEqual(InterviewBridgeStatus.Disabled, h.Client.Status);
        Assert.AreEqual(InterviewBridgeDisabledReason.TransportUnavailable, h.Client.DisabledReason);
        Assert.IsEmpty(h.Transport.Posted);
    }

    [Test]
    public void Ready_is_accepted_only_for_an_outstanding_hello_correlation()
    {
        var h = new BridgeHarness();
        h.Client.Connect();

        h.Transport.Deliver(HostReply.Ready("corr-9999"));
        Assert.AreEqual(InterviewBridgeStatus.Connecting, h.Client.Status);
        Assert.AreEqual(1, h.Client.IgnoredMessageCount);

        h.Transport.Deliver(HostReply.Ready(h.LastPosted.CorrelationId));
        Assert.AreEqual(InterviewBridgeStatus.Ready, h.Client.Status);

        // A duplicate ready changes nothing.
        h.Transport.Deliver(HostReply.Ready(h.Ids.IssuedCorrelations[0], "signed_out"));
        Assert.AreEqual(InterviewBridgeStatus.Ready, h.Client.Status);
    }

    [Test]
    public void Hello_is_retried_with_backoff_until_ready_and_then_stops()
    {
        var h = new BridgeHarness();
        h.Client.Connect();
        Assert.AreEqual(1, h.Transport.Posted.Count);

        h.Clock.Now = 0.5;
        h.Client.Tick();
        Assert.AreEqual(1, h.Transport.Posted.Count);

        h.Clock.Now = 1.0;
        h.Client.Tick();
        Assert.AreEqual(2, h.Transport.Posted.Count);
        Assert.AreNotEqual(h.Ids.IssuedCorrelations[0], h.LastPosted.CorrelationId);
        Assert.AreEqual("bridge.hello", h.LastPosted.Type);

        // A reply to the earlier hello still completes the handshake.
        h.Transport.Deliver(HostReply.Ready(h.Ids.IssuedCorrelations[0]));
        Assert.AreEqual(InterviewBridgeStatus.Ready, h.Client.Status);

        h.Clock.Now = 1000;
        h.Client.Tick();
        Assert.AreEqual(2, h.Transport.Posted.Count);
    }

    [Test]
    public void Hello_stops_after_the_maximum_attempts_and_reports_unresponsive()
    {
        var h = new BridgeHarness();
        h.Client.Connect();

        for (int i = 0; i < 200; i++)
        {
            h.Clock.Now += 5;
            h.Client.Tick();
        }

        Assert.AreEqual(InterviewBridgeClient.MaxHelloAttempts, h.Transport.Posted.Count);
        Assert.AreEqual(InterviewBridgeStatus.Unresponsive, h.Client.Status);
    }

    [TestCase("signed_out", InterviewBridgeStatus.SignedOut)]
    [TestCase("scenario_mismatch", InterviewBridgeStatus.ScenarioMismatch)]
    [TestCase("version_mismatch", InterviewBridgeStatus.VersionMismatch)]
    public void Non_ready_host_states_block_every_operation_without_sending(string hostState, InterviewBridgeStatus expected)
    {
        var h = new BridgeHarness();
        h.Client.Connect();
        h.Transport.Deliver(HostReply.Ready(h.LastPosted.CorrelationId, hostState));

        Assert.AreEqual(expected, h.Client.Status);
        int posted = h.Transport.Posted.Count;
        Assert.AreEqual(InterviewOperationStatus.NotReady, Done(h.Client.StartAsync()).Status);
        Assert.AreEqual(posted, h.Transport.Posted.Count);
    }

    // -- Conversation tracking -----------------------------------------------------

    [Test]
    public void Start_sends_only_a_request_id_and_tracks_the_server_conversation()
    {
        var h = new BridgeHarness().Handshake();
        Task<InterviewOperationOutcome> start = h.Client.StartAsync();

        PostedMessage posted = h.LastPosted;
        Assert.AreEqual("conversation.start", posted.Type);
        CollectionAssert.AreEqual(new[] { "requestId" }, posted.Payload.Keys.ToArray());
        AssertPending(start);
        Assert.IsFalse(h.Client.HasActiveConversation);

        h.Transport.Deliver(HostReply.Active(posted.CorrelationId, 0));

        Assert.AreEqual(InterviewOperationStatus.Succeeded, Done(start).Status);
        Assert.AreEqual(HostReply.ConversationId, h.Client.ActiveConversationId);
        Assert.AreEqual(0, h.Client.CurrentTurnIndex);
    }

    [Test]
    public void Answer_advances_the_turn_only_from_an_accepted_host_response()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);

        PostedMessage posted = h.LastPosted;
        Assert.AreEqual("conversation.answer", posted.Type);
        Assert.AreEqual(HostReply.ConversationId, posted.Str("conversationId"));
        Assert.AreEqual(0, posted.Int("turnIndex"));
        Assert.AreEqual(Answer, posted.Str("answerText"));
        Assert.AreEqual(0, h.Client.CurrentTurnIndex, "no advance before the host replies");

        h.Transport.Deliver(HostReply.Active(posted.CorrelationId, 1, "follow_up_presented", "follow_up"));

        Assert.AreEqual(InterviewOperationStatus.Succeeded, Done(answer).Status);
        Assert.AreEqual(1, h.Client.CurrentTurnIndex);
    }

    [Test]
    public void Clarify_sends_no_text_and_never_advances_the_turn()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> clarify = h.Client.ClarifyAsync(0);

        PostedMessage posted = h.LastPosted;
        Assert.AreEqual("conversation.clarify", posted.Type);
        CollectionAssert.AreEqual(new[] { "conversationId", "requestId", "turnIndex" }, posted.Payload.Keys.ToArray());

        h.Transport.Deliver(HostReply.Active(posted.CorrelationId, 0, "clarification_provided"));

        Assert.AreEqual(InterviewOperationStatus.Succeeded, Done(clarify).Status);
        Assert.AreEqual(0, h.Client.CurrentTurnIndex);
    }

    [Test]
    public void Natural_completion_ends_the_conversation_and_blocks_further_operations()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);
        h.Transport.Deliver(HostReply.Terminal(h.LastPosted.CorrelationId, "completed", "Thank you."));

        InterviewOperationOutcome outcome = Done(answer);
        Assert.AreEqual(InterviewOperationStatus.Succeeded, outcome.Status);
        Assert.IsTrue(outcome.Result.IsTerminal);
        Assert.IsFalse(h.Client.HasActiveConversation);

        int posted = h.Transport.Posted.Count;
        Assert.AreEqual(InterviewOperationStatus.Rejected, Done(h.Client.AnswerAsync(0, Answer)).Status);
        Assert.AreEqual(InterviewOperationStatus.ConversationEnded, Done(h.Client.EndAsync("user_ended")).Status);
        Assert.AreEqual(posted, h.Transport.Posted.Count);
    }

    [Test]
    public void Only_one_start_answer_or_clarify_may_be_outstanding()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> first = h.Client.AnswerAsync(0, Answer);
        int posted = h.Transport.Posted.Count;

        Assert.AreEqual(InterviewOperationStatus.Busy, Done(h.Client.AnswerAsync(0, Answer)).Status);
        Assert.AreEqual(InterviewOperationStatus.Busy, Done(h.Client.ClarifyAsync(0)).Status);
        Assert.AreEqual(InterviewOperationStatus.Busy, Done(h.Client.StartAsync()).Status);
        Assert.AreEqual(posted, h.Transport.Posted.Count);
        AssertPending(first);
    }

    [Test]
    public void Local_validation_rejects_without_sending()
    {
        var h = new BridgeHarness().Started();
        int posted = h.Transport.Posted.Count;

        InterviewOperationOutcome wrongTurn = Done(h.Client.AnswerAsync(3, Answer));
        Assert.AreEqual(InterviewOperationStatus.Rejected, wrongTurn.Status);
        Assert.AreEqual(InterviewBridgeProtocol.ServerErrorCodes.TurnMismatch, wrongTurn.ErrorCode);

        Assert.AreEqual(InterviewOperationStatus.Rejected, Done(h.Client.AnswerAsync(0, "   ")).Status);
        Assert.AreEqual(InterviewBridgeProtocol.ServerErrorCodes.AnswerTooLong,
            Done(h.Client.AnswerAsync(0, new string('a', 2001))).ErrorCode);
        Assert.AreEqual(InterviewOperationStatus.Rejected, Done(h.Client.EndAsync("because")).Status);
        Assert.AreEqual(InterviewOperationStatus.Rejected, Done(h.Client.StartAsync()).Status, "already active");
        Assert.AreEqual(posted, h.Transport.Posted.Count);
    }

    // -- Stale, duplicate, malformed -----------------------------------------------

    [Test]
    public void A_duplicate_result_is_ignored_and_cannot_advance_twice()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);
        string correlation = h.LastPosted.CorrelationId;

        h.Transport.Deliver(HostReply.Active(correlation, 1));
        h.Transport.Deliver(HostReply.Active(correlation, 1));
        h.Transport.Deliver(HostReply.Active(correlation, 2));

        Assert.AreEqual(InterviewOperationStatus.Succeeded, Done(answer).Status);
        Assert.AreEqual(1, h.Client.CurrentTurnIndex);
        Assert.AreEqual(2, h.Client.IgnoredMessageCount);
    }

    [Test]
    public void Unsolicited_malformed_and_unknown_messages_change_nothing()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);
        string correlation = h.LastPosted.CorrelationId;

        h.Transport.Deliver(HostReply.Active("corr-7777", 1));
        h.Transport.Deliver("garbage");
        h.Transport.Deliver(HostReply.Active(correlation, 1).Replace("\"ok\":true", "\"ok\":true,\"extra\":1"));
        h.Transport.Deliver(HostReply.Envelope("conversation.unknown", correlation, "{}"));
        h.Transport.Deliver(null);

        AssertPending(answer);
        Assert.AreEqual(0, h.Client.CurrentTurnIndex);
        Assert.AreEqual(HostReply.ConversationId, h.Client.ActiveConversationId);
        Assert.AreEqual(5, h.Client.IgnoredMessageCount);

        // The genuine reply still completes it.
        h.Transport.Deliver(HostReply.Active(correlation, 1));
        Assert.AreEqual(InterviewOperationStatus.Succeeded, Done(answer).Status);
    }

    [Test]
    public void A_well_formed_result_that_does_not_fit_the_operation_changes_nothing()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);

        // Wrong conversation for this answer.
        h.Transport.Deliver(HostReply.Active(h.LastPosted.CorrelationId, 1, conversationId: "another-conversation"));

        InterviewOperationOutcome outcome = Done(answer);
        Assert.AreEqual(InterviewOperationStatus.Rejected, outcome.Status);
        Assert.AreEqual(0, h.Client.CurrentTurnIndex);
        Assert.AreEqual(HostReply.ConversationId, h.Client.ActiveConversationId);
        Assert.IsTrue(h.Client.HasUnresolvedOrdinaryOperation);
        Assert.AreEqual(InterviewOperationStatus.Rejected, Done(h.Client.AnswerAsync(0, Answer)).Status);
    }

    [Test]
    public void A_turn_that_skips_ahead_is_rejected()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);
        h.Transport.Deliver(HostReply.Active(h.LastPosted.CorrelationId, 5));

        Assert.AreEqual(InterviewOperationStatus.Rejected, Done(answer).Status);
        Assert.AreEqual(0, h.Client.CurrentTurnIndex);
    }

    // -- End and supersession ------------------------------------------------------

    [Test]
    public void End_supersedes_an_outstanding_answer_and_late_results_are_discarded()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);
        string answerCorrelation = h.LastPosted.CorrelationId;

        Task<InterviewOperationOutcome> end = h.Client.EndAsync("user_ended");
        PostedMessage endMessage = h.LastPosted;
        Assert.AreEqual("conversation.end", endMessage.Type);
        Assert.AreEqual("user_ended", endMessage.Str("reason"));
        AssertPending(answer);

        h.Transport.Deliver(HostReply.Terminal(endMessage.CorrelationId, "abandoned"));

        Assert.AreEqual(InterviewOperationStatus.Succeeded, Done(end).Status);
        Assert.AreEqual(InterviewOperationStatus.Superseded, Done(answer).Status);
        Assert.IsFalse(h.Client.HasActiveConversation);

        // The host's superseded notice and a late success are both ignored.
        h.Transport.Deliver(HostReply.Error(answerCorrelation, "bridge_superseded"));
        h.Transport.Deliver(HostReply.Active(answerCorrelation, 1));
        Assert.IsFalse(h.Client.HasActiveConversation);
        Assert.AreEqual(-1, h.Client.CurrentTurnIndex);
        Assert.AreEqual(2, h.Client.IgnoredMessageCount);
    }

    [Test]
    public void Repeated_end_requests_are_one_logical_operation()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> first = h.Client.EndAsync("user_ended");
        int posted = h.Transport.Posted.Count;

        Task<InterviewOperationOutcome> second = h.Client.EndAsync("user_ended");
        Task<InterviewOperationOutcome> third = h.Client.EndAsync("restart_requested");

        Assert.AreSame(first, second);
        Assert.AreSame(first, third);
        Assert.AreEqual(posted, h.Transport.Posted.Count);
        Assert.AreEqual(1, h.Transport.Posted.Select(j => new PostedMessage(j)).Count(m => m.Type == "conversation.end"));
    }

    [Test]
    public void Restart_uses_restart_requested_and_only_a_terminal_acknowledgment_ends_it()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> end = h.Client.EndAsync("restart_requested");
        Assert.AreEqual("restart_requested", h.LastPosted.Str("reason"));
        Assert.IsTrue(h.Client.HasActiveConversation, "still active until acknowledged");

        h.Transport.Deliver(HostReply.Terminal(h.LastPosted.CorrelationId, "abandoned"));

        Assert.AreEqual(InterviewOperationStatus.Succeeded, Done(end).Status);
        Assert.IsFalse(h.Client.HasActiveConversation);
    }

    [Test]
    public void End_acknowledges_an_already_completed_conversation()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> end = h.Client.EndAsync("user_ended");
        h.Transport.Deliver(HostReply.Terminal(h.LastPosted.CorrelationId, "completed"));

        InterviewOperationOutcome outcome = Done(end);
        Assert.AreEqual(InterviewOperationStatus.Succeeded, outcome.Status);
        Assert.AreEqual("completed", outcome.Result.ConversationState);
    }

    [TestCase("interview_conversation_operation_superseded")]
    [TestCase("bridge_superseded")]
    public void A_superseded_answer_ends_the_conversation_without_content(string code)
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);
        h.Transport.Deliver(HostReply.Error(h.LastPosted.CorrelationId, code, 409));

        InterviewOperationOutcome outcome = Done(answer);
        Assert.AreEqual(InterviewOperationStatus.Superseded, outcome.Status);
        Assert.IsNull(outcome.Result);
        Assert.IsFalse(h.Client.HasActiveConversation);
    }

    [TestCase("interview_conversation_already_terminal")]
    [TestCase("interview_conversation_not_found_or_forbidden")]
    [TestCase("bridge_conversation_mismatch")]
    public void A_conversation_the_host_no_longer_has_is_ended_locally(string code)
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);
        h.Transport.Deliver(HostReply.Error(h.LastPosted.CorrelationId, code, 409));

        Assert.AreEqual(InterviewOperationStatus.ConversationEnded, Done(answer).Status);
        Assert.IsFalse(h.Client.HasActiveConversation);
    }

    // -- Unknown-outcome retries ---------------------------------------------------

    [Test]
    public void Unknown_outcomes_retry_with_the_same_request_id_and_payload_at_most_twice()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);
        PostedMessage first = h.LastPosted;

        h.Transport.Deliver(HostReply.Error(first.CorrelationId, "bridge_transport_failure"));
        AssertPending(answer);

        h.Clock.Now += 1.9;
        h.Client.Tick();
        Assert.AreEqual(first.CorrelationId, h.LastPosted.CorrelationId, "not before the delay");

        h.Clock.Now += 0.2;
        h.Client.Tick();
        PostedMessage second = h.LastPosted;

        h.Transport.Deliver(HostReply.Error(second.CorrelationId, "interview_conversation_internal_error", 500));
        h.Clock.Now += 5;
        h.Client.Tick();
        PostedMessage third = h.LastPosted;

        h.Transport.Deliver(HostReply.Error(third.CorrelationId, "bridge_transport_failure"));
        h.Clock.Now += 100;
        h.Client.Tick();

        var answers = h.Transport.Posted.Select(j => new PostedMessage(j)).Where(m => m.Type == "conversation.answer").ToList();
        Assert.AreEqual(3, answers.Count, "original + two retries");
        Assert.AreEqual(3, answers.Select(m => m.CorrelationId).Distinct().Count(), "fresh correlation per attempt");
        Assert.IsTrue(answers.All(m => m.RequestId == first.RequestId), "same RequestId");
        Assert.IsTrue(answers.All(m => m.Str("answerText") == Answer && m.Int("turnIndex") == 0 &&
                                       m.Str("conversationId") == HostReply.ConversationId), "same payload");
        Assert.AreEqual(1, h.Ids.IssuedRequests.Count(r => r == first.RequestId));

        InterviewOperationOutcome outcome = Done(answer);
        Assert.AreEqual(InterviewOperationStatus.OutcomeUnknown, outcome.Status);
        Assert.AreEqual(0, h.Client.CurrentTurnIndex, "no advance");

        // No new RequestId is minted to recover it: further answers are blocked; End remains.
        int posted = h.Transport.Posted.Count;
        Assert.AreEqual(InterviewOperationStatus.Rejected, Done(h.Client.AnswerAsync(0, Answer)).Status);
        Assert.AreEqual(posted, h.Transport.Posted.Count);
        Assert.IsTrue(h.Client.HasUnresolvedOrdinaryOperation);
        AssertPending(h.Client.EndAsync("user_ended"));
    }

    [Test]
    public void A_reply_timeout_is_an_unknown_outcome_and_the_late_reply_is_ignored()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> clarify = h.Client.ClarifyAsync(0);
        PostedMessage first = h.LastPosted;

        h.Clock.Now += InterviewBridgeClient.ReplyTimeoutSeconds;
        h.Client.Tick();
        h.Clock.Now += 2;
        h.Client.Tick();
        PostedMessage retry = h.LastPosted;

        Assert.AreEqual("conversation.clarify", retry.Type);
        Assert.AreEqual(first.RequestId, retry.RequestId);
        Assert.AreNotEqual(first.CorrelationId, retry.CorrelationId);

        h.Transport.Deliver(HostReply.Active(first.CorrelationId, 0, "clarification_provided"));
        AssertPending(clarify);

        h.Transport.Deliver(HostReply.Active(retry.CorrelationId, 0, "clarification_provided"));
        Assert.AreEqual(InterviewOperationStatus.Succeeded, Done(clarify).Status);
    }

    [TestCase("interview_conversation_invalid_request")]
    [TestCase("interview_conversation_turn_mismatch")]
    [TestCase("interview_conversation_request_conflict")]
    [TestCase("interview_conversation_answer_too_long")]
    [TestCase("interview_conversation_payload_too_large")]
    [TestCase("interview_conversation_request_budget_exceeded")]
    [TestCase("bridge_invalid_message")]
    [TestCase("bridge_authentication_required")]
    [TestCase("interview_conversation_already_terminal")]
    [TestCase("interview_conversation_operation_superseded")]
    [TestCase("interview_conversation_not_found_or_forbidden")]
    [TestCase("bridge_busy")]
    [TestCase("bridge_not_ready")]
    public void Definitive_failures_are_never_retried(string code)
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);
        h.Transport.Deliver(HostReply.Error(h.LastPosted.CorrelationId, code, 400));

        Assert.IsTrue(answer.IsCompleted);
        Assert.AreNotEqual(InterviewOperationStatus.OutcomeUnknown, answer.Result.Status);

        h.Clock.Now += 1000;
        h.Client.Tick();
        Assert.AreEqual(1, h.Transport.Posted.Select(j => new PostedMessage(j)).Count(m => m.Type == "conversation.answer"));
    }

    [Test]
    public void Scenario_and_version_mismatch_and_session_failure_are_not_retried()
    {
        foreach (string code in new[] { "interview_conversation_scenario_mismatch", "interview_conversation_scenario_not_allowed", "bridge_session_start_failed" })
        {
            var h = new BridgeHarness().Handshake();
            Task<InterviewOperationOutcome> start = h.Client.StartAsync();
            h.Transport.Deliver(HostReply.Error(h.LastPosted.CorrelationId, code, 400));
            h.Clock.Now += 1000;
            h.Client.Tick();

            Assert.AreEqual(InterviewOperationStatus.Rejected, Done(start).Status, code);
            Assert.AreEqual(1, h.Transport.Posted.Select(j => new PostedMessage(j)).Count(m => m.Type == "conversation.start"), code);
        }
    }

    [Test]
    public void Authentication_failure_is_reported_and_blocks_further_sends()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);
        h.Transport.Deliver(HostReply.Error(h.LastPosted.CorrelationId, "bridge_authentication_required", 401));

        Assert.AreEqual(InterviewOperationStatus.AuthenticationRequired, Done(answer).Status);
        Assert.AreEqual(InterviewBridgeStatus.SignedOut, h.Client.Status);
        Assert.AreEqual(InterviewOperationStatus.NotReady, Done(h.Client.ClarifyAsync(0)).Status);
    }

    [Test]
    public void A_retry_waits_for_readiness_and_resumes_with_the_same_request_id()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);
        PostedMessage first = h.LastPosted;

        h.Transport.Deliver(HostReply.Error(first.CorrelationId, "bridge_transport_failure"));
        h.Clock.Now += 2;
        h.Client.Tick();
        PostedMessage second = h.LastPosted;

        // The host lost readiness: the earlier attempt's outcome is still unknown, so park.
        h.Transport.Deliver(HostReply.Error(second.CorrelationId, "bridge_not_ready"));
        Assert.AreEqual(InterviewBridgeStatus.Connecting, h.Client.Status);
        Assert.AreEqual("bridge.hello", h.LastPosted.Type);
        AssertPending(answer);

        h.Clock.Now += 0.5;
        h.Client.Tick();
        Assert.AreEqual("bridge.hello", h.LastPosted.Type, "nothing sent while not ready");

        h.Transport.Deliver(HostReply.Ready(h.LastPosted.CorrelationId));
        PostedMessage resumed = h.LastPosted;
        Assert.AreEqual("conversation.answer", resumed.Type);
        Assert.AreEqual(first.RequestId, resumed.RequestId);
        Assert.AreEqual(Answer, resumed.Str("answerText"));

        h.Transport.Deliver(HostReply.Active(resumed.CorrelationId, 1));
        Assert.AreEqual(InterviewOperationStatus.Succeeded, Done(answer).Status);
        Assert.AreEqual(1, h.Client.CurrentTurnIndex);
    }

    [Test]
    public void Busy_on_a_retry_leaves_the_outcome_unknown_instead_of_retrying_again()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);
        h.Transport.Deliver(HostReply.Error(h.LastPosted.CorrelationId, "bridge_transport_failure"));
        h.Clock.Now += 2;
        h.Client.Tick();
        h.Transport.Deliver(HostReply.Error(h.LastPosted.CorrelationId, "bridge_busy"));
        h.Clock.Now += 100;
        h.Client.Tick();

        Assert.AreEqual(InterviewOperationStatus.OutcomeUnknown, Done(answer).Status);
        Assert.AreEqual(2, h.Transport.Posted.Select(j => new PostedMessage(j)).Count(m => m.Type == "conversation.answer"));
    }

    [Test]
    public void An_end_with_an_unknown_outcome_is_resent_with_the_identical_request_id_and_payload()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> end = h.Client.EndAsync("user_ended");
        PostedMessage original = h.LastPosted;

        for (int attempt = 0; attempt < InterviewBridgeClient.MaxTransportAttempts; attempt++)
        {
            h.Transport.Deliver(HostReply.Error(h.LastPosted.CorrelationId, "bridge_transport_failure"));
            h.Clock.Now += 10;
            h.Client.Tick();
        }

        Assert.AreEqual(InterviewOperationStatus.OutcomeUnknown, Done(end).Status);
        Assert.IsTrue(h.Client.HasActiveConversation, "not ended until acknowledged");
        Assert.IsTrue(h.Client.HasUnresolvedEnd);

        // An explicit second click re-sends the same logical end, even with a different reason.
        Task<InterviewOperationOutcome> again = h.Client.EndAsync("restart_requested");
        PostedMessage resent = h.LastPosted;
        Assert.AreEqual(original.RequestId, resent.RequestId);
        Assert.AreEqual("user_ended", resent.Str("reason"));
        Assert.AreEqual(original.Str("conversationId"), resent.Str("conversationId"));
        Assert.AreNotEqual(original.CorrelationId, resent.CorrelationId);

        h.Transport.Deliver(HostReply.Terminal(resent.CorrelationId, "abandoned"));
        Assert.AreEqual(InterviewOperationStatus.Succeeded, Done(again).Status);
        Assert.IsFalse(h.Client.HasActiveConversation);
    }

    [Test]
    public void A_start_with_an_unknown_outcome_is_resent_with_the_same_request_id()
    {
        var h = new BridgeHarness().Handshake();
        Task<InterviewOperationOutcome> start = h.Client.StartAsync();
        string requestId = h.LastPosted.RequestId;

        for (int attempt = 0; attempt < InterviewBridgeClient.MaxTransportAttempts; attempt++)
        {
            h.Transport.Deliver(HostReply.Error(h.LastPosted.CorrelationId, "bridge_transport_failure"));
            h.Clock.Now += 10;
            h.Client.Tick();
        }

        Assert.AreEqual(InterviewOperationStatus.OutcomeUnknown, Done(start).Status);
        Assert.IsTrue(h.Client.HasUnresolvedStart);

        h.Client.StartAsync();
        Assert.AreEqual(requestId, h.LastPosted.RequestId);
        Assert.AreEqual(1, h.Ids.IssuedRequests.Count);
    }

    [Test]
    public void Reset_cancels_outstanding_operations_and_their_late_replies_are_ignored()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);
        string correlation = h.LastPosted.CorrelationId;

        h.Client.ResetConversation();

        Assert.AreEqual(InterviewOperationStatus.Cancelled, Done(answer).Status);
        Assert.IsFalse(h.Client.HasActiveConversation);

        h.Transport.Deliver(HostReply.Active(correlation, 1));
        Assert.IsFalse(h.Client.HasActiveConversation);
        Assert.AreEqual(-1, h.Client.CurrentTurnIndex);
    }

    [Test]
    public void Reset_during_a_sent_start_keeps_its_request_id_for_the_next_start()
    {
        var h = new BridgeHarness().Handshake();
        h.Client.StartAsync();
        string requestId = h.LastPosted.RequestId;

        h.Client.ResetConversation();
        h.Client.StartAsync();

        Assert.AreEqual(requestId, h.LastPosted.RequestId);
    }

    [Test]
    public void Best_effort_end_is_sent_once_and_never_after_an_explicit_end()
    {
        var h = new BridgeHarness().Started();
        h.Client.SendBestEffortEnd();
        h.Client.SendBestEffortEnd();

        var ends = h.Transport.Posted.Select(j => new PostedMessage(j)).Where(m => m.Type == "conversation.end").ToList();
        Assert.AreEqual(1, ends.Count);
        Assert.AreEqual("user_ended", ends[0].Str("reason"));

        var h2 = new BridgeHarness().Started();
        h2.Client.EndAsync("user_ended");
        h2.Client.SendBestEffortEnd();
        Assert.AreEqual(1, h2.Transport.Posted.Select(j => new PostedMessage(j)).Count(m => m.Type == "conversation.end"));
    }

    [Test]
    public void Expiry_is_tracked_from_host_relative_milliseconds_and_cleared_when_terminal()
    {
        var h = new BridgeHarness().Started();

        Assert.IsTrue(h.Client.TryGetRemainingSeconds(out double idle, out double absolute));
        Assert.AreEqual(720, idle, 0.001);
        Assert.AreEqual(1800, absolute, 0.001);

        h.Clock.Now += 60;
        h.Client.TryGetRemainingSeconds(out idle, out absolute);
        Assert.AreEqual(660, idle, 0.001);

        h.Client.EndAsync("user_ended");
        h.Transport.Deliver(HostReply.Terminal(h.LastPosted.CorrelationId, "abandoned"));
        Assert.IsFalse(h.Client.TryGetRemainingSeconds(out _, out _));
    }

    [Test]
    public void Dispose_detaches_from_the_transport()
    {
        var h = new BridgeHarness().Started();
        Task<InterviewOperationOutcome> answer = h.Client.AnswerAsync(0, Answer);
        string correlation = h.LastPosted.CorrelationId;

        h.Client.Dispose();

        Assert.AreEqual(InterviewOperationStatus.Cancelled, Done(answer).Status);
        h.Transport.Deliver(HostReply.Active(correlation, 1));
        Assert.AreEqual(InterviewBridgeStatus.Disabled, h.Client.Status);
    }
}
