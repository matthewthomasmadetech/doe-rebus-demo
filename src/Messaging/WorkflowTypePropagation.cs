using Rebus.Config;
using Rebus.Messages;
using Rebus.Pipeline;
using Rebus.Pipeline.Send;

namespace RebusDemo.Messaging;

/// <summary>Header names the orchestrator reads on every message it receives.</summary>
public static class WorkflowHeaders
{
    /// <summary>
    /// The workflow or action the message belongs to, such as register-learner. The orchestrator
    /// has its own copy of this name, so the two must match.
    /// </summary>
    public const string WorkflowType = "x-workflow-type";
}

/// <summary>Copies the workflow type from the command being handled onto every message the handler sends.</summary>
public static class WorkflowTypePropagation
{
    public static void PropagateWorkflowType(this OptionsConfigurer options) =>
        options.Decorate<IPipeline>(context => new PipelineStepInjector(context.Get<IPipeline>())
            .OnSend(new PropagateWorkflowTypeStep(), PipelineRelativePosition.After, typeof(AutoHeadersOutgoingStep)));

    private sealed class PropagateWorkflowTypeStep : IOutgoingStep
    {
        public Task Process(OutgoingStepContext context, Func<Task> next)
        {
            // A message sent outside a handler, such as from an HTTP endpoint, must set the header itself.
            var message = context.Load<Message>();
            if (!message.Headers.ContainsKey(WorkflowHeaders.WorkflowType)
                && MessageContext.Current?.Headers.TryGetValue(WorkflowHeaders.WorkflowType, out var workflowType) == true)
            {
                message.Headers[WorkflowHeaders.WorkflowType] = workflowType;
            }

            return next();
        }
    }
}
