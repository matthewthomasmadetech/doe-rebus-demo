using Microsoft.Data.SqlClient;

namespace RebusDemo.Orchestrator.Registry;

/// <summary>A message type and the service queue registered to receive it.</summary>
public sealed record RouteRegistration(string MessageType, string Queue);

/// <summary>Looks up where the orchestrator should deliver each registered message type.</summary>
internal sealed class RouteRegistry(string connectionString)
{
    /// <summary>Creates the registry table and adds any seed routes that are not registered yet.</summary>
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
                        RegisteredAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME()
                    );
            END TRY
            BEGIN CATCH
                -- Another orchestrator may have created the table after our existence check.
                IF ERROR_NUMBER() <> 2714 OR OBJECT_ID(N'dbo.MessageRoutes', N'U') IS NULL
                    THROW;
            END CATCH;
            """;
        await create.ExecuteNonQueryAsync();

        // Seeding never overwrites a row, so routes edited in the database survive a restart.
        foreach (var route in seedRoutes)
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                IF NOT EXISTS (SELECT 1 FROM dbo.MessageRoutes WHERE MessageType = @type)
                    INSERT INTO dbo.MessageRoutes (MessageType, DestinationQueue) VALUES (@type, @queue);
                """;
            seed.Parameters.AddWithValue("@type", route.MessageType);
            seed.Parameters.AddWithValue("@queue", route.Queue);
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

    /// <summary>Returns the registered queue for a message type, or null when nothing is registered.</summary>
    public async Task<string?> FindQueueAsync(string messageType)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DestinationQueue FROM dbo.MessageRoutes WHERE MessageType = @type";
        command.Parameters.AddWithValue("@type", messageType);
        return await command.ExecuteScalarAsync() as string;
    }
}
