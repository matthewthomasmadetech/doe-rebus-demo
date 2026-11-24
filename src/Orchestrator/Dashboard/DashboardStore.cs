using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using RebusDemo.Contracts;

namespace RebusDemo.Orchestrator.Dashboard;

/// <summary>Reads the live saga and error tables and retains completed registration outcomes.</summary>
public sealed class DashboardStore(string connectionString)
{
    // A live row takes precedence if recording completion succeeded but Rebus still has to delete the saga.
    private const string SagaRows = """
        WITH SagaRows AS (
            SELECT saga.id AS SagaId, data.CorrelationId, data.Stage, data.RequestedAtUtc,
                   data.LearnerId, data.LearningPlanId, data.FailedAtStage, data.FailureReason,
                   CAST(NULL AS datetimeoffset) AS CompletedAtUtc, CAST(NULL AS nvarchar(100)) AS AssessmentId
            FROM dbo.Sagas AS saga
            CROSS APPLY OPENJSON(CONVERT(varchar(max), saga.data)) WITH (
                CorrelationId uniqueidentifier, Stage int, RequestedAtUtc datetimeoffset,
                LearnerId nvarchar(100), LearningPlanId nvarchar(100), FailedAtStage int, FailureReason nvarchar(max)
            ) AS data
            WHERE data.CorrelationId IS NOT NULL AND data.Stage IS NOT NULL AND data.RequestedAtUtc IS NOT NULL

            UNION ALL

            SELECT completed.SagaId, completed.CorrelationId, 4, completed.RequestedAtUtc,
                   completed.LearnerId, completed.LearningPlanId, NULL, NULL, completed.CompletedAtUtc, completed.AssessmentId
            FROM dbo.CompletedRegistrations AS completed
            WHERE NOT EXISTS (SELECT 1 FROM dbo.Sagas WHERE id = completed.SagaId)
        )
        """;

    public async Task EnsureTableExistsAsync()
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            BEGIN TRY
                IF OBJECT_ID(N'dbo.CompletedRegistrations', N'U') IS NULL
                    CREATE TABLE dbo.CompletedRegistrations (
                        SagaId uniqueidentifier NOT NULL PRIMARY KEY,
                        CorrelationId uniqueidentifier NOT NULL,
                        RequestedAtUtc datetimeoffset NOT NULL,
                        CompletedAtUtc datetimeoffset NOT NULL DEFAULT SYSUTCDATETIME(),
                        LearnerId nvarchar(100) NOT NULL,
                        LearningPlanId nvarchar(100) NOT NULL,
                        AssessmentId nvarchar(100) NOT NULL
                    );
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() <> 2714 OR OBJECT_ID(N'dbo.CompletedRegistrations', N'U') IS NULL
                    THROW;
            END CATCH;
            """;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Writes the final outcome once, including when Rebus redelivers the final reply.</summary>
    public async Task RecordCompletionAsync(LearnerRegistrationSagaData saga, AssessmentScheduled message)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.CompletedRegistrations (SagaId, CorrelationId, RequestedAtUtc, LearnerId, LearningPlanId, AssessmentId)
            SELECT @id, @correlationId, @requestedAtUtc, @learnerId, @planId, @assessmentId
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.CompletedRegistrations WITH (UPDLOCK, HOLDLOCK) WHERE SagaId = @id
            );
            """;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = saga.Id;
        command.Parameters.Add("@correlationId", SqlDbType.UniqueIdentifier).Value = saga.CorrelationId;
        command.Parameters.Add("@requestedAtUtc", SqlDbType.DateTimeOffset).Value = saga.RequestedAtUtc;
        command.Parameters.Add("@learnerId", SqlDbType.NVarChar, 100).Value = message.LearnerId;
        command.Parameters.Add("@planId", SqlDbType.NVarChar, 100).Value = message.LearningPlanId;
        command.Parameters.Add("@assessmentId", SqlDbType.NVarChar, 100).Value = message.AssessmentId;
        await command.ExecuteNonQueryAsync();
    }

    internal async Task<DashboardTotals> GetTotalsAsync(CancellationToken cancellation)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = SagaRows + "\n" + """
            SELECT COUNT(CASE WHEN Stage NOT IN (4, 5) THEN 1 END), COUNT(CASE WHEN Stage = 4 THEN 1 END),
                   COUNT(CASE WHEN Stage = 5 THEN 1 END), (SELECT COUNT(*) FROM dbo.RebusDeadLetters)
            FROM SagaRows;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        await reader.ReadAsync(cancellation);
        return new DashboardTotals(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    internal async Task<List<SagaRow>> GetSagasAsync(string query, string status, int offset, int limit, CancellationToken cancellation)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellation);
        await using var command = connection.CreateCommand();
        command.CommandText = SagaRows + "\n" + """
            SELECT CorrelationId, Stage, RequestedAtUtc, LearnerId, LearningPlanId,
                   FailedAtStage, FailureReason, CompletedAtUtc, AssessmentId
            FROM SagaRows
            WHERE (@status = '' OR (@status = 'active' AND Stage NOT IN (4, 5))
                   OR (@status = 'completed' AND Stage = 4) OR (@status = 'failed' AND Stage = 5))
              AND (@query = '' OR CHARINDEX(@query, CONVERT(nvarchar(36), CorrelationId)) > 0
                   OR CHARINDEX(@query, LearnerId) > 0 OR CHARINDEX(@query, LearningPlanId) > 0)
            ORDER BY RequestedAtUtc DESC, SagaId
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 12).Value = status;
        AddPageParameters(command, query, offset, limit);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        var rows = new List<SagaRow>();
        while (await reader.ReadAsync(cancellation))
        {
            rows.Add(new SagaRow(reader.GetGuid(0), (RegistrationStage)reader.GetInt32(1),
                reader.GetDateTimeOffset(2), Text(reader, 3), Text(reader, 4),
                reader.IsDBNull(5) ? null : (RegistrationStage)reader.GetInt32(5), Text(reader, 6),
                reader.IsDBNull(7) ? null : reader.GetDateTimeOffset(7), Text(reader, 8)));
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
internal sealed record SagaRow(Guid CorrelationId, RegistrationStage Stage, DateTimeOffset RequestedAtUtc,
    string? LearnerId, string? LearningPlanId, RegistrationStage? FailedAtStage, string? FailureReason,
    DateTimeOffset? CompletedAtUtc, string? AssessmentId);
internal sealed record DeadLetterRow(string Key, string? MessageId, string? MessageType, string? SourceQueue,
    string? CorrelationId, DateTimeOffset ArchivedAtUtc);
internal sealed record DeadLetterDetails(DeadLetterRow Message, DateTimeOffset? SentAtUtc, string? ErrorDetails,
    string Headers, string Body);
