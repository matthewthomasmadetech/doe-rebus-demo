namespace RebusDemo.Contracts;

// Every message carries the same workflow correlation ID in its body. The saga uses that
// value to find its state in SQL Server. The V1 suffix identifies this version of each contract.

/// <summary>Starts one registration workflow when the orchestrator consumes it from its queue.</summary>
public sealed record RegisterLearnerV1(
    Guid CorrelationId, string FirstName, string LastName, string Email,
    string LearningGoal, DateTimeOffset RequestedAtUtc);

/// <summary>Asks Manage to create the learner registration.</summary>
public sealed record CreateLearnerV1(
    Guid CorrelationId, string FirstName, string LastName, string Email);

/// <summary>Gives the orchestrator the learner ID needed for the remaining steps.</summary>
public sealed record LearnerCreatedV1(Guid CorrelationId, string LearnerId);

/// <summary>Tells the saga that Manage exhausted its attempts to create the learner.</summary>
public sealed record LearnerCreationFailedV1(Guid CorrelationId, string Reason);

/// <summary>Asks Develop to build a plan for the learner's stated goal.</summary>
public sealed record CreateLearningPlanV1(Guid CorrelationId, string LearnerId, string LearningGoal);

/// <summary>Returns the plan ID and the modules chosen by Develop.</summary>
public sealed record LearningPlanCreatedV1(
    Guid CorrelationId, string LearnerId, string LearningPlanId, string[] Modules);

/// <summary>Tells the saga that Develop exhausted its attempts to create the learning plan.</summary>
public sealed record LearningPlanCreationFailedV1(Guid CorrelationId, string Reason);

/// <summary>Asks Take to book an assessment using the original request time as its scheduling reference.</summary>
public sealed record ScheduleAssessmentV1(
    Guid CorrelationId, string LearnerId, string LearningPlanId, DateTimeOffset RequestedAtUtc);

/// <summary>Returns the assessment details so the orchestrator can complete the workflow.</summary>
public sealed record AssessmentScheduledV1(
    Guid CorrelationId, string LearnerId, string LearningPlanId, string AssessmentId,
    string AssessmentName, DateTimeOffset ScheduledAtUtc);

/// <summary>Tells the saga that Take exhausted its attempts to schedule the assessment.</summary>
public sealed record AssessmentSchedulingFailedV1(Guid CorrelationId, string Reason);
