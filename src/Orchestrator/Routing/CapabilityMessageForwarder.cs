using System.Text.RegularExpressions;
using Rebus.Extensions;
using Rebus.Messages;
using Rebus.Routing.TransportMessages;
using RebusDemo.Contracts;

namespace RebusDemo.Orchestrator.Routing;

/// <summary>Chooses a Rebus transport action using the message type header and the routing snapshot.</summary>
internal sealed partial class CapabilityMessageForwarder(
    CapabilityRouteCache routes, ILogger<CapabilityMessageForwarder> logger)
{
    // These messages are consumed here. A SQL route cannot override a saga handler.
    private static readonly HashSet<string> SagaMessageTypes = new(StringComparer.Ordinal)
    {
        typeof(RegisterLearnerV1).GetSimpleAssemblyQualifiedName(),
        typeof(LearnerCreated).GetSimpleAssemblyQualifiedName(),
        typeof(LearnerCreationFailed).GetSimpleAssemblyQualifiedName(),
        typeof(LearningPlanCreated).GetSimpleAssemblyQualifiedName(),
        typeof(LearningPlanCreationFailed).GetSimpleAssemblyQualifiedName(),
        typeof(AssessmentScheduled).GetSimpleAssemblyQualifiedName(),
        typeof(AssessmentSchedulingFailed).GetSimpleAssemblyQualifiedName(),
    };

    /// <summary>Lets saga messages continue and forwards each worker command to exactly one queue.</summary>
    public Task<ForwardAction> RouteAsync(TransportMessage message)
    {
        var type = message.Headers.GetValueOrDefault(Headers.Type) ?? "";
        if (SagaMessageTypes.Contains(type))
            return Task.FromResult(ForwardAction.None);

        var contract = VersionedContractPattern().Match(type);
        if (!contract.Success)
            throw new InvalidOperationException($"Unrecognized Rebus message type '{type}' on the orchestrator queue.");

        var name = contract.Groups["name"].Value;
        var version = contract.Groups["version"].Value;
        var destination = routes.FindDestination(name, version)
            ?? throw new InvalidOperationException($"No enabled capability route for {name} {version}.");

        logger.LogInformation("Forwarding {ContractName} {Version} to {DestinationQueue}\n" +
            "    Correlation {CorrelationId} | Message {MessageId}", name, version, destination,
            message.Headers.GetValueOrDefault(Headers.CorrelationId), message.Headers.GetValueOrDefault(Headers.MessageId));

        // Rebus sends the original transport message, including its headers and payload.
        return Task.FromResult(ForwardAction.ForwardTo(destination));
    }

    // The default Rebus type header includes the namespace, type name and simple assembly name.
    [GeneratedRegex(@"\ARebusDemo\.Contracts\.(?<name>[A-Za-z][A-Za-z0-9]*)(?<version>V[1-9][0-9]*), Contracts\z")]
    private static partial Regex VersionedContractPattern();
}
