using Rebus.Bus;
using Rebus.Messages;
using Rebus.Pipeline;
using Rebus.Transport;

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
        if (message.Headers.TryGetValue(Headers.Type, out var messageType) && await store.IsReplyTriggerAsync(messageType))
        {
            await engine.HandleReplyAsync(bus(), context.Load<ITransactionContext>(), messageType, message.Body);
            return;
        }

        await next();
    }
}
