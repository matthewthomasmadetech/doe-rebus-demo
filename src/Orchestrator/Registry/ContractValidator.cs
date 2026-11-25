using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;

namespace RebusDemo.Orchestrator.Registry;

/// <summary>Raised when a message body does not satisfy the JSON Schema registered for its type.</summary>
internal sealed class InvalidMessageContractException(string message) : Exception(message);

/// <summary>Validates raw message bodies against the JSON Schemas held in the registry.</summary>
internal static class ContractValidator
{
    private static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new();

    /// <summary>Throws when the body is not valid JSON or does not satisfy the schema.</summary>
    public static void Validate(string messageType, string contractSchema, ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException exception)
        {
            throw new InvalidMessageContractException($"{messageType} body is not valid JSON. {exception.Message}");
        }

        using var _ = document;
        var schema = Schemas.GetOrAdd(contractSchema, text => JsonSchema.FromText(text));
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid)
            return;

        // Keywords under "not" report an error when they pass, so they would mislead the reader.
        var errors = (result.Details ?? [])
            .Where(detail => detail.Errors is { Count: > 0 } && !detail.EvaluationPath.ToString().Contains("/not"))
            .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}"));
        throw new InvalidMessageContractException(
            $"{messageType} failed its registered contract. {string.Join(" ", errors)}");
    }
}
