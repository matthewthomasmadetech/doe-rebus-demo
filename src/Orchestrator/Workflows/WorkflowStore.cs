using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace RebusDemo.Orchestrator.Workflows;

/// <summary>A seed workflow definition read from configuration.</summary>
public sealed record WorkflowSeed(string Name, string StartMessageType, string? StartSchemaFile, TransitionSeed[] Transitions);

/// <summary>
/// One step: when <see cref="OnMessageType"/> arrives in <see cref="FromState"/>, move to <see cref="ToState"/>
/// and optionally send a command built from the workflow's data.
/// </summary>
/// <param name="Priority">Lower values are tried first when several transitions match.</param>
/// <param name="WhenPresent">Only matches when this data property has a non-blank value.</param>
/// <param name="SendMapping">Maps each property of the command to the workflow data property that supplies it.</param>
public sealed record TransitionSeed(
    string FromState, string OnMessageType, string ToState, int Priority = 0, string? WhenPresent = null,
    string? SendMessageType = null, Dictionary<string, string>? SendMapping = null);

internal sealed record WorkflowDefinition(string Name, string StartMessageType, string? StartSchema);

internal sealed record Transition(
    int TransitionId, string ToState, string? WhenPresent, string? SendMessageType, Dictionary<string, string>? SendMapping);

/// <summary>One receiver's answer to a fanned-out command.</summary>
internal sealed record ReceiverReply(string Queue, string MessageType, bool Failed, string? Reason, int TransitionId);

/// <summary>The receivers a command was sent to and the replies collected so far.</summary>
internal sealed class PendingJoin(string[] queues)
{
    public string[] Queues { get; set; } = queues;
    public List<ReceiverReply> Replies { get; set; } = [];
}

internal sealed class WorkflowInstance(Guid correlationId, string workflowName, string state, JsonObject data, PendingJoin? pending)
{
    public Guid CorrelationId { get; } = correlationId;
    public string WorkflowName { get; } = workflowName;
    public string State { get; } = state;

    /// <summary>Every property from the start request and each reply received so far.</summary>
    public JsonObject Data { get; } = data;

    /// <summary>The receivers the workflow is waiting on, or null when it is not waiting for replies.</summary>
    public PendingJoin? Pending { get; set; } = pending;
}

