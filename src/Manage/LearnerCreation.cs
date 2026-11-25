using Rebus.Bus;
using Rebus.Exceptions;
using RebusDemo.Contracts;

namespace RebusDemo.Manage;

/// <summary>Creates the demo learner and reports the same outcomes for both command versions.</summary>
public sealed class LearnerCreation(IBus bus, ILogger<LearnerCreation> logger)
{
    /// <summary>Sends a stable demo learner ID and logs whether a preferred name was supplied.</summary>
    public async Task CreateAsync(Guid correlationId, string commandName, string? preferredName = null)
    {
        // A redelivery for the same workflow produces the same learner ID.
        var learnerId = $"LRN-{correlationId:N}";
        await bus.Send(new LearnerCreated(correlationId, learnerId));
        logger.LogInformation("Handled {Command} -> sent {Reply}\n" +
            "    Learner {LearnerId} | Correlation {CorrelationId} | Has preferred name {HasPreferredName}",
            commandName, nameof(LearnerCreated), learnerId, correlationId, preferredName is not null);
    }

    /// <summary>Reports failure to the workflow and dead-letters the original command for inspection.</summary>
    public async Task FailAsync(Guid correlationId, string commandName, string errorDescription)
    {
        if (correlationId != Guid.Empty)
        {
            await bus.Send(new LearnerCreationFailed(correlationId, "Manage could not create the learner."));
            logger.LogWarning("Failed {Command} -> queued {Reply}\n    Correlation {CorrelationId}",
                commandName, nameof(LearnerCreationFailed), correlationId);
        }

        await bus.Advanced.TransportMessage.Deadletter(errorDescription);
    }
}
