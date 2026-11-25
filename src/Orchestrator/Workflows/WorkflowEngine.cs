using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Rebus.Bus;
using Rebus.Messages;
using Rebus.Transport;
using RebusDemo.Orchestrator.Registry;

namespace RebusDemo.Orchestrator.Workflows;

/// <summary>
/// Moves workflow instances between the states the registry defines. A workflow's data is the combined
/// properties of its start request and every reply, and each command is built from that data.
/// A command can go to several queues. The workflow then waits for a reply from every receiver before it moves on.
/// </summary>
internal sealed class WorkflowEngine(WorkflowStore store, RouteRegistry registry, ILogger<WorkflowEngine> logger)
{
    /// <summary>Creates an instance from an HTTP start request and sends its first command.</summary>
    public async Task<Guid> StartAsync(IBus bus, WorkflowDefinition workflow, JsonObject request)
    {
        var correlationId = Guid.NewGuid();
        request["CorrelationId"] = correlationId.ToString();
        request["StartedAtUtc"] = DateTimeOffset.UtcNow.ToString("O");

        await using var connection = await store.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var instance = new WorkflowInstance(correlationId, workflow.Name, WorkflowStore.NewState, new JsonObject(), null);
        await store.InsertInstanceAsync(connection, transaction, instance);

        var data = Merge(instance.Data, request);
        var transition = FirstMatch(
            await store.FindTransitionsAsync(connection, transaction, workflow.Name, WorkflowStore.NewState, workflow.StartMessageType), data)
            ?? throw new InvalidOperationException($"Workflow {workflow.Name} has no start transition for {workflow.StartMessageType}.");
        await ExecuteAsync(bus, connection, transaction, instance, data, transition, workflow.StartMessageType, null);

        // The commands are queued before this commit, so a failed send leaves no instance behind.
        await transaction.CommitAsync();
        return correlationId;
    }

