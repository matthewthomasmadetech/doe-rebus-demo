using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace RebusDemo.Orchestrator.Registry;

/// <summary>
/// A seed route: when a message of <see cref="MessageType"/> belongs to <see cref="WorkflowType"/>, deliver it to every
/// queue in <see cref="Queues"/>, after checking it against the optional JSON Schema file.
/// </summary>
public sealed record RouteRegistration(string WorkflowType, string MessageType, string[] Queues, string? SchemaFile = null);

/// <summary>The queues registered for a workflow type and message type, and the JSON Schema their messages must satisfy.</summary>
internal sealed record RegisteredRoute(string[] Queues, string? ContractSchema);

/// <summary>Looks up where the orchestrator should deliver each message, keyed by workflow type and message type.</summary>
internal sealed class RouteRegistry(string connectionString)
{
    /// <summary>Creates the registry table and fills in any seed routes or schemas that are missing.</summary>
    public async Task EnsureTableExistsAsync(IEnumerable<RouteRegistration> seedRoutes)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var create = connection.CreateCommand();
        create.CommandText = """
            BEGIN TRY
                -- Routes used to be keyed by message type alone. The seed repopulates the rebuilt table.
                IF OBJECT_ID(N'dbo.MessageRoutes', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.MessageRoutes', N'WorkflowType') IS NULL
                    DROP TABLE dbo.MessageRoutes;
                IF OBJECT_ID(N'dbo.MessageRoutes', N'U') IS NULL
                    CREATE TABLE dbo.MessageRoutes (
                        WorkflowType nvarchar(100) NOT NULL,
                        MessageType nvarchar(300) NOT NULL,
                        DestinationQueues nvarchar(max) NOT NULL,
                        ContractSchema nvarchar(max) NULL,
                        RegisteredAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                        PRIMARY KEY (WorkflowType, MessageType)
                    );
            END TRY
            BEGIN CATCH
                -- Another orchestrator may have changed the table after our existence checks.
                IF ERROR_NUMBER() NOT IN (2714, 3701) OR OBJECT_ID(N'dbo.MessageRoutes', N'U') IS NULL
                    THROW;
            END CATCH;
            """;
        await create.ExecuteNonQueryAsync();

        // Seeding never overwrites a value, so routes and schemas edited in the database survive a restart.
        foreach (var route in seedRoutes)
        {
            var schema = route.SchemaFile is null
                ? null
                : await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, route.SchemaFile));

            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                IF NOT EXISTS (SELECT 1 FROM dbo.MessageRoutes WHERE WorkflowType = @workflow AND MessageType = @type)
                    INSERT INTO dbo.MessageRoutes (WorkflowType, MessageType, DestinationQueues, ContractSchema)
                    VALUES (@workflow, @type, @queues, @schema);
                ELSE
                    UPDATE dbo.MessageRoutes SET ContractSchema = @schema
                    WHERE WorkflowType = @workflow AND MessageType = @type AND ContractSchema IS NULL;
                """;
            seed.Parameters.AddWithValue("@workflow", route.WorkflowType);
            seed.Parameters.AddWithValue("@type", route.MessageType);
            seed.Parameters.AddWithValue("@queues", JsonSerializer.Serialize(route.Queues));
            seed.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
            try
            {
                await seed.ExecuteNonQueryAsync();
            }
            catch (SqlException exception) when (exception.Number is 2627 or 2601)
            {
                // Another orchestrator seeded the same route first.
            }
        }
    }

    /// <summary>Returns the registered route for a workflow type and message type, or null when nothing is registered.</summary>
    public async Task<RegisteredRoute?> FindRouteAsync(string workflowType, string messageType)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DestinationQueues, ContractSchema FROM dbo.MessageRoutes
            WHERE WorkflowType = @workflow AND MessageType = @type
            """;
        command.Parameters.AddWithValue("@workflow", workflowType);
        command.Parameters.AddWithValue("@type", messageType);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new RegisteredRoute(JsonSerializer.Deserialize<string[]>(reader.GetString(0)) ?? [],
                reader.IsDBNull(1) ? null : reader.GetString(1))
            : null;
    }
}
