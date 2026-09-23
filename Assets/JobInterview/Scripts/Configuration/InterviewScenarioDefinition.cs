using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(
    fileName = "InterviewScenario",
    menuName = "Job Interview/Scenario Definition")]
public class InterviewScenarioDefinition : ScriptableObject
{
    [Header("Identity")]

    [SerializeField]
    private string simulationId = JobInterviewIdentifiers.SimulationId;

    [SerializeField]
    private string scenarioId =
        JobInterviewIdentifiers.MedicalSupplyTechnicianScenarioId;

    [SerializeField]
    private string displayRole = "Medical Supply Technician";

    [SerializeField]
    private string employerType =
        "Healthcare supply or hospital logistics organization";

    [Header("Interview Text")]

    [SerializeField]
    [TextArea(3, 8)]
    private string introduction = string.Empty;

    [SerializeField]
    [TextArea(3, 8)]
    private string completionMessage = string.Empty;

    [Header("Questions")]

    [SerializeField]
    private List<InterviewQuestionDefinition> questions = new();

    public string SimulationId => simulationId;
    public string ScenarioId => scenarioId;
    public string DisplayRole => displayRole;
    public string EmployerType => employerType;
    public string Introduction => introduction;
    public string CompletionMessage => completionMessage;
    public IReadOnlyList<InterviewQuestionDefinition> Questions => questions;

    public bool IsConfigured
    {
        get
        {
            if (string.IsNullOrWhiteSpace(simulationId) ||
                string.IsNullOrWhiteSpace(scenarioId) ||
                string.IsNullOrWhiteSpace(displayRole) ||
                questions == null ||
                questions.Count == 0)
            {
                return false;
            }

            foreach (InterviewQuestionDefinition question in questions)
            {
                if (question == null || !question.IsConfigured)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
