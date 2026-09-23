using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public sealed class InterviewQuestionDefinition
{
    [SerializeField]
    [Tooltip("Stable internal identifier. Never use the displayed question text as an ID.")]
    private string questionId = string.Empty;

    [SerializeField]
    [TextArea(3, 8)]
    [Tooltip("The interview question shown to the applicant.")]
    private string promptText = string.Empty;

    [SerializeField]
    [TextArea(2, 6)]
    [Tooltip("A simpler restatement shown when the applicant requests clarification.")]
    private string clarificationText = string.Empty;

    [SerializeField]
    [Tooltip("Optional controlled content category. This is configuration, not applicant telemetry.")]
    private string topicTag = string.Empty;

    public string QuestionId => questionId;
    public string PromptText => promptText;
    public string ClarificationText => clarificationText;
    public string TopicTag => topicTag;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(questionId) &&
        !string.IsNullOrWhiteSpace(promptText);
}
