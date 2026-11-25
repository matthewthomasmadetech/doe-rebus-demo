using System.Text.Json;
using System.Text.Json.Nodes;
using Rebus.Bus;
using RebusDemo.Orchestrator.Registry;

namespace RebusDemo.Orchestrator.Workflows;

/// <summary>Starts any registered workflow from an HTTP request.</summary>
internal static class StartWorkflowEndpoint
{
    /// <summary>Validates the request against the workflow's start contract, then returns HTTP 202 once the first command is queued.</summary>
    public static async Task<IResult> StartAsync(
        string name, JsonElement body, IBus bus, WorkflowStore store, WorkflowEngine engine)
    {
        var workflow = await store.FindWorkflowAsync(name);
        if (workflow is null)
            return Results.NotFound(new { error = $"No workflow named {name} is registered." });

        if (body.ValueKind != JsonValueKind.Object)
            return Results.BadRequest(new { error = "The request body must be a JSON object." });

        // Rebus writes PascalCase property names, so requests are normalised to match the registered contracts.
        var request = new JsonObject();
        foreach (var property in body.EnumerateObject())
            request[char.ToUpperInvariant(property.Name[0]) + property.Name[1..]] = JsonNode.Parse(property.Value.GetRawText());

        if (workflow.StartSchema is not null)
        {
            try
            {
                ContractValidator.Validate(workflow.StartMessageType, workflow.StartSchema,
                    JsonSerializer.SerializeToUtf8Bytes(request));
            }
            catch (InvalidMessageContractException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }

        var correlationId = await engine.StartAsync(bus, workflow, request);
        return Results.Accepted(value: new { correlationId });
    }
}
