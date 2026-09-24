using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

// Stage 6B: wire-format tests. The expected values are the committed Stage 6A protocol
// (Recursor repository: InterviewBridgeProtocol.cs and interview-bridge.js).
public class InterviewBridgeMessageTests
{
    private const string Corr = "corr-0001";
    private const string Conv = HostReply.ConversationId;

    // -- Protocol constants ---------------------------------------------------------

    [Test]
    public void Protocol_constants_match_stage_6a_exactly()
    {
        Assert.AreEqual("recursor.interview.bridge", InterviewBridgeProtocol.Name);
        Assert.AreEqual(1, InterviewBridgeProtocol.Version);
        Assert.AreEqual("medical-supply-technician-interview-v1", InterviewBridgeProtocol.ScenarioId);
        Assert.AreEqual(2000, InterviewBridgeProtocol.MaxAnswerTextLength);
        Assert.AreEqual(0, InterviewBridgeProtocol.MinTurnIndex);
        Assert.AreEqual(11, InterviewBridgeProtocol.MaxTurnIndex);
        Assert.AreEqual(64, InterviewBridgeProtocol.MaxSimVersionLength);

        CollectionAssert.AreEqual(new[] { "protocol", "version", "type", "correlationId", "payload" }, InterviewBridgeProtocol.EnvelopeKeys.All);
        CollectionAssert.AreEqual(new[] { "scenarioId", "simVersion" }, InterviewBridgeProtocol.PayloadKeys.Hello);
        CollectionAssert.AreEqual(new[] { "requestId" }, InterviewBridgeProtocol.PayloadKeys.Start);
        CollectionAssert.AreEqual(new[] { "conversationId", "requestId", "turnIndex", "answerText" }, InterviewBridgeProtocol.PayloadKeys.Answer);
        CollectionAssert.AreEqual(new[] { "conversationId", "requestId", "turnIndex" }, InterviewBridgeProtocol.PayloadKeys.Clarify);
        CollectionAssert.AreEqual(new[] { "conversationId", "requestId", "reason" }, InterviewBridgeProtocol.PayloadKeys.End);
        CollectionAssert.AreEqual(new[] { "hostState" }, InterviewBridgeProtocol.PayloadKeys.Ready);
        CollectionAssert.AreEqual(new[]
        {
            "ok", "httpStatus", "errorCode", "conversationId", "hasTurnIndex", "turnIndex", "promptKind",
            "questionRef", "interviewerText", "action", "conversationState", "hasExpiry",
            "idleRemainingMs", "absoluteRemainingMs",
        }, InterviewBridgeProtocol.PayloadKeys.Result);

        CollectionAssert.AreEquivalent(new[] { "ready", "signed_out", "scenario_mismatch", "version_mismatch" }, InterviewBridgeProtocol.HostStates.All);
        CollectionAssert.AreEquivalent(new[]
        {
            "bridge_transport_failure", "bridge_authentication_required", "bridge_busy", "bridge_conversation_mismatch",
            "bridge_invalid_message", "bridge_not_ready", "bridge_session_start_failed", "bridge_superseded",
        }, InterviewBridgeProtocol.HostErrorCodes.All);
        Assert.AreEqual(17, InterviewBridgeProtocol.ServerErrorCodes.All.Count);
        Assert.IsTrue(InterviewBridgeProtocol.ServerErrorCodes.All.All(c => c.StartsWith("interview_conversation_", StringComparison.Ordinal)));
        CollectionAssert.AreEquivalent(new[] { "user_ended", "client_error", "restart_requested" }, InterviewBridgeProtocol.EndReasons.All);
    }

    // -- Identifier and version validation -----------------------------------------

    [TestCase("corr-0001", true)]
    [TestCase("abcdefgh", true)]
    [TestCase("short", false)]
    [TestCase("has space1", false)]
    [TestCase("under_score", false)]
    [TestCase("corr-0001\n", false)]
    public void Correlation_ids_follow_the_stage_6a_pattern(string value, bool valid) =>
        Assert.AreEqual(valid, InterviewBridgeProtocol.IsValidCorrelationId(value));

