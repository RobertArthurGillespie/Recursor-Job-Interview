using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

// Job-interview telemetry I-T1 (Recursor docs/contracts/job-interview-telemetry-v1.md): the Unity sender. Event
// generation, topic boundaries, batching, ordering, Unity-owned exact resends, and finish, through the real
// bridge client against exact host telemetry replies. Deterministic: a manual clock, no waiting.
public class InterviewTelemetrySenderTests
{
    private const string AnswerSentinel = "APPLICANT-ANSWER-SENTINEL I count stock twice a week.";

    // Server ConversationIds of two successive interview attempts (contract section 3.4).
    private const string ConversationA = HostReply.ConversationId;
    private const string ConversationB = "4a2d0b8e5f3c4d7a9b0e1f2a3b4c5d6e";

    // A sender wired to a collector and to a bridge client, as InterviewSessionController wires them.
    private sealed class Rig
    {
        public static readonly DateTime Start = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

        public Rig(bool handshake = true)
        {
            H = new BridgeHarness();
            if (handshake)
                H.Handshake();

            Collector = new InterviewBehaviorCollector(() => Seconds, () => Start.AddSeconds(Seconds));
            Sender = new InterviewTelemetrySender(H.Client, new SequentialTelemetryIds(), H.Clock.Read, () => Start.AddSeconds(Seconds));
            Collector.Recorded += Sender.Record;
        }

        public BridgeHarness H { get; }
        public InterviewBehaviorCollector Collector { get; }
        public InterviewTelemetrySender Sender { get; }

        // The collector's own clock (response timings); the bridge and sender use H.Clock.
        public double Seconds { get; set; }

        // presented, started 4 s later, submitted 21 s after that (150 characters: "medium"), completed.
        public void AnswerTurn(int turn, bool topicCompleted, bool interviewCompleted = false)
        {
            Collector.Present(turn);
            Seconds += 4;
            Collector.ResponseStarted(turn);
            Seconds += 21;
            Collector.Submit(turn, 150);
            Collector.CompleteTurn(turn, topicCompleted, interviewCompleted);
        }

        public void Tick()
        {
            H.Client.Tick();
            Sender.Tick();
        }

        public void Advance(double seconds)
        {
            H.Clock.Now += seconds;
            Tick();
        }

        public List<string> TelemetryPosts =>
            H.Transport.Posted.Where(json => new PostedEnvelope(json).Type.StartsWith("telemetry.", StringComparison.Ordinal)).ToList();

        public PostedEnvelope Last => new PostedEnvelope(TelemetryPosts.Last());

        public void Reply(string outcome, string errorCode = "")
        {
            PostedEnvelope last = Last;
            H.Transport.Deliver(HostReply.TelemetryResult(last.CorrelationId, last.Str("batchId"), outcome, errorCode));
        }

        public void Finished(string outcome, string errorCode = "") =>
            H.Transport.Deliver(HostReply.TelemetryFinished(Last.CorrelationId, outcome, errorCode));
    }

    // A posted envelope, decoded with the permissive test reader (telemetry.batch carries an array).
    private sealed class PostedEnvelope
    {
        public PostedEnvelope(string json)
        {
            Json = json;
            Envelope = TestJson.Object(TestJson.Parse(json));
            Payload = TestJson.Object(Envelope["payload"]);
        }

        public string Json { get; }
        public Dictionary<string, object> Envelope { get; }
        public Dictionary<string, object> Payload { get; }

        public string Type => (string)Envelope["type"];
        public string CorrelationId => (string)Envelope["correlationId"];
        public string Str(string key) => (string)Payload[key];
        public long Int(string key) => (long)Payload[key];

        public List<Dictionary<string, object>> Events =>
            TestJson.Array(Payload["events"]).Select(TestJson.Object).ToList();

        // Everything after the correlation id: identical for an exact resend.
        public string PayloadJson => Json.Substring(Json.IndexOf("\"payload\":", StringComparison.Ordinal));
    }

    private static Dictionary<string, object> Metrics(Dictionary<string, object> e) => TestJson.Object(e["metrics"]);

    private static Dictionary<string, object> Payload(Dictionary<string, object> e) => TestJson.Object(e["payload"]);

    private static Dictionary<string, object> Submitted(PostedEnvelope batch) =>
        batch.Events.Single(e => (string)e["eventType"] == "interview_response_submitted");

    // -- Event generation ------------------------------------------------------------

