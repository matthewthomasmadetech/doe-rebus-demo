using Rebus.Sagas.Idempotent;

namespace RebusDemo.Orchestrator;

/// <summary>
/// The persisted state of one registration. Rebus loads it into Data before handling a message.
/// </summary>
public sealed class LearnerRegistrationSagaData : IdempotentSagaData
{
    // Rebus supplies its own storage ID, revision and message history through the base class.
    // CorrelationId is the workflow ID from the HTTP response and the message bodies.
    public Guid CorrelationId { get; set; }
    public RegistrationStage Stage { get; set; }

    // Later handlers need these request values after the original message has been consumed.
    public string LearningGoal { get; set; } = "";
    public string? PreferredFormat { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public string? LearnerId { get; set; }
    public string? LearningPlanId { get; set; }

    // Failed registrations stay in SQL Server with the stage and reason that ended the workflow.
    public RegistrationStage? FailedAtStage { get; set; }
    public string? FailureReason { get; set; }
}

/// <summary>Tracks the reply the saga is waiting for or the workflow's final outcome.</summary>
public enum RegistrationStage
{
    New = 0,
    AwaitingLearner = 1,
    AwaitingLearningPlan = 2,
    AwaitingAssessment = 3,
    Completed = 4,
    Failed = 5
}
