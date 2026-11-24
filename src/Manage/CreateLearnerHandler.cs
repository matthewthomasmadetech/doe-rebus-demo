using Rebus.Handlers;
using Rebus.Retry.Simple;
using RebusDemo.Contracts;

namespace RebusDemo.Manage;

/// <summary>Handles the original learner creation contract on the V1 queue.</summary>
public sealed class CreateLearnerHandler(LearnerCreation learnerCreation)
    : IHandleMessages<CreateLearnerV1>, IHandleMessages<IFailed<CreateLearnerV1>>
{
    /// <summary>Creates the demo learner using the original request fields.</summary>
    public Task Handle(CreateLearnerV1 message) =>
        learnerCreation.CreateAsync(message.CorrelationId, nameof(CreateLearnerV1));

    /// <summary>Reports an exhausted V1 delivery using the common failure status update.</summary>
    public Task Handle(IFailed<CreateLearnerV1> failure) =>
        learnerCreation.FailAsync(failure.Message.CorrelationId, nameof(CreateLearnerV1), failure.ErrorDescription);
}
