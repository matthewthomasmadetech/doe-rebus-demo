using System.ComponentModel.DataAnnotations;
using Rebus.Bus;
using Rebus.Messages;
using RebusDemo.Contracts;

namespace RebusDemo.Develop;

/// <summary>Inputs for a standalone assessment request sent from Develop.</summary>
internal sealed record ScheduleAssessmentRequest(string? LearnerId, string? LearningPlanId, int? DurationMinutes);

/// <summary>Demonstrates sending a versioned capability command through the orchestrator.</summary>
internal static class ScheduleAssessmentEndpoint
{
    /// <summary>Validates the chosen contract and returns its correlation ID once queued.</summary>
    public static async Task<IResult> ScheduleAsync(string version, ScheduleAssessmentRequest request,
        IBus bus, ILogger<ScheduleAssessmentRequest> logger)
    {
        var correlationId = Guid.NewGuid();
        var learnerId = request.LearnerId?.Trim() ?? "";
        var planId = request.LearningPlanId?.Trim() ?? "";
        var requestedAtUtc = DateTimeOffset.UtcNow;

        // Standalone bookings have a correlation ID for tracing but do not start or complete a saga.
        IValidatedMessage? command = version switch
        {
            "v1" => new ScheduleAssessmentV1(correlationId, learnerId, planId, requestedAtUtc, NotifySaga: false),
            "v2" => new ScheduleAssessmentV2(correlationId, learnerId, planId, requestedAtUtc,
                request.DurationMinutes.GetValueOrDefault(), NotifySaga: false),
            _ => null
        };
        if (command is null)
            return Results.BadRequest(new { error = "Choose assessment version v1 or v2." });

        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(command, new ValidationContext(command), errors, validateAllProperties: true))
            return Results.BadRequest(new { errors = errors.Select(error => error.ErrorMessage) });

        await bus.Send(command, new Dictionary<string, string> { [Headers.CorrelationId] = correlationId.ToString() });
        logger.LogInformation("Queued {Command} to {DestinationQueue}\n    Correlation {CorrelationId}",
            command.GetType().Name, QueueNames.Orchestrator, correlationId);
        return Results.Accepted(value: new { correlationId });
    }
}