/// <summary>Reads and writes workflow definitions and instance state in SQL Server.</summary>
internal sealed class WorkflowStore(string connectionString)
{
    public const string NewState = "New";
    public const string CompletedState = "Completed";
    public const string FailedState = "Failed";

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellation = default)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellation);
        return connection;
    }

    /// <summary>Creates the workflow tables and registers any seed workflow that does not exist yet.</summary>
    public async Task EnsureTablesAsync(IEnumerable<WorkflowSeed> seeds)
    {
        await using var connection = await OpenAsync();
        await using var create = connection.CreateCommand();
        create.CommandText = """
            BEGIN TRY
                IF OBJECT_ID(N'dbo.Workflows', N'U') IS NULL
                    CREATE TABLE dbo.Workflows (
                        WorkflowName nvarchar(200) NOT NULL PRIMARY KEY,
                        StartMessageType nvarchar(450) NOT NULL,
                        StartSchema nvarchar(max) NULL
                    );
                IF OBJECT_ID(N'dbo.WorkflowTransitions', N'U') IS NULL
                    CREATE TABLE dbo.WorkflowTransitions (
                        TransitionId int IDENTITY NOT NULL PRIMARY KEY,
                        WorkflowName nvarchar(200) NOT NULL,
                        FromState nvarchar(100) NOT NULL,
                        OnMessageType nvarchar(450) NOT NULL,
                        Priority int NOT NULL,
                        WhenPresent nvarchar(100) NULL,
                        ToState nvarchar(100) NOT NULL,
                        SendMessageType nvarchar(450) NULL,
                        SendMapping nvarchar(max) NULL,
                        INDEX IX_WorkflowTransitions_Lookup (WorkflowName, FromState, OnMessageType),
                        INDEX IX_WorkflowTransitions_MessageType (OnMessageType)
                    );
                IF OBJECT_ID(N'dbo.WorkflowInstances', N'U') IS NULL
                    CREATE TABLE dbo.WorkflowInstances (
                        CorrelationId uniqueidentifier NOT NULL PRIMARY KEY,
                        WorkflowName nvarchar(200) NOT NULL,
                        State nvarchar(100) NOT NULL,
                        Data nvarchar(max) NOT NULL,
                        FailedAtState nvarchar(100) NULL,
                        FailureReason nvarchar(max) NULL,
                        Pending nvarchar(max) NULL,
                        StartedAtUtc datetime2(7) NOT NULL,
                        UpdatedAtUtc datetime2(7) NOT NULL,
                        CompletedAtUtc datetime2(7) NULL,
                        INDEX IX_WorkflowInstances_Started (StartedAtUtc)
                    );
                IF COL_LENGTH(N'dbo.WorkflowInstances', N'Pending') IS NULL
                    ALTER TABLE dbo.WorkflowInstances ADD Pending nvarchar(max) NULL;
            END TRY
            BEGIN CATCH
                -- Another orchestrator may have changed a table after our existence checks.
                IF ERROR_NUMBER() NOT IN (2705, 2714)
                    THROW;
            END CATCH;
            """;
        await create.ExecuteNonQueryAsync();

        foreach (var seed in seeds)
            await SeedWorkflowAsync(connection, seed);
    }

    // A workflow that already exists is left alone, so definitions edited in the database survive a restart.
    private static async Task SeedWorkflowAsync(SqlConnection connection, WorkflowSeed seed)
    {
        var schema = seed.StartSchemaFile is null
            ? null
            : await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, seed.StartSchemaFile));

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO dbo.Workflows (WorkflowName, StartMessageType, StartSchema)
            SELECT @name, @startType, @schema
            WHERE NOT EXISTS (SELECT 1 FROM dbo.Workflows WITH (UPDLOCK, HOLDLOCK) WHERE WorkflowName = @name);
            """;
        insert.Parameters.AddWithValue("@name", seed.Name);
        insert.Parameters.AddWithValue("@startType", seed.StartMessageType);
        insert.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
        if (await insert.ExecuteNonQueryAsync() == 0)
        {
            await transaction.RollbackAsync();
            return;
        }

        foreach (var transition in seed.Transitions)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO dbo.WorkflowTransitions
                    (WorkflowName, FromState, OnMessageType, Priority, WhenPresent, ToState, SendMessageType, SendMapping)
                VALUES (@name, @from, @on, @priority, @when, @to, @send, @mapping);
                """;
            command.Parameters.AddWithValue("@name", seed.Name);
            command.Parameters.AddWithValue("@from", transition.FromState);
            command.Parameters.AddWithValue("@on", transition.OnMessageType);
            command.Parameters.AddWithValue("@priority", transition.Priority);
            command.Parameters.AddWithValue("@when", (object?)transition.WhenPresent ?? DBNull.Value);
            command.Parameters.AddWithValue("@to", transition.ToState);
            command.Parameters.AddWithValue("@send", (object?)transition.SendMessageType ?? DBNull.Value);
            command.Parameters.AddWithValue("@mapping", transition.SendMapping is null
                ? DBNull.Value
                : JsonSerializer.Serialize(transition.SendMapping));
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task<WorkflowDefinition?> FindWorkflowAsync(string name)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT StartMessageType, StartSchema FROM dbo.Workflows WHERE WorkflowName = @name";
        command.Parameters.AddWithValue("@name", name);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new WorkflowDefinition(name, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))
            : null;
    }

    /// <summary>True when this workflow waits for a reply of this type, so the orchestrator handles it itself.</summary>
    public async Task<bool> IsReplyTriggerAsync(string workflowType, string messageType)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP 1 1 FROM dbo.WorkflowTransitions
            WHERE WorkflowName = @workflow AND OnMessageType = @type AND FromState <> @new
            """;
        command.Parameters.AddWithValue("@workflow", workflowType);
        command.Parameters.AddWithValue("@type", messageType);
        command.Parameters.AddWithValue("@new", NewState);
        return await command.ExecuteScalarAsync() is not null;
    }

    public async Task InsertInstanceAsync(SqlConnection connection, SqlTransaction transaction, WorkflowInstance instance)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dbo.WorkflowInstances (CorrelationId, WorkflowName, State, Data, StartedAtUtc, UpdatedAtUtc)
            VALUES (@id, @workflow, @state, @data, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = instance.CorrelationId;
        command.Parameters.AddWithValue("@workflow", instance.WorkflowName);
        command.Parameters.AddWithValue("@state", instance.State);
        command.Parameters.AddWithValue("@data", instance.Data.ToJsonString());
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Loads an instance and locks its row so concurrent replies for one workflow run one at a time.</summary>
    public async Task<WorkflowInstance?> LoadInstanceForUpdateAsync(
        SqlConnection connection, SqlTransaction transaction, Guid correlationId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT WorkflowName, State, Data, Pending FROM dbo.WorkflowInstances WITH (UPDLOCK, ROWLOCK)
            WHERE CorrelationId = @id
            """;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = correlationId;
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new WorkflowInstance(correlationId, reader.GetString(0), reader.GetString(1),
                (JsonObject)JsonNode.Parse(reader.GetString(2))!,
                reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<PendingJoin>(reader.GetString(3)))
            : null;
    }

    /// <summary>Returns the transitions for a state and message, lowest priority value first.</summary>
    public async Task<List<Transition>> FindTransitionsAsync(
        SqlConnection connection, SqlTransaction transaction, string workflowName, string state, string messageType)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {TransitionColumns} FROM dbo.WorkflowTransitions
            WHERE WorkflowName = @workflow AND FromState = @state AND OnMessageType = @type
            ORDER BY Priority, TransitionId
            """;
        command.Parameters.AddWithValue("@workflow", workflowName);
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@type", messageType);
        await using var reader = await command.ExecuteReaderAsync();
        var transitions = new List<Transition>();
        while (await reader.ReadAsync())
            transitions.Add(ReadTransition(reader));
        return transitions;
    }

    public async Task<Transition> GetTransitionAsync(SqlConnection connection, SqlTransaction transaction, int transitionId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {TransitionColumns} FROM dbo.WorkflowTransitions WHERE TransitionId = @id";
        command.Parameters.AddWithValue("@id", transitionId);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return ReadTransition(reader);
    }

    private const string TransitionColumns = "TransitionId, ToState, WhenPresent, SendMessageType, SendMapping";

    private static Transition ReadTransition(SqlDataReader reader) =>
        new(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4)));

    /// <summary>Saves the instance's state, data and pending receivers. Pass the current state to record progress only.</summary>
    public async Task SaveInstanceAsync(
        SqlConnection connection, SqlTransaction transaction, WorkflowInstance instance, string newState,
        string? failedAtState, string? failureReason, PendingJoin? pending)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE dbo.WorkflowInstances
            SET State = @state, Data = @data, FailedAtState = @failedAt, FailureReason = @reason, Pending = @pending,
                UpdatedAtUtc = SYSUTCDATETIME(),
                CompletedAtUtc = CASE WHEN @state = @completed THEN SYSUTCDATETIME() ELSE CompletedAtUtc END
            WHERE CorrelationId = @id
            """;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = instance.CorrelationId;
        command.Parameters.AddWithValue("@state", newState);
        command.Parameters.AddWithValue("@data", instance.Data.ToJsonString());
        command.Parameters.AddWithValue("@failedAt", (object?)failedAtState ?? DBNull.Value);
        command.Parameters.AddWithValue("@reason", (object?)failureReason ?? DBNull.Value);
        command.Parameters.AddWithValue("@pending", pending is null ? DBNull.Value : JsonSerializer.Serialize(pending));
        command.Parameters.AddWithValue("@completed", CompletedState);
        await command.ExecuteNonQueryAsync();
    }
}
