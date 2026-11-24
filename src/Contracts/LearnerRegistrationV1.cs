using System.ComponentModel.DataAnnotations;

namespace RebusDemo.Contracts;

// Every message carries the same workflow correlation ID in its body. The saga uses that
// value to find its state in SQL Server. The V1 suffix identifies this version of each contract.

/// <summary>Starts one registration workflow when the orchestrator consumes it from its queue.</summary>
public sealed record RegisterLearnerV1(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string FirstName,
    [property: Required, StringLength(80)] string LastName,
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(160)] string LearningGoal,
    [property: RequiredValue] DateTimeOffset RequestedAtUtc,
    [property: AllowedValues(null, LearningPlanFormats.Online, LearningPlanFormats.Workbook,
        ErrorMessage = LearningPlanFormats.ValidationMessage)] string? PreferredFormat = null) : IValidatedMessage;

/// <summary>Asks Manage to create the learner registration.</summary>
public sealed record CreateLearnerV1(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string FirstName,
    [property: Required, StringLength(80)] string LastName,
    [property: Required, EmailAddress, StringLength(254)] string Email) : IValidatedMessage;

/// <summary>Gives the orchestrator the learner ID needed for the remaining steps.</summary>
public sealed record LearnerCreatedV1(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string LearnerId) : IValidatedMessage;

/// <summary>Tells the saga that Manage exhausted its attempts to create the learner.</summary>
public sealed record LearnerCreationFailedV1(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(200)] string Reason) : IValidatedMessage;

/// <summary>Asks Develop to build a plan for the learner's stated goal.</summary>
public sealed record CreateLearningPlanV1(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string LearnerId,
    [property: Required, StringLength(160)] string LearningGoal,
    [property: AllowedValues(null, LearningPlanFormats.Online, LearningPlanFormats.Workbook,
        ErrorMessage = LearningPlanFormats.ValidationMessage)] string? PreferredFormat = null) : IValidatedMessage;

/// <summary>Returns the plan ID and the modules chosen by Develop.</summary>
public sealed record LearningPlanCreatedV1(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string LearnerId,
    [property: Required, StringLength(80)] string LearningPlanId,
    [property: Required, Length(1, 20)] string[] Modules) : IValidatedMessage;

/// <summary>Tells the saga that Develop exhausted its attempts to create the learning plan.</summary>
public sealed record LearningPlanCreationFailedV1(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(200)] string Reason) : IValidatedMessage;

/// <summary>Asks Take to book an assessment using the original request time as its scheduling reference.</summary>
public sealed record ScheduleAssessmentV1(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string LearnerId,
    [property: Required, StringLength(80)] string LearningPlanId,
    [property: RequiredValue] DateTimeOffset RequestedAtUtc) : IValidatedMessage;

/// <summary>Returns the assessment details so the orchestrator can complete the workflow.</summary>
public sealed record AssessmentScheduledV1(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string LearnerId,
    [property: Required, StringLength(80)] string LearningPlanId,
    [property: Required, StringLength(80)] string AssessmentId,
    [property: Required, StringLength(120)] string AssessmentName,
    [property: RequiredValue] DateTimeOffset ScheduledAtUtc) : IValidatedMessage;

/// <summary>Tells the saga that Take exhausted its attempts to schedule the assessment.</summary>
public sealed record AssessmentSchedulingFailedV1(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(200)] string Reason) : IValidatedMessage;
