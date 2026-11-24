using Rebus.Bus;
using Rebus.Exceptions;
using Rebus.Handlers;
using Rebus.Retry.Simple;
using RebusDemo.Contracts;

namespace RebusDemo.Develop;

/// <summary>Processes standalone reminder commands on their dedicated capability queue.</summary>
public sealed class SendLearnerReminderHandler(IBus bus, ILogger<SendLearnerReminderHandler> logger)
    : IHandleMessages<SendLearnerReminderV1>, IHandleMessages<IFailed<SendLearnerReminderV1>>
{
    /// <summary>Simulates sending a learning plan reminder by logging the result.</summary>
    public Task Handle(SendLearnerReminderV1 message)
    {
        logger.LogInformation("Handled {Command} | Simulated sending a learning plan reminder\n" +
            "    Learner {LearnerId} | Reminder {ReminderId}",
            nameof(SendLearnerReminderV1), message.LearnerId, message.ReminderId);

        return Task.CompletedTask;
    }

    /// <summary>Preserves the reminder and its diagnostics in the error queue after exhausted retries.</summary>
    public Task Handle(IFailed<SendLearnerReminderV1> failure) =>
        bus.Advanced.TransportMessage.Deadletter(failure.ErrorDescription);
}
