using Rebus.Bus;
using Rebus.Messages;
using Rebus.Pipeline;
using Rebus.Transport;
using RebusDemo.Orchestrator.Registry;

namespace RebusDemo.Orchestrator.Workflows;

/// <summary>
/// Handles replies that workflows are waiting for, working on the raw JSON before Rebus
/// tries to turn it into a .NET type. Every other message continues down the pipeline.
/// </summary>
internal sealed class WorkflowMessageStep(WorkflowStore store, WorkflowEngine engine, Func<IBus> bus) : IIncomingStep
{
    public async Task Process(IncomingStepContext context, Func<Task> next)
    {
        var message = context.Load<TransportMessage>();
        var headers = message.Headers;

        // A message with no workflow type continues, and the forwarder rejects it.
        if (headers.TryGetValue(Headers.Type, out var messageType)
            && headers.TryGetValue(WorkflowHeaders.WorkflowType, out var workflowType)
            && await store.IsReplyTriggerAsync(workflowType, messageType))
        {
            await engine.HandleReplyAsync(bus(), context.Load<ITransactionContext>(), messageType, workflowType,
                headers.GetValueOrDefault(Headers.ReturnAddress), message.Body);
            return;
        }

        await next();
    }
}
