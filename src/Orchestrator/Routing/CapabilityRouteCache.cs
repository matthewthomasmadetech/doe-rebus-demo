using System.Collections.Frozen;
using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using RebusDemo.Contracts;

namespace RebusDemo.Orchestrator.Routing;

/// <summary>Loads worker destinations from SQL and publishes a read-only snapshot for the forwarder.</summary>
internal sealed partial class CapabilityRouteCache(
    string connectionString, TimeSpan refreshInterval, ILogger<CapabilityRouteCache> logger) : BackgroundService
{
    private volatile FrozenDictionary<string, string> _routes = FrozenDictionary<string, string>.Empty;

    /// <summary>Creates and seeds the table once, then loads routes before Rebus starts consuming.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;

            -- Serialize table creation if two orchestrators start against an empty database.
            DECLARE @lockResult int;
            EXEC @lockResult = sys.sp_getapplock @Resource = N'RebusDemo.CapabilityContractRoute',
                @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000;
            IF @lockResult < 0 THROW 50001, 'Could not lock capability route initialization.', 1;

            IF OBJECT_ID(N'dbo.CapabilityContractRoute', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.CapabilityContractRoute (
                    Id int IDENTITY PRIMARY KEY,
                    ContractName nvarchar(200) NOT NULL,
                    Version nvarchar(50) NOT NULL,
                    QueueAddress nvarchar(500) NOT NULL,
                    Enabled bit NOT NULL
                );

                INSERT INTO dbo.CapabilityContractRoute (ContractName, Version, QueueAddress, Enabled)
                VALUES (N'CreateLearner', N'V1', @createLearnerV1, 1),
                       (N'CreateLearner', N'V2', @createLearnerV2, 1),
                       (N'CreateLearningPlan', N'V1', @createLearningPlanV1, 1),
                       (N'SendLearnerReminder', N'V1', @sendLearnerReminderV1, 1),
                       (N'ScheduleAssessment', N'V1', @scheduleAssessmentV1, 1),
                       (N'ScheduleAssessment', N'V2', @scheduleAssessmentV2, 1);
            END;

            COMMIT TRANSACTION;
            """;
        command.Parameters.Add("@createLearnerV1", SqlDbType.NVarChar, 500).Value = QueueNames.CreateLearnerV1;
        command.Parameters.Add("@createLearnerV2", SqlDbType.NVarChar, 500).Value = QueueNames.CreateLearnerV2;
        command.Parameters.Add("@createLearningPlanV1", SqlDbType.NVarChar, 500).Value = QueueNames.CreateLearningPlanV1;
        command.Parameters.Add("@sendLearnerReminderV1", SqlDbType.NVarChar, 500).Value = QueueNames.SendLearnerReminderV1;
        command.Parameters.Add("@scheduleAssessmentV1", SqlDbType.NVarChar, 500).Value = QueueNames.ScheduleAssessmentV1;
        command.Parameters.Add("@scheduleAssessmentV2", SqlDbType.NVarChar, 500).Value = QueueNames.ScheduleAssessmentV2;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    /// <summary>Looks up a single destination from the current snapshot without querying SQL.</summary>
    public string? FindDestination(string contractName, string version) =>
        _routes.GetValueOrDefault(RouteKey(contractName, version));

    /// <summary>Refreshes routes in the background and keeps the last valid snapshot when a refresh fails.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(refreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Capability route refresh failed. Keeping the last valid routing snapshot.");
            }
        }
    }

    /// <summary>Validates the full result before replacing the snapshot used by active deliveries.</summary>
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ContractName, Version, QueueAddress FROM dbo.CapabilityContractRoute WHERE Enabled = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var refreshed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var contractName = reader.GetString(0);
            var version = reader.GetString(1);
            var destination = reader.GetString(2);
            if (destination is QueueNames.Orchestrator or QueueNames.Errors || !QueueAddressPattern().IsMatch(destination))
                throw new InvalidOperationException($"Invalid capability destination '{destination}' for {contractName} {version}.");
            if (!refreshed.TryAdd(RouteKey(contractName, version), destination))
                throw new InvalidOperationException($"Multiple enabled routes for {contractName} {version}.");
        }

        var current = _routes;
        if (current.Count == refreshed.Count && refreshed.All(route => current.GetValueOrDefault(route.Key) == route.Value))
            return;

        _routes = refreshed.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        logger.LogInformation("Loaded {RouteCount} enabled capability routes from SQL", refreshed.Count);
    }

    private static string RouteKey(string contractName, string version) => $"{contractName}:{version}";

    // Azure Queue names use lowercase characters and may not have adjacent hyphens.
    [GeneratedRegex(@"\A[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){1,61}[a-z0-9]\z")]
    private static partial Regex QueueAddressPattern();
}