    [TestCase("req-1", true)]
    [TestCase("a_b-C9", true)]
    [TestCase("", false)]
    [TestCase("bad id", false)]
    [TestCase("../../etc", false)]
    [TestCase("req-1\n", false)]
    public void Request_and_conversation_ids_follow_the_stage_6a_pattern(string value, bool valid)
    {
        Assert.AreEqual(valid, InterviewBridgeProtocol.IsValidRequestId(value));
        Assert.AreEqual(valid, InterviewBridgeProtocol.IsValidConversationId(value));
    }

    [Test]
    public void Identifier_bounds_are_exact()
    {
        Assert.IsTrue(InterviewBridgeProtocol.IsValidRequestId(new string('r', 64)));
        Assert.IsFalse(InterviewBridgeProtocol.IsValidRequestId(new string('r', 65)));
        Assert.IsTrue(InterviewBridgeProtocol.IsValidCorrelationId(new string('c', 64)));
        Assert.IsFalse(InterviewBridgeProtocol.IsValidCorrelationId(new string('c', 65)));
        Assert.IsFalse(InterviewBridgeProtocol.IsValidRequestId(null));
    }

    [TestCase("1", true)]
    [TestCase("0.1.0", true)]
    [TestCase("1.0.0", true)]
    [TestCase("0.9.3-beta_2+build.17", true)]
    [TestCase("v2", true)]
    [TestCase("", false)]
    [TestCase(" ", false)]
    [TestCase(" 1.0.0", false)]
    [TestCase("1.0.0 ", false)]
    [TestCase("1.0 0", false)]
    [TestCase(".1", false)]
    [TestCase("-1", false)]
    [TestCase("_1", false)]
    [TestCase("+1", false)]
    [TestCase("1.0/2", false)]
    [TestCase("1.0:2", false)]
    [TestCase("1.0@2", false)]
    [TestCase("1.0\n", false)]
    [TestCase("1.0.0\u00e9", false)]
    [TestCase(null, false)]
    public void Sim_version_rules_match_stage_6a(string value, bool valid) =>
        Assert.AreEqual(valid, InterviewBridgeProtocol.IsValidSimVersion(value));

    [Test]
    public void Sim_version_is_bounded_at_exactly_64_characters()
    {
        Assert.IsTrue(InterviewBridgeProtocol.IsValidSimVersion(new string('A', 64)));
        Assert.IsFalse(InterviewBridgeProtocol.IsValidSimVersion(new string('A', 65)));
    }

    [Test]
    public void Generated_ids_are_valid_random_and_content_free()
    {
        var ids = new InterviewBridgeIdSource();
        var correlations = Enumerable.Range(0, 200).Select(_ => ids.NewCorrelationId()).ToList();
        var requests = Enumerable.Range(0, 200).Select(_ => ids.NewRequestId()).ToList();

        Assert.IsTrue(correlations.All(InterviewBridgeProtocol.IsValidCorrelationId));
        Assert.IsTrue(requests.All(InterviewBridgeProtocol.IsValidRequestId));
        Assert.AreEqual(200, correlations.Distinct().Count());
        Assert.AreEqual(200, requests.Distinct().Count());
        StringAssert.IsMatch("^uc-[0-9a-f]{32}$", correlations[0]);
        StringAssert.IsMatch("^ur-[0-9a-f]{32}$", requests[0]);
    }

    // -- Outbound (Unity -> host): exact JSON -------------------------------------

