using Rebus.Bus;
using Rebus.Handlers;
using RebusDemo.Contracts;

namespace RebusDemo.Manage;

/// <summary>
/// Handles learner creation commands received by Manage and sends the result over Rebus.
/// </summary>
public sealed class CreateLearnerHandler(IBus bus, ILogger<CreateLearnerHandler> logger)
    : IHandleMessages<CreateLearnerV1>
{
    /// <summary>Generates a demo learner ID and sends it back with the same workflow correlation ID.</summary>
    public async Task Handle(CreateLearnerV1 message)
    {
        // One demo registration per workflow. A redelivery produces the same learner ID.
        var learnerId = $"LRN-{message.CorrelationId:N}";

        // The outgoing message type selects the queue via the route in Manage's Program.cs.
        await bus.Send(new LearnerCreatedV1(message.CorrelationId, learnerId));
        logger.LogInformation("Handled {Command} -> sent {Reply}\n    Learner {LearnerId} | Correlation {CorrelationId}",
            nameof(CreateLearnerV1), nameof(LearnerCreatedV1), learnerId, message.CorrelationId);
    }
}
