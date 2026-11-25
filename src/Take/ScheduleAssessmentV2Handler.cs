using Rebus.Handlers;
using Rebus.Retry.Simple;
using RebusDemo.Contracts;

namespace RebusDemo.Take;

/// <summary>Handles V2 assessment requests using the requested duration.</summary>
internal sealed class ScheduleAssessmentV2Handler(AssessmentScheduling scheduling)
    : IHandleMessages<ScheduleAssessmentV2>, IHandleMessages<IFailed<ScheduleAssessmentV2>>
{
    /// <summary>Uses the common booking logic for the version that carries the duration.</summary>
    public Task Handle(ScheduleAssessmentV2 message) =>
        scheduling.BookAsync(message.CorrelationId, message.LearnerId, message.LearningPlanId,
            message.RequestedAtUtc, message.DurationMinutes, message.NotifySaga, nameof(ScheduleAssessmentV2));

    /// <summary>Reports an exhausted delivery and archives the original V2 command.</summary>
    public Task Handle(IFailed<ScheduleAssessmentV2> failure) =>
        scheduling.FailAsync(failure.Message.CorrelationId, failure.Message.NotifySaga,
            nameof(ScheduleAssessmentV2), failure.ErrorDescription);
}
