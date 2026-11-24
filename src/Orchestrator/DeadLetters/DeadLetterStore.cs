using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Rebus.Messages;

namespace RebusDemo.Orchestrator.DeadLetters;

/// <summary>Stores the original Rebus envelopes and their diagnostic metadata in SQL Server.</summary>
internal sealed class DeadLetterStore(string connectionString)
{
    /// <summary>Creates the archive table before the orchestrator starts consuming messages.</summary>
    public async Task EnsureTableExistsAsync()
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            BEGIN TRY
                IF OBJECT_ID(N'dbo.RebusDeadLetters', N'U') IS NULL
                    CREATE TABLE dbo.RebusDeadLetters (
                        ArchiveKey binary(32) NOT NULL PRIMARY KEY,
                        MessageId nvarchar(max) NULL,
                        MessageType nvarchar(max) NULL,
                        SourceQueue nvarchar(max) NULL,
                        CorrelationId nvarchar(max) NULL,
                        ErrorDetails nvarchar(max) NULL,
                        SentAtUtc datetime2(7) NULL,
                        ArchivedAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                        HeadersJson nvarchar(max) NOT NULL,
                        Body varbinary(max) NOT NULL
                    );
            END TRY
            BEGIN CATCH
                -- Another orchestrator may have created the table after our existence check.
                IF ERROR_NUMBER() <> 2714 OR OBJECT_ID(N'dbo.RebusDeadLetters', N'U') IS NULL
                    THROW;
            END CATCH;
            """;

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Inserts the envelope once and returns whether a new row was written.</summary>
    public async Task<bool> SaveAsync(TransportMessage message, CancellationToken cancellationToken)
    {
        var headers = message.Headers;
        var messageId = headers.GetValueOrDefault(Headers.MessageId);
        var sourceQueue = headers.GetValueOrDefault(Headers.SourceQueue);
        var sentTime = headers.GetValueOrDefault(Headers.SentTime);
        object sentAtUtc = DateTimeOffset.TryParse(sentTime, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var timestamp) ? timestamp.UtcDateTime : DBNull.Value;

        // Deduplicate redeliveries by source and Rebus message ID. A missing ID falls back to a hash of the envelope.
        var identity = string.IsNullOrWhiteSpace(messageId)
            ? JsonSerializer.SerializeToUtf8Bytes(new { Headers = headers.OrderBy(header => header.Key, StringComparer.Ordinal), message.Body })
            : JsonSerializer.SerializeToUtf8Bytes(new { sourceQueue, messageId });

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // Lock the key range while inserting so concurrent deliveries produce a single archive row.
        command.CommandText = """
            INSERT INTO dbo.RebusDeadLetters (
                ArchiveKey, MessageId, MessageType, SourceQueue, CorrelationId, ErrorDetails, SentAtUtc, HeadersJson, Body
            )
            SELECT @key, @messageId, @messageType, @sourceQueue, @correlationId, @errorDetails, @sentAtUtc, @headers, @body
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.RebusDeadLetters WITH (UPDLOCK, HOLDLOCK) WHERE ArchiveKey = @key
            );
            """;
        command.Parameters.Add("@key", SqlDbType.Binary, 32).Value = SHA256.HashData(identity);
        command.Parameters.Add("@messageId", SqlDbType.NVarChar, -1).Value = (object?)messageId ?? DBNull.Value;
        command.Parameters.Add("@messageType", SqlDbType.NVarChar, -1).Value = (object?)headers.GetValueOrDefault(Headers.Type) ?? DBNull.Value;
        command.Parameters.Add("@sourceQueue", SqlDbType.NVarChar, -1).Value = (object?)sourceQueue ?? DBNull.Value;
        command.Parameters.Add("@correlationId", SqlDbType.NVarChar, -1).Value = (object?)headers.GetValueOrDefault(Headers.CorrelationId) ?? DBNull.Value;
        command.Parameters.Add("@errorDetails", SqlDbType.NVarChar, -1).Value = (object?)headers.GetValueOrDefault(Headers.ErrorDetails) ?? DBNull.Value;
        command.Parameters.Add("@sentAtUtc", SqlDbType.DateTime2).Value = sentAtUtc;
        command.Parameters.Add("@headers", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(headers);
        command.Parameters.Add("@body", SqlDbType.VarBinary, -1).Value = message.Body;

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
