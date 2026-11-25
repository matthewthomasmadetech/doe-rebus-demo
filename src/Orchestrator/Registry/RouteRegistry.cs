using Microsoft.Data.SqlClient;

namespace RebusDemo.Orchestrator.Registry;

/// <summary>A seed entry: a message type, its destination queue and an optional JSON Schema file.</summary>
public sealed record RouteRegistration(string MessageType, string Queue, string? SchemaFile = null);

/// <summary>A registered destination and the JSON Schema its messages must satisfy, if any.</summary>
internal sealed record RegisteredRoute(string Queue, string? ContractSchema);

/// <summary>Looks up where the orchestrator should deliver each registered message type.</summary>
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
                IF OBJECT_ID(N'dbo.MessageRoutes', N'U') IS NULL
                    CREATE TABLE dbo.MessageRoutes (
                        MessageType nvarchar(450) NOT NULL PRIMARY KEY,
                        DestinationQueue nvarchar(200) NOT NULL,
                        ContractSchema nvarchar(max) NULL,
                        RegisteredAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME()
                    );
                IF COL_LENGTH(N'dbo.MessageRoutes', N'ContractSchema') IS NULL
                    ALTER TABLE dbo.MessageRoutes ADD ContractSchema nvarchar(max) NULL;
            END TRY
            BEGIN CATCH
                -- Another orchestrator may have changed the table after our existence checks.
                IF ERROR_NUMBER() NOT IN (2714, 1911, 2705) OR OBJECT_ID(N'dbo.MessageRoutes', N'U') IS NULL
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
                IF NOT EXISTS (SELECT 1 FROM dbo.MessageRoutes WHERE MessageType = @type)
                    INSERT INTO dbo.MessageRoutes (MessageType, DestinationQueue, ContractSchema) VALUES (@type, @queue, @schema);
                ELSE
                    UPDATE dbo.MessageRoutes SET ContractSchema = @schema WHERE MessageType = @type AND ContractSchema IS NULL;
                """;
            seed.Parameters.AddWithValue("@type", route.MessageType);
            seed.Parameters.AddWithValue("@queue", route.Queue);
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

    /// <summary>Returns the registered route for a message type, or null when nothing is registered.</summary>
    public async Task<RegisteredRoute?> FindRouteAsync(string messageType)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DestinationQueue, ContractSchema FROM dbo.MessageRoutes WHERE MessageType = @type";
        command.Parameters.AddWithValue("@type", messageType);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new RegisteredRoute(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))
            : null;
    }
}
