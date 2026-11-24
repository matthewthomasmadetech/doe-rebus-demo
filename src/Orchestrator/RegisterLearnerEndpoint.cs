using System.ComponentModel.DataAnnotations;
using Rebus.Bus;
using Rebus.Messages;
using RebusDemo.Contracts;

namespace RebusDemo.Orchestrator;

/// <summary>The HTTP request body, which the endpoint validates before building the bus command.</summary>
public sealed record RegisterLearnerRequest(
    [property: Required, StringLength(80)] string FirstName,
    [property: Required, StringLength(80)] string LastName,
    [property: Required, EmailAddress, StringLength(254)] string Email,
    [property: Required, StringLength(160)] string LearningGoal);

/// <summary>Connects the HTTP API to the orchestrator's Rebus queue.</summary>
internal static class RegisterLearnerEndpoint
{
    /// <summary>Validates a registration and returns HTTP 202 once Rebus queues the command.</summary>
    public static async Task<IResult> RegisterAsync(
        RegisterLearnerRequest request, IBus bus, ILogger<RegisterLearnerRequest> logger)
    {
        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults,
            validateAllProperties: true))
        {
            var errors = validationResults
                .SelectMany(result => result.MemberNames.Select(member => (Member: member, result.ErrorMessage)))
                .GroupBy(error => error.Member)
                .ToDictionary(group => group.Key, group => group.Select(error => error.ErrorMessage!).ToArray());

            return Results.ValidationProblem(errors);
        }

        // The response gives the caller a workflow ID to follow in the service logs.
        var correlationId = Guid.NewGuid();
        var command = new RegisterLearnerV1(correlationId, request.FirstName.Trim(), request.LastName.Trim(),
            request.Email.Trim(), request.LearningGoal.Trim(), DateTimeOffset.UtcNow);

        // The saga correlates on the message body. Rebus propagates the matching header when
        // handlers send subsequent messages, so transport-level diagnostics can use the same ID.
        await bus.Send(command, new Dictionary<string, string> { [Headers.CorrelationId] = correlationId.ToString() });
        logger.LogInformation("Queued {Command}\n    Correlation {CorrelationId}", nameof(RegisterLearnerV1), correlationId);

        return Results.Accepted(value: new { correlationId });
    }
}
