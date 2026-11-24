using Rebus.Bus;
using Rebus.Exceptions;
using Rebus.Handlers;
using Rebus.Pipeline;
using Rebus.Retry.Simple;
using RebusDemo.Contracts;

namespace RebusDemo.Develop;

/// <summary>
/// Builds a learning plan for the learner's goal and sends the result to the orchestrator.
/// </summary>
public sealed class CreateLearningPlanHandler(
    IBus bus, DemoFailure demoFailure, ILogger<CreateLearningPlanHandler> logger)
    : IHandleMessages<CreateLearningPlanV1>, IHandleMessages<IFailed<CreateLearningPlanV1>>
{
    private const string PermanentFailureGoal = "DEMO_FAIL_LEARNING_PLAN";

    /// <summary>Handles one delivery of a plan request, including the optional retry demonstration.</summary>
    public async Task Handle(CreateLearningPlanV1 message)
    {
        // This Rebus message ID identifies the individual command across retries. The
        // correlation ID in the body is shared by every command and reply in the workflow.
        var messageId = MessageContext.Current.TransportMessage.GetMessageId();
        // This sample value throws on every delivery so Rebus reaches second-level handling.
        if (message.LearningGoal == PermanentFailureGoal)
        {
            logger.LogWarning("Simulating a permanent planning failure\n    Correlation {CorrelationId} | Message {MessageId}",
                message.CorrelationId, messageId);
            throw new InvalidOperationException("Simulated permanent learning plan failure for the demo goal.");
        }

        if (demoFailure.ConsumeFailure())
        {
            logger.LogWarning("Simulating a temporary planning failure\n    Correlation {CorrelationId} | Message {MessageId}",
                message.CorrelationId, messageId);

            // Let the exception reach Rebus so its retry policy can redeliver the command.
            throw new InvalidOperationException("Simulated temporary planning service failure.");
        }

        // Keep the plan ID and content stable across deliveries of this command.
        var planId = $"PLAN-{message.CorrelationId:N}";
        var format = message.PreferredFormat ?? LearningPlanFormats.Online;
        var practice = format == LearningPlanFormats.Workbook ? "Workbook practice" : "Guided practice";
        string[] modules = [
            "Baseline skills review",
            $"{practice}: {message.LearningGoal}",
            "Independent practice and reflection"
        ];

        await bus.Send(new LearningPlanCreatedV1(message.CorrelationId, message.LearnerId, planId, modules));
        logger.LogInformation("Handled {Command} -> sent {Reply}\n" +
            "    Plan {LearningPlanId} ({ModuleCount} modules, {PlanFormat})\n    Correlation {CorrelationId} | Message {MessageId}",
            nameof(CreateLearningPlanV1), nameof(LearningPlanCreatedV1), planId, modules.Length, format,
            message.CorrelationId, messageId);
    }

    /// <summary>Reports a failed command to its saga and dead-letters the original command.</summary>
    public async Task Handle(IFailed<CreateLearningPlanV1> failure)
    {
        var correlationId = failure.Message.CorrelationId;
        if (correlationId != Guid.Empty)
        {
            await bus.Send(new LearningPlanCreationFailedV1(correlationId, "Develop could not create the learning plan."));
            logger.LogWarning("Failed {Command} -> queued {Reply}\n    Correlation {CorrelationId}",
                nameof(CreateLearningPlanV1), nameof(LearningPlanCreationFailedV1), correlationId);
        }

        await bus.Advanced.TransportMessage.Deadletter(failure.ErrorDescription);
    }
}