    /// <summary>Applies a reply from a service to its workflow, committing with the Rebus delivery.</summary>
    public async Task HandleReplyAsync(
        IBus bus, ITransactionContext context, string messageType, string workflowType, string? replyQueue, byte[] body)
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
            else if (instance.WorkflowName != workflowType)
                throw new InvalidOperationException(
                    $"{ShortName(messageType)} says it belongs to workflow {workflowType}, but {correlationId} is a {instance.WorkflowName} workflow.");
            else
                await ApplyReplyAsync(bus, connection, transaction, instance, messageType, replyQueue, reply);
        }
        catch
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
            throw;
        }

        // Commit after Rebus has queued the outgoing commands. If this commit fails, Rebus redelivers the reply and
        // the services handle the repeated command idempotently.
        context.OnCommit(async _ => await transaction.CommitAsync());
        context.OnDisposed(_ =>
        {
            transaction.Dispose();
            connection.Dispose();
        });
    }

    private async Task ApplyReplyAsync(
        IBus bus, SqlConnection connection, SqlTransaction transaction, WorkflowInstance instance, string messageType,
        string? replyQueue, JsonObject reply)
    {
        var data = Merge(instance.Data, reply);
        var transition = FirstMatch(
            await store.FindTransitionsAsync(connection, transaction, instance.WorkflowName, instance.State, messageType), data);
        if (transition is null)
        {
            // A redelivered reply finds the workflow already advanced, so it is safe to ignore.
            logger.LogInformation("Ignored {MessageType} in {State} (workflow {Workflow})\n    Correlation {CorrelationId}",
                ShortName(messageType), instance.State, instance.WorkflowName, instance.CorrelationId);
            return;
        }

        var failed = transition.ToState == WorkflowStore.FailedState;
        var reason = failed ? reply["Reason"]?.ToString() ?? "No reason was given." : null;

        var pending = instance.Pending;
        if (pending is null)
        {
            await ExecuteAsync(bus, connection, transaction, instance, data, transition, messageType, reason);
            return;
        }

        // With one receiver any reply queue is accepted. With several, each registered receiver must answer once.
        // Rebus sets the return address to the input queue of the service that sent the reply.
        var fanOut = pending.Queues.Length > 1;
        var receiver = fanOut
            ? pending.Queues.FirstOrDefault(queue => string.Equals(queue, replyQueue, StringComparison.OrdinalIgnoreCase))
            : pending.Queues[0];
        if (receiver is null || pending.Replies.Any(existing => existing.Queue == receiver))
        {
            logger.LogInformation("Ignored {MessageType} from {Queue}: {Why}\n    Correlation {CorrelationId}",
                ShortName(messageType), replyQueue, receiver is null ? "not a receiver of this command" : "already replied",
                instance.CorrelationId);
            return;
        }

        pending.Replies.Add(new ReceiverReply(receiver, messageType, failed, reason, transition.TransitionId));
        if (pending.Replies.Count < pending.Queues.Length)
        {
            CopyInto(instance.Data, data);
            await store.SaveInstanceAsync(connection, transaction, instance, instance.State, null, null, pending);
            logger.LogInformation("{State}: {MessageType} from {Queue}\n    Waiting for {Remaining} of {Total} receivers | Correlation {CorrelationId}",
                instance.State, ShortName(messageType), receiver, pending.Queues.Length - pending.Replies.Count,
                pending.Queues.Length, instance.CorrelationId);
            return;
        }

        // Every receiver has answered. Any failure fails the workflow, naming who succeeded and who failed.
        var failures = pending.Replies.Where(answer => answer.Failed).ToList();
        if (failures.Count > 0)
        {
            var failure = await store.GetTransitionAsync(connection, transaction, failures[0].TransitionId);
            await ExecuteAsync(bus, connection, transaction, instance, data, failure, messageType, Summarise(pending, failures));
            return;
        }

        // Receivers can answer with different success types. The lowest transition ID decides, whatever the arrival order.
        var next = await store.GetTransitionAsync(connection, transaction, pending.Replies.Min(answer => answer.TransitionId));
        await ExecuteAsync(bus, connection, transaction, instance, data, next, messageType, null);
    }

    /// <summary>Moves the instance to the transition's state, sending its command to every registered receiver.</summary>
    private async Task ExecuteAsync(
        IBus bus, SqlConnection connection, SqlTransaction transaction, WorkflowInstance instance, JsonObject data,
        Transition transition, string triggerType, string? failureReason)
    {
        PendingJoin? next = null;
        if (transition.SendMessageType is not null)
        {
            var route = await registry.FindRouteAsync(instance.WorkflowName, transition.SendMessageType);
            if (route is null || route.Queues.Length == 0)
                throw new InvalidOperationException(
                    $"No route is registered for {transition.SendMessageType} in workflow {instance.WorkflowName}.");

            var command = new JsonObject();
            foreach (var (target, source) in transition.SendMapping ?? [])
                if (data[source] is { } value)
                    command[target] = value.DeepClone();
            var json = command.ToJsonString();

            // Commands the orchestrator builds are held to the same contract as messages it forwards.
            if (route.ContractSchema is not null)
                ContractValidator.Validate(transition.SendMessageType, route.ContractSchema, Encoding.UTF8.GetBytes(json));

            foreach (var queue in route.Queues)
            {
                // The correlation header links archived errors to this workflow, and the workflow type
                // travels on to the reply so the orchestrator can check which workflow it belongs to.
                await bus.Advanced.Routing.Send(queue, new RawJsonMessage(transition.SendMessageType, json),
                    new Dictionary<string, string>
                    {
                        [Headers.CorrelationId] = instance.CorrelationId.ToString(),
                        [WorkflowHeaders.WorkflowType] = instance.WorkflowName
                    });
            }

            next = new PendingJoin(route.Queues);
        }

        var failed = transition.ToState == WorkflowStore.FailedState;
        CopyInto(instance.Data, data);
        await store.SaveInstanceAsync(connection, transaction, instance, transition.ToState,
            failed ? instance.State : null, failed ? failureReason ?? "No reason was given." : null, next);

        if (failed)
            logger.LogWarning("{PreviousState} -> {State} on {MessageType}\n    Reason {FailureReason} | Correlation {CorrelationId}",
                instance.State, transition.ToState, ShortName(triggerType), failureReason, instance.CorrelationId);
        else
            logger.LogInformation("{PreviousState} -> {State} on {MessageType}\n    {Action} | Correlation {CorrelationId}",
                instance.State, transition.ToState, ShortName(triggerType), Describe(transition, next), instance.CorrelationId);
    }

    private static string Describe(Transition transition, PendingJoin? sentTo) =>
        transition.SendMessageType is null ? "Workflow finished"
        : sentTo!.Queues.Length == 1 ? $"Queued {ShortName(transition.SendMessageType)}"
        : $"Queued {ShortName(transition.SendMessageType)} to {sentTo.Queues.Length} receivers ({string.Join(", ", sentTo.Queues)})";

    private static string Summarise(PendingJoin pending, List<ReceiverReply> failures)
    {
        if (pending.Replies.Count == 1)
            return failures[0].Reason ?? "No reason was given.";

        var succeeded = pending.Replies.Where(answer => !answer.Failed).Select(answer => answer.Queue).ToList();
        return $"{failures.Count} of {pending.Replies.Count} receivers failed: "
            + string.Join("; ", failures.Select(failure => $"{failure.Queue} ({failure.Reason})"))
            + $". Succeeded: {(succeeded.Count == 0 ? "none" : string.Join(", ", succeeded))}.";
    }

    private static Transition? FirstMatch(List<Transition> candidates, JsonObject data) =>
        candidates.FirstOrDefault(candidate => candidate.WhenPresent is null || IsPresent(data[candidate.WhenPresent]));

    private static JsonObject Merge(JsonObject current, JsonObject message)
    {
        var data = (JsonObject)current.DeepClone();
        foreach (var (name, value) in message)
            data[name] = value?.DeepClone();
        return data;
    }

    private static void CopyInto(JsonObject target, JsonObject source)
    {
        foreach (var (name, value) in source)
            target[name] = value?.DeepClone();
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
