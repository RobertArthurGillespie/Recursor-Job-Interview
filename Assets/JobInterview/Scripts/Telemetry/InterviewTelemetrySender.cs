using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

// Where a telemetry run stands. Only Ended may be presented as complete, and even Ended means only that the
// host acknowledged every batch and ended the Recursor session: the server hands rows to ADX queued
// ingestion and never confirms they are stored or queryable, so nothing here says "saved".
public enum InterviewTelemetryRunStatus
{
    // No run has started on this page.
    None,

    // The interview is in progress; each completed turn's batch is sent as it is cut.
    Recording,

    // The interview reached a terminal state; the remaining batches and then telemetry.finish are being sent.
    Delivering,

    // telemetry.finished answered "ended".
    Ended,

    // Telemetry is not known to be complete (a batch was refused or never acknowledged, the host refused the
    // finish, or the host never answered). StatusCode holds the fixed reason.
    Incomplete,
}

public interface IInterviewTelemetryIdSource
{
    // ^[A-Za-z0-9_-]{1,64}$, random per batch.
    string NewBatchId();

    // ^[A-Za-z0-9_-]{1,128}$, random and unique within the run.
    string NewEventId();
}

// Random, content-free identifiers: 128 bits from a cryptographic source, lowercase hex. Never derived from
// user, conversation, time or answer data.
public sealed class InterviewTelemetryIdSource : IInterviewTelemetryIdSource
{
    private readonly RandomNumberGenerator random = RandomNumberGenerator.Create();

    public string NewBatchId() => "ub-" + RandomHex();

    public string NewEventId() => "ue-" + RandomHex();

    private string RandomHex()
    {
        var bytes = new byte[16];
        random.GetBytes(bytes);

        var builder = new StringBuilder(32);
        foreach (byte b in bytes)
            builder.Append(b.ToString("x2"));

        return builder.ToString();
    }
}

// The Unity sender for job-interview observation telemetry (Recursor docs/contracts/job-interview-telemetry-v1.md
// sections 2 and 3). One run per interview conversation:
// - events are numbered from 0 within the run and turned into wire events as they are recorded;
// - a batch is cut immediately after each interview_turn_completed, and the remaining events form one final
//   batch when the run ends (no empty batch);
// - batches go out one at a time, in BatchSequence order, and a run's finish only after its last batch has a
//   result; a later run waits for the earlier one;
// - Unity owns retry: after an ambiguous outcome (transport_failure, busy, no reply, not posted) the same
//   batch - the same BatchId, BatchSequence, event ids and content - is resent at most twice, after 1 s and
//   then 3 s. A definitive outcome is never resent;
// - telemetry.finish {conversationId, lastBatchSequence} is sent after every batch has a result;
// - each run is bound, when it begins, to the ConversationId the host returned for that run's own start
//   (contract section 3.4). Every batch and the finish carry that id, unchanged, through resends, resets and
//   queued runs; the sender never reads the current conversation. A run without a valid id sends nothing and
//   is Incomplete (NoConversationCode). The id is routing only: it is never part of an event.
// It carries no answer, clarification or interviewer text, no token, learner key or server SessionId, and
// never logs. Driven by Tick() from the interview controller's Update.
public sealed class InterviewTelemetrySender
{
    // The original send plus at most two exact resends.
    public const int MaxSendsPerMessage = 3;

    public static readonly IReadOnlyList<double> ResendDelaySeconds = new[] { 1.0, 3.0 };

    // Local reasons, in the same fixed-code style as the host's codes.
    public const string NoReplyCode = "telemetry_no_reply";
    public const string NotSentCode = "telemetry_not_sent";
    public const string CancelledCode = "telemetry_cancelled";

    // A run began without a valid ConversationId from its own start: nothing is sent for it.
    public const string NoConversationCode = "telemetry_no_conversation";

    private readonly IInterviewTelemetryChannel channel;
    private readonly IInterviewTelemetryIdSource ids;
    private readonly Func<double> clock;
    private readonly Func<DateTime> utcClock;
    private readonly Queue<Run> runs = new Queue<Run>();

    private Run recording;
    private Run latest;

