using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;

// Local observations only. No transcript fields, network calls or serialization; InterviewTelemetrySender
// turns these into telemetry.batch events (Recursor docs/contracts/job-interview-telemetry-v1.md section 2).
public enum InterviewBehaviorKind
{
    TurnPresented, ResponseStarted, ResponseSubmitted,
    ClarificationRequested, TurnCompleted
}
public sealed class InterviewBehaviorEvent
{
    public InterviewBehaviorKind Kind { get; }
    public int TurnIndex { get; }

    // The client's UTC clock when the observation was made; recorded as client-reported.
    public DateTime TimestampUtc { get; }

    // Whole milliseconds, or null when unobserved or outside 0-600000. Never imputed.
    public double? ResponseLatencyMs { get; }
    public double? ResponseDurationMs { get; }
    public string ResponseLengthBand { get; }

    // TurnCompleted only. Structural (P1): true when the conversation's next action was a different core
    // question or the interview's genuine completion; false for a follow-up on the same question. It never
    // asserts answer quality, topic coverage or mastery. Null for every other kind.
    public bool? TopicCompleted { get; }

    // "completed" only on the last TurnCompleted of a genuinely completed interview (P3); otherwise null.
    public string CompletionStatus { get; }

    public string EventType
    {
        get
        {
            switch (Kind)
            {
                case InterviewBehaviorKind.TurnPresented: return "interview_turn_presented";
                case InterviewBehaviorKind.ResponseStarted: return "interview_response_started";
                case InterviewBehaviorKind.ResponseSubmitted: return "interview_response_submitted";
                case InterviewBehaviorKind.ClarificationRequested: return "interview_clarification_requested";
                default: return "interview_turn_completed";
            }
        }
    }

    internal InterviewBehaviorEvent(InterviewBehaviorKind kind, int turn, DateTime timestampUtc,
        double? latency = null, double? duration = null, string band = null,
        bool? topicCompleted = null, string completionStatus = null)
    {
        Kind = kind;
        TurnIndex = turn;
        TimestampUtc = timestampUtc;
        ResponseLatencyMs = latency;
        ResponseDurationMs = duration;
        ResponseLengthBand = band;
        TopicCompleted = topicCompleted;
        CompletionStatus = completionStatus;
    }
}

public sealed class InterviewBehaviorCollector
{
    private const int Capacity = 200;
    private readonly List<InterviewBehaviorEvent> events = new List<InterviewBehaviorEvent>();
    private readonly Func<double> now;
    private readonly Func<DateTime> utcNow;
    private int activeTurn = -1;
    private double presentedAt;
    private double? startedAt;
    private bool submitted;
    private bool completed;
    public ReadOnlyCollection<InterviewBehaviorEvent> Events { get; }
    public int DroppedEventCount { get; private set; }
    public int OmittedTimingCount { get; private set; }

    // Raised once for each observation kept (never for a dropped one). Carries no conversation content.
    public event Action<InterviewBehaviorEvent> Recorded;

    public InterviewBehaviorCollector() : this(
        () => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency, () => DateTime.UtcNow)
    { }

    // Clock injection permits deterministic checks without changing gameplay time.
    public InterviewBehaviorCollector(Func<double> clock) : this(clock, () => DateTime.UtcNow)
    { }

    public InterviewBehaviorCollector(Func<double> clock, Func<DateTime> utcClock)
    {
        now = clock ?? throw new ArgumentNullException(nameof(clock));
        utcNow = utcClock ?? throw new ArgumentNullException(nameof(utcClock));
        Events = events.AsReadOnly();
    }

    public void Present(int turn)
    {
        if (turn < 0 || turn > 500 || turn == activeTurn) return;
        activeTurn = turn;
        presentedAt = now();
        startedAt = null;
        submitted = completed = false;
        Add(new InterviewBehaviorEvent(InterviewBehaviorKind.TurnPresented, turn, utcNow()));
    }

    public void ResponseStarted(int turn)
    {
        if (turn != activeTurn || submitted || startedAt.HasValue) return;
        startedAt = now();
        Add(new InterviewBehaviorEvent(InterviewBehaviorKind.ResponseStarted, turn, utcNow()));
    }

    public void ClarificationRequested(int turn)
    {
        if (turn != activeTurn || submitted) return;
        Add(new InterviewBehaviorEvent(InterviewBehaviorKind.ClarificationRequested, turn, utcNow()));
    }

    public void Submit(int turn, int characterCount)
    {
        if (turn != activeTurn || submitted || characterCount <= 0) return;
        submitted = true;
        double submittedAt = now();
        double? latency = null;
        double? duration = null;
        if (startedAt.HasValue)
        {
            latency = AllowedMilliseconds(startedAt.Value - presentedAt);
            duration = AllowedMilliseconds(submittedAt - startedAt.Value);
        }
        else
        {
            // Programmatic submissions without an observed input start have unknown timings.
            OmittedTimingCount += 2;
        }
        // Only the band leaves this method; the count and the text never do.
        string band = characterCount <= 20 ? "very-short" :
            characterCount <= 100 ? "short" : characterCount <= 300 ? "medium" : "long";
        Add(new InterviewBehaviorEvent(InterviewBehaviorKind.ResponseSubmitted,
            turn, utcNow(), latency, duration, band));
    }

    // topicCompleted: the structural signal described on InterviewBehaviorEvent.TopicCompleted.
    // interviewCompleted: the interview genuinely completed after this turn; never true for an
    // explicit end, timeout, expiry, client error or interruption.
    public void CompleteTurn(int turn, bool topicCompleted, bool interviewCompleted)
    {
        if (turn != activeTurn || !submitted || completed) return;
        completed = true;
        Add(new InterviewBehaviorEvent(InterviewBehaviorKind.TurnCompleted, turn, utcNow(),
            topicCompleted: topicCompleted || interviewCompleted,
            completionStatus: interviewCompleted ? "completed" : null));
    }

    public void Clear()
    {
        events.Clear();
        activeTurn = -1;
        startedAt = null;
        submitted = completed = false;
        DroppedEventCount = OmittedTimingCount = 0;
    }

    private double? AllowedMilliseconds(double seconds)
    {
        // Whole milliseconds (contract section 2.1).
        double value = Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 600000)
        {
            OmittedTimingCount++;
            return null;
        }
        return value;
    }

    private void Add(InterviewBehaviorEvent observation)
    {
        if (events.Count >= Capacity) { DroppedEventCount++; return; }
        events.Add(observation);
        Recorded?.Invoke(observation);
    }
}
