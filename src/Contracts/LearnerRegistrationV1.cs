using System.ComponentModel.DataAnnotations;

namespace RebusDemo.Contracts;

// Every command carries the workflow correlation ID. The V1 suffix identifies its request contract version.

/// <summary>Starts one registration workflow when the orchestrator consumes it from its queue.</summary>
public sealed record RegisterLearnerV1(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string FirstName,
    [property: Required, StringLength(80)] string LastName,
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(160)] string LearningGoal,
    [property: RequiredValue] DateTimeOffset RequestedAtUtc,
    [property: AllowedValues(null, LearningPlanFormats.Online, LearningPlanFormats.Workbook,
        ErrorMessage = LearningPlanFormats.ValidationMessage)] string? PreferredFormat = null,
    [property: StringLength(80)] string? PreferredName = null) : IValidatedMessage;

/// <summary>Asks Manage to create the learner registration.</summary>
public sealed record CreateLearnerV1(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string FirstName,
    [property: Required, StringLength(80)] string LastName,
    [property: Required, EmailAddress, StringLength(254)] string Email) : IValidatedMessage;

/// <summary>Asks Develop to build a plan for the learner's stated goal.</summary>
public sealed record CreateLearningPlanV1(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string LearnerId,
    [property: Required, StringLength(160)] string LearningGoal,
    [property: AllowedValues(null, LearningPlanFormats.Online, LearningPlanFormats.Workbook,
        ErrorMessage = LearningPlanFormats.ValidationMessage)] string? PreferredFormat = null) : IValidatedMessage;

/// <summary>Asks Take to book an assessment using the original request time as its scheduling reference.</summary>
public sealed record ScheduleAssessmentV1(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string LearnerId,
    [property: Required, StringLength(80)] string LearningPlanId,
    [property: RequiredValue] DateTimeOffset RequestedAtUtc,
    bool NotifySaga = true) : IValidatedMessage;
