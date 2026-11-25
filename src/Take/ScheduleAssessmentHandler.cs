using Rebus.Handlers;
using Rebus.Retry.Simple;
using RebusDemo.Contracts;

namespace RebusDemo.Take;

/// <summary>Handles V1 assessment requests using a fixed 30-minute duration.</summary>
internal sealed class ScheduleAssessmentHandler(AssessmentScheduling scheduling)
    : IHandleMessages<ScheduleAssessmentV1>, IHandleMessages<IFailed<ScheduleAssessmentV1>>
{
    /// <summary>Uses the common booking logic for the original contract.</summary>
    public Task Handle(ScheduleAssessmentV1 message) =>
        scheduling.BookAsync(message.CorrelationId, message.LearnerId, message.LearningPlanId,
            message.RequestedAtUtc, 30, message.NotifySaga, nameof(ScheduleAssessmentV1));

    /// <summary>Reports an exhausted delivery and archives the original V1 command.</summary>
    public Task Handle(IFailed<ScheduleAssessmentV1> failure) =>
        scheduling.FailAsync(failure.Message.CorrelationId, failure.Message.NotifySaga,
            nameof(ScheduleAssessmentV1), failure.ErrorDescription);
}
