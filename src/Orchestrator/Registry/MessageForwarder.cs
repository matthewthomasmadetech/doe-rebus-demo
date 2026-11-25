using Rebus.Messages;
using Rebus.Routing.TransportMessages;

namespace RebusDemo.Orchestrator.Registry;

/// <summary>The header names the orchestrator reads. These match the names the services set.</summary>
internal static class WorkflowHeaders
{
    public const string WorkflowType = "x-workflow-type";
}

/// <summary>Forwards messages that services send as standalone actions.</summary>
internal static class MessageForwarder
{
    /// <summary>
    /// Validates a message from a service against its registered contract, then forwards it to every
    /// queue registered for its workflow type and message type, without deserializing it.
    /// </summary>
    public static async Task<ForwardAction> ForwardAsync(RouteRegistry registry, TransportMessage message)
    {
        if (!message.Headers.TryGetValue(Headers.Type, out var messageType))
            return ForwardAction.None;

        if (!message.Headers.TryGetValue(WorkflowHeaders.WorkflowType, out var workflowType))
            throw new InvalidMessageContractException(
                $"{messageType} has no {WorkflowHeaders.WorkflowType} header, so the orchestrator cannot tell which workflow it belongs to.");

        var route = await registry.FindRouteAsync(workflowType, messageType);

        // Unregistered types continue to the workflow engine, which handles replies that workflows wait for.
        if (route is null)
            return ForwardAction.None;

        if (route.ContractSchema is not null)
            ContractValidator.Validate(messageType, route.ContractSchema, message.Body);

        return ForwardAction.ForwardTo(route.Queues);
    }
}
