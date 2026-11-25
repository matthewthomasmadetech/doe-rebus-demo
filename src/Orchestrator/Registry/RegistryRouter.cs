using System.Text;
using Rebus.Messages;
using Rebus.Routing;
using Rebus.Routing.TransportMessages;
using RebusDemo.Orchestrator.Workflows;
using IRouter = Rebus.Routing.IRouter;

namespace RebusDemo.Orchestrator.Registry;

/// <summary>Routes outgoing orchestrator commands using the registry, so no queue names live in code.</summary>
internal sealed class RegistryRouter(RouteRegistry registry) : IRouter
{
    public async Task<string> GetDestinationAddress(Message message)
    {
        if (message.Body is not RawJsonMessage raw)
            throw new InvalidOperationException($"The orchestrator only sends {nameof(RawJsonMessage)}, not {message.Body.GetType().Name}.");

        var route = await registry.FindRouteAsync(raw.MessageType)
            ?? throw new InvalidOperationException($"No route is registered for {raw.MessageType}.");

        // Commands the orchestrator builds are held to the same contract as messages it forwards.
        if (route.ContractSchema is not null)
            ContractValidator.Validate(raw.MessageType, route.ContractSchema, Encoding.UTF8.GetBytes(raw.Json));

        return route.Queue;
    }

    // Publish/subscribe is not used in this design.
    public Task<string> GetOwnerAddress(string topic) => Task.FromResult(Queues.Orchestrator);

    /// <summary>
    /// Validates a message from a service against its registered contract, then forwards it to its
    /// registered queue without deserializing it.
    /// </summary>
    public static async Task<ForwardAction> ForwardRegisteredAsync(RouteRegistry registry, TransportMessage message)
    {
        if (!message.Headers.TryGetValue(Headers.Type, out var messageType))
            return ForwardAction.None;

        var route = await registry.FindRouteAsync(messageType);

        // Unregistered types continue to the workflow engine, which handles replies that workflows wait for.
        if (route is null || route.Queue == Queues.Orchestrator)
            return ForwardAction.None;

        if (route.ContractSchema is not null)
            ContractValidator.Validate(messageType, route.ContractSchema, message.Body);

        return ForwardAction.ForwardTo(route.Queue);
    }
}
