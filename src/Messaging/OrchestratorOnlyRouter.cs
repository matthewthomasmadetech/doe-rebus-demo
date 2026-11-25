using Rebus.Messages;
using Rebus.Routing;
using RebusDemo.Contracts;

namespace RebusDemo.Messaging;

/// <summary>Sends every outgoing message to the orchestrator queue, so a service never needs another service's address.</summary>
internal sealed class OrchestratorOnlyRouter : IRouter
{
    public Task<string> GetDestinationAddress(Message message) => Task.FromResult(QueueNames.Orchestrator);

    // Publish/subscribe is not used in this design.
    public Task<string> GetOwnerAddress(string topic) => Task.FromResult(QueueNames.Orchestrator);
}
