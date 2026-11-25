using Rebus.Bus;
using Rebus.Exceptions;
using Rebus.Pipeline;
using RebusDemo.Contracts;

namespace RebusDemo.Take;

/// <summary>Identifies which queue this listener reads for logging.</summary>
internal sealed record AssessmentQueue(string Address);

/// <summary>Simulates a booking and reports outcomes for saga requests.</summary>
internal sealed class AssessmentScheduling(IBus bus, AssessmentQueue queue, ILogger<AssessmentScheduling> logger)
{
    /// <summary>Derives a stable demo slot and sends a reply when a saga is waiting for it.</summary>
    public async Task BookAsync(Guid correlationId, string learnerId, string planId, DateTimeOffset requestedAtUtc,
        int durationMinutes, bool notifySaga, string commandName)
    {
        // Derive the slot and ID from the command so a redelivery produces the same booking.
        var scheduledAt = new DateTimeOffset(requestedAtUtc.UtcDateTime.Date.AddDays(7).AddHours(10));
        var assessmentId = $"ASM-{correlationId:N}";

        if (notifySaga)
            await bus.Send(new AssessmentScheduled(correlationId, learnerId, planId,
                assessmentId, "Online diagnostic assessment", scheduledAt));

        logger.LogInformation("Handled {Command} from {QueueAddress}\n" +
            "    Assessment {AssessmentId} scheduled for {ScheduledAtUtc:u} ({DurationMinutes} minutes) | Saga notified {SagaNotified}\n" +
            "    Correlation {CorrelationId} | Message {MessageId}", commandName, queue.Address, assessmentId,
            scheduledAt, durationMinutes, notifySaga, correlationId, MessageContext.Current.TransportMessage.GetMessageId());
    }

    /// <summary>Reports a saga failure when requested and dead-letters the original command.</summary>
    public async Task FailAsync(Guid correlationId, bool notifySaga, string commandName, string errorDescription)
    {
        if (notifySaga && correlationId != Guid.Empty)
        {
            await bus.Send(new AssessmentSchedulingFailed(correlationId, "Take could not schedule the assessment."));
            logger.LogWarning("Failed {Command} -> queued {Reply}\n    Correlation {CorrelationId}",
                commandName, nameof(AssessmentSchedulingFailed), correlationId);
        }

        await bus.Advanced.TransportMessage.Deadletter(errorDescription);
    }
}
