# Rebus learner registration demo

## Architecture

Four .NET 10 applications run independently on the host. [Docker Compose](docker-compose.yml) runs Azurite for Azure Storage Queues and SQL Server 2022 for the orchestrator's saga state and correlation index.

The workflow starts at [`POST /learners/register`](src/Orchestrator/RegisterLearnerEndpoint.cs). The endpoint queues a registration command and returns `202 Accepted` with a correlation ID. The Rebus saga sends commands to Manage, Develop and Take in sequence, advancing after each service sends a reply to the orchestrator queue. Every registration command and reply carries the same correlation ID so Rebus can load the correct saga from SQL Server, even after an orchestrator restart.

Manage also sends a standalone reminder command directly to Develop's queue. Develop handles it using a regular Rebus message handler. [Send a standalone reminder](#send-a-standalone-reminder)

The orchestrator runs a second Rebus listener to archive messages from `rebus-errors` in SQL Server. [Inspect archived errors](#inspect-archived-errors)

The orchestrator also serves a read-only dashboard for registration status and archived messages. It reads the same SQL database as the saga and archive listener. [Open the dashboard](#open-the-dashboard)

All four services load the same message-to-queue map at startup. Each service uses the map when sending and selects its own input queue and handlers. [Shared Rebus routes](src/Messaging/RebusRoutes.cs)

## Projects and entry points

| Project | Startup | Input queue | Responsibility |
| --- | --- | --- | --- |
| Orchestrator | [Program.cs](src/Orchestrator/Program.cs) | `orchestrator`, `rebus-errors` | Hosts the HTTP API, [registration saga](src/Orchestrator/LearnerRegistrationSaga.cs), dead-letter archive, and [dashboard](src/Orchestrator/Pages/Dashboard.cshtml). |
| Manage | [Program.cs](src/Manage/Program.cs) | `manage` | Creates demo learner IDs for the saga and exposes [`POST /learners/remind`](src/Manage/SendLearnerReminderEndpoint.cs). |
| Develop | [Program.cs](src/Develop/Program.cs) | `develop` | Builds [learning plans](src/Develop/CreateLearningPlanHandler.cs) and handles [standalone reminders](src/Develop/SendLearnerReminderHandler.cs). |
| Take | [Program.cs](src/Take/Program.cs) | `take` | Handles [`ScheduleAssessmentV1`](src/Take/ScheduleAssessmentHandler.cs) and returns an online assessment slot. |
| Contracts | Shared library | — | Defines the versioned [commands and replies](src/Contracts/LearnerRegistrationV1.cs) and [queue names](src/Contracts/QueueNames.cs). |
| Messaging | Shared library | — | Registers [outgoing Rebus routes](src/Messaging/RebusRoutes.cs) for all four services. |

## Extend the demo with Rebus

A **contract** is the C# type shared by the sender and receiver. A **command** asks a service to do work, and a **reply** reports the result. A Welcome service that prepares a learner's welcome pack would use two message types and its own queue.

1. **Define the messages.** Add records such as `PrepareWelcomePackV1` and `WelcomePackPreparedV1` to the shared Contracts project. Give both a `CorrelationId` if they belong to the saga. Use a new type such as `PrepareWelcomePackV2` for a breaking change and keep the `V1` handler available while old messages are queued. [Existing contracts](src/Contracts/LearnerRegistrationV1.cs)
2. **Add the receiver.** Add a .NET worker to `RebusDemo.slnx` and reference `Contracts` and `Messaging`. In `AddRebus`, use `UseAzureStorageQueues` with a new queue name from `QueueNames` and load the shared map with `.Routing(RebusRoutes.Configure)`. Implement `IHandleMessages<PrepareWelcomePackV1>` and register the handler with `AddRebusHandler<PrepareWelcomePackHandler>()`. To add a command to an existing service, register its handler there and use the existing queue. [Worker setup example](src/Develop/Program.cs)
3. **Route and send.** In `RebusRoutes`, map `PrepareWelcomePackV1` to `QueueNames.Welcome` and `WelcomePackPreparedV1` to `QueueNames.Orchestrator`. Send each message with `IBus.Send` and carry the same `CorrelationId` into the reply. To make the saga wait for the reply, implement `IHandleMessages<WelcomePackPreparedV1>` on the saga, map it in `CorrelateMessages`, and advance the stage in `Handle`. [Shared routes](src/Messaging/RebusRoutes.cs) and [saga example](src/Orchestrator/LearnerRegistrationSaga.cs)

## Run locally

Install the .NET 10 SDK and Docker with Compose v2. From the repository root, start the infrastructure and wait for the database to be created.

```sh
docker compose up -d --wait azurite sqlserver
docker compose run --rm sqlserver-init
dotnet build RebusDemo.slnx
```

The database initialization command is safe to rerun. Rebus creates the input queues, the shared `rebus-errors` queue, and the `dbo.Sagas` and `dbo.SagaIndex` tables. The orchestrator also creates `dbo.RebusDeadLetters` and `dbo.CompletedRegistrations` at startup. Azurite and SQL Server keep their data in Docker volumes.

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

- **Sagas** shows the current stage for registrations in `dbo.Sagas`, including failed registrations and their reasons. The final assessment handler saves completed outcomes in `dbo.CompletedRegistrations` before Rebus removes the live saga. Completed outcomes are available for registrations that finish after this dashboard is installed.
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
| Orchestrator → Manage | `CreateLearnerV1` | |
| Manage → Orchestrator | `LearnerCreatedV1` | `AwaitingLearningPlan` |
| Orchestrator → Develop | `CreateLearningPlanV1` | |
| Develop → Orchestrator | `LearningPlanCreatedV1` | `AwaitingAssessment` |
| Orchestrator → Take | `ScheduleAssessmentV1` | |
| Take → Orchestrator | `AssessmentScheduledV1` | `Completed` |

The final log includes the learner, plan and assessment IDs and the assessment's UTC time. The dashboard shows the saved completed outcome after Rebus removes the live saga and its correlation index.

## Try message validation

The shared contracts use Data Annotations for rules such as a valid email, a 160-character learning goal limit, non-default IDs and timestamps, and a learning-plan reply with at least one module. Each application bus runs those rules after deserialization and before dispatching the message to its handler. [Registration contracts](src/Contracts/LearnerRegistrationV1.cs) · [Rebus validation step](src/Messaging/RebusMessageValidation.cs)

`preferredFormat` is optional. Leave it out or set it to `null` for the default `online` plan. To request `workbook`, send the sample below and look for `workbook` in Develop's log.

```sh
curl -i http://127.0.0.1:5080/learners/register \
  -H 'Content-Type: application/json' \
  --data-binary @samples/register-learner-workbook.json
```

Change `preferredFormat` to `video` in a copy of the sample to see HTTP `400`. If an invalid message arrives directly on a queue, Rebus treats the validation failure as permanent and archives it through `rebus-errors`. A worker can also send a failure reply when the command includes a valid workflow correlation ID.

## Send a standalone reminder

This example needs only Azurite, Manage and Develop. Start Azurite and build from the repository root.

```sh
docker compose up -d --wait azurite
dotnet build RebusDemo.slnx
```

Run one command per terminal, also from the repository root.

```sh
dotnet run --no-build --project src/Manage
dotnet run --no-build --project src/Develop
```

Once Manage is listening on port `5082`, send the reminder.

```sh
curl -i http://127.0.0.1:5082/learners/remind \
  -H 'Content-Type: application/json' \
  --data '{"learnerId":"LRN-0142"}'
```

Manage returns `202 Accepted` with a `reminderId`. Look for `>>> [Develop] Handled SendLearnerReminderV1` and the same ID in Develop's terminal. The handler simulates sending the reminder in its log.

When Manage sends `SendLearnerReminderV1`, Rebus looks up `QueueNames.Develop` in the shared map and delivers the message to `develop`. Develop's `IHandleMessages<SendLearnerReminderV1>` handler completes the work on receipt. This example runs with the orchestrator and SQL Server stopped. [Reminder contract](src/Contracts/SendLearnerReminderV1.cs) · [Shared routes](src/Messaging/RebusRoutes.cs) · [Develop handler](src/Develop/SendLearnerReminderHandler.cs)

## Demonstrate restart recovery

1. Stop Develop with `Ctrl+C` and leave the other apps and both containers running.
2. Submit a registration. Wait for the orchestrator to log `AwaitingLearningPlan` and keep the returned correlation ID. Develop's command is queued while the service is stopped.
3. Stop the orchestrator with `Ctrl+C`. Start Develop and wait for it to log that it sent `LearningPlanCreatedV1` for that correlation ID.
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

The sample sets `learningGoal` to `DEMO_FAIL_LEARNING_PLAN`. Develop throws on every delivery of that command. After five attempts, Rebus dispatches it to Develop's `IHandleMessages<IFailed<CreateLearningPlanV1>>` handler. The handler sends `LearningPlanCreationFailedV1` to the orchestrator and dead-letters the original command to `rebus-errors` with the exception details.

The API returns `202 Accepted` when it queues the registration. Use its `correlationId` to find the eventual outcome. The orchestrator logs `AwaitingLearningPlan -> Failed on LearningPlanCreationFailedV1` and saves `Failed`, the previous stage, and the reason in SQL Server. `Failed` is terminal in this demo. Submit a fresh registration after correcting the cause.

| Service | Failure reply after exhausted retries |
| --- | --- |
| Manage | `LearnerCreationFailedV1` |
| Develop | `LearningPlanCreationFailedV1` |
| Take | `AssessmentSchedulingFailedV1` |

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
