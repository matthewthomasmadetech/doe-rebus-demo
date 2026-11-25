using Rebus.Config;
using Rebus.Retry.Simple;
using RebusDemo.Contracts;

namespace RebusDemo.Messaging;

/// <summary>Applies the common Rebus configuration for a worker's capability queue.</summary>
public static class CapabilityQueueConfiguration
{
    /// <summary>Sets the incoming queue, shared outgoing routes, validation and retry policy.</summary>
    public static RebusConfigurer UseCapabilityQueue(this RebusConfigurer configure, string storage, string queueName)
    {
        return configure
            .Transport(transport => transport.UseAzureStorageQueues(storage, queueName))
            .Routing(RebusRoutes.ToOrchestrator)
            .Options(options =>
            {
                // Second-level retries let the handler report the failure before dead-lettering the command.
                options.RetryStrategy(QueueNames.Errors, maxDeliveryAttempts: 5,
                    secondLevelRetriesEnabled: true, errorDetailsHeaderMaxLength: 4096);
                options.ValidateMessageContracts();
            });
    }
}
