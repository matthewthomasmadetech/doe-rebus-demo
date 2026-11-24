using Rebus.Bus;
using Rebus.Exceptions;
using Rebus.Handlers;
using Rebus.Retry.Simple;
using RebusDemo.Contracts;

namespace RebusDemo.Manage;

/// <summary>
/// Handles learner creation commands received by Manage and sends the result over Rebus.
/// </summary>
public sealed class CreateLearnerHandler(IBus bus, ILogger<CreateLearnerHandler> logger)
    : IHandleMessages<CreateLearnerV1>, IHandleMessages<IFailed<CreateLearnerV1>>
{
    /// <summary>Generates a demo learner ID and sends it back with the same workflow correlation ID.</summary>
    public async Task Handle(CreateLearnerV1 message)
    {
        // One demo registration per workflow. A redelivery produces the same learner ID.
        var learnerId = $"LRN-{message.CorrelationId:N}";

        // The shared route for LearnerCreated sends the result back to the orchestrator.
        await bus.Send(new LearnerCreated(message.CorrelationId, learnerId));
        logger.LogInformation("Handled {Command} -> sent {Reply}\n    Learner {LearnerId} | Correlation {CorrelationId}",
            nameof(CreateLearnerV1), nameof(LearnerCreated), learnerId, message.CorrelationId);
    }

    /// <summary>Reports a failed command to its saga and dead-letters the original command.</summary>
    public async Task Handle(IFailed<CreateLearnerV1> failure)
    {
        var correlationId = failure.Message.CorrelationId;
        if (correlationId != Guid.Empty)
        {
            await bus.Send(new LearnerCreationFailed(correlationId, "Manage could not create the learner."));
            logger.LogWarning("Failed {Command} -> queued {Reply}\n    Correlation {CorrelationId}",
                nameof(CreateLearnerV1), nameof(LearnerCreationFailed), correlationId);
        }

        await bus.Advanced.TransportMessage.Deadletter(failure.ErrorDescription);
    }
}
