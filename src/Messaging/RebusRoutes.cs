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
            .Map<LearnerCreated>(QueueNames.Orchestrator)
            .Map<LearnerCreationFailed>(QueueNames.Orchestrator)
            .Map<LearningPlanCreated>(QueueNames.Orchestrator)
            .Map<LearningPlanCreationFailed>(QueueNames.Orchestrator)
            .Map<AssessmentScheduled>(QueueNames.Orchestrator)
            .Map<AssessmentSchedulingFailed>(QueueNames.Orchestrator)
            .Map<CreateLearnerV1>(QueueNames.Manage)
            .Map<CreateLearningPlanV1>(QueueNames.Develop)
            .Map<SendLearnerReminderV1>(QueueNames.Develop)
            .Map<ScheduleAssessmentV1>(QueueNames.Take);
    }
}
