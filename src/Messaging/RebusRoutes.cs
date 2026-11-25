using Rebus.Config;
using Rebus.Routing;

namespace RebusDemo.Messaging;

/// <summary>Outgoing message routing for the services that push to the orchestrator.</summary>
public static class RebusRoutes
{
    /// <summary>Routes every outgoing message from a service to the orchestrator queue.</summary>
    public static void ToOrchestrator(StandardConfigurer<IRouter> routing) =>
        routing.Register(_ => new OrchestratorOnlyRouter());
}
