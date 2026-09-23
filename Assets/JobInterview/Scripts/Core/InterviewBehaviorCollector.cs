using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;

// Local observations only. No transcript fields, network calls or serialization.
public enum InterviewBehaviorKind
{
    TurnPresented, ResponseStarted, ResponseSubmitted,
    ClarificationRequested, TurnCompleted
}
public sealed class InterviewBehaviorEvent
{
    public InterviewBehaviorKind Kind { get; }
    public int TurnIndex { get; }
    public double? ResponseLatencyMs { get; }
    public double? ResponseDurationMs { get; }
    public string ResponseLengthBand { get; }
    public string CompletionStatus => Kind == InterviewBehaviorKind.TurnCompleted
        ? "completed" : null;
    // Topic coverage cannot be inferred from simply submitting an answer.
    // Deliberately omit topicCompleted from the eventual wire representation.
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

    internal InterviewBehaviorEvent(InterviewBehaviorKind kind, int turn,
        double? latency = null, double? duration = null, string band = null)
    {
        Kind = kind;
        TurnIndex = turn;
        ResponseLatencyMs = latency;
        ResponseDurationMs = duration;
        ResponseLengthBand = band;
    }
}

public sealed class InterviewBehaviorCollector
{
    private const int Capacity = 200;
    private readonly List<InterviewBehaviorEvent> events = new List<InterviewBehaviorEvent>();
    private readonly Func<double> now;
    private int activeTurn = -1;
    private double presentedAt;
    private double? startedAt;
    private bool submitted;
    private bool completed;
    public ReadOnlyCollection<InterviewBehaviorEvent> Events { get; }
    public int DroppedEventCount { get; private set; }
    public int OmittedTimingCount { get; private set; }

    public InterviewBehaviorCollector() : this(
        () => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency)
    { }

    // Clock injection permits deterministic checks without changing gameplay time.
    public InterviewBehaviorCollector(Func<double> clock)
    {
        now = clock ?? throw new ArgumentNullException(nameof(clock));
        Events = events.AsReadOnly();
    }

    public void Present(int turn)
    {
        if (turn < 0 || turn > 500 || turn == activeTurn) return;
        activeTurn = turn;
        presentedAt = now();
        startedAt = null;
        submitted = completed = false;
        Add(new InterviewBehaviorEvent(InterviewBehaviorKind.TurnPresented, turn));
    }

    public void ResponseStarted(int turn)
    {
        if (turn != activeTurn || submitted || startedAt.HasValue) return;
        startedAt = now();
        Add(new InterviewBehaviorEvent(InterviewBehaviorKind.ResponseStarted, turn));
    }

    public void ClarificationRequested(int turn)
    {
        if (turn != activeTurn || submitted) return;
        Add(new InterviewBehaviorEvent(InterviewBehaviorKind.ClarificationRequested, turn));
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
        string band = characterCount <= 20 ? "very-short" :
            characterCount <= 100 ? "short" : characterCount <= 300 ? "medium" : "long";
        Add(new InterviewBehaviorEvent(InterviewBehaviorKind.ResponseSubmitted,
            turn, latency, duration, band));
    }

    public void CompleteTurn(int turn)
    {
        if (turn != activeTurn || !submitted || completed) return;
        completed = true;
        Add(new InterviewBehaviorEvent(InterviewBehaviorKind.TurnCompleted, turn));
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
        double value = seconds * 1000;
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
    }
}