    public InterviewTelemetrySender(
        IInterviewTelemetryChannel channel,
        IInterviewTelemetryIdSource ids,
        Func<double> clock,
        Func<DateTime> utcClock)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        this.ids = ids ?? throw new ArgumentNullException(nameof(ids));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.utcClock = utcClock ?? throw new ArgumentNullException(nameof(utcClock));
    }

    // The latest run's status and fixed reason code (empty unless Incomplete or a retry is pending).
    public InterviewTelemetryRunStatus Status => latest?.Status ?? InterviewTelemetryRunStatus.None;
    public string StatusCode => latest?.Code ?? string.Empty;

    // Runs not yet Ended or Incomplete, including one still recording.
    public int OpenRunCount => runs.Count;

    // Contract P1: a turn completes its topic when the conversation's next prompt is a different core question
    // (a different questionRef, which the host passes as InterviewTurn.QuestionId) or when there is no next
    // prompt because the interview genuinely completed. A follow-up keeps its core question's questionRef.
    // Structural only: never a judgment of the answer, coverage or mastery.
    public static bool IsTopicBoundary(string submittedQuestionRef, InterviewTurn nextTurn) =>
        nextTurn == null || !string.Equals(nextTurn.QuestionId, submittedQuestionRef, StringComparison.Ordinal);

    // Fixed, content-free wording for a status. It never says telemetry was saved or stored.
    public static string Describe(InterviewTelemetryRunStatus status)
    {
        switch (status)
        {
            case InterviewTelemetryRunStatus.Recording:
                return "Telemetry is being sent as the interview proceeds.";
            case InterviewTelemetryRunStatus.Delivering:
                return "Sending the remaining telemetry.";
            case InterviewTelemetryRunStatus.Ended:
                return "The host acknowledged all telemetry and closed the session. This does not confirm the data is stored.";
            case InterviewTelemetryRunStatus.Incomplete:
                return "Telemetry was not fully acknowledged. Nothing is reported as saved.";
            default:
                return string.Empty;
        }
    }

    // A new conversation began; conversationId is the id the host returned for exactly that conversation's start.
    // Any run still recording ends first (final batch, then finish) and keeps its own id. Without a valid id the
    // new run records and sends nothing and is Incomplete at once (NoConversationCode); it is never given another
    // run's or the current conversation's id.
    public void BeginRun(string conversationId)
    {
        EndRun();

        if (!InterviewBridgeProtocol.IsValidConversationId(conversationId))
        {
            var refused = new Run(null) { Ended = true, Stopped = true };
            Finish(refused, InterviewTelemetryRunStatus.Incomplete, NoConversationCode);
            latest = refused;
            return;
        }

        recording = new Run(conversationId);
        latest = recording;
        runs.Enqueue(recording);
    }

    // Converts one observation into a wire event of the recording run. Ignored when no run is recording.
    public void Record(InterviewBehaviorEvent observation)
    {
        if (observation == null || recording == null || recording.Stopped)
            return;

        recording.Uncut.Add(ToWire(observation, recording.NextEventSequence++));

        if (observation.Kind == InterviewBehaviorKind.TurnCompleted ||
            recording.Uncut.Count >= InterviewBridgeProtocol.Telemetry.MaxEventsPerBatch)
        {
            Cut(recording);
        }
    }

    // The conversation reached a terminal state (completed, ended, abandoned, expired, superseded) or the screen
    // was reset: the uncut events form the final batch, then the run finishes once every batch has a result.
    public void EndRun()
    {
        if (recording == null)
            return;

        Cut(recording);
        recording.Ended = true;
        if (recording.Status == InterviewTelemetryRunStatus.Recording)
            recording.Status = InterviewTelemetryRunStatus.Delivering;
        recording = null;
    }

    public void Tick()
    {
        while (runs.Count > 0)
        {
            if (!Advance(runs.Peek()))
                return;
            runs.Dequeue();
        }
    }

    // True once the run is Ended or Incomplete.
    private bool Advance(Run run)
    {
        if (run.Attempt != null)
        {
            if (!run.Attempt.IsComplete)
                return false;

            InterviewTelemetryAttempt attempt = run.Attempt;
            run.Attempt = null;

            if (run.SendingFinish)
                HandleFinish(run, attempt);
            else
                HandleBatch(run, attempt);
        }

        if (IsDone(run))
            return true;

        if (run.ResendAt.HasValue)
        {
            if (clock() < run.ResendAt.Value)
                return false;
            run.ResendAt = null;
        }

        // Nothing is spent while the bridge is not ready; the message waits.
        if (!channel.IsReady)
            return false;

        if (run.Outbox.Count > 0)
        {
            run.SendingFinish = false;
            run.Sends++;
            run.Attempt = channel.SendTelemetryBatch(run.Outbox.Peek());
            return false;
        }

        if (!run.Ended)
            return false;

        run.SendingFinish = true;
        run.Sends++;
        run.Attempt = channel.SendTelemetryFinish(run.ConversationId, run.LastBatchSequence);
        return false;
    }

    private void HandleBatch(Run run, InterviewTelemetryAttempt attempt)
    {
        switch (attempt.Status)
        {
            case InterviewTelemetryAttemptStatus.Replied:
                switch (attempt.Outcome)
                {
                    case InterviewBridgeProtocol.Telemetry.Outcomes.Succeeded:
                    case InterviewBridgeProtocol.Telemetry.Outcomes.Rejected:
                        // Acknowledged, or definitively refused (never resent); either way the next batch goes.
                        NextMessage(run);
                        return;

                    case InterviewBridgeProtocol.Telemetry.Outcomes.TransportFailure:
                    case InterviewBridgeProtocol.Telemetry.Outcomes.Busy:
                        ResendOrMoveOn(run, CodeOr(attempt, attempt.Outcome));
                        return;

                    default:
                        // forbidden, authentication_required, not_permitted: nothing further can be accepted
                        // for this run, so no further batch and no finish are sent.
                        Stop(run, CodeOr(attempt, attempt.Outcome));
                        return;
                }

            case InterviewTelemetryAttemptStatus.Cancelled:
                Stop(run, CancelledCode);
                return;

            default:
                ResendOrMoveOn(run, attempt.Status == InterviewTelemetryAttemptStatus.NoReply ? NoReplyCode : NotSentCode);
                return;
        }
    }

    private void HandleFinish(Run run, InterviewTelemetryAttempt attempt)
    {
        switch (attempt.Status)
        {
            case InterviewTelemetryAttemptStatus.Replied:
                switch (attempt.Outcome)
                {
                    case InterviewBridgeProtocol.Telemetry.FinishOutcomes.Ended:
                        Finish(run, InterviewTelemetryRunStatus.Ended, string.Empty);
                        return;

                    case InterviewBridgeProtocol.Telemetry.FinishOutcomes.Busy:
                        ResendOrGiveUp(run, CodeOr(attempt, attempt.Outcome));
                        return;

                    case InterviewBridgeProtocol.Telemetry.FinishOutcomes.Incomplete:
                        // A batch not acknowledged or refused cannot change by asking again, and neither can a
                        // missing sign-in; a failed end (e.g. terminal_window_not_submitted) or a transport
                        // failure can.
                        if (attempt.ErrorCode == InterviewBridgeProtocol.Telemetry.Codes.BatchNotAcknowledged ||
                            attempt.ErrorCode == InterviewBridgeProtocol.Telemetry.Codes.BatchRejected ||
                            attempt.ErrorCode == InterviewBridgeProtocol.HostErrorCodes.AuthenticationRequired)
                        {
                            Finish(run, InterviewTelemetryRunStatus.Incomplete, attempt.ErrorCode);
                        }
                        else
                        {
                            ResendOrGiveUp(run, CodeOr(attempt, attempt.Outcome));
                        }
                        return;

                    default:
                        Finish(run, InterviewTelemetryRunStatus.Incomplete, CodeOr(attempt, attempt.Outcome));
                        return;
                }

            case InterviewTelemetryAttemptStatus.Cancelled:
                Finish(run, InterviewTelemetryRunStatus.Incomplete, CancelledCode);
                return;

            default:
                ResendOrGiveUp(run, attempt.Status == InterviewTelemetryAttemptStatus.NoReply ? NoReplyCode : NotSentCode);
                return;
        }
    }

    // An ambiguous batch outcome: resend the identical batch, or after the last allowed send leave it
    // unacknowledged and continue with the next one (the host will then answer the finish "incomplete").
    private void ResendOrMoveOn(Run run, string code)
    {
        if (run.Sends < MaxSendsPerMessage)
        {
            ScheduleResend(run, code);
            return;
        }

        NextMessage(run);
    }

    private void ResendOrGiveUp(Run run, string code)
    {
        if (run.Sends < MaxSendsPerMessage)
        {
            ScheduleResend(run, code);
            return;
        }

        Finish(run, InterviewTelemetryRunStatus.Incomplete, code);
    }

    private void ScheduleResend(Run run, string code)
    {
        run.Code = code;
        run.ResendAt = clock() + ResendDelaySeconds[Math.Min(run.Sends - 1, ResendDelaySeconds.Count - 1)];
    }

    private static void NextMessage(Run run)
    {
        run.Outbox.Dequeue();
        run.Sends = 0;
        run.Code = string.Empty;
    }

    private void Stop(Run run, string code)
    {
        run.Stopped = true;
        run.Outbox.Clear();
        run.Uncut.Clear();
        Finish(run, InterviewTelemetryRunStatus.Incomplete, code);
    }

    private static void Finish(Run run, InterviewTelemetryRunStatus status, string code)
    {
        run.Status = status;
        run.Code = code ?? string.Empty;
        run.ResendAt = null;
        run.Sends = 0;
    }

    private static bool IsDone(Run run) =>
        run.Status == InterviewTelemetryRunStatus.Ended || run.Status == InterviewTelemetryRunStatus.Incomplete;

    private static string CodeOr(InterviewTelemetryAttempt attempt, string fallback) =>
        string.IsNullOrEmpty(attempt.ErrorCode) ? fallback : attempt.ErrorCode;

    private void Cut(Run run)
    {
        if (run.Uncut.Count == 0 || run.Stopped)
            return;

        var batch = new InterviewTelemetryBatch(
            run.ConversationId,
            ids.NewBatchId(),
            run.NextBatchSequence++,
            FormatTimestamp(utcClock()),
            run.Uncut.ToArray());

        run.LastBatchSequence = batch.BatchSequence;
        run.Uncut.Clear();
        run.Outbox.Enqueue(batch);
    }

    private InterviewTelemetryWireEvent ToWire(InterviewBehaviorEvent e, long sequenceNumber)
    {
        var metrics = new List<KeyValuePair<string, long>>
        {
            new KeyValuePair<string, long>(InterviewBridgeProtocol.Telemetry.MetricKeys.TurnIndex, e.TurnIndex),
        };
        var payload = new List<KeyValuePair<string, object>>();
        string category = InterviewBridgeProtocol.Telemetry.Categories.TaskAction;
        string actor = InterviewBridgeProtocol.Telemetry.Actors.Applicant;
        string target = InterviewBridgeProtocol.Telemetry.Targets.Turn;

        switch (e.Kind)
        {
            case InterviewBehaviorKind.TurnPresented:
                category = InterviewBridgeProtocol.Telemetry.Categories.System;
                actor = InterviewBridgeProtocol.Telemetry.Actors.System;
                break;

            case InterviewBehaviorKind.ResponseStarted:
                target = InterviewBridgeProtocol.Telemetry.Targets.Response;
                break;

            case InterviewBehaviorKind.ResponseSubmitted:
                target = InterviewBridgeProtocol.Telemetry.Targets.Response;
                // Only when observed; never imputed.
                if (e.ResponseLatencyMs.HasValue)
                    metrics.Add(new KeyValuePair<string, long>(InterviewBridgeProtocol.Telemetry.MetricKeys.ResponseLatencyMs, (long)e.ResponseLatencyMs.Value));
                if (e.ResponseDurationMs.HasValue)
                    metrics.Add(new KeyValuePair<string, long>(InterviewBridgeProtocol.Telemetry.MetricKeys.ResponseDurationMs, (long)e.ResponseDurationMs.Value));
                if (e.ResponseLengthBand != null)
                    payload.Add(new KeyValuePair<string, object>(InterviewBridgeProtocol.Telemetry.PayloadKeys.ResponseLengthBand, e.ResponseLengthBand));
                break;

            case InterviewBehaviorKind.TurnCompleted:
                category = InterviewBridgeProtocol.Telemetry.Categories.System;
                actor = InterviewBridgeProtocol.Telemetry.Actors.System;
                payload.Add(new KeyValuePair<string, object>(InterviewBridgeProtocol.Telemetry.PayloadKeys.TopicCompleted, e.TopicCompleted == true));
                if (e.CompletionStatus == InterviewBridgeProtocol.Telemetry.CompletionStatusCompleted)
                    payload.Add(new KeyValuePair<string, object>(InterviewBridgeProtocol.Telemetry.PayloadKeys.CompletionStatus, e.CompletionStatus));
                break;

            // ClarificationRequested: task_action / applicant / turn.
        }

        return new InterviewTelemetryWireEvent(
            ids.NewEventId(), sequenceNumber, FormatTimestamp(e.TimestampUtc), e.EventType, category, actor, target,
            metrics, payload);
    }

    // ISO 8601 UTC with milliseconds, e.g. 2026-10-30T12:00:00.123Z (the host bridge's timestamp form).
    private static string FormatTimestamp(DateTime value)
    {
        DateTime utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private sealed class Run
    {
        public Run(string conversationId) => ConversationId = conversationId;

        // Set once, when the run begins; the only id this run's batches and finish ever carry.
        public readonly string ConversationId;

        public readonly List<InterviewTelemetryWireEvent> Uncut = new List<InterviewTelemetryWireEvent>();
        public readonly Queue<InterviewTelemetryBatch> Outbox = new Queue<InterviewTelemetryBatch>();
        public long NextEventSequence;
        public int NextBatchSequence;
        public int LastBatchSequence = -1;
        public bool Ended;
        public bool Stopped;
        public InterviewTelemetryRunStatus Status = InterviewTelemetryRunStatus.Recording;
        public string Code = string.Empty;

        // The message in flight or awaiting a resend: the head of Outbox, or the finish.
        public InterviewTelemetryAttempt Attempt;
        public bool SendingFinish;
        public int Sends;
        public double? ResendAt;
    }
}
