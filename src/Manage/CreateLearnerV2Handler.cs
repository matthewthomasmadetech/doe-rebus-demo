using Rebus.Handlers;
using Rebus.Retry.Simple;
using RebusDemo.Contracts;

namespace RebusDemo.Manage;

/// <summary>Handles the learner creation contract that requires a preferred name on the V2 queue.</summary>
public sealed class CreateLearnerV2Handler(LearnerCreation learnerCreation)
    : IHandleMessages<CreateLearnerV2>, IHandleMessages<IFailed<CreateLearnerV2>>
{
    /// <summary>Creates the demo learner and records that a preferred name was supplied.</summary>
    public Task Handle(CreateLearnerV2 message) =>
        learnerCreation.CreateAsync(message.CorrelationId, nameof(CreateLearnerV2), message.PreferredName);

    /// <summary>Reports an exhausted V2 delivery using the common failure status update.</summary>
    public Task Handle(IFailed<CreateLearnerV2> failure) =>
        learnerCreation.FailAsync(failure.Message.CorrelationId, nameof(CreateLearnerV2), failure.ErrorDescription);
}
