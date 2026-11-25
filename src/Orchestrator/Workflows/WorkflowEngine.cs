using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Rebus.Bus;
using Rebus.Messages;
using Rebus.Transport;

namespace RebusDemo.Orchestrator.Workflows;

/// <summary>
/// Moves workflow instances between the states the registry defines. A workflow's data is the combined
/// properties of its start request and every reply, and each command is built from that data.
/// </summary>
internal sealed class WorkflowEngine(WorkflowStore store, ILogger<WorkflowEngine> logger)
{
    /// <summary>Creates an instance from an HTTP start request and sends its first command.</summary>
    public async Task<Guid> StartAsync(IBus bus, WorkflowDefinition workflow, JsonObject request)
    {
        var correlationId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        request["CorrelationId"] = correlationId.ToString();
        request["StartedAtUtc"] = startedAt.ToString("O");

        await using var connection = await store.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var instance = new WorkflowInstance(correlationId, workflow.Name, WorkflowStore.NewState, new JsonObject());
        await store.InsertInstanceAsync(connection, transaction, instance);

        if (!await ApplyAsync(bus, connection, transaction, instance, workflow.StartMessageType, request))
            throw new InvalidOperationException($"Workflow {workflow.Name} has no start transition for {workflow.StartMessageType}.");

        // The command is queued before this commit, so a failed send leaves no instance behind.
        await transaction.CommitAsync();
        return correlationId;
    }

    /// <summary>Applies a reply from a service to its workflow, committing with the Rebus delivery.</summary>
    public async Task HandleReplyAsync(IBus bus, ITransactionContext context, string messageType, byte[] body)
    {
        if (JsonNode.Parse(body) is not JsonObject reply
            || !Guid.TryParse(reply["CorrelationId"]?.ToString(), out var correlationId))
            throw new InvalidOperationException($"{ShortName(messageType)} has no CorrelationId, so no workflow can receive it.");

        var connection = await store.OpenAsync();
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            var instance = await store.LoadInstanceForUpdateAsync(connection, transaction, correlationId);
            if (instance is null)
                logger.LogWarning("Dropped {MessageType}: no workflow instance\n    Correlation {CorrelationId}",
                    ShortName(messageType), correlationId);
            else
                await ApplyAsync(bus, connection, transaction, instance, messageType, reply);
        }
        catch
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }

        // Commit after Rebus has queued the outgoing command. If this commit fails, Rebus redelivers the reply and
        // the services handle the repeated command idempotently.
        context.OnCommit(async _ => await transaction.CommitAsync());
        context.OnDisposed(_ =>
        {
            transaction.Dispose();
            connection.Dispose();
        });
    }

    /// <summary>Applies the first matching transition and returns false when the message does not fit the current state.</summary>
    private async Task<bool> ApplyAsync(
        IBus bus, SqlConnection connection, SqlTransaction transaction, WorkflowInstance instance, string messageType,
        JsonObject message)
    {
        var candidates = await store.FindTransitionsAsync(
            connection, transaction, instance.WorkflowName, instance.State, messageType);
        var data = (JsonObject)instance.Data.DeepClone();
        foreach (var (name, value) in message)
            data[name] = value?.DeepClone();

        var transition = candidates.FirstOrDefault(candidate => candidate.WhenPresent is null || IsPresent(data[candidate.WhenPresent]));
        if (transition is null)
        {
            // A redelivered reply finds the workflow already advanced, so it is safe to ignore.
            logger.LogInformation("Ignored {MessageType} in {State} (workflow {Workflow})\n    Correlation {CorrelationId}",
                ShortName(messageType), instance.State, instance.WorkflowName, instance.CorrelationId);
            return false;
        }

        if (transition.SendMessageType is not null)
        {
            var command = new JsonObject();
            foreach (var (target, source) in transition.SendMapping ?? [])
                if (data[source] is { } value)
                    command[target] = value.DeepClone();

            // The correlation header lets the dashboard link archived errors to this workflow.
            await bus.Send(new RawJsonMessage(transition.SendMessageType, command.ToJsonString()),
                new Dictionary<string, string> { [Headers.CorrelationId] = instance.CorrelationId.ToString() });
        }

        var failed = transition.ToState == WorkflowStore.FailedState;
        var reason = failed ? data["Reason"]?.ToString() ?? "No reason was given." : null;
        foreach (var (name, value) in data)
            instance.Data[name] = value?.DeepClone();
        await store.SaveInstanceAsync(connection, transaction, instance, transition.ToState,
            failed ? instance.State : null, reason);

        if (failed)
            logger.LogWarning("{PreviousState} -> {State} on {MessageType}\n    Reason {FailureReason} | Correlation {CorrelationId}",
                instance.State, transition.ToState, ShortName(messageType), reason, instance.CorrelationId);
        else
            logger.LogInformation("{PreviousState} -> {State} on {MessageType}\n    {Action} | Correlation {CorrelationId}",
                instance.State, transition.ToState, ShortName(messageType),
                transition.SendMessageType is null ? "Workflow finished" : $"Queued {ShortName(transition.SendMessageType)}",
                instance.CorrelationId);
        return true;
    }

    private static bool IsPresent(JsonNode? value) =>
        value is not null && !(value is JsonValue text && text.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s));

    /// <summary>The type name without its namespace and assembly, such as LearnerCreated.</summary>
    public static string ShortName(string messageType)
    {
        var typeName = messageType.Split(',', 2)[0];
        return typeName[(typeName.LastIndexOf('.') + 1)..];
    }
}
