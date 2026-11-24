using Rebus.Bus;
using RebusDemo.Contracts;

namespace RebusDemo.Manage;

/// <summary>The learner ID supplied by the caller requesting a demo reminder.</summary>
internal sealed record SendLearnerReminderRequest(string? LearnerId);

/// <summary>Queues a standalone reminder from Manage's HTTP API.</summary>
internal static class SendLearnerReminderEndpoint
{
    /// <summary>Validates the learner ID and returns HTTP 202 once the command is queued.</summary>
    public static async Task<IResult> SendAsync(
        SendLearnerReminderRequest request, IBus bus, ILogger<SendLearnerReminderRequest> logger)
    {
        var learnerId = request.LearnerId?.Trim();
        if (string.IsNullOrEmpty(learnerId) || learnerId.Length > 80)
            return Results.BadRequest(new { error = "learnerId must contain between 1 and 80 characters." });

        // The caller can use this ID to find the matching log entry in Develop.
        var reminderId = Guid.NewGuid();
        await bus.Send(new SendLearnerReminderV1(reminderId, learnerId));
        logger.LogInformation("Queued {Command} for Develop\n    Learner {LearnerId} | Reminder {ReminderId}",
            nameof(SendLearnerReminderV1), learnerId, reminderId);

        return Results.Accepted(value: new { reminderId });
    }
}
