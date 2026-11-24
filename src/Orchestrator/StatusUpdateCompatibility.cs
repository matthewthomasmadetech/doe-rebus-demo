using Rebus.Config;
using Rebus.Serialization;
using Rebus.Serialization.Custom;
using RebusDemo.Contracts;

namespace RebusDemo.Orchestrator;

/// <summary>Allows the orchestrator to receive status updates queued under their previous names.</summary>
internal static class StatusUpdateCompatibility
{
    public static void Configure(StandardConfigurer<ISerializer> serialization)
    {
        // Rebus reads historical names through these aliases and resolves current names through its default convention.
        serialization.UseCustomMessageTypeNames()
            .AllowFallbackToDefaultConvention()
            .AddWithCustomName<LearnerCreated>("RebusDemo.Contracts.LearnerCreatedV1, Contracts")
            .AddWithCustomName<LearnerCreationFailed>("RebusDemo.Contracts.LearnerCreationFailedV1, Contracts")
            .AddWithCustomName<LearningPlanCreated>("RebusDemo.Contracts.LearningPlanCreatedV1, Contracts")
            .AddWithCustomName<LearningPlanCreationFailed>("RebusDemo.Contracts.LearningPlanCreationFailedV1, Contracts")
            .AddWithCustomName<AssessmentScheduled>("RebusDemo.Contracts.AssessmentScheduledV1, Contracts")
            .AddWithCustomName<AssessmentSchedulingFailed>("RebusDemo.Contracts.AssessmentSchedulingFailedV1, Contracts");
    }
}
