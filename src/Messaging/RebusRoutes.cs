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
            .Map<CreateLearnerV1>(QueueNames.CreateLearnerV1)
            .Map<CreateLearnerV2>(QueueNames.CreateLearnerV2)
            .Map<CreateLearningPlanV1>(QueueNames.CreateLearningPlanV1)
            .Map<SendLearnerReminderV1>(QueueNames.SendLearnerReminderV1)
            .Map<ScheduleAssessmentV1>(QueueNames.ScheduleAssessmentV1);
    }
}
