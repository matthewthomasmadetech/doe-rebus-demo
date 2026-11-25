using Rebus.Config;
using Rebus.Routing;
using Rebus.Routing.TypeBased;
using RebusDemo.Contracts;

namespace RebusDemo.Messaging;

/// <summary>The single entry point shared by every application message sender.</summary>
public static class RebusRoutes
{
    /// <summary>Sends shared contracts to the orchestrator, which resolves worker destinations.</summary>
    public static void Configure(StandardConfigurer<IRouter> routing)
    {
        routing.TypeBased().MapAssemblyOf<RegisterLearnerV1>(QueueNames.Orchestrator);
    }
}
