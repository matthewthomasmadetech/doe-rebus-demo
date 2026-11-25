using System.ComponentModel.DataAnnotations;

namespace RebusDemo.Contracts;

// Each status update reports a command's outcome and carries its correlation ID back to the orchestrator.

/// <summary>Gives the orchestrator the learner ID needed for the remaining steps.</summary>
public sealed record LearnerCreated(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string LearnerId) : IValidatedMessage;

/// <summary>Reports that Manage could not create the learner.</summary>
public sealed record LearnerCreationFailed(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(200)] string Reason) : IValidatedMessage;

/// <summary>Returns the plan ID and the modules chosen by Develop.</summary>
public sealed record LearningPlanCreated(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string LearnerId,
    [property: Required, StringLength(80)] string LearningPlanId,
    [property: Required, Length(1, 20)] string[] Modules) : IValidatedMessage;

/// <summary>Reports that Develop could not create the learning plan.</summary>
public sealed record LearningPlanCreationFailed(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(200)] string Reason) : IValidatedMessage;

/// <summary>Returns the assessment details so the orchestrator can complete the workflow.</summary>
public sealed record AssessmentScheduled(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string LearnerId,
    [property: Required, StringLength(80)] string LearningPlanId,
    [property: Required, StringLength(80)] string AssessmentId,
    [property: Required, StringLength(120)] string AssessmentName,
    [property: RequiredValue] DateTimeOffset ScheduledAtUtc) : IValidatedMessage;

/// <summary>Reports that Take could not schedule the assessment.</summary>
public sealed record AssessmentSchedulingFailed(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(200)] string Reason) : IValidatedMessage;
