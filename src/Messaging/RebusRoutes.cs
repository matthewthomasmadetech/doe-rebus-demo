using Rebus.Config;
using Rebus.Routing;
using Rebus.Routing.TypeBased;
using RebusDemo.Contracts;

namespace RebusDemo.Messaging;

/// <summary>The queue destinations shared by every Rebus sender in the demo.</summary>
public static class RebusRoutes
{
    /// <summary>Registers the same outgoing message routes on each service's local Rebus bus.</summary>
    public static void Configure(StandardConfigurer<IRouter> routing)
    {
        routing.TypeBased()
            .Map<RegisterLearnerV1>(QueueNames.Orchestrator)
            .Map<LearnerCreatedV1>(QueueNames.Orchestrator)
            .Map<LearnerCreationFailedV1>(QueueNames.Orchestrator)
            .Map<LearningPlanCreatedV1>(QueueNames.Orchestrator)
            .Map<LearningPlanCreationFailedV1>(QueueNames.Orchestrator)
            .Map<AssessmentScheduledV1>(QueueNames.Orchestrator)
            .Map<AssessmentSchedulingFailedV1>(QueueNames.Orchestrator)
            .Map<CreateLearnerV1>(QueueNames.Manage)
            .Map<CreateLearningPlanV1>(QueueNames.Develop)
            .Map<SendLearnerReminderV1>(QueueNames.Develop)
            .Map<ScheduleAssessmentV1>(QueueNames.Take);
    }
}
