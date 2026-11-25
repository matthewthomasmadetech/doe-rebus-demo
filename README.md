# Rebus learner registration demo

## Architecture

Four .NET 10 applications run independently on the host. [Docker Compose](docker-compose.yml) runs Azurite for Azure Storage Queues and SQL Server 2022 for the routing table, saga state, correlation index, and dashboard data.

All four apps send shared contracts to `orchestrator-queue` using `IBus.Send`. The orchestrator uses Rebus's `AddTransportMessageForwarder` to read the type header and resolve a worker command's destination from an in-memory snapshot of `dbo.CapabilityContractRoute`. Rebus forwards the original headers and payload to one queue. The orchestrator's saga initiation and status messages continue to their normal Rebus handlers. [Shared outgoing route](src/Messaging/RebusRoutes.cs) · [Transport forwarding](src/Orchestrator/Routing/CapabilityMessageForwarder.cs) · [SQL routing cache](src/Orchestrator/Routing/CapabilityRouteCache.cs)

The workflow starts at [`POST /learners/register`](src/Orchestrator/RegisterLearnerEndpoint.cs). The endpoint queues `RegisterLearnerV1` and returns `202 Accepted` with a correlation ID. The saga queues commands for Manage, Develop and Take in sequence and advances after each status update. The same correlation ID ties the messages to the saga in SQL Server, including after an orchestrator restart.

