using System.ComponentModel.DataAnnotations;

namespace RebusDemo.Contracts;

/// <summary>Books an assessment with an explicit slot duration.</summary>
public sealed record ScheduleAssessmentV2(
    [property: RequiredValue] Guid CorrelationId,
    [property: Required, StringLength(80)] string LearnerId,
    [property: Required, StringLength(80)] string LearningPlanId,
    [property: RequiredValue] DateTimeOffset RequestedAtUtc,
    [property: Range(15, 120)] int DurationMinutes,
    bool NotifySaga = true) : IValidatedMessage;
