using Rebus.Sagas.Idempotent;

namespace RebusDemo.Orchestrator;

/// <summary>
/// The state of one active registration. Rebus loads it into the saga's Data property before handling a message.
/// </summary>
public sealed class LearnerRegistrationSagaData : IdempotentSagaData
{
    // Rebus supplies its own storage ID, revision and message history through the base class.
    // CorrelationId is the workflow ID from the HTTP response and the message bodies.
    public Guid CorrelationId { get; set; }
    public RegistrationStage Stage { get; set; }

    // Later handlers need these request values after the original message has been consumed.
    public string LearningGoal { get; set; } = "";
    public DateTimeOffset RequestedAtUtc { get; set; }
    public string? LearnerId { get; set; }
    public string? LearningPlanId { get; set; }
}

/// <summary>Tracks which reply the saga is waiting for. Active stage values are stored in SQL Server.</summary>
public enum RegistrationStage
{
    New = 0,
    AwaitingLearner = 1,
    AwaitingLearningPlan = 2,
    AwaitingAssessment = 3,
    Completed = 4
}
