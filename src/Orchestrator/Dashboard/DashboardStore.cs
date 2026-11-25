using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;

namespace RebusDemo.Orchestrator.Dashboard;

/// <summary>Reads workflow instances and archived errors for the dashboard.</summary>
public sealed class DashboardStore(string connectionString)
{
    // Instances that are neither completed nor failed are still waiting for a reply.
    private const string ActiveFilter = "State NOT IN (N'Completed', N'Failed')";

    internal async Task<DashboardTotals> GetTotalsAsync(CancellationToken cancellation)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(CASE WHEN {ActiveFilter} THEN 1 END), COUNT(CASE WHEN State = N'Completed' THEN 1 END),
                   COUNT(CASE WHEN State = N'Failed' THEN 1 END), (SELECT COUNT(*) FROM dbo.RebusDeadLetters)
            FROM dbo.WorkflowInstances;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        await reader.ReadAsync(cancellation);
        return new DashboardTotals(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    internal async Task<List<WorkflowRow>> GetWorkflowsAsync(string query, string status, int offset, int limit, CancellationToken cancellation)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT CorrelationId, WorkflowName, State, StartedAtUtc, CompletedAtUtc, FailedAtState, FailureReason
            FROM dbo.WorkflowInstances
            WHERE (@status = '' OR (@status = 'active' AND {ActiveFilter})
                   OR (@status = 'completed' AND State = N'Completed') OR (@status = 'failed' AND State = N'Failed'))
              AND (@query = '' OR CHARINDEX(@query, CONVERT(nvarchar(36), CorrelationId)) > 0
                   OR CHARINDEX(@query, WorkflowName) > 0 OR CHARINDEX(@query, Data) > 0)
            ORDER BY StartedAtUtc DESC, CorrelationId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 12).Value = status;
        AddPageParameters(command, query, offset, limit);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        var rows = new List<WorkflowRow>();
        while (await reader.ReadAsync(cancellation))
        {
            rows.Add(new WorkflowRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), Utc(reader, 3),
                reader.IsDBNull(4) ? null : Utc(reader, 4), Text(reader, 5), Text(reader, 6)));
        }
        return rows;
    }

    internal async Task<List<DeadLetterRow>> GetDeadLettersAsync(string query, int offset, int limit, CancellationToken cancellation)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONVERT(varchar(64), ArchiveKey, 2), MessageId, MessageType, SourceQueue, CorrelationId, ArchivedAtUtc
            FROM dbo.RebusDeadLetters
            WHERE @query = '' OR CHARINDEX(@query, MessageType) > 0 OR CHARINDEX(@query, SourceQueue) > 0
               OR CHARINDEX(@query, CorrelationId) > 0 OR CHARINDEX(@query, MessageId) > 0
            ORDER BY ArchivedAtUtc DESC, ArchiveKey
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;
        AddPageParameters(command, query, offset, limit);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        var rows = new List<DeadLetterRow>();
        while (await reader.ReadAsync(cancellation))
            rows.Add(new DeadLetterRow(reader.GetString(0), Text(reader, 1), Text(reader, 2), Text(reader, 3),
                Text(reader, 4), Utc(reader, 5)));
        return rows;
    }

    internal async Task<DeadLetterDetails?> GetDeadLetterAsync(byte[] key, CancellationToken cancellation)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONVERT(varchar(64), ArchiveKey, 2), MessageId, MessageType, SourceQueue, CorrelationId, ArchivedAtUtc,
                   SentAtUtc, ErrorDetails, HeadersJson, Body
            FROM dbo.RebusDeadLetters WHERE ArchiveKey = @key;
            """;
        command.Parameters.Add("@key", SqlDbType.Binary, 32).Value = key;
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        if (!await reader.ReadAsync(cancellation))
            return null;

        var row = new DeadLetterRow(reader.GetString(0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4), Utc(reader, 5));
        var bytes = reader.GetFieldValue<byte[]>(9);
        return new DeadLetterDetails(row, reader.IsDBNull(6) ? null : Utc(reader, 6), Text(reader, 7), reader.GetString(8), DisplayBody(bytes));
    }

    private static void AddPageParameters(SqlCommand command, string query, int offset, int limit)
    {
        command.Parameters.Add("@query", SqlDbType.NVarChar, 128).Value = query;
        command.Parameters.Add("@offset", SqlDbType.Int).Value = offset;
        command.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
    }

    private static string? Text(SqlDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

    private static DateTimeOffset Utc(SqlDataReader reader, int column) => new(reader.GetDateTime(column), TimeSpan.Zero);

    private static string DisplayBody(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return $"Base64\n{Convert.ToBase64String(bytes)}";
        }
    }
}

internal sealed record DashboardTotals(int Active, int Completed, int Failed, int DeadLetters);
internal sealed record WorkflowRow(Guid CorrelationId, string WorkflowName, string State, DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc, string? FailedAtState, string? FailureReason);
internal sealed record DeadLetterRow(string Key, string? MessageId, string? MessageType, string? SourceQueue,
    string? CorrelationId, DateTimeOffset ArchivedAtUtc);
internal sealed record DeadLetterDetails(DeadLetterRow Message, DateTimeOffset? SentAtUtc, string? ErrorDetails,
    string Headers, string Body);
