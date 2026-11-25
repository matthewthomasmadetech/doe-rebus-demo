using Rebus.Bus;
using Rebus.Handlers;
using Rebus.Sagas;
using Rebus.Sagas.Idempotent;
using RebusDemo.Contracts;
using RebusDemo.Orchestrator.Dashboard;

namespace RebusDemo.Orchestrator;

/// <summary>
/// Coordinates registration across Manage, Develop and Take, with one saga per correlation ID.
/// </summary>
/// <remarks>
/// Rebus loads Data before each handler runs and saves it after the handler returns successfully.
/// IdempotentSaga derives from Saga and lets Rebus retain outgoing commands for replay on redelivery.
/// </remarks>
public sealed class LearnerRegistrationSaga(IBus bus, DashboardStore dashboard, ILogger<LearnerRegistrationSaga> logger)
    : IdempotentSaga<LearnerRegistrationSagaData>,
        IAmInitiatedBy<RegisterLearnerV1>,
        IHandleMessages<LearnerCreated>,
        IHandleMessages<LearnerCreationFailed>,
        IHandleMessages<LearningPlanCreated>,
        IHandleMessages<LearningPlanCreationFailed>,
        IHandleMessages<AssessmentScheduled>,
        IHandleMessages<AssessmentSchedulingFailed>
{
    /// <summary>Maps the correlation ID from each incoming message to the indexed saga property.</summary>
    protected override void CorrelateMessages(ICorrelationConfig<LearnerRegistrationSagaData> config)
    {
        // IAmInitiatedBy lets Rebus create saga data when a registration has no match.
        // Success and failure replies match an existing saga through the same workflow ID.
        config.Correlate<RegisterLearnerV1>(message => message.CorrelationId, data => data.CorrelationId);
        config.Correlate<LearnerCreated>(message => message.CorrelationId, data => data.CorrelationId);
        config.Correlate<LearnerCreationFailed>(message => message.CorrelationId, data => data.CorrelationId);
        config.Correlate<LearningPlanCreated>(message => message.CorrelationId, data => data.CorrelationId);
        config.Correlate<LearningPlanCreationFailed>(message => message.CorrelationId, data => data.CorrelationId);
        config.Correlate<AssessmentScheduled>(message => message.CorrelationId, data => data.CorrelationId);
        config.Correlate<AssessmentSchedulingFailed>(message => message.CorrelationId, data => data.CorrelationId);
    }

    /// <summary>Starts the registration by saving the values needed later and asking Manage to create the learner.</summary>
    public async Task Handle(RegisterLearnerV1 message)
    {
        if (!IsAtStage(RegistrationStage.New, nameof(RegisterLearnerV1)))
            return;

        Data.CorrelationId = message.CorrelationId;
        Data.LearningGoal = message.LearningGoal;
        Data.PreferredFormat = message.PreferredFormat;
        Data.RequestedAtUtc = message.RequestedAtUtc;

        // A preferred name requires V2. Both versions send the same LearnerCreated status update.
        // Both commands enter the orchestrator queue before the transport forwarder sends them to Manage.
        if (string.IsNullOrWhiteSpace(message.PreferredName))
        {
            await bus.Send(new CreateLearnerV1(Data.CorrelationId, message.FirstName, message.LastName, message.Email));
            MoveTo(RegistrationStage.AwaitingLearner, nameof(RegisterLearnerV1), nameof(CreateLearnerV1));
        }
        else
        {
            await bus.Send(new CreateLearnerV2(Data.CorrelationId, message.FirstName, message.LastName, message.Email,
                message.PreferredName));
            MoveTo(RegistrationStage.AwaitingLearner, nameof(RegisterLearnerV1), nameof(CreateLearnerV2));
        }
    }

    /// <summary>Records the learner ID and asks Develop to build a plan using the saved learning goal.</summary>
    public async Task Handle(LearnerCreated message)
    {
        if (!IsAtStage(RegistrationStage.AwaitingLearner, nameof(LearnerCreated)))
            return;

        Data.LearnerId = message.LearnerId;
        await bus.Send(new CreateLearningPlanV1(Data.CorrelationId, message.LearnerId, Data.LearningGoal, Data.PreferredFormat));
        MoveTo(RegistrationStage.AwaitingLearningPlan, nameof(LearnerCreated), nameof(CreateLearningPlanV1));
    }

    /// <summary>Records the plan ID and asks Take to schedule the assessment.</summary>
    public async Task Handle(LearningPlanCreated message)
    {
        if (!IsAtStage(RegistrationStage.AwaitingLearningPlan, nameof(LearningPlanCreated)))
            return;

        Data.LearningPlanId = message.LearningPlanId;
        await bus.Send(new ScheduleAssessmentV1(Data.CorrelationId, message.LearnerId,
            message.LearningPlanId, Data.RequestedAtUtc));
        MoveTo(RegistrationStage.AwaitingAssessment, nameof(LearningPlanCreated), nameof(ScheduleAssessmentV1));
    }

    /// <summary>Completes the saga after Take confirms the assessment booking.</summary>
    public async Task Handle(AssessmentScheduled message)
    {
        if (!IsAtStage(RegistrationStage.AwaitingAssessment, nameof(AssessmentScheduled)))
            return;

        // Keep the completed outcome available for the dashboard before Rebus removes the live saga.
        await dashboard.RecordCompletionAsync(Data, message);
        // Rebus removes the completed saga row and its index entry after successful handling.
        MarkAsComplete();
        logger.LogInformation("{PreviousStage} -> {Stage} on {MessageType}\n" +
            "    Learner {LearnerId} | Plan {LearningPlanId}\n" +
            "    Assessment {AssessmentId} at {ScheduledAtUtc:u}\n    Correlation {CorrelationId}",
            Data.Stage, RegistrationStage.Completed, nameof(AssessmentScheduled), Data.LearnerId,
            Data.LearningPlanId, message.AssessmentId, message.ScheduledAtUtc, Data.CorrelationId);
    }

    /// <summary>Fails the registration when Manage exhausts its learner creation attempts.</summary>
    public Task Handle(LearnerCreationFailed message) =>
        RecordFailure(RegistrationStage.AwaitingLearner, nameof(LearnerCreationFailed), message.Reason);

    /// <summary>Fails the registration when Develop exhausts its planning attempts.</summary>
    public Task Handle(LearningPlanCreationFailed message) =>
        RecordFailure(RegistrationStage.AwaitingLearningPlan, nameof(LearningPlanCreationFailed), message.Reason);

    /// <summary>Fails the registration when Take exhausts its scheduling attempts.</summary>
    public Task Handle(AssessmentSchedulingFailed message) =>
        RecordFailure(RegistrationStage.AwaitingAssessment, nameof(AssessmentSchedulingFailed), message.Reason);

    /// <summary>Records a failure for the current stage and retains the saga for inspection.</summary>
    private Task RecordFailure(RegistrationStage expectedStage, string messageType, string reason)
    {
        if (!IsAtStage(expectedStage, messageType))
            return Task.CompletedTask;

        Data.FailedAtStage = Data.Stage;
        Data.FailureReason = reason;
        Data.Stage = RegistrationStage.Failed;

        logger.LogWarning("{PreviousStage} -> {Stage} on {MessageType}\n" +
            "    Reason {FailureReason} | Correlation {CorrelationId}",
            expectedStage, Data.Stage, messageType, reason, Data.CorrelationId);

        return Task.CompletedTask;
    }

    /// <summary>Checks the workflow stage before allowing a message to advance the saga.</summary>
    private bool IsAtStage(RegistrationStage expectedStage, string messageType)
    {
        if (Data.Stage == expectedStage)
            return true;

        // A redelivered command can produce a reply with a new message ID. The stage check
        // also covers that case because Rebus's replay history is keyed by individual message ID.
        logger.LogInformation("Ignored {MessageType} in {Stage} (expected {ExpectedStage})\n    Correlation {CorrelationId}",
            messageType, Data.Stage, expectedStage, Data.CorrelationId);
        return false;
    }

    /// <summary>Updates the stage Rebus will persist after handling and logs the transition.</summary>
    private void MoveTo(RegistrationStage nextStage, string messageType, string nextCommand)
    {
        var previousStage = Data.Stage;
        Data.Stage = nextStage;
        logger.LogInformation("{PreviousStage} -> {Stage} on {MessageType}\n" +
            "    Queued {NextCommand} | Correlation {CorrelationId}",
            previousStage, nextStage, messageType, nextCommand, Data.CorrelationId);
    }
}
