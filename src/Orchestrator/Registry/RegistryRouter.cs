using Rebus.Messages;
using Rebus.Routing;
using Rebus.Routing.TransportMessages;
using RebusDemo.Contracts;
using IRouter = Rebus.Routing.IRouter;

namespace RebusDemo.Orchestrator.Registry;

/// <summary>Routes outgoing orchestrator commands using the registry, so no queue names live in code.</summary>
internal sealed class RegistryRouter(RouteRegistry registry) : IRouter
{
    public async Task<string> GetDestinationAddress(Message message)
    {
        var type = message.Body.GetType();
        var messageType = $"{type.FullName}, {type.Assembly.GetName().Name}";

        // Messages with no registration, such as the registration command, stay on the orchestrator queue.
        return (await registry.FindRouteAsync(messageType))?.Queue ?? QueueNames.Orchestrator;
    }

    // Publish/subscribe is not used in this design.
    public Task<string> GetOwnerAddress(string topic) => Task.FromResult(QueueNames.Orchestrator);

    /// <summary>
    /// Validates a message from a service against its registered contract, then forwards it to its
    /// registered queue without deserializing it.
    /// </summary>
    public static async Task<ForwardAction> ForwardRegisteredAsync(RouteRegistry registry, TransportMessage message)
    {
        if (!message.Headers.TryGetValue(Headers.Type, out var messageType))
            return ForwardAction.None;

        var route = await registry.FindRouteAsync(messageType);

        // Unregistered types fall through to the orchestrator's own handlers, such as the saga.
        if (route is null || route.Queue == QueueNames.Orchestrator)
            return ForwardAction.None;

        if (route.ContractSchema is not null)
            ContractValidator.Validate(messageType, route.ContractSchema, message.Body);

        return ForwardAction.ForwardTo(route.Queue);
    }
}