Manage sends reminder commands through the orchestrator to Develop. Develop also exposes a demo endpoint that sends V1 and V2 assessment commands through the orchestrator to Take. [Standalone reminder](#send-a-standalone-reminder) · [Assessment routing demo](#demonstrate-central-routing-with-take-v1-and-v2)

The orchestrator runs a second Rebus listener to archive messages from `rebus-errors` in SQL Server. [Inspect archived errors](#inspect-archived-errors)

The orchestrator also serves a read-only dashboard for registration status and archived messages. It reads the same SQL database as the saga and archive listener. [Open the dashboard](#open-the-dashboard)

Worker queues have their own handler registrations. Manage, Develop and Take use separate Rebus listeners to consume multiple queues in one process. [Rebus hosting multiple buses](https://github.com/rebus-org/Rebus.ServiceProvider#starting-one-or-more-rebus-instances-using-one-or-more-separate-container-instances)

## Projects and entry points

| Project | Startup | Input queues | Responsibility |
| --- | --- | --- | --- |
| Orchestrator | [Program.cs](src/Orchestrator/Program.cs) | `orchestrator-queue`, `rebus-errors` | Forwards worker commands and hosts the HTTP API, [registration saga](src/Orchestrator/LearnerRegistrationSaga.cs), dead-letter archive, and [dashboard](src/Orchestrator/Pages/Dashboard.cshtml). |
| Manage | [Program.cs](src/Manage/Program.cs) | `create-learner-v1`, `create-learner-v2` | Creates demo learner IDs for the saga and exposes [`POST /learners/remind`](src/Manage/SendLearnerReminderEndpoint.cs). |
| Develop | [Program.cs](src/Develop/Program.cs) | `create-learning-plan-v1`, `send-learner-reminder-v1` | Builds [learning plans](src/Develop/CreateLearningPlanHandler.cs), handles [reminders](src/Develop/SendLearnerReminderHandler.cs), and exposes [`POST /assessments/schedule/{version}`](src/Develop/ScheduleAssessmentEndpoint.cs). |
| Take | [Program.cs](src/Take/Program.cs) | `schedule-assessment-v1`, `schedule-assessment-v2`, `schedule-assessment-v2-alternate` | Processes [V1](src/Take/ScheduleAssessmentHandler.cs) and [V2](src/Take/ScheduleAssessmentV2Handler.cs) bookings. The alternate V2 listener demonstrates changing the destination in SQL. |
| Contracts | Shared library | — | Defines [versioned commands](src/Contracts/LearnerRegistrationV1.cs), [status updates](src/Contracts/LearnerRegistrationStatusUpdates.cs), and [queue names](src/Contracts/QueueNames.cs). |
| Messaging | Shared library | — | Configures [worker queues](src/Messaging/CapabilityQueueConfiguration.cs) and the shared outgoing [route to the orchestrator](src/Messaging/RebusRoutes.cs). |

## Extend the demo with Rebus

A **contract** is the C# type shared by the sender and receiver. A **command** asks a service to do work. A **status update** reports the outcome. Commands use a version suffix and status updates use names such as `LearnerCreated` or `LearnerCreationFailed`.

1. **Define the messages and queue.** Add `PrepareWelcomePackV1`, `WelcomePackPrepared`, and a `QueueNames.PrepareWelcomePackV1` constant with the value `prepare-welcome-pack-v1` to the shared Contracts project. Give both messages a `CorrelationId` if they belong to the saga. To demonstrate two versions side by side, give each version its own command type, queue and handler, such as `PrepareWelcomePackV2` on `prepare-welcome-pack-v2`. [Queue names](src/Contracts/QueueNames.cs)
2. **Add the receiver.** For a new service, add a .NET worker to `RebusDemo.slnx` and reference `Contracts`, `Messaging` and `Rebus.ServiceProvider`. Implement `IHandleMessages<PrepareWelcomePackV1>` and call `AddRebusHandler<PrepareWelcomePackHandler>()` and `AddRebus(configure => configure.UseCapabilityQueue(storage, QueueNames.PrepareWelcomePackV1))`. For an existing service, put those registrations and the handler's dependencies inside `AddRebusService`. Call `AddLogging()` inside that listener if the handler uses `ILogger<T>`. [Worker setup example](src/Develop/Program.cs)
3. **Route and send.** Add an enabled SQL row with `ContractName = PrepareWelcomePack`, `Version = V1` and `QueueAddress = prepare-welcome-pack-v1`. For fresh demo databases, add the same row to the seeds. `IBus.Send` already routes shared contracts to the orchestrator. For a saga reply, add `WelcomePackPrepared` to the forwarder's local message types, map the same correlation ID in `CorrelateMessages`, and handle the reply in the saga. [Routing seeds](src/Orchestrator/Routing/CapabilityRouteCache.cs) · [Forwarder](src/Orchestrator/Routing/CapabilityMessageForwarder.cs) · [Saga](src/Orchestrator/LearnerRegistrationSaga.cs)

## Run locally

Install the .NET 10 SDK and Docker with Compose v2. From the repository root, start the infrastructure and wait for the database to be created.

```sh
docker compose up -d --wait azurite sqlserver
docker compose run --rm sqlserver-init
dotnet build RebusDemo.slnx
```

The database initialization command is safe to rerun. Rebus creates the input queues, `rebus-errors`, and the `dbo.Sagas` and `dbo.SagaIndex` tables. The orchestrator creates `dbo.RebusDeadLetters` and `dbo.CompletedRegistrations` and creates and seeds `dbo.CapabilityContractRoute` on its first startup against a fresh database. Azurite and SQL Server keep their data in Docker volumes.

This proof of concept uses the message contracts and database schema from the current build. Use fresh local demo data when changing either of them.

Run each application in its own terminal from the repository root.

```sh
dotnet run --no-build --project src/Manage
dotnet run --no-build --project src/Develop
dotnet run --no-build --project src/Take
dotnet run --no-build --project src/Orchestrator -- --urls http://127.0.0.1:5080
```

Azurite listens on `127.0.0.1:10001` and SQL Server on `127.0.0.1:1433`. The defaults match the Compose file. Override the queue connection with `ConnectionStrings__AzureQueues` in all four applications. If you change `MSSQL_SA_PASSWORD` for Compose, set `ConnectionStrings__RebusSqlServer` in the orchestrator to the matching connection string. The checked-in credentials are for this local demo.

## Open the dashboard

Start the services as described above and open [http://127.0.0.1:5080/dashboard](http://127.0.0.1:5080/dashboard). The dashboard runs in the orchestrator on the same port as `POST /learners/register`.

- **Sagas** shows the current stage for registrations in `dbo.Sagas`, including failed registrations and their reasons. The final assessment handler saves completed outcomes in `dbo.CompletedRegistrations` before Rebus removes the live saga.
- **Archived errors** reads `dbo.RebusDeadLetters`. Search by correlation ID, message ID, message type, or source queue. Open **View details** to see the original headers, body, and exception. From a failed saga, **Find errors** opens the archive filtered to that registration.

Use [Register a learner](#register-a-learner) for a completed row and [Demonstrate a failed saga](#demonstrate-a-failed-saga) for a failed row and its archived error. Click **Refresh** after the queue processing finishes. To see a live waiting stage, use the [restart recovery example](#demonstrate-restart-recovery) and open the dashboard while Develop is stopped and the orchestrator is still running.

## Register a learner

```sh
curl -i http://127.0.0.1:5080/learners/register \
  -H 'Content-Type: application/json' \
  --data-binary @samples/register-learner.json
```

The API returns `202 Accepted` with a `correlationId` once `RegisterLearnerV1` is queued. Follow that ID in the `>>> [Service]` blocks in each terminal. Each block shows the command and reply or the saga transition, with details underneath. Routine Rebus and ASP.NET Core messages are hidden at the default log level. Warnings and errors remain visible.

Set `Logging__Console__FormatterName=json` when launching a service to see the structured JSON logs.

| From → To | Message | Saga stage after handling |
| --- | --- | --- |
| HTTP endpoint → Orchestrator | `RegisterLearnerV1` | `AwaitingLearner` |
| Orchestrator → Manage | `CreateLearnerV1` or `CreateLearnerV2` | |
| Manage → Orchestrator | `LearnerCreated` | `AwaitingLearningPlan` |
| Orchestrator → Develop | `CreateLearningPlanV1` | |
| Develop → Orchestrator | `LearningPlanCreated` | `AwaitingAssessment` |
| Orchestrator → Take | `ScheduleAssessmentV1` | |
| Take → Orchestrator | `AssessmentScheduled` | `Completed` |

The final log includes the learner, plan and assessment IDs and the assessment's UTC time. The dashboard shows the saved completed outcome after Rebus removes the live saga and its correlation index.

### Try both learner creation versions

The sample above uses `create-learner-v1`. Submit the sample below to supply a nonblank `preferredName`. The saga queues `CreateLearnerV2` on `orchestrator-queue`, and the forwarder sends it to `create-learner-v2`. Both handlers send `LearnerCreated` to `orchestrator-queue`.

```sh
curl -i http://127.0.0.1:5080/learners/register \
  -H 'Content-Type: application/json' \
  --data-binary @samples/register-learner-v2.json
```

To see the isolation, start Manage normally once to create both queues, then stop it and restart with only the V1 listener.

```sh
Demo__EnableCreateLearnerV2=false dotnet run --no-build --project src/Manage
```

Submit both samples again. The V1 registration completes, and the V2 registration stays at `AwaitingLearner`. Restart Manage without the environment variable, and it processes the queued V2 command. You can also leave the V1-only instance running and start a second Manage instance with both listeners on a different HTTP port.

```sh
dotnet run --no-build --project src/Manage -- --urls http://127.0.0.1:5083
```

With both instances running, either can handle V1. The instance with both listeners handles V2.

## Try message validation

The shared contracts use Data Annotations for rules such as a valid email, a 160-character learning goal limit, non-default IDs and timestamps, and a learning-plan reply with at least one module. Each application bus runs those rules after deserialization and before dispatching the message to its handler. [Registration commands](src/Contracts/LearnerRegistrationV1.cs) · [Status updates](src/Contracts/LearnerRegistrationStatusUpdates.cs) · [Rebus validation step](src/Messaging/RebusMessageValidation.cs)

`preferredFormat` is optional. Leave it out or set it to `null` for the default `online` plan. To request `workbook`, send the sample below and look for `workbook` in Develop's log.

```sh
curl -i http://127.0.0.1:5080/learners/register \
  -H 'Content-Type: application/json' \
  --data-binary @samples/register-learner-workbook.json
```

Change `preferredFormat` to `video` in a copy of the sample to see HTTP `400`. If an invalid message arrives directly on a queue, Rebus treats the validation failure as permanent and archives it through `rebus-errors`. A worker can also send a failure reply when the command includes a valid workflow correlation ID.

## Send a standalone reminder

With the infrastructure, Manage, Develop and the orchestrator running, send the reminder to Manage on port `5082`.

```sh
curl -i http://127.0.0.1:5082/learners/remind \
  -H 'Content-Type: application/json' \
  --data '{"learnerId":"LRN-0142"}'
```

Manage returns `202 Accepted` with a `reminderId`. Look for `>>> [Develop] Handled SendLearnerReminderV1` and the same ID in Develop's terminal. The handler simulates sending the reminder in its log.

Manage queues `SendLearnerReminderV1` on `orchestrator-queue`. The forwarder sends it to `send-learner-reminder-v1`, where Develop's regular Rebus handler completes the work. [Reminder contract](src/Contracts/SendLearnerReminderV1.cs) · [Develop handler](src/Develop/SendLearnerReminderHandler.cs)

## Demonstrate central routing with Take V1 and V2

With all services running, submit both standalone assessment requests to Develop on port `5084`.

```sh
curl -i http://127.0.0.1:5084/assessments/schedule/v1 \
  -H 'Content-Type: application/json' \
  --data-binary @samples/schedule-assessment-v1.json

curl -i http://127.0.0.1:5084/assessments/schedule/v2 \
  -H 'Content-Type: application/json' \
  --data-binary @samples/schedule-assessment-v2.json
```

Follow each response's `correlationId`. Develop logs the command going to `orchestrator-queue`. The orchestrator logs `Forwarding ScheduleAssessment V1 to schedule-assessment-v1` or `Forwarding ScheduleAssessment V2 to schedule-assessment-v2`. Take logs the actual receiving queue and the duration. V1 uses 30 minutes. V2 uses the sample's 45 minutes. The forwarder and Take show the same Rebus message ID for each delivery.

### Change the V2 destination while the apps are running

Run this from the repository root. Take already listens on the alternate V2 queue.

```sh
docker compose exec -T sqlserver bash -c '
  exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -d RebusDemo' <<'SQL'
UPDATE dbo.CapabilityContractRoute
SET QueueAddress = N'schedule-assessment-v2-alternate'
WHERE ContractName = N'ScheduleAssessment' AND Version = N'V2';
GO
SQL
```

Wait for `Loaded 6 enabled capability routes from SQL` in the orchestrator log, then send the V2 request again. The orchestrator forwards it to `schedule-assessment-v2-alternate`, and Take logs that queue for the new correlation ID. V1 still goes to `schedule-assessment-v1`. To restore the seeded route, rerun the SQL with `schedule-assessment-v2` as the `QueueAddress`.

The orchestrator checks SQL every five seconds by default. Set `Routing__RefreshIntervalSeconds` to change the interval. When routes change, a successful refresh replaces the whole in-memory snapshot. The new destination applies to commands forwarded after the refresh. A failed SQL read, duplicate enabled route, or invalid destination leaves the last valid snapshot in use. The orchestrator and error queues are forbidden as worker destinations. With no enabled route in the current snapshot, Rebus archives the command through `rebus-errors` with the routing error. A saga waiting for that command's reply remains at its current stage.

## Demonstrate restart recovery

1. Stop Develop with `Ctrl+C` and leave the other apps and both containers running.
2. Submit a registration. Keep the returned correlation ID and wait for the orchestrator to log `Forwarding CreateLearningPlan V1 to create-learning-plan-v1` for that ID. Develop's command is queued while the service is stopped.
3. Stop the orchestrator with `Ctrl+C`. Start Develop and wait for it to log that it sent `LearningPlanCreated` for that correlation ID.
4. Restart the orchestrator with its original command. Rebus consumes the queued reply, restores the saga from SQL Server and advances to `AwaitingAssessment`. The same correlation ID reaches `Completed` without another HTTP request.

To inspect durable state after the orchestrator has stopped in step 3, run this in another terminal. The first query shows the saga's `Stage` value of `2` (`AwaitingLearningPlan`). The second shows the indexed correlation ID. Both rows disappear when that saga completes.

```sh
docker compose exec -T sqlserver bash -c '
  exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d RebusDemo' <<'SQL'
SELECT id, revision, JSON_VALUE(CONVERT(varchar(max), data), '$.Stage') AS stage FROM dbo.Sagas;
SELECT * FROM dbo.SagaIndex;
GO
SQL
```

## Demonstrate a transient failure

Stop Develop and restart it with this option. Leave the other applications running and submit a fresh registration.

```sh
Demo__FailFirstLearningPlan=true dotnet run --no-build --project src/Develop
```

Develop logs a simulated exception for the first `CreateLearningPlanV1` it handles. Rebus retries the same message. The successful Develop log shows the same message ID and correlation ID, and the orchestrator reaches `Completed`. The switch injects one failure per Develop process.

## Demonstrate a failed saga

With all four services running, submit the failure sample from the repository root.

```sh
curl -i http://127.0.0.1:5080/learners/register \
  -H 'Content-Type: application/json' \
  --data-binary @samples/register-learner-failure.json
```

The sample sets `learningGoal` to `DEMO_FAIL_LEARNING_PLAN`. Develop throws on every delivery of that command. After five attempts, Rebus dispatches it to Develop's `IHandleMessages<IFailed<CreateLearningPlanV1>>` handler. The handler sends `LearningPlanCreationFailed` to the orchestrator and dead-letters the original command to `rebus-errors` with the exception details.

The API returns `202 Accepted` when it queues the registration. Use its `correlationId` to find the eventual outcome. The orchestrator logs `AwaitingLearningPlan -> Failed on LearningPlanCreationFailed` and saves `Failed`, the previous stage, and the reason in SQL Server. `Failed` is terminal in this demo. Submit a fresh registration after correcting the cause.

| Service | Failure reply after exhausted retries |
| --- | --- |
| Manage | `LearnerCreationFailed` |
| Develop | `LearningPlanCreationFailed` |
| Take | `AssessmentSchedulingFailed` |

Inspect failed registrations in SQL Server. `Stage = 5` means `Failed` and `FailedAtStage = 2` means it failed while awaiting the learning plan.

```sh
docker compose exec -T sqlserver bash -c '
  exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d RebusDemo' <<'SQL'
SELECT registration.*
FROM dbo.Sagas AS saga
CROSS APPLY OPENJSON(CONVERT(varchar(max), saga.data)) WITH (
    CorrelationId uniqueidentifier,
    Stage int,
    FailedAtStage int,
    FailureReason nvarchar(200)
) AS registration
WHERE registration.Stage = 5;
GO
SQL
```

Rebus stores handled message IDs and outgoing commands with the active SQL saga. If a queue delivery repeats, Rebus can replay the outgoing command. Service handlers derive the demo resource IDs from the correlation ID and Take derives its slot from the original request date. The saga checks its current stage before advancing on a reply with a new message ID. Every registration request starts a fresh workflow, and the demo service results are generated in the handlers without separate business databases.

## Inspect archived errors

Run the failed saga sample, then open **Archived errors** in the dashboard or look for `Archived` in the orchestrator log. Its second Rebus listener reads `rebus-errors` and saves the message ID, type, source queue, correlation ID, exception details, and timestamps in `dbo.RebusDeadLetters`. The full headers and raw message body are also saved, including bodies that couldn’t be deserialized. [Archive handler](src/Orchestrator/DeadLetters/DeadLetterMessageHandler.cs)

Rebus removes the original message from the queue after the SQL write succeeds. If a write fails, the listener only logs the failure and retries the original delivery after a delay. Redelivery of a message already archived from the same source queue keeps a single row.

```sh
docker compose exec -T sqlserver bash -c '
  exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d RebusDemo' <<'SQL'
SELECT TOP (10) MessageId, MessageType, SourceQueue, CorrelationId, ErrorDetails, SentAtUtc, ArchivedAtUtc
FROM dbo.RebusDeadLetters
ORDER BY ArchivedAtUtc DESC;
GO
SQL
```

Stop the applications with `Ctrl+C` and run `docker compose down` to stop the containers and retain their data volumes.