    [Test]
    public void A_turn_becomes_the_contract_events_with_exactly_the_contract_fields()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: false);
        rig.Tick();

        PostedEnvelope batch = rig.Last;
        Assert.AreEqual("telemetry.batch", batch.Type);
        CollectionAssert.AreEqual(InterviewBridgeProtocol.PayloadKeys.TelemetryBatch.ToArray(), batch.Payload.Keys.ToArray());
        Assert.AreEqual("batch-0001", batch.Str("batchId"));
        Assert.AreEqual(0, batch.Int("batchSequence"));
        StringAssert.IsMatch("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z$", batch.Str("clientTimestampUtc"));

        List<Dictionary<string, object>> events = batch.Events;
        CollectionAssert.AreEqual(
            new[] { "interview_turn_presented", "interview_response_started", "interview_response_submitted", "interview_turn_completed" },
            events.Select(e => (string)e["eventType"]).ToArray());
        CollectionAssert.AreEqual(new long[] { 0, 1, 2, 3 }, events.Select(e => (long)e["sequenceNumber"]).ToArray());
        CollectionAssert.AreEqual(new[] { "evt-0001", "evt-0002", "evt-0003", "evt-0004" }, events.Select(e => (string)e["eventId"]).ToArray());

        foreach (Dictionary<string, object> e in events)
        {
            CollectionAssert.AreEqual(InterviewBridgeProtocol.PayloadKeys.TelemetryEvent.ToArray(), e.Keys.ToArray());
            StringAssert.IsMatch("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z$", (string)e["timestampUtc"]);
            Assert.AreEqual(0L, Metrics(e)["turnIndex"]);
        }

        // Category, actor and target per contract section 2.
        CollectionAssert.AreEqual(new[] { "system", "task_action", "task_action", "system" }, events.Select(e => (string)e["category"]).ToArray());
        CollectionAssert.AreEqual(new[] { "system", "applicant", "applicant", "system" }, events.Select(e => (string)e["actor"]).ToArray());
        CollectionAssert.AreEqual(new[] { "turn", "response", "response", "turn" }, events.Select(e => (string)e["target"]).ToArray());

        // Submitted: whole milliseconds and the length band only.
        Dictionary<string, object> submitted = events[2];
        CollectionAssert.AreEqual(new[] { "turnIndex", "responseLatencyMs", "responseDurationMs" }, Metrics(submitted).Keys.ToArray());
        Assert.AreEqual(4000L, Metrics(submitted)["responseLatencyMs"]);
        Assert.AreEqual(21000L, Metrics(submitted)["responseDurationMs"]);
        CollectionAssert.AreEqual(new[] { "responseLengthBand" }, Payload(submitted).Keys.ToArray());
        Assert.AreEqual("medium", Payload(submitted)["responseLengthBand"]);

        // Completed: structural topicCompleted only; no completionStatus on an ordinary turn.
        CollectionAssert.AreEqual(new[] { "topicCompleted" }, Payload(events[3]).Keys.ToArray());
        Assert.AreEqual(false, Payload(events[3])["topicCompleted"]);

        foreach (Dictionary<string, object> e in events.Take(2))
            Assert.IsEmpty(Payload(e));
    }

    [Test]
    public void A_clarification_request_is_an_applicant_turn_event_with_its_turn_index_only()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.Collector.Present(0);
        rig.Collector.ClarificationRequested(0);
        rig.Sender.EndRun();
        rig.Tick();

        Dictionary<string, object> clarification = rig.Last.Events[1];
        Assert.AreEqual("interview_clarification_requested", clarification["eventType"]);
        Assert.AreEqual("task_action", clarification["category"]);
        Assert.AreEqual("applicant", clarification["actor"]);
        Assert.AreEqual("turn", clarification["target"]);
        CollectionAssert.AreEqual(new[] { "turnIndex" }, Metrics(clarification).Keys.ToArray());
        Assert.IsEmpty(Payload(clarification));
    }

    [Test]
    public void Unobserved_or_out_of_range_timings_are_omitted_and_never_imputed()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);

        // No observed input start: neither timing.
        rig.Collector.Present(0);
        rig.Seconds += 10;
        rig.Collector.Submit(0, 5);
        rig.Collector.CompleteTurn(0, topicCompleted: true, interviewCompleted: false);

        // Typing for longer than 600000 ms: latency kept, duration omitted.
        rig.Collector.Present(1);
        rig.Seconds += 2.0004;
        rig.Collector.ResponseStarted(1);
        rig.Seconds += 700;
        rig.Collector.Submit(1, 400);
        rig.Collector.CompleteTurn(1, topicCompleted: true, interviewCompleted: false);

        rig.Tick();
        Dictionary<string, object> first = Submitted(rig.Last);
        CollectionAssert.AreEqual(new[] { "turnIndex" }, Metrics(first).Keys.ToArray());
        Assert.AreEqual("very-short", Payload(first)["responseLengthBand"]);

        rig.Reply("succeeded");
        rig.Tick();
        Dictionary<string, object> second = Submitted(rig.Last);
        CollectionAssert.AreEqual(new[] { "turnIndex", "responseLatencyMs" }, Metrics(second).Keys.ToArray());
        Assert.AreEqual(2000L, Metrics(second)["responseLatencyMs"]);
        Assert.AreEqual("long", Payload(second)["responseLengthBand"]);
    }

    [Test]
    public void Telemetry_never_carries_answer_text_identity_or_context()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.Collector.Present(0);
        rig.Collector.ResponseStarted(0);
        // The collector takes only a character count; the answer itself never reaches it.
        rig.Collector.Submit(0, AnswerSentinel.Length);
        rig.Collector.CompleteTurn(0, topicCompleted: true, interviewCompleted: true);
        rig.Sender.EndRun();
        rig.Tick();
        rig.Reply("succeeded");
        rig.Tick();

        foreach (string json in rig.TelemetryPosts)
            foreach (string marker in new[] { "APPLICANT-ANSWER", "stock", "userId", "UserId", "sessionId", "SessionId", "token", "Bearer", "eyJ", "lrn-", "context", "Context", "simId", "scenarioId" })
                StringAssert.DoesNotContain(marker, json);
    }

    // -- Topic boundaries and completion -----------------------------------------------

    [Test]
    public void Topic_boundaries_are_structural_a_new_core_question_or_genuine_completion()
    {
        var followUp = new InterviewTurn(1, "mst-q1", "a follow-up", isClarification: false);
        var nextCoreQuestion = new InterviewTurn(1, "mst-q2", "the next question", isClarification: false);

        Assert.IsFalse(InterviewTelemetrySender.IsTopicBoundary("mst-q1", followUp));
        Assert.IsTrue(InterviewTelemetrySender.IsTopicBoundary("mst-q1", nextCoreQuestion));
        Assert.IsTrue(InterviewTelemetrySender.IsTopicBoundary("mst-q1", null));
    }

    [Test]
    public void CompletionStatus_is_sent_only_on_the_last_turn_of_a_genuinely_completed_interview()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: true);
        rig.AnswerTurn(1, topicCompleted: false, interviewCompleted: true);
        rig.Tick();

        Dictionary<string, object> ordinary = rig.Last.Events[3];
        CollectionAssert.AreEqual(new[] { "topicCompleted" }, Payload(ordinary).Keys.ToArray());
        Assert.AreEqual(true, Payload(ordinary)["topicCompleted"]);

        rig.Reply("succeeded");
        rig.Tick();
        Dictionary<string, object> last = rig.Last.Events[3];
        CollectionAssert.AreEqual(new[] { "topicCompleted", "completionStatus" }, Payload(last).Keys.ToArray());
        Assert.AreEqual(true, Payload(last)["topicCompleted"]); // completion closes the topic
        Assert.AreEqual("completed", Payload(last)["completionStatus"]);
    }

    // -- Batching and ordering ---------------------------------------------------------

    [Test]
    public void Each_completed_turn_is_its_own_batch_sent_in_order_one_at_a_time()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: false);
        rig.AnswerTurn(1, topicCompleted: true);
        rig.Tick();
        rig.Tick();

        Assert.AreEqual(1, rig.TelemetryPosts.Count, "a second batch waits for the first one's result");
        Assert.AreEqual(0, rig.Last.Int("batchSequence"));
        Assert.AreEqual(4, rig.Last.Events.Count);

        rig.Reply("succeeded");
        rig.Tick();

        Assert.AreEqual(2, rig.TelemetryPosts.Count);
        Assert.AreEqual(1, rig.Last.Int("batchSequence"));
        Assert.AreEqual("batch-0002", rig.Last.Str("batchId"));
        CollectionAssert.AreEqual(new long[] { 4, 5, 6, 7 }, rig.Last.Events.Select(e => (long)e["sequenceNumber"]).ToArray());
    }

    [Test]
    public void The_finish_is_sent_only_after_every_batch_including_the_final_one_has_a_result()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: true);
        rig.AnswerTurn(1, topicCompleted: true);
        rig.Collector.Present(2); // uncut when the interview ends
        rig.Sender.EndRun();
        Assert.AreEqual(InterviewTelemetryRunStatus.Delivering, rig.Sender.Status);

        string[] expected = { "telemetry.batch", "telemetry.batch", "telemetry.batch" };
        for (int i = 0; i < expected.Length; i++)
        {
            rig.Tick();
            Assert.AreEqual(i + 1, rig.TelemetryPosts.Count);
            Assert.AreEqual(expected[i], rig.Last.Type);
            Assert.AreEqual(i, rig.Last.Int("batchSequence"));
            rig.Reply("succeeded");
        }

        // The final batch holds only the events after the last completed turn.
        Assert.AreEqual(1, new PostedEnvelope(rig.TelemetryPosts[2]).Events.Count);

        rig.Tick();
        Assert.AreEqual("telemetry.finish", rig.Last.Type);
        CollectionAssert.AreEqual(InterviewBridgeProtocol.PayloadKeys.TelemetryFinish.ToArray(), rig.Last.Payload.Keys.ToArray());
        Assert.AreEqual(2, rig.Last.Int("lastBatchSequence"));

        rig.Finished("ended");
        rig.Tick();
        Assert.AreEqual(InterviewTelemetryRunStatus.Ended, rig.Sender.Status);
        Assert.AreEqual(0, rig.Sender.OpenRunCount);
    }

    [Test]
    public void No_empty_batch_is_sent_and_an_empty_run_finishes_with_minus_one()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: false, interviewCompleted: true);
        rig.Sender.EndRun(); // nothing uncut
        rig.Tick();
        rig.Reply("succeeded");
        rig.Tick();
        Assert.AreEqual("telemetry.finish", rig.Last.Type);
        Assert.AreEqual(0, rig.Last.Int("lastBatchSequence"));
        Assert.AreEqual(2, rig.TelemetryPosts.Count);

        var empty = new Rig();
        empty.Sender.BeginRun(ConversationA);
        empty.Sender.EndRun();
        empty.Tick();
        Assert.AreEqual(1, empty.TelemetryPosts.Count);
        Assert.AreEqual("telemetry.finish", empty.Last.Type);
        Assert.AreEqual(-1, empty.Last.Int("lastBatchSequence"));
    }

    [Test]
    public void A_later_run_waits_for_the_earlier_runs_finish_and_numbers_from_zero()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: true, interviewCompleted: true);
        rig.Sender.BeginRun(ConversationB); // ends the first run
        rig.Collector.Clear(); // as InterviewSessionController.ClearSession does between interviews
        rig.AnswerTurn(0, topicCompleted: true);

        rig.Tick();
        Assert.AreEqual("batch-0001", rig.Last.Str("batchId"));
        rig.Reply("succeeded");
        rig.Tick();
        Assert.AreEqual("telemetry.finish", rig.Last.Type);
        rig.Tick();
        Assert.AreEqual(2, rig.TelemetryPosts.Count, "the second run waits for the first finish's result");

        rig.Finished("ended");
        rig.Tick();
        Assert.AreEqual("telemetry.batch", rig.Last.Type);
        Assert.AreEqual("batch-0002", rig.Last.Str("batchId"));
        Assert.AreEqual(0, rig.Last.Int("batchSequence"));
        Assert.AreEqual(0L, rig.Last.Events[0]["sequenceNumber"]);
        Assert.AreEqual(InterviewTelemetryRunStatus.Recording, rig.Sender.Status);
    }

    // -- Unity-owned exact resends ------------------------------------------------------

    [Test]
    public void An_ambiguous_outcome_resends_the_identical_batch_at_most_twice_after_1s_then_3s()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: true);
        rig.Tick();
        PostedEnvelope first = rig.Last;

        rig.Reply("transport_failure", "bridge_transport_failure");
        rig.Tick();
        rig.Advance(0.5);
        Assert.AreEqual(1, rig.TelemetryPosts.Count);
        Assert.AreEqual("bridge_transport_failure", rig.Sender.StatusCode);

        rig.Advance(0.5); // 1 s after the outcome
        Assert.AreEqual(2, rig.TelemetryPosts.Count);
        PostedEnvelope second = rig.Last;
        Assert.AreNotEqual(first.CorrelationId, second.CorrelationId);
        Assert.AreEqual(first.PayloadJson, second.PayloadJson, "same BatchId, sequence, event ids and content");

        rig.Reply("busy", "bridge_busy");
        rig.Tick();
        rig.Advance(2.5);
        Assert.AreEqual(2, rig.TelemetryPosts.Count);
        rig.Advance(0.5); // 3 s after the outcome
        Assert.AreEqual(3, rig.TelemetryPosts.Count);
        Assert.AreEqual(first.PayloadJson, rig.Last.PayloadJson);

        // The third send is the last: the batch is left unacknowledged and never sent again.
        rig.Reply("transport_failure", "bridge_transport_failure");
        rig.Advance(30);
        Assert.AreEqual(3, rig.TelemetryPosts.Count);

        rig.Sender.EndRun();
        rig.Tick();
        Assert.AreEqual("telemetry.finish", rig.Last.Type);
        Assert.AreEqual(0, rig.Last.Int("lastBatchSequence"));
        rig.Finished("incomplete", "bridge_batch_not_acknowledged");
        rig.Tick();
        Assert.AreEqual(InterviewTelemetryRunStatus.Incomplete, rig.Sender.Status);
        Assert.AreEqual("bridge_batch_not_acknowledged", rig.Sender.StatusCode);
    }

    [Test]
    public void No_reply_within_the_timeout_is_ambiguous_and_the_batch_is_resent_unchanged()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: true);
        rig.Tick();
        PostedEnvelope first = rig.Last;

        rig.Advance(InterviewBridgeClient.ReplyTimeoutSeconds);
        Assert.AreEqual(InterviewTelemetrySender.NoReplyCode, rig.Sender.StatusCode);
        rig.Advance(1);

        Assert.AreEqual(2, rig.TelemetryPosts.Count);
        Assert.AreEqual(first.PayloadJson, rig.Last.PayloadJson);

        // A late reply to the first attempt is ignored; the second attempt's reply counts.
        int ignored = rig.H.Client.IgnoredMessageCount;
        rig.H.Transport.Deliver(HostReply.TelemetryResult(first.CorrelationId, "batch-0001", "succeeded"));
        Assert.AreEqual(ignored + 1, rig.H.Client.IgnoredMessageCount);
        rig.Reply("succeeded");
        rig.Sender.EndRun();
        rig.Tick();
        Assert.AreEqual("telemetry.finish", rig.Last.Type);
    }

    [TestCase("rejected", "interview_field_not_allowed")]
    [TestCase("rejected", "bridge_duplicate_batch")]
    public void A_definitive_rejection_is_never_resent_and_the_next_batch_follows(string outcome, string code)
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: true);
        rig.AnswerTurn(1, topicCompleted: true);
        rig.Tick();
        rig.Reply(outcome, code);
        rig.Tick();

        Assert.AreEqual(2, rig.TelemetryPosts.Count);
        Assert.AreEqual(1, rig.Last.Int("batchSequence"));
        rig.Reply("succeeded");
        rig.Advance(30);
        Assert.AreEqual(2, rig.TelemetryPosts.Count);

        rig.Sender.EndRun();
        rig.Tick();
        rig.Finished("incomplete", "bridge_batch_rejected");
        rig.Advance(30);
        Assert.AreEqual(3, rig.TelemetryPosts.Count, "an incomplete finish for a refused batch is final");
        Assert.AreEqual(InterviewTelemetryRunStatus.Incomplete, rig.Sender.Status);
        Assert.AreEqual("bridge_batch_rejected", rig.Sender.StatusCode);
    }

    [TestCase("not_permitted", "bridge_no_session")]
    [TestCase("not_permitted", "bridge_session_ended")]
    [TestCase("forbidden", "session_not_found_or_forbidden")]
    [TestCase("authentication_required", "bridge_authentication_required")]
    public void A_host_refusal_stops_the_run_without_further_batches_or_a_finish(string outcome, string code)
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: true);
        rig.AnswerTurn(1, topicCompleted: true);
        rig.Tick();
        rig.Reply(outcome, code);
        rig.Sender.EndRun();
        rig.Advance(30);

        Assert.AreEqual(1, rig.TelemetryPosts.Count);
        Assert.AreEqual(InterviewTelemetryRunStatus.Incomplete, rig.Sender.Status);
        Assert.AreEqual(code, rig.Sender.StatusCode);
    }

    // -- Finish outcomes ---------------------------------------------------------------

    [TestCase("terminal_window_not_submitted")]
    [TestCase("bridge_transport_failure")]
    public void An_incomplete_finish_after_a_failed_end_is_resent_and_can_still_end(string code)
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.Sender.EndRun();
        rig.Tick();
        rig.Finished("incomplete", code);
        rig.Tick();
        rig.Advance(0.5);
        Assert.AreEqual(1, rig.TelemetryPosts.Count);
        Assert.AreEqual(InterviewTelemetryRunStatus.Delivering, rig.Sender.Status);

        rig.Advance(0.5);
        Assert.AreEqual(2, rig.TelemetryPosts.Count);
        Assert.AreEqual("telemetry.finish", rig.Last.Type);
        rig.Finished("ended");
        rig.Tick();
        Assert.AreEqual(InterviewTelemetryRunStatus.Ended, rig.Sender.Status);
    }

    [Test]
    public void A_finish_that_never_ends_gives_up_after_three_sends_as_incomplete()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.Sender.EndRun();
        rig.Tick();
        rig.Finished("busy", "bridge_busy");
        rig.Tick();
        rig.Advance(1);
        Assert.AreEqual(2, rig.TelemetryPosts.Count);
        rig.Finished("incomplete", "terminal_window_not_submitted");
        rig.Tick();
        rig.Advance(3);
        Assert.AreEqual(3, rig.TelemetryPosts.Count);
        rig.Finished("incomplete", "terminal_window_not_submitted");
        rig.Advance(30);

        Assert.AreEqual(3, rig.TelemetryPosts.Count);
        Assert.AreEqual(InterviewTelemetryRunStatus.Incomplete, rig.Sender.Status);
        Assert.AreEqual("terminal_window_not_submitted", rig.Sender.StatusCode);
    }

    [Test]
    public void Status_wording_never_claims_telemetry_was_saved()
    {
        foreach (InterviewTelemetryRunStatus status in Enum.GetValues(typeof(InterviewTelemetryRunStatus)))
        {
            string text = InterviewTelemetrySender.Describe(status);
            StringAssert.DoesNotContain("saved to", text);
            StringAssert.DoesNotContain("persisted", text);
            StringAssert.DoesNotContain("was stored", text);
        }

        StringAssert.Contains("does not confirm the data is stored", InterviewTelemetrySender.Describe(InterviewTelemetryRunStatus.Ended));
        StringAssert.Contains("Nothing is reported as saved", InterviewTelemetrySender.Describe(InterviewTelemetryRunStatus.Incomplete));
    }

    // -- Readiness and isolation from the conversation ------------------------------------

    [Test]
    public void Nothing_is_sent_or_spent_while_the_bridge_is_not_ready()
    {
        var rig = new Rig(handshake: false);
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: true);
        rig.Advance(300);
        Assert.IsEmpty(rig.TelemetryPosts);

        rig.H.Handshake();
        rig.Tick();
        Assert.AreEqual(1, rig.TelemetryPosts.Count);
        Assert.AreEqual(0, rig.Last.Int("batchSequence"));
    }

    [Test]
    public void A_conversation_reset_does_not_cancel_the_telemetry_lane()
    {
        var rig = new Rig();
        rig.H.Started();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: true);
        rig.Tick();

        rig.H.Client.ResetConversation();
        rig.Sender.EndRun();
        rig.Reply("succeeded");
        rig.Tick();

        Assert.AreEqual("telemetry.finish", rig.Last.Type);
        Assert.AreEqual(0, rig.Last.Int("lastBatchSequence"));
    }

    [Test]
    public void Telemetry_sending_never_blocks_a_conversation_operation()
    {
        var rig = new Rig();
        rig.H.Started();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: true);
        rig.Tick(); // a telemetry batch is in flight

        var answer = rig.H.Client.AnswerAsync(0, "an answer");
        Assert.AreEqual("conversation.answer", new PostedMessage(rig.H.Transport.Last).Type);
        rig.H.Transport.Deliver(HostReply.Active(new PostedMessage(rig.H.Transport.Last).CorrelationId, 1));
        Assert.IsTrue(answer.IsCompleted);
        Assert.AreEqual(InterviewOperationStatus.Succeeded, answer.Result.Status);
    }

    [Test]
    public void A_reply_for_another_batch_or_of_the_other_kind_is_ignored()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: true);
        rig.AnswerTurn(1, topicCompleted: true);
        rig.Tick();
        PostedEnvelope batch = rig.Last;

        int ignored = rig.H.Client.IgnoredMessageCount;
        rig.H.Transport.Deliver(HostReply.TelemetryResult(batch.CorrelationId, "batch-9999", "succeeded"));
        rig.H.Transport.Deliver(HostReply.TelemetryFinished(batch.CorrelationId, "ended"));
        rig.H.Transport.Deliver(HostReply.Error(batch.CorrelationId, "bridge_transport_failure"));
        rig.Tick();

        Assert.AreEqual(ignored + 3, rig.H.Client.IgnoredMessageCount);
        Assert.AreEqual(1, rig.TelemetryPosts.Count, "still waiting for this batch's own result");

        rig.Reply("succeeded");
        rig.Tick();
        Assert.AreEqual(1, rig.Last.Int("batchSequence"));
    }

    // -- Run conversation ids (Recursor contract section 3.4) ------------------------------

    private static string Conversation(string json) => new PostedEnvelope(json).Str("conversationId");

    private static int Occurrences(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Test]
    public void Every_batch_and_the_finish_carry_their_own_runs_conversation_id_and_no_event_does()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: true);
        rig.Collector.Present(1);
        rig.Sender.EndRun();
        rig.Tick();
        rig.Reply("succeeded");
        rig.Tick();
        rig.Reply("succeeded");
        rig.Tick();

        CollectionAssert.AreEqual(new[] { "telemetry.batch", "telemetry.batch", "telemetry.finish" },
            rig.TelemetryPosts.Select(json => new PostedEnvelope(json).Type).ToArray());
        foreach (string json in rig.TelemetryPosts)
        {
            var message = new PostedEnvelope(json);
            Assert.AreEqual(ConversationA, message.Str("conversationId"));
            Assert.AreEqual("conversationId", message.Payload.Keys.First());
            // Exactly once: the routing field of the envelope payload, never inside an event.
            Assert.AreEqual(1, Occurrences(json, ConversationA));
            if (message.Type == "telemetry.batch")
                foreach (Dictionary<string, object> e in message.Events)
                    CollectionAssert.AreEqual(InterviewBridgeProtocol.PayloadKeys.TelemetryEvent.ToArray(), e.Keys.ToArray());
        }
    }

    [Test]
    public void Two_runs_queued_across_a_reset_keep_their_own_ids_through_resends_and_finishes()
    {
        var rig = new Rig();
        rig.H.Started(); // conversation A is active in the bridge client
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: true);
        rig.Tick(); // A's batch 0 is in flight

        // Reset: A's conversation is cleared locally and its run ends, still delivering.
        rig.H.Client.ResetConversation();
        rig.Sender.EndRun();
        rig.Collector.Clear();

        // The next attempt's conversation B starts and records while A is still delivering.
        var start = rig.H.Client.StartAsync();
        rig.H.Transport.Deliver(HostReply.Active(rig.H.LastPosted.CorrelationId, 0, conversationId: ConversationB));
        Assert.AreEqual(InterviewOperationStatus.Succeeded, start.Result.Status);
        rig.Sender.BeginRun(ConversationB);
        rig.AnswerTurn(0, topicCompleted: true);
        Assert.AreEqual(2, rig.Sender.OpenRunCount);

        // A's ambiguous outcome: its exact resend still names A, although B is now the active conversation.
        PostedEnvelope original = rig.Last;
        rig.Reply("transport_failure", "bridge_transport_failure");
        rig.Tick();
        rig.Advance(1);
        Assert.AreNotEqual(original.CorrelationId, rig.Last.CorrelationId, "a resend was posted");
        Assert.AreEqual(ConversationA, rig.Last.Str("conversationId"));
        Assert.AreEqual(0, rig.Last.Int("batchSequence"));

        rig.Reply("succeeded");
        rig.Tick();
        Assert.AreEqual("telemetry.finish", rig.Last.Type);
        Assert.AreEqual(ConversationA, rig.Last.Str("conversationId"));
        rig.Finished("ended");
        rig.Tick();

        // Then B's own run, from sequence 0, under B.
        Assert.AreEqual("telemetry.batch", rig.Last.Type);
        Assert.AreEqual(ConversationB, rig.Last.Str("conversationId"));
        Assert.AreEqual(0, rig.Last.Int("batchSequence"));
        rig.Reply("succeeded");
        rig.Sender.EndRun();
        rig.Tick();
        Assert.AreEqual("telemetry.finish", rig.Last.Type);
        Assert.AreEqual(ConversationB, rig.Last.Str("conversationId"));
        rig.Finished("ended");
        rig.Tick();

        CollectionAssert.AreEqual(
            new[] { ConversationA, ConversationA, ConversationA, ConversationB, ConversationB },
            rig.TelemetryPosts.Select(Conversation).ToArray());
        Assert.AreEqual(InterviewTelemetryRunStatus.Ended, rig.Sender.Status);
        Assert.AreEqual(0, rig.Sender.OpenRunCount);
    }

    [Test]
    public void Resends_of_a_batch_and_of_a_finish_are_byte_identical_including_the_conversation_id()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: true);
        rig.Sender.EndRun();
        rig.Tick();
        PostedEnvelope batch = rig.Last;

        // Another conversation becomes active before the resend; the resend is unchanged.
        rig.Reply("transport_failure", "bridge_transport_failure");
        var start = rig.H.Client.StartAsync();
        rig.H.Transport.Deliver(HostReply.Active(rig.H.LastPosted.CorrelationId, 0, conversationId: ConversationB));
        Assert.AreEqual(InterviewOperationStatus.Succeeded, start.Result.Status);
        rig.Tick();
        rig.Advance(1);

        PostedEnvelope batchResend = rig.Last;
        Assert.AreEqual("telemetry.batch", batchResend.Type);
        Assert.AreNotEqual(batch.CorrelationId, batchResend.CorrelationId);
        Assert.AreEqual(batch.PayloadJson, batchResend.PayloadJson);
        StringAssert.Contains("\"conversationId\":\"" + ConversationA + "\"", batchResend.PayloadJson);

        rig.Reply("succeeded");
        rig.Tick();
        PostedEnvelope finish = rig.Last;
        rig.Finished("busy", "bridge_busy");
        rig.Tick();
        rig.Advance(1);

        PostedEnvelope finishResend = rig.Last;
        Assert.AreEqual("telemetry.finish", finishResend.Type);
        Assert.AreNotEqual(finish.CorrelationId, finishResend.CorrelationId);
        Assert.AreEqual(finish.PayloadJson, finishResend.PayloadJson);
        StringAssert.Contains("\"conversationId\":\"" + ConversationA + "\"", finishResend.PayloadJson);
        Assert.IsFalse(rig.TelemetryPosts.Any(json => json.Contains(ConversationB)), "the active conversation never replaces the run's own id");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("bad id!")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65 characters
    public void A_run_without_a_valid_conversation_id_sends_nothing_and_is_incomplete(string conversationId)
    {
        var rig = new Rig();
        rig.Sender.BeginRun(conversationId);
        rig.AnswerTurn(0, topicCompleted: true);
        rig.Collector.Present(1);
        rig.Sender.EndRun();
        rig.Advance(30);

        Assert.IsEmpty(rig.TelemetryPosts);
        Assert.AreEqual(InterviewTelemetryRunStatus.Incomplete, rig.Sender.Status);
        Assert.AreEqual(InterviewTelemetrySender.NoConversationCode, rig.Sender.StatusCode);
        Assert.AreEqual(0, rig.Sender.OpenRunCount);
    }

    [Test]
    public void A_run_without_an_id_between_two_valid_runs_sends_nothing_and_borrows_no_id()
    {
        var rig = new Rig();
        rig.Sender.BeginRun(ConversationA);
        rig.AnswerTurn(0, topicCompleted: true);
        rig.Sender.BeginRun(null);
        rig.Collector.Clear();
        rig.AnswerTurn(0, topicCompleted: true);
        Assert.AreEqual(InterviewTelemetrySender.NoConversationCode, rig.Sender.StatusCode);
        rig.Sender.BeginRun(ConversationB);
        rig.Collector.Clear();
        rig.AnswerTurn(0, topicCompleted: true);
        rig.Sender.EndRun();

        for (int i = 0; i < 4; i++)
        {
            rig.Tick();
            if (rig.Last.Type == "telemetry.batch")
                rig.Reply("succeeded");
            else
                rig.Finished("ended");
        }
        rig.Tick();

        CollectionAssert.AreEqual(new[] { ConversationA, ConversationA, ConversationB, ConversationB },
            rig.TelemetryPosts.Select(Conversation).ToArray());
        // Two batches of four events each: nothing the id-less run recorded was sent.
        Assert.AreEqual(8, rig.TelemetryPosts.Where(json => json.Contains("\"telemetry.batch\"")).Sum(json => new PostedEnvelope(json).Events.Count));
        Assert.AreEqual(InterviewTelemetryRunStatus.Ended, rig.Sender.Status);
    }

    [Test]
    public void The_builders_and_the_channel_refuse_a_missing_or_invalid_conversation_id_and_post_nothing()
    {
        foreach (string bad in new[] { null, "", "bad id!", new string('a', 65) })
        {
            Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildTelemetryBatch(Corr,
                new InterviewTelemetryBatch(bad, "batch-1", 0, "2026-10-30T12:00:00.000Z", new[] { WireEvent() })));
            Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildTelemetryFinish(Corr, bad, 0));

            var h = new BridgeHarness().Handshake();
            int posted = h.Transport.Posted.Count;
            InterviewTelemetryAttempt batch = h.Client.SendTelemetryBatch(
                new InterviewTelemetryBatch(bad, "batch-1", 0, "2026-10-30T12:00:00.000Z", new[] { WireEvent() }));
            InterviewTelemetryAttempt finish = h.Client.SendTelemetryFinish(bad, 0);

            Assert.AreEqual(InterviewTelemetryAttemptStatus.NotSent, batch.Status);
            Assert.AreEqual(InterviewTelemetryAttemptStatus.NotSent, finish.Status);
            Assert.AreEqual(posted, h.Transport.Posted.Count);
        }
    }

    // Unity's main-thread SynchronizationContext would post the provider's continuations to a later editor frame;
    // without one they run on the pool as soon as the bridge client completes the start, so these waits are
    // deterministic in the Editor and in the offline harness alike.
    private static void WithoutSynchronizationContext(Action body)
    {
        SynchronizationContext previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            body();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void Settle(Task task)
    {
        try
        {
            task.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // A faulted task is settled; the test inspects it.
        }

        Assert.IsTrue(task.IsCompleted, "the provider task did not complete");
    }

    [Test]
    public void A_reset_while_a_start_is_outstanding_never_lends_an_id_to_the_next_run() => WithoutSynchronizationContext(() =>
    {
        var rig = new Rig();
        var provider = new HostBridgeInterviewConversationProvider(rig.H.Client);
        var scenario = UnityEngine.ScriptableObject.CreateInstance<InterviewScenarioDefinition>();
        try
        {
            // Attempt 1: its start is posted, then the screen is reset before any reply.
            Task<InterviewTurn> first = provider.BeginAsync(scenario, CancellationToken.None);
            string firstStart = rig.H.LastPosted.CorrelationId;
            provider.Reset();
            Settle(first);
            Assert.IsTrue(first.IsFaulted, "a reset start yields no turn and so no run");

            // Its late reply (conversation A) is ignored and binds nothing.
            int ignored = rig.H.Client.IgnoredMessageCount;
            rig.H.Transport.Deliver(HostReply.Active(firstStart, 0, conversationId: ConversationA));
            Assert.AreEqual(ignored + 1, rig.H.Client.IgnoredMessageCount);

            // Attempt 2: the host answers this start with conversation B.
            Task<InterviewTurn> second = provider.BeginAsync(scenario, CancellationToken.None);
            rig.H.Transport.Deliver(HostReply.Active(rig.H.LastPosted.CorrelationId, 0, conversationId: ConversationB));
            Settle(second);
            InterviewTurn turn = second.Result;

            // The id comes only from that start's own reply, and only for that exact first turn.
            Assert.AreEqual(ConversationB, provider.StartedConversationIdFor(turn));
            Assert.IsNull(provider.StartedConversationIdFor(new InterviewTurn(turn.TurnIndex, turn.QuestionId, turn.DisplayText, false)));
            Assert.IsNull(provider.StartedConversationIdFor(null));

            // Attempt 1's reply arriving even later changes nothing.
            rig.H.Transport.Deliver(HostReply.Active(firstStart, 0, conversationId: ConversationA));
            Assert.AreEqual(ConversationB, provider.StartedConversationIdFor(turn));

            // As InterviewSessionController wires it: the run gets the id its own start returned.
            rig.Sender.BeginRun(provider.StartedConversationIdFor(turn));
            rig.AnswerTurn(0, topicCompleted: true);
            rig.Sender.EndRun();
            rig.Tick();
            rig.Reply("succeeded");
            rig.Tick();

            CollectionAssert.AreEqual(new[] { ConversationB, ConversationB }, rig.TelemetryPosts.Select(Conversation).ToArray());
            Assert.IsFalse(rig.TelemetryPosts.Any(json => json.Contains(ConversationA)));
        }
        finally
        {
            provider.Dispose();
            UnityEngine.Object.DestroyImmediate(scenario);
        }
    });

    // -- Messages ----------------------------------------------------------------------

    private const string Corr = "corr-0001";

    private static InterviewTelemetryWireEvent WireEvent(string payloadValue = "medium", string eventId = "evt-1", string timestamp = "2026-10-30T12:00:00.000Z") =>
        new InterviewTelemetryWireEvent(
            eventId, 0, timestamp, "interview_response_submitted", "task_action", "applicant", "response",
            new[] { new KeyValuePair<string, long>("turnIndex", 0) },
            new[] { new KeyValuePair<string, object>("responseLengthBand", payloadValue) });

    private static InterviewTelemetryBatch Batch(params InterviewTelemetryWireEvent[] events) =>
        new InterviewTelemetryBatch(ConversationA, "batch-1", 0, "2026-10-30T12:00:00.000Z", events);

    [Test]
    public void A_valid_batch_builds_and_the_same_batch_always_builds_the_same_payload()
    {
        string a = InterviewBridgeMessages.BuildTelemetryBatch(Corr, Batch(WireEvent()));
        string b = InterviewBridgeMessages.BuildTelemetryBatch("corr-0002", Batch(WireEvent()));

        Assert.AreEqual(new PostedEnvelope(a).PayloadJson, new PostedEnvelope(b).PayloadJson);
        StringAssert.DoesNotContain("\"*\"", a);
    }

    [TestCase(AnswerSentinel)]
    [TestCase("Medium")]
    [TestCase("very short")]
    [TestCase("")]
    public void A_payload_value_that_is_not_an_enum_member_is_never_posted(string value)
    {
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildTelemetryBatch(Corr, Batch(WireEvent(value))));
    }

    [Test]
    public void Malformed_batches_are_refused_before_posting()
    {
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildTelemetryBatch(Corr, Batch()));
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildTelemetryBatch(Corr, Batch(WireEvent(eventId: "has space"))));
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildTelemetryBatch(Corr, Batch(WireEvent(timestamp: "2026-10-30 12:00:00"))));
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildTelemetryBatch(Corr,
            new InterviewTelemetryBatch(ConversationA, "bad id!", 0, "2026-10-30T12:00:00.000Z", new[] { WireEvent() })));
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildTelemetryBatch(Corr,
            new InterviewTelemetryBatch(ConversationA, "batch-1", 100001, "2026-10-30T12:00:00.000Z", new[] { WireEvent() })));
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildTelemetryBatch(Corr,
            new InterviewTelemetryBatch(ConversationA, "batch-1", 0, "2026-10-30T12:00:00.000Z", Enumerable.Repeat(WireEvent(), 201).ToArray())));
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildTelemetryFinish(Corr, ConversationA, -2));
    }

    [Test]
    public void Telemetry_replies_parse_only_in_their_exact_shape()
    {
        InterviewBridgeInbound result = InterviewBridgeMessages.Parse(HostReply.TelemetryResult(Corr, "batch-1", "succeeded"));
        Assert.AreEqual(InterviewBridgeInboundKind.TelemetryResult, result.Kind);
        Assert.AreEqual("batch-1", result.Telemetry.BatchId);
        Assert.AreEqual("succeeded", result.Telemetry.Outcome);

        InterviewBridgeInbound finished = InterviewBridgeMessages.Parse(HostReply.TelemetryFinished(Corr, "incomplete", "bridge_batch_rejected"));
        Assert.AreEqual(InterviewBridgeInboundKind.TelemetryFinished, finished.Kind);
        Assert.AreEqual("bridge_batch_rejected", finished.Telemetry.ErrorCode);

        // The host echoes an empty batchId for a batch it could not read.
        Assert.AreEqual(InterviewBridgeInboundKind.TelemetryResult,
            InterviewBridgeMessages.Parse(HostReply.TelemetryResult(Corr, "", "rejected", "bridge_invalid_message")).Kind);

        var invalid = new[]
        {
            HostReply.TelemetryResult(Corr, "batch-1", "saved"),
            HostReply.TelemetryResult(Corr, "batch-1", "succeeded", "Free text"),
            HostReply.TelemetryResult(Corr, "bad id!", "succeeded"),
            HostReply.TelemetryFinished(Corr, "persisted"),
            HostReply.Envelope("telemetry.result", Corr, "{\"batchId\":\"batch-1\",\"outcome\":\"succeeded\"}"),
            HostReply.Envelope("telemetry.result", Corr, "{\"batchId\":\"batch-1\",\"outcome\":\"succeeded\",\"errorCode\":\"\",\"sessionId\":\"x\"}"),
            HostReply.Envelope("telemetry.finished", Corr, "{\"batchId\":\"batch-1\",\"outcome\":\"ended\",\"errorCode\":\"\"}"),
            HostReply.Envelope("telemetry.batch", Corr, "{\"batchId\":\"batch-1\",\"outcome\":\"succeeded\",\"errorCode\":\"\"}"),
        };

        foreach (string json in invalid)
        {
            InterviewBridgeInbound inbound = InterviewBridgeMessages.Parse(json);
            Assert.AreEqual(InterviewBridgeInboundKind.Invalid, inbound.Kind, json);
            Assert.IsNull(inbound.Telemetry);
        }
    }

    [Test]
    public void Telemetry_types_have_no_token_identity_or_text_fields()
    {
        var forbidden = new[] { "token", "jwt", "bearer", "authorization", "password", "secret", "session", "owner", "userid", "learner", "cookie", "answer", "text", "transcript", "context" };
        var types = new[]
        {
            typeof(InterviewTelemetrySender), typeof(InterviewTelemetryBatch), typeof(InterviewTelemetryWireEvent),
            typeof(InterviewTelemetryReply), typeof(InterviewTelemetryAttempt), typeof(InterviewBehaviorEvent),
        };

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (Type type in types)
        {
            IEnumerable<string> names = type.GetFields(all).Select(f => f.Name)
                .Concat(type.GetProperties(all).Select(p => p.Name))
                .Select(n => n.ToLowerInvariant());

            foreach (string name in names)
                foreach (string word in forbidden)
                    Assert.IsFalse(name.Contains(word), $"{type.Name}.{name}");
        }

        var keys = InterviewBridgeProtocol.PayloadKeys.TelemetryBatch
            .Concat(InterviewBridgeProtocol.PayloadKeys.TelemetryFinish)
            .Concat(InterviewBridgeProtocol.PayloadKeys.TelemetryEvent)
            .Concat(InterviewBridgeProtocol.PayloadKeys.TelemetryResult)
            .Concat(InterviewBridgeProtocol.PayloadKeys.TelemetryFinished)
            .Select(k => k.ToLowerInvariant());

        foreach (string key in keys)
            foreach (string word in forbidden)
                Assert.IsFalse(key.Contains(word), $"payload key {key}");
    }
}
