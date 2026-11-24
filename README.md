# Rebus learner registration demo

## Architecture

Four .NET 10 applications run independently on the host. [Docker Compose](docker-compose.yml) runs Azurite for Azure Storage Queues and SQL Server 2022 for the orchestrator's saga state and correlation index.

The workflow starts at [`POST /learners/register`](src/Orchestrator/RegisterLearnerEndpoint.cs). The endpoint queues a registration command and returns `202 Accepted` with a correlation ID. The Rebus saga sends commands to Manage, Develop and Take in sequence, advancing after each service sends a reply to the orchestrator queue. Every registration command and reply carries the same correlation ID so Rebus can load the correct saga from SQL Server, even after an orchestrator restart.

Manage also sends a standalone reminder command directly to `send-learner-reminder-v1`. Develop handles it using a regular Rebus message handler. [Send a standalone reminder](#send-a-standalone-reminder)

The orchestrator runs a second Rebus listener to archive messages from `rebus-errors` in SQL Server. [Inspect archived errors](#inspect-archived-errors)

The orchestrator also serves a read-only dashboard for registration status and archived messages. It reads the same SQL database as the saga and archive listener. [Open the dashboard](#open-the-dashboard)

All four services use the same outgoing message-to-queue map. `RegisterLearnerV1` and all saga status updates go to the shared `orchestrator` input queue. Every worker command version has its own queue. Manage and Develop each host two Rebus listeners, with only the corresponding command and failure handlers registered on each listener. [Shared Rebus routes](src/Messaging/RebusRoutes.cs) · [Rebus hosting multiple buses](https://github.com/rebus-org/Rebus.ServiceProvider#starting-one-or-more-rebus-instances-using-one-or-more-separate-container-instances)

## Projects and entry points

| Project | Startup | Input queues | Responsibility |
| --- | --- | --- | --- |
| Orchestrator | [Program.cs](src/Orchestrator/Program.cs) | `orchestrator`, `rebus-errors` | Hosts the HTTP API, [registration saga](src/Orchestrator/LearnerRegistrationSaga.cs), dead-letter archive, and [dashboard](src/Orchestrator/Pages/Dashboard.cshtml). |
| Manage | [Program.cs](src/Manage/Program.cs) | `create-learner-v1`, `create-learner-v2` | Creates demo learner IDs for the saga and exposes [`POST /learners/remind`](src/Manage/SendLearnerReminderEndpoint.cs). |
| Develop | [Program.cs](src/Develop/Program.cs) | `create-learning-plan-v1`, `send-learner-reminder-v1` | Builds [learning plans](src/Develop/CreateLearningPlanHandler.cs) and handles [standalone reminders](src/Develop/SendLearnerReminderHandler.cs). |
| Take | [Program.cs](src/Take/Program.cs) | `schedule-assessment-v1` | Handles [`ScheduleAssessmentV1`](src/Take/ScheduleAssessmentHandler.cs) and returns an online assessment slot. |
| Contracts | Shared library | — | Defines [versioned commands](src/Contracts/LearnerRegistrationV1.cs), [status updates](src/Contracts/LearnerRegistrationStatusUpdates.cs), and [queue names](src/Contracts/QueueNames.cs). |
| Messaging | Shared library | — | Configures [worker queues](src/Messaging/CapabilityQueueConfiguration.cs) and provides [outgoing Rebus routes](src/Messaging/RebusRoutes.cs) for all four services. |

## Extend the demo with Rebus

A **contract** is the C# type shared by the sender and receiver. A **command** asks a service to do work. A **status update** reports the outcome. Commands use a version suffix and status updates use names such as `LearnerCreated` or `LearnerCreationFailed`. During a rolling deployment, update the orchestrator first so it can read both current and previously queued status names. [Compatibility configuration](src/Orchestrator/StatusUpdateCompatibility.cs)

1. **Define the messages and queue.** Add `PrepareWelcomePackV1`, `WelcomePackPrepared`, and a `QueueNames.PrepareWelcomePackV1` constant with the value `prepare-welcome-pack-v1` to the shared Contracts project. Give both messages a `CorrelationId` if they belong to the saga. A breaking request change gets its own command type, queue and handler, such as `PrepareWelcomePackV2` on `prepare-welcome-pack-v2`. Keep the V1 listener running while it still has senders or queued commands. [Queue names](src/Contracts/QueueNames.cs)
2. **Add the receiver.** For a new service, add a .NET worker to `RebusDemo.slnx` and reference `Contracts`, `Messaging` and `Rebus.ServiceProvider`. Implement `IHandleMessages<PrepareWelcomePackV1>` and call `AddRebusHandler<PrepareWelcomePackHandler>()` and `AddRebus(configure => configure.UseCapabilityQueue(storage, QueueNames.PrepareWelcomePackV1))`. For an existing service, put those registrations and the handler's dependencies inside `AddRebusService`. Call `AddLogging()` inside that listener if the handler uses `ILogger<T>`. [Worker setup example](src/Develop/Program.cs)
3. **Route and send.** In `RebusRoutes`, map `PrepareWelcomePackV1` to its new queue and `WelcomePackPrepared` to `QueueNames.Orchestrator`. Send each message with `IBus.Send` and carry the same `CorrelationId` into the status update. To make the saga wait for it, implement `IHandleMessages<WelcomePackPrepared>` on the saga, map it in `CorrelateMessages`, and advance the stage in `Handle`. [Shared routes](src/Messaging/RebusRoutes.cs) · [Saga example](src/Orchestrator/LearnerRegistrationSaga.cs)

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
| Orchestrator → Manage | `CreateLearnerV1` or `CreateLearnerV2` | |
| Manage → Orchestrator | `LearnerCreated` | `AwaitingLearningPlan` |
| Orchestrator → Develop | `CreateLearningPlanV1` | |
| Develop → Orchestrator | `LearningPlanCreated` | `AwaitingAssessment` |
| Orchestrator → Take | `ScheduleAssessmentV1` | |
| Take → Orchestrator | `AssessmentScheduled` | `Completed` |

The final log includes the learner, plan and assessment IDs and the assessment's UTC time. The dashboard shows the saved completed outcome after Rebus removes the live saga and its correlation index.

### Try both learner creation versions

The sample above uses `create-learner-v1`. Submit the sample below to supply a nonblank `preferredName` and have the saga send `CreateLearnerV2` to `create-learner-v2`. Both handlers send the same `LearnerCreated` status update to `orchestrator`.

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

When Manage sends `SendLearnerReminderV1`, Rebus looks up the message type in the shared map and delivers it to `send-learner-reminder-v1`. Develop's `IHandleMessages<SendLearnerReminderV1>` handler completes the work on receipt. This example runs with the orchestrator and SQL Server stopped. [Reminder contract](src/Contracts/SendLearnerReminderV1.cs) · [Shared routes](src/Messaging/RebusRoutes.cs) · [Develop handler](src/Develop/SendLearnerReminderHandler.cs)

## Demonstrate restart recovery

1. Stop Develop with `Ctrl+C` and leave the other apps and both containers running.
2. Submit a registration. Wait for the orchestrator to log `AwaitingLearningPlan` and keep the returned correlation ID. Develop's command is queued while the service is stopped.
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
