using Rebus.Bus;
using Rebus.Handlers;
using Rebus.Pipeline;
using RebusDemo.Contracts;

namespace RebusDemo.Develop;

/// <summary>
/// Builds a learning plan for the learner's goal and sends the result to the orchestrator.
/// </summary>
public sealed class CreateLearningPlanHandler(
    IBus bus, DemoFailure demoFailure, ILogger<CreateLearningPlanHandler> logger)
    : IHandleMessages<CreateLearningPlanV1>
{
    /// <summary>Handles one delivery of a plan request, including the optional retry demonstration.</summary>
    public async Task Handle(CreateLearningPlanV1 message)
    {
        // This Rebus message ID identifies the individual command across retries. The
        // correlation ID in the body is shared by every command and reply in the workflow.
        var messageId = MessageContext.Current.TransportMessage.GetMessageId();
        if (demoFailure.ConsumeFailure())
        {
            logger.LogWarning("Simulating a temporary planning failure for {CorrelationId}, message {MessageId}",
                message.CorrelationId, messageId);

            // Let the exception reach Rebus so its retry policy can redeliver the command.
            throw new InvalidOperationException("Simulated temporary planning service failure.");
        }

        // Keep the plan ID and content stable across deliveries of this command.
        var planId = $"PLAN-{message.CorrelationId:N}";
        string[] modules = [
            "Baseline skills review",
            $"Guided practice: {message.LearningGoal}",
            "Independent practice and reflection"
        ];

        await bus.Send(new LearningPlanCreatedV1(message.CorrelationId, message.LearnerId, planId, modules));
        logger.LogInformation("Handled {Command} and sent {Reply} for {CorrelationId}, message {MessageId}. " +
            "Plan {LearningPlanId} contains {ModuleCount} modules",
            nameof(CreateLearningPlanV1), nameof(LearningPlanCreatedV1), message.CorrelationId, messageId,
            planId, modules.Length);
    }
}
