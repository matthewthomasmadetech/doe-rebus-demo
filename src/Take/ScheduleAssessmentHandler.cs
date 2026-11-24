using Rebus.Bus;
using Rebus.Handlers;
using RebusDemo.Contracts;

namespace RebusDemo.Take;

/// <summary>
/// Simulates scheduling an online assessment once Develop has created the learning plan.
/// </summary>
public sealed class ScheduleAssessmentHandler(IBus bus, ILogger<ScheduleAssessmentHandler> logger)
    : IHandleMessages<ScheduleAssessmentV1>
{
    /// <summary>Chooses a stable demo slot and returns the booking details to the orchestrator.</summary>
    public async Task Handle(ScheduleAssessmentV1 message)
    {
        // Derive the slot and ID from the command so a redelivery produces the same booking.
        var scheduledAt = new DateTimeOffset(message.RequestedAtUtc.UtcDateTime.Date.AddDays(7).AddHours(10));
        var assessmentId = $"ASM-{message.CorrelationId:N}";

        await bus.Send(new AssessmentScheduledV1(message.CorrelationId, message.LearnerId, message.LearningPlanId,
            assessmentId, "Online diagnostic assessment", scheduledAt));
        logger.LogInformation("Handled {Command} -> sent {Reply}\n" +
            "    Assessment {AssessmentId} scheduled for {ScheduledAtUtc:u}\n    Correlation {CorrelationId}",
            nameof(ScheduleAssessmentV1), nameof(AssessmentScheduledV1), assessmentId, scheduledAt,
            message.CorrelationId);
    }
}
