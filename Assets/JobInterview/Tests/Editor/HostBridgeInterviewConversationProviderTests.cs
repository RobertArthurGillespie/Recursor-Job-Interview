using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// Stage 6B: the host-backed IInterviewConversationProvider, plus content/telemetry isolation.
public class HostBridgeInterviewConversationProviderTests
{
    private const string AnswerSentinel = "APPLICANT-ANSWER-SENTINEL my private answer";

    private BridgeHarness harness;
    private HostBridgeInterviewConversationProvider provider;
    private InterviewScenarioDefinition scenario;

    [SetUp]
    public void SetUp()
    {
        harness = new BridgeHarness().Handshake();
        provider = new HostBridgeInterviewConversationProvider(harness.Client);
        scenario = ScriptableObject.CreateInstance<InterviewScenarioDefinition>();
    }

    [TearDown]
    public void TearDown()
    {
        provider.Dispose();
        UnityEngine.Object.DestroyImmediate(scenario);
    }

    private static IEnumerator WaitFor(Task task)
    {
        double deadline = Time.realtimeSinceStartupAsDouble + 5;
        while (!task.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Assert.IsTrue(task.IsCompleted, "task did not complete");
    }

    private IEnumerator Begin()
    {
        Task<InterviewTurn> begin = provider.BeginAsync(scenario, CancellationToken.None);
        harness.Transport.Deliver(HostReply.Active(harness.LastPosted.CorrelationId, 0));
        yield return WaitFor(begin);
        Assert.AreEqual(TaskStatus.RanToCompletion, begin.Status);
    }

    private static InterviewConversationException Failure(Task task)
    {
        Assert.IsTrue(task.IsFaulted, "expected a failure");
        return (InterviewConversationException)task.Exception.InnerExceptions.Single();
    }

    [UnityTest]
    public IEnumerator Begin_presents_the_server_prompt_as_the_first_turn()
    {
        Task<InterviewTurn> begin = provider.BeginAsync(scenario, CancellationToken.None);
        Assert.AreEqual("conversation.start", harness.LastPosted.Type);

        harness.Transport.Deliver(HostReply.Active(harness.LastPosted.CorrelationId, 0));
        yield return WaitFor(begin);

        InterviewTurn turn = begin.Result;
        Assert.AreEqual(0, turn.TurnIndex);
        Assert.AreEqual("mst-q1", turn.QuestionId);
        Assert.AreEqual(HostReply.InterviewerText, turn.DisplayText);
        Assert.IsFalse(turn.IsClarification);
    }

    [UnityTest]
    public IEnumerator Begin_refuses_a_scenario_asset_other_than_the_fixed_scenario()
    {
        typeof(InterviewScenarioDefinition)
            .GetField("scenarioId", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(scenario, "some-other-scenario");
        int posted = harness.Transport.Posted.Count;

        Task<InterviewTurn> begin = provider.BeginAsync(scenario, CancellationToken.None);
        yield return WaitFor(begin);

        Assert.AreEqual(InterviewConversationFailure.Rejected, Failure(begin).Failure);
        Assert.AreEqual(posted, harness.Transport.Posted.Count);
    }

    [UnityTest]
    public IEnumerator Submit_continues_with_the_next_server_prompt()
    {
        yield return Begin();

        Task<InterviewTurnResult> submit = provider.SubmitResponseAsync(new InterviewResponse(0, "mst-q1", AnswerSentinel), CancellationToken.None);
        Assert.AreEqual(AnswerSentinel, harness.LastPosted.Str("answerText"));
        harness.Transport.Deliver(HostReply.Active(harness.LastPosted.CorrelationId, 1, "follow_up_presented", "follow_up",
            interviewerText: "A follow-up question?"));
        yield return WaitFor(submit);

        Assert.IsFalse(submit.Result.InterviewComplete);
        Assert.AreEqual(1, submit.Result.NextTurn.TurnIndex);
        Assert.AreEqual("A follow-up question?", submit.Result.NextTurn.DisplayText);
    }

    [UnityTest]
    public IEnumerator Clarification_keeps_the_turn_and_marks_it_as_a_clarification()
    {
        yield return Begin();

        Task<InterviewTurn> clarify = provider.RequestClarificationAsync(CancellationToken.None);
        harness.Transport.Deliver(HostReply.Active(harness.LastPosted.CorrelationId, 0, "clarification_provided",
            interviewerText: "Put another way: how do you track stock?"));
        yield return WaitFor(clarify);

        Assert.AreEqual(0, clarify.Result.TurnIndex);
        Assert.IsTrue(clarify.Result.IsClarification);
        Assert.AreEqual("Put another way: how do you track stock?", clarify.Result.DisplayText);
    }

    [UnityTest]
    public IEnumerator Terminal_answers_map_to_completed_abandoned_and_expired()
    {
        foreach (var (state, text, kind, expected) in new[]
        {
            ("completed", "That covers everything for today.", InterviewEndKind.Completed, "That covers everything for today."),
            ("completed", "", InterviewEndKind.Completed, HostBridgeInterviewConversationProvider.CompletionFallbackMessage),
            ("abandoned", "", InterviewEndKind.Abandoned, HostBridgeInterviewConversationProvider.AbandonedMessage),
            ("expired", "", InterviewEndKind.Expired, HostBridgeInterviewConversationProvider.ExpiredMessage),
        })
        {
            TearDown();
            SetUp();
            yield return Begin();

            Task<InterviewTurnResult> submit = provider.SubmitResponseAsync(new InterviewResponse(0, "mst-q1", AnswerSentinel), CancellationToken.None);
            harness.Transport.Deliver(HostReply.Terminal(harness.LastPosted.CorrelationId, state, text));
            yield return WaitFor(submit);

            Assert.IsTrue(submit.Result.InterviewComplete, state);
            Assert.IsNull(submit.Result.NextTurn);
            Assert.AreEqual(kind, submit.Result.EndKind, state);
            Assert.AreEqual(expected, submit.Result.CompletionMessage, state);
        }
    }

    [UnityTest]
    public IEnumerator A_response_for_a_different_turn_or_question_is_refused_without_sending()
    {
        yield return Begin();
        int posted = harness.Transport.Posted.Count;

        Task<InterviewTurnResult> wrongTurn = provider.SubmitResponseAsync(new InterviewResponse(1, "mst-q1", AnswerSentinel), CancellationToken.None);
        Task<InterviewTurnResult> wrongQuestion = provider.SubmitResponseAsync(new InterviewResponse(0, "mst-q9", AnswerSentinel), CancellationToken.None);
        yield return WaitFor(wrongTurn);
        yield return WaitFor(wrongQuestion);

        Assert.AreEqual(InterviewConversationFailure.Rejected, Failure(wrongTurn).Failure);
        Assert.AreEqual(InterviewConversationFailure.Rejected, Failure(wrongQuestion).Failure);
        Assert.AreEqual(posted, harness.Transport.Posted.Count);
    }

    [UnityTest]
    public IEnumerator Failures_surface_only_a_category_and_a_fixed_message()
    {
        yield return Begin();

        Task<InterviewTurnResult> submit = provider.SubmitResponseAsync(new InterviewResponse(0, "mst-q1", AnswerSentinel), CancellationToken.None);
        for (int attempt = 0; attempt < InterviewBridgeClient.MaxTransportAttempts; attempt++)
        {
            harness.Transport.Deliver(HostReply.Error(harness.LastPosted.CorrelationId, "interview_conversation_internal_error", 500));
            harness.Clock.Now += 10;
            harness.Client.Tick();
        }
        yield return WaitFor(submit);

        InterviewConversationException failure = Failure(submit);
        Assert.AreEqual(InterviewConversationFailure.OutcomeUnknown, failure.Failure);
        Assert.AreEqual(InterviewConversationException.FixedMessage, failure.Message);
        StringAssert.DoesNotContain("SENTINEL", failure.ToString());
        StringAssert.DoesNotContain("internal_error", failure.Message);
        Assert.IsTrue(provider.HasUnresolvedOperation);
    }

    [UnityTest]
    public IEnumerator End_maps_acknowledgments_and_failures()
    {
        yield return Begin();

        Task<InterviewEndResult> end = provider.EndAsync("user_ended");
        harness.Transport.Deliver(HostReply.Terminal(harness.LastPosted.CorrelationId, "abandoned"));
        yield return WaitFor(end);
        Assert.IsTrue(end.Result.IsEnded);
        Assert.AreEqual(InterviewEndKind.Abandoned, end.Result.EndKind);
        Assert.IsFalse(provider.HasActiveConversation);

        Task<InterviewEndResult> again = provider.EndAsync("user_ended");
        yield return WaitFor(again);
        Assert.IsTrue(again.Result.IsEnded, "an already-ended conversation counts as ended");

        TearDown();
        SetUp();
        yield return Begin();
        Task<InterviewEndResult> unauthorized = provider.EndAsync("restart_requested");
        harness.Transport.Deliver(HostReply.Error(harness.LastPosted.CorrelationId, "bridge_authentication_required", 401));
        yield return WaitFor(unauthorized);
        Assert.IsFalse(unauthorized.Result.IsEnded);
        Assert.AreEqual(InterviewConversationFailure.AuthenticationRequired, unauthorized.Result.Failure);
        Assert.IsTrue(provider.HasActiveConversation, "a failed end never ends the conversation locally");
    }

    [Test]
    public void Availability_reflects_the_bridge_status()
    {
        Assert.AreEqual(InterviewHostAvailability.Ready, provider.Availability);

        var disabled = new HostBridgeInterviewConversationProvider(new BridgeHarness(transportEnabled: false).Client);
        Assert.AreEqual(InterviewHostAvailability.NotConfigured, disabled.Availability);

        var fresh = new BridgeHarness();
        var connecting = new HostBridgeInterviewConversationProvider(fresh.Client);
        connecting.Connect();
        Assert.AreEqual(InterviewHostAvailability.Connecting, connecting.Availability);
        fresh.Transport.Deliver(HostReply.Ready(fresh.LastPosted.CorrelationId, "version_mismatch"));
        Assert.AreEqual(InterviewHostAvailability.VersionMismatch, connecting.Availability);
    }

    // -- Content / telemetry isolation ---------------------------------------------

    [Test]
    public void Behavioral_telemetry_events_can_carry_no_conversation_text()
    {
        var properties = typeof(InterviewBehaviorEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "CompletionStatus", "EventType", "Kind", "ResponseDurationMs", "ResponseLatencyMs", "ResponseLengthBand", "TurnIndex" },
            properties);
    }

    [Test]
    public void The_bridge_and_host_provider_have_no_path_to_telemetry_or_logging()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var bridgeTypes = new[]
        {
            typeof(InterviewBridgeClient), typeof(HostBridgeInterviewConversationProvider), typeof(InterviewBridgeMessages),
            typeof(InterviewBridgeJson), typeof(WebGLInterviewBridgeTransport),
        };

        foreach (Type type in bridgeTypes)
        {
            foreach (FieldInfo field in type.GetFields(all))
            {
                Assert.AreNotEqual(typeof(InterviewBehaviorCollector), field.FieldType, $"{type.Name}.{field.Name}");
                Assert.AreNotEqual(typeof(InterviewSessionController), field.FieldType, $"{type.Name}.{field.Name}");
            }
        }
    }

    [Test]
    public void Submitting_through_the_bridge_logs_nothing()
    {
        var h = new BridgeHarness().Started();
        h.Client.AnswerAsync(0, AnswerSentinel);
        h.Transport.Deliver(HostReply.Error(h.LastPosted.CorrelationId, "bridge_transport_failure"));
        h.Clock.Now += 3;
        h.Client.Tick();
        h.Transport.Deliver("malformed " + AnswerSentinel);
        h.Transport.Deliver(HostReply.Active(h.LastPosted.CorrelationId, 1));

        LogAssert.NoUnexpectedReceived();
    }
}