    [Test]
    public void Hello_carries_exactly_the_fixed_scenario_and_the_supplied_application_version()
    {
        Assert.AreEqual(
            "{\"protocol\":\"recursor.interview.bridge\",\"version\":1,\"type\":\"bridge.hello\",\"correlationId\":\"corr-0001\"," +
            "\"payload\":{\"scenarioId\":\"medical-supply-technician-interview-v1\",\"simVersion\":\"7.3.1-rc_2+webgl.415\"}}",
            InterviewBridgeMessages.BuildHello(Corr, "7.3.1-rc_2+webgl.415"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" 1.0")]
    [TestCase("1.0/2")]
    public void Hello_refuses_an_invalid_version_and_never_substitutes_one(string version)
    {
        var ex = Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildHello(Corr, version));
        if (!string.IsNullOrEmpty(version))
            StringAssert.DoesNotContain(version, ex.Message);
    }

    [Test]
    public void Start_answer_clarify_and_end_have_exactly_their_stage_6a_payload_keys()
    {
        Assert.AreEqual(
            "{\"protocol\":\"recursor.interview.bridge\",\"version\":1,\"type\":\"conversation.start\",\"correlationId\":\"corr-0001\",\"payload\":{\"requestId\":\"req-1\"}}",
            InterviewBridgeMessages.BuildStart(Corr, "req-1"));
        Assert.AreEqual(
            "{\"protocol\":\"recursor.interview.bridge\",\"version\":1,\"type\":\"conversation.answer\",\"correlationId\":\"corr-0001\"," +
            "\"payload\":{\"conversationId\":\"" + Conv + "\",\"requestId\":\"req-2\",\"turnIndex\":3,\"answerText\":\"My answer\"}}",
            InterviewBridgeMessages.BuildAnswer(Corr, Conv, "req-2", 3, "My answer"));
        Assert.AreEqual(
            "{\"protocol\":\"recursor.interview.bridge\",\"version\":1,\"type\":\"conversation.clarify\",\"correlationId\":\"corr-0001\"," +
            "\"payload\":{\"conversationId\":\"" + Conv + "\",\"requestId\":\"req-3\",\"turnIndex\":11}}",
            InterviewBridgeMessages.BuildClarify(Corr, Conv, "req-3", 11));
        Assert.AreEqual(
            "{\"protocol\":\"recursor.interview.bridge\",\"version\":1,\"type\":\"conversation.end\",\"correlationId\":\"corr-0001\"," +
            "\"payload\":{\"conversationId\":\"" + Conv + "\",\"requestId\":\"req-4\",\"reason\":\"user_ended\"}}",
            InterviewBridgeMessages.BuildEnd(Corr, Conv, "req-4", "user_ended"));
    }

    [Test]
    public void Answer_text_is_escaped_and_round_trips_exactly()
    {
        const string text = "Line \"one\"\nTab\t back\\slash \u2028 caf\u00e9 \ud83d\ude00 </script>";
        var posted = new PostedMessage(InterviewBridgeMessages.BuildAnswer(Corr, Conv, "req-1", 0, text));

        Assert.AreEqual(text, posted.Str("answerText"));
        Assert.AreEqual(0, posted.Int("turnIndex"));
    }

