# Rebus learner registration demo

## Architecture

Four .NET 10 applications run independently on the host. [Docker Compose](docker-compose.yml) runs Azurite for Azure Storage Queues and SQL Server 2022 for the orchestrator's saga state and correlation index.

The workflow starts at [`POST /learners/register`](src/Orchestrator/RegisterLearnerEndpoint.cs). The endpoint queues a registration command and returns `202 Accepted` with a correlation ID. The Rebus saga sends commands to Manage, Develop and Take in sequence, advancing after each service sends a reply to the orchestrator queue. Every command and reply carries the same correlation ID so Rebus can load the correct saga from SQL Server, even after an orchestrator restart.

## Projects and entry points

| Project | Startup | Input queue | Responsibility |
| --- | --- | --- | --- |
| Orchestrator | [Program.cs](src/Orchestrator/Program.cs) | `orchestrator` | Hosts the HTTP API and the [registration saga](src/Orchestrator/LearnerRegistrationSaga.cs). |
| Manage | [Program.cs](src/Manage/Program.cs) | `manage` | Handles [`CreateLearnerV1`](src/Manage/CreateLearnerHandler.cs) and returns a demo learner ID. |
| Develop | [Program.cs](src/Develop/Program.cs) | `develop` | Handles [`CreateLearningPlanV1`](src/Develop/CreateLearningPlanHandler.cs) and builds a plan around the learner's goal. |
| Take | [Program.cs](src/Take/Program.cs) | `take` | Handles [`ScheduleAssessmentV1`](src/Take/ScheduleAssessmentHandler.cs) and returns an online assessment slot. |
| Contracts | Shared library | — | Defines the versioned [commands and replies](src/Contracts/LearnerRegistrationV1.cs) and [queue names](src/Contracts/QueueNames.cs). |

## Extend the demo with Rebus

A **contract** is the C# type shared by the sender and receiver. A **command** asks a service to do work, and a **reply** reports the result. A Welcome service that prepares a learner's welcome pack would use two message types and its own queue.

1. **Define the messages.** Add records such as `PrepareWelcomePackV1` and `WelcomePackPreparedV1` to the shared Contracts project. Give both a `CorrelationId` if they belong to the saga. Use a new type such as `PrepareWelcomePackV2` for a breaking change and keep the `V1` handler available while old messages are queued. [Existing contracts](src/Contracts/LearnerRegistrationV1.cs)
2. **Add the receiver.** Add a .NET worker to `RebusDemo.slnx` and reference `Contracts`. In `AddRebus`, use `UseAzureStorageQueues` with a new queue name from `QueueNames`. Implement `IHandleMessages<PrepareWelcomePackV1>` and register the handler with `AddRebusHandler<PrepareWelcomePackHandler>()`. To add a command to an existing service, register its handler there and use the existing queue. [Worker setup example](src/Develop/Program.cs)
3. **Route and send.** In the sending app, add a type-based route such as `.Map<PrepareWelcomePackV1>(QueueNames.Welcome)` and send the command with `IBus.Send`. In the receiving service, map the reply type back to the orchestrator and send it with the same `CorrelationId`. To make the saga wait for the reply, implement `IHandleMessages<WelcomePackPreparedV1>` on the saga, map it in `CorrelateMessages`, and advance the stage in `Handle`. [Sender setup](src/Orchestrator/Program.cs) and [saga example](src/Orchestrator/LearnerRegistrationSaga.cs)

## Run locally

Install the .NET 10 SDK and Docker with Compose v2. From the repository root, start the infrastructure and wait for the database to be created.

```sh
docker compose up -d --wait azurite sqlserver
docker compose run --rm sqlserver-init
dotnet build RebusDemo.slnx
```

The database initialization command is safe to rerun. On startup Rebus creates the input queues, the shared `rebus-errors` queue, and the `dbo.Sagas` and `dbo.SagaIndex` tables. Azurite and SQL Server keep their data in Docker volumes.

Run each application in its own terminal from the repository root.

```sh
dotnet run --no-build --project src/Manage
dotnet run --no-build --project src/Develop
dotnet run --no-build --project src/Take
dotnet run --no-build --project src/Orchestrator -- --urls http://127.0.0.1:5080
```

Azurite listens on `127.0.0.1:10001` and SQL Server on `127.0.0.1:1433`. The defaults match the Compose file. Override the queue connection with `ConnectionStrings__AzureQueues` in all four applications. If you change `MSSQL_SA_PASSWORD` for Compose, set `ConnectionStrings__RebusSqlServer` in the orchestrator to the matching connection string. The checked-in credentials are for this local demo.

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

The final log includes the learner, plan and assessment IDs and the assessment's UTC time. Rebus removes the saga and its correlation index when it completes.

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

Develop logs a simulated exception for the first `CreateLearningPlanV1` it handles. Rebus retries the same message. The successful Develop log shows the same message ID and correlation ID, and the orchestrator reaches `Completed`. The switch injects one failure per Develop process. Rebus forwards messages that exhaust five delivery attempts to `rebus-errors`.

Rebus stores handled message IDs and outgoing commands with the active SQL saga. If a queue delivery repeats, Rebus can replay the outgoing command. Service handlers derive the demo resource IDs from the correlation ID and Take derives its slot from the original request date. The saga checks its current stage before advancing on a reply with a new message ID. Every HTTP submission starts a fresh workflow, and the demo service results are generated in the handlers without separate business databases.

Stop the applications with `Ctrl+C` and run `docker compose down` to stop the containers and retain their data volumes.
