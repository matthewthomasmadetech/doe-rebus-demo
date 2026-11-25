namespace RebusDemo.Contracts;

/// <summary>
/// Queue addresses shared by the receiving endpoints and the applications that send to them.
/// </summary>
public static class QueueNames
{
    public const string Orchestrator = "orchestrator-queue";

    // Each worker command version has its own queue so a receiver can choose which versions it consumes.
    public const string CreateLearnerV1 = "create-learner-v1";
    public const string CreateLearnerV2 = "create-learner-v2";
    public const string CreateLearningPlanV1 = "create-learning-plan-v1";
    public const string SendLearnerReminderV1 = "send-learner-reminder-v1";
    public const string ScheduleAssessmentV1 = "schedule-assessment-v1";
    public const string ScheduleAssessmentV2 = "schedule-assessment-v2";

    // A second V2 destination for demonstrating a routing change while the apps are running.
    public const string ScheduleAssessmentV2Alternate = "schedule-assessment-v2-alternate";

    // All application endpoints forward messages here when their delivery attempts are exhausted.
    public const string Errors = "rebus-errors";
}