    [Test]
    public void Outbound_builders_reject_invalid_fields_without_echoing_them()
    {
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildStart("short", "req-1"));
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildStart(Corr, "bad id"));
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildAnswer(Corr, Conv, "req-1", 12, "x"));
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildAnswer(Corr, Conv, "req-1", -1, "x"));
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildAnswer(Corr, Conv, "req-1", 0, "   "));
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildAnswer(Corr, "../x", "req-1", 0, "x"));
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildEnd(Corr, Conv, "req-1", "because"));

        const string secret = "ANSWER-SENTINEL-" + "abc";
        var ex = Assert.Throws<ArgumentException>(() =>
            InterviewBridgeMessages.BuildAnswer(Corr, Conv, "req-1", 0, secret + new string('x', 2000)));
        StringAssert.DoesNotContain("ANSWER-SENTINEL", ex.Message);
    }

    [Test]
    public void Answer_length_bound_is_exactly_2000_characters()
    {
        Assert.DoesNotThrow(() => InterviewBridgeMessages.BuildAnswer(Corr, Conv, "req-1", 0, new string('a', 2000)));
        Assert.Throws<ArgumentException>(() => InterviewBridgeMessages.BuildAnswer(Corr, Conv, "req-1", 0, new string('a', 2001)));
    }

    // -- Inbound (host -> Unity) ---------------------------------------------------

    [Test]
    public void Parses_bridge_ready_and_every_host_state()
    {
        foreach (string state in new[] { "ready", "signed_out", "scenario_mismatch", "version_mismatch" })
        {
            InterviewBridgeInbound inbound = InterviewBridgeMessages.Parse(HostReply.Ready(Corr, state));
            Assert.AreEqual(InterviewBridgeInboundKind.Ready, inbound.Kind, state);
            Assert.AreEqual(state, inbound.Ready.HostState);
            Assert.AreEqual(Corr, inbound.Ready.CorrelationId);
        }
    }

    [Test]
    public void Parses_an_active_result_with_all_contract_fields()
    {
        InterviewBridgeInbound inbound = InterviewBridgeMessages.Parse(HostReply.Active(Corr, 2, "follow_up_presented", "follow_up"));

        Assert.AreEqual(InterviewBridgeInboundKind.Result, inbound.Kind);
        InterviewBridgeResult r = inbound.Result;
        Assert.IsTrue(r.Ok && r.IsActive && !r.IsTerminal);
        Assert.AreEqual(200, r.HttpStatus);
        Assert.AreEqual(Conv, r.ConversationId);
        Assert.IsTrue(r.HasTurnIndex);
        Assert.AreEqual(2, r.TurnIndex);
        Assert.AreEqual("follow_up", r.PromptKind);
        Assert.AreEqual("mst-q1", r.QuestionRef);
        Assert.AreEqual(HostReply.InterviewerText, r.InterviewerText);
        Assert.AreEqual("follow_up_presented", r.Action);
        Assert.IsTrue(r.HasExpiry);
        Assert.AreEqual(720000, r.IdleRemainingMs);
        Assert.AreEqual(1800000, r.AbsoluteRemainingMs);
    }

    [Test]
    public void Parses_terminal_results_and_error_results()
    {
        Assert.IsTrue(InterviewBridgeMessages.Parse(HostReply.Terminal(Corr, "completed", "Thank you.")).Result.IsTerminal);
        Assert.IsTrue(InterviewBridgeMessages.Parse(HostReply.Terminal(Corr, "abandoned")).Result.IsTerminal);
        Assert.IsTrue(InterviewBridgeMessages.Parse(HostReply.Terminal(Corr, "expired")).Result.IsTerminal);

        InterviewBridgeResult error = InterviewBridgeMessages.Parse(HostReply.Error(Corr, "bridge_busy")).Result;
        Assert.IsFalse(error.Ok);
        Assert.AreEqual("bridge_busy", error.ErrorCode);

        Assert.AreEqual(409, InterviewBridgeMessages.Parse(
            HostReply.Error(Corr, "interview_conversation_already_terminal", 409)).Result.HttpStatus);
    }

    private static IEnumerable<TestCaseData> MalformedInbound()
    {
        string valid = HostReply.Active(Corr, 0);
        string payload = valid.Substring(valid.IndexOf("\"payload\":", StringComparison.Ordinal) + 10).TrimEnd('}') + "}";

        yield return new TestCaseData(new object[] { null }).SetName("null");
        yield return new TestCaseData("").SetName("empty");
        yield return new TestCaseData("not json").SetName("garbage");
        yield return new TestCaseData("[]").SetName("array");
        yield return new TestCaseData("\"" + valid.Replace("\"", "\\\"") + "\"").SetName("json string");
        yield return new TestCaseData(valid + " x").SetName("trailing content");
        yield return new TestCaseData(valid.Replace("recursor.interview.bridge", "other")).SetName("wrong protocol");
        yield return new TestCaseData(valid.Replace("\"version\":1", "\"version\":2")).SetName("wrong version");
        yield return new TestCaseData(valid.Replace("\"version\":1", "\"version\":\"1\"")).SetName("string version");
        yield return new TestCaseData(valid.Replace("\"version\":1", "\"version\":1.0")).SetName("fractional version");
        yield return new TestCaseData(valid.Replace("conversation.result", "conversation.answer")).SetName("unity type echoed");
        yield return new TestCaseData(valid.Replace("conversation.result", "conversation.delete")).SetName("unknown type");
        yield return new TestCaseData(valid.Replace(Corr, "short")).SetName("bad correlation");
        yield return new TestCaseData(valid.Replace("\"payload\"", "\"extra\":1,\"payload\"")).SetName("extra envelope key");
        yield return new TestCaseData(valid.Replace("\"ok\":true", "\"ok\":true,\"accessToken\":\"eyJabc\"")).SetName("token field");
        yield return new TestCaseData(valid.Replace("\"ok\":true", "\"ok\":true,\"sessionId\":\"s\"")).SetName("sessionId field");
        yield return new TestCaseData(valid.Replace("\"ok\":true,", "")).SetName("missing key");
        yield return new TestCaseData(valid.Replace("\"ok\":true", "\"ok\":true,\"ok\":true")).SetName("duplicate key");
        yield return new TestCaseData(valid.Replace("\"ok\":true", "\"ok\":\"true\"")).SetName("string bool");
        yield return new TestCaseData(valid.Replace("\"turnIndex\":0", "\"turnIndex\":null")).SetName("null value");
        yield return new TestCaseData(valid.Replace("\"turnIndex\":0", "\"turnIndex\":0.5")).SetName("fractional turn");
        yield return new TestCaseData(valid.Replace("\"turnIndex\":0", "\"turnIndex\":12")).SetName("turn out of range");
        yield return new TestCaseData(valid.Replace("\"turnIndex\":0", "\"turnIndex\":[0]")).SetName("array value");
        yield return new TestCaseData(valid.Replace("question_presented", "question_invented")).SetName("unknown action");
        yield return new TestCaseData(valid.Replace("\"active\"", "\"paused\"")).SetName("unknown state");
        yield return new TestCaseData(valid.Replace("core_question", "follow_up")).SetName("action kind mismatch");
        yield return new TestCaseData(valid.Replace("\"httpStatus\":200", "\"httpStatus\":201")).SetName("ok not 200");
        yield return new TestCaseData(valid.Replace("\"idleRemainingMs\":720000", "\"idleRemainingMs\":-1")).SetName("negative expiry");
        yield return new TestCaseData(valid.Replace("\"hasExpiry\":true", "\"hasExpiry\":false")).SetName("active without expiry");
        yield return new TestCaseData(valid.Replace("\"payload\":{", "\"payload\":{\"inner\":{\"x\":{}},")).SetName("deep nesting");
        yield return new TestCaseData(HostReply.Envelope("conversation.result", Corr, "\"text\"")).SetName("payload not object");
        yield return new TestCaseData(valid.Replace("}}", "}") + "}" + new string(' ', 17000)).SetName("oversized");
        yield return new TestCaseData(HostReply.Error(Corr, "some_invented_code")).SetName("unknown error code");
        yield return new TestCaseData(HostReply.Result(Corr, false, 500, "interview_conversation_internal_error", "", false, 0, "", "", "RAW PROVIDER ERROR", "", "", false, 0, 0)).SetName("error with text");
        yield return new TestCaseData(HostReply.Result(Corr, true, 200, "", HostReply.ConversationId, false, 0, "", "", "late text", "interview_abandoned", "abandoned", false, 0, 0)).SetName("abandoned with text");
        yield return new TestCaseData(HostReply.Result(Corr, true, 200, "", HostReply.ConversationId, true, 3, "", "", "", "interview_completed", "completed", false, 0, 0)).SetName("terminal with turn");
        yield return new TestCaseData(HostReply.Result(Corr, true, 200, "", HostReply.ConversationId, false, 0, "", "", "", "interview_expired", "completed", false, 0, 0)).SetName("terminal action mismatch");
        yield return new TestCaseData(HostReply.Result(Corr, true, 200, "", "../bad", true, 0, "core_question", "mst-q1", "t", "question_presented", "active", true, 1, 1)).SetName("bad conversation id");
        yield return new TestCaseData(HostReply.Envelope("bridge.ready", Corr, "{\"hostState\":\"maybe\"}")).SetName("unknown host state");
        yield return new TestCaseData(HostReply.Envelope("bridge.ready", Corr, "{\"hostState\":\"ready\",\"token\":\"x\"}")).SetName("ready extra key");
        yield return new TestCaseData(payload).SetName("bare payload");
    }

    [TestCaseSource(nameof(MalformedInbound))]
    public void Malformed_or_unknown_inbound_messages_fail_closed(string json)
    {
        InterviewBridgeInbound inbound = null;
        Assert.DoesNotThrow(() => inbound = InterviewBridgeMessages.Parse(json));
        Assert.AreEqual(InterviewBridgeInboundKind.Invalid, inbound.Kind);
        Assert.IsNull(inbound.Result);
        Assert.IsNull(inbound.Ready);
    }

    // -- No token/identity fields anywhere in the Unity bridge models -------------

    [Test]
    public void Bridge_models_have_no_token_session_owner_or_telemetry_identity_fields()
    {
        var forbidden = new[] { "token", "jwt", "bearer", "authorization", "password", "secret", "session", "owner", "userid", "cookie" };
        var types = new[]
        {
            typeof(InterviewBridgeReady), typeof(InterviewBridgeResult), typeof(InterviewBridgeInbound),
            typeof(InterviewOperationOutcome), typeof(InterviewBridgeClient), typeof(HostBridgeInterviewConversationProvider),
            typeof(InterviewTurn), typeof(InterviewTurnResult), typeof(InterviewResponse),
        };

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (Type type in types)
        {
            IEnumerable<string> names = type.GetFields(all).Select(f => f.Name)
                .Concat(type.GetProperties(all).Select(p => p.Name))
                .Select(n => n.ToLowerInvariant());

            foreach (string name in names)
                foreach (string word in forbidden)
                    Assert.IsFalse(name.Contains(word), $"{type.Name}.{name} looks like identity/credential data");
        }

        var allKeys = InterviewBridgeProtocol.PayloadKeys.Hello
            .Concat(InterviewBridgeProtocol.PayloadKeys.Start)
            .Concat(InterviewBridgeProtocol.PayloadKeys.Answer)
            .Concat(InterviewBridgeProtocol.PayloadKeys.Clarify)
            .Concat(InterviewBridgeProtocol.PayloadKeys.End)
            .Concat(InterviewBridgeProtocol.PayloadKeys.Ready)
            .Concat(InterviewBridgeProtocol.PayloadKeys.Result)
            .Select(k => k.ToLowerInvariant());

        foreach (string key in allKeys)
            foreach (string word in forbidden)
                Assert.IsFalse(key.Contains(word), $"payload key {key}");
    }

    [Test]
    public void Every_outbound_message_is_free_of_credential_markers()
    {
        var messages = new[]
        {
            InterviewBridgeMessages.BuildHello(Corr, "1.0.0"),
            InterviewBridgeMessages.BuildStart(Corr, "req-1"),
            InterviewBridgeMessages.BuildAnswer(Corr, Conv, "req-1", 0, "answer"),
            InterviewBridgeMessages.BuildClarify(Corr, Conv, "req-1", 0),
            InterviewBridgeMessages.BuildEnd(Corr, Conv, "req-1", "restart_requested"),
        };

        foreach (string json in messages)
            foreach (string marker in new[] { "token", "Token", "eyJ", "Bearer", "bearer", "authorization", "sessionId", "ownerId", "userId", "\"*\"" })
                StringAssert.DoesNotContain(marker, json);
    }
}
