# Rebus learner registration demo

## Architecture

Four .NET 10 applications run independently on the host. [Docker Compose](docker-compose.yml) runs Azurite for Azure Storage Queues and SQL Server 2022 for the orchestrator's registry and workflow state.

The orchestrator knows nothing about the other services except the queues registered in its database. It has no reference to the shared contract types. A workflow starts at [`POST /workflows/{name}/start`](src/Orchestrator/Workflows/StartWorkflowEndpoint.cs), for example `/workflows/register-learner/start`. The orchestrator validates the request against the workflow's start contract, creates a workflow instance, queues the first command and returns `202 Accepted` with a correlation ID. Manage, Develop and Take reply to the orchestrator queue, and the orchestrator sends the next command. Every command and reply carries the same correlation ID, so the orchestrator finds the instance in SQL Server, even after a restart.

Manage also sends a standalone reminder command. Like every service message it goes to the `orchestrator` queue, and the orchestrator forwards it to `send-learner-reminder-v1`. Develop handles it using a regular Rebus message handler. [Send a standalone reminder](#send-a-standalone-reminder)

The orchestrator runs a second Rebus listener to archive messages from `rebus-errors` in SQL Server. [Inspect archived errors](#inspect-archived-errors)

The orchestrator also serves a read-only dashboard for workflow status and archived messages. It reads the same SQL database as the workflow engine and archive listener. [Open the dashboard](#open-the-dashboard)

### Routing and contracts

Services know only the `orchestrator` queue: every message they send goes there. The orchestrator decides what happens next from three registry tables in SQL Server. [Seeding](src/Orchestrator/appsettings.json) fills only what is missing and never overwrites existing rows, so edits made in the database survive a restart and apply to the next message without a restart.

- **`dbo.MessageRoutes`** maps a message type name to a destination queue and an optional JSON Schema in `ContractSchema`. A registered type from a service is validated against its schema and forwarded to its queue without deserializing it. A message that fails is moved to `rebus-errors` and archived, with the schema errors in its details. [Registry routing](src/Orchestrator/Registry/RegistryRouter.cs) · [Schemas](src/Orchestrator/Registry/Schemas)
- **`dbo.Workflows` and `dbo.WorkflowTransitions`** define each workflow. A transition says that when message type X arrives in state A, the workflow moves to state B and optionally sends command Y. Each property of Y is copied from the workflow's data, which holds the start request and every reply received so far. A transition can require a data property to be present, and the lowest `Priority` that matches wins. That is how `PreferredName` selects `CreateLearnerV2` over `CreateLearnerV1`. A transition to `Failed` records the reply's `Reason`. [Workflow engine](src/Orchestrator/Workflows/WorkflowEngine.cs)
- **`dbo.WorkflowInstances`** holds one row per running or finished workflow: its state, data, failure details and timestamps.

The orchestrator handles replies on the raw JSON in a Rebus pipeline step before Rebus tries to deserialize them, and sends commands as raw JSON under the type name registered for the route. A command it builds is validated against the same registered schema as a forwarded message, and a command with no registered route fails the workflow step instead of going to the wrong queue. [Pipeline step](src/Orchestrator/Workflows/WorkflowMessageStep.cs) · [Raw JSON sending](src/Orchestrator/Workflows/RawJsonMessage.cs)

Every worker command version has its own queue. Manage and Develop each host two Rebus listeners, with only the corresponding command and failure handlers registered on each listener. [Service routing](src/Messaging/RebusRoutes.cs) · [Rebus hosting multiple buses](https://github.com/rebus-org/Rebus.ServiceProvider#starting-one-or-more-rebus-instances-using-one-or-more-separate-container-instances)

## Projects and entry points

| Project | Startup | Input queues | Responsibility |
| --- | --- | --- | --- |
| Orchestrator | [Program.cs](src/Orchestrator/Program.cs) | `orchestrator`, `rebus-errors` | Hosts the HTTP API, the [registry-driven workflow engine](src/Orchestrator/Workflows/WorkflowEngine.cs), dead-letter archive, and [dashboard](src/Orchestrator/Pages/Dashboard.cshtml). It does not reference `Contracts`. |
| Manage | [Program.cs](src/Manage/Program.cs) | `create-learner-v1`, `create-learner-v2` | Creates demo learner IDs for the workflow and exposes [`POST /learners/remind`](src/Manage/SendLearnerReminderEndpoint.cs). |
| Develop | [Program.cs](src/Develop/Program.cs) | `create-learning-plan-v1`, `send-learner-reminder-v1` | Builds [learning plans](src/Develop/CreateLearningPlanHandler.cs) and handles [standalone reminders](src/Develop/SendLearnerReminderHandler.cs). |
| Take | [Program.cs](src/Take/Program.cs) | `schedule-assessment-v1` | Handles [`ScheduleAssessmentV1`](src/Take/ScheduleAssessmentHandler.cs) and returns an online assessment slot. |
| Contracts | Shared library | — | Defines the [versioned commands](src/Contracts/LearnerRegistrationV1.cs), [status updates](src/Contracts/LearnerRegistrationStatusUpdates.cs) and [queue names](src/Contracts/QueueNames.cs) used by the services. The orchestrator uses registered JSON Schemas instead. |
| Messaging | Shared library | — | Configures [worker queues](src/Messaging/CapabilityQueueConfiguration.cs) and provides the [service routing](src/Messaging/RebusRoutes.cs) that sends every outgoing message to the orchestrator. |

## Extend the demo with Rebus

A **contract** is the shape of a message. Services share it as a C# type, and the orchestrator checks it as a registered JSON Schema. A **command** asks a service to do work. A **status update** reports the outcome. Commands use a version suffix and status updates use names such as `LearnerCreated` or `LearnerCreationFailed`. The orchestrator matches replies by exact type name, so a renamed status update needs a transition for the new name before services start sending it.

1. **Define the messages and queue.** Add `PrepareWelcomePackV1`, `WelcomePackPrepared`, and a `QueueNames.PrepareWelcomePackV1` constant with the value `prepare-welcome-pack-v1` to the shared Contracts project. Give both messages a `CorrelationId` so the orchestrator can find the workflow. A breaking request change gets its own command type, queue and handler, such as `PrepareWelcomePackV2` on `prepare-welcome-pack-v2`. Keep the V1 listener running while it still has senders or queued commands. [Queue names](src/Contracts/QueueNames.cs)
2. **Add the receiver.** For a new service, add a .NET worker to `RebusDemo.slnx` and reference `Contracts`, `Messaging` and `Rebus.ServiceProvider`. Implement `IHandleMessages<PrepareWelcomePackV1>` and call `AddRebusHandler<PrepareWelcomePackHandler>()` and `AddRebus(configure => configure.UseCapabilityQueue(storage, QueueNames.PrepareWelcomePackV1))`. For an existing service, put those registrations and the handler's dependencies inside `AddRebusService`. Call `AddLogging()` inside that listener if the handler uses `ILogger<T>`. The service sends `WelcomePackPrepared` with `IBus.Send` and needs no routing change. [Worker setup example](src/Develop/Program.cs)
3. **Register the route and the workflow step.** Add a route to `Registry:Routes` in the orchestrator's appsettings (or a row in `dbo.MessageRoutes`) mapping `RebusDemo.Contracts.PrepareWelcomePackV1, Contracts` to `prepare-welcome-pack-v1`, with a JSON Schema file in `Registry/Schemas` as its `SchemaFile`. Then add transitions to the workflow. One sends `PrepareWelcomePackV1` from the state that precedes it, using a `SendMapping` of property names, and moves to `AwaitingWelcomePack`. Another handles `RebusDemo.Contracts.WelcomePackPrepared, Contracts` in that state. The orchestrator needs no code change, but seeding only creates a workflow that does not exist yet. For an existing database, edit the rows in `dbo.WorkflowTransitions` or delete the workflow's rows so they are seeded again. [Workflow seed](src/Orchestrator/appsettings.json)

## Run locally

Install the .NET 10 SDK and Docker with Compose v2. From the repository root, start the infrastructure and wait for the database to be created.

```sh
docker compose up -d --wait azurite sqlserver
docker compose run --rm sqlserver-init
dotnet build RebusDemo.slnx
```

The database initialization command is safe to rerun. Rebus creates the input queues and the shared `rebus-errors` queue. The orchestrator creates `dbo.MessageRoutes`, `dbo.Workflows`, `dbo.WorkflowTransitions`, `dbo.WorkflowInstances` and `dbo.RebusDeadLetters` at startup and seeds the routes and the `register-learner` workflow from its appsettings. Azurite and SQL Server keep their data in Docker volumes.

Run each application in its own terminal from the repository root.

```sh
dotnet run --no-build --project src/Manage
dotnet run --no-build --project src/Develop
dotnet run --no-build --project src/Take
dotnet run --no-build --project src/Orchestrator -- --urls http://127.0.0.1:5080
```

Azurite listens on `127.0.0.1:10001` and SQL Server on `127.0.0.1:1433`. The defaults match the Compose file. Override the queue connection with `ConnectionStrings__AzureQueues` in all four applications. If you change `MSSQL_SA_PASSWORD` for Compose, set `ConnectionStrings__RebusSqlServer` in the orchestrator to the matching connection string. The checked-in credentials are for this local demo.

## Open the dashboard

Start the services as described above and open [http://127.0.0.1:5080/dashboard](http://127.0.0.1:5080/dashboard). The dashboard runs in the orchestrator on the same port as `POST /workflows/{name}/start`.

- **Workflows** shows the current state of every instance in `dbo.WorkflowInstances`, including failed workflows and their reasons, and the completion time of finished ones. Search by correlation ID, workflow name or any value held in the workflow's data.
- **Archived errors** reads `dbo.RebusDeadLetters`. Search by correlation ID, message ID, message type, or source queue. Open **View details** to see the original headers, body, and exception. From a failed workflow, **Find errors** opens the archive filtered to that workflow.

Use [Register a learner](#register-a-learner) for a completed row and [Demonstrate a failed workflow](#demonstrate-a-failed-workflow) for a failed row and its archived error. Click **Refresh** after the queue processing finishes. To see a live waiting state, use the [restart recovery example](#demonstrate-restart-recovery) and open the dashboard while Develop is stopped and the orchestrator is still running.

## Register a learner

```sh
curl -i http://127.0.0.1:5080/workflows/register-learner/start \
  -H 'Content-Type: application/json' \
  --data-binary @samples/register-learner.json
```

The API returns `202 Accepted` with a `correlationId` once the first command is queued. Follow that ID in the `>>> [Service]` blocks in each terminal. Each block shows the command and reply or the workflow transition, with details underneath. Routine Rebus and ASP.NET Core messages are hidden at the default log level. Warnings and errors remain visible.

Set `Logging__Console__FormatterName=json` when launching a service to see the structured JSON logs.

| From → To | Message | Workflow state after handling |
| --- | --- | --- |
| HTTP endpoint → Orchestrator | start request (`RegisterLearnerV1`) | `AwaitingLearner` |
| Orchestrator → Manage | `CreateLearnerV1` or `CreateLearnerV2` | |
| Manage → Orchestrator | `LearnerCreated` | `AwaitingLearningPlan` |
| Orchestrator → Develop | `CreateLearningPlanV1` | |
| Develop → Orchestrator | `LearningPlanCreated` | `AwaitingAssessment` |
| Orchestrator → Take | `ScheduleAssessmentV1` | |
| Take → Orchestrator | `AssessmentScheduled` | `Completed` |

The final log shows the workflow reaching `Completed`. The dashboard shows its completion time, and `dbo.WorkflowInstances` keeps the combined data, including the learner, plan and assessment IDs.

### Try both learner creation versions

The sample above uses `create-learner-v1`. Submit the sample below to supply a nonblank `preferredName` and have the orchestrator send `CreateLearnerV2` to `create-learner-v2`. Both handlers send the same `LearnerCreated` status update to `orchestrator`.

```sh
curl -i http://127.0.0.1:5080/workflows/register-learner/start \
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

The orchestrator validates each workflow start request against the JSON Schema registered for the workflow, and every command it sends or forwards against the schema registered for that command. The schemas cover rules such as a valid email, a 160-character learning goal limit and non-default IDs and timestamps. A start request that breaks the contract returns `400` with the reasons. The services also validate incoming commands and replies with Data Annotations, after deserialization and before dispatching to a handler. [Start schema](src/Orchestrator/Registry/Schemas/RegisterLearnerV1.json) · [Command schemas](src/Orchestrator/Registry/Schemas) · [Service commands](src/Contracts/LearnerRegistrationV1.cs) · [Status updates](src/Contracts/LearnerRegistrationStatusUpdates.cs) · [Rebus validation step](src/Messaging/RebusMessageValidation.cs)

`preferredFormat` is optional. Leave it out or set it to `null` for the default `online` plan. To request `workbook`, send the sample below and look for `workbook` in Develop's log.

```sh
curl -i http://127.0.0.1:5080/workflows/register-learner/start \
  -H 'Content-Type: application/json' \
  --data-binary @samples/register-learner-workbook.json
```

Change `preferredFormat` to `video` in a copy of the sample to see HTTP `400`. If an invalid message arrives from a service, the orchestrator moves it to `rebus-errors` instead of forwarding it, and it is archived with the schema errors. A worker can also send a failure reply when the command includes a valid workflow correlation ID.

## Send a standalone reminder

This example needs the orchestrator, Manage and Develop, because the orchestrator forwards the reminder. Start the infrastructure and build from the repository root, as in [Run locally](#run-locally).

```sh
docker compose up -d --wait azurite sqlserver
docker compose run --rm sqlserver-init
dotnet build RebusDemo.slnx
```

Run one command per terminal, also from the repository root.

```sh
dotnet run --no-build --project src/Manage
dotnet run --no-build --project src/Develop
dotnet run --no-build --project src/Orchestrator -- --urls http://127.0.0.1:5080
```

Once Manage is listening on port `5082`, send the reminder.

```sh
curl -i http://127.0.0.1:5082/learners/remind \
  -H 'Content-Type: application/json' \
  --data '{"learnerId":"LRN-0142"}'
```

Manage returns `202 Accepted` with a `reminderId`. Look for `>>> [Develop] Handled SendLearnerReminderV1` and the same ID in Develop's terminal. The handler simulates sending the reminder in its log.

When Manage sends `SendLearnerReminderV1`, its only route is the `orchestrator` queue. The orchestrator finds the type in `dbo.MessageRoutes` and forwards it to `send-learner-reminder-v1`. Develop's `IHandleMessages<SendLearnerReminderV1>` handler completes the work on receipt. [Reminder contract](src/Contracts/SendLearnerReminderV1.cs) · [Registry routing](src/Orchestrator/Registry/RegistryRouter.cs) · [Develop handler](src/Develop/SendLearnerReminderHandler.cs)

## Demonstrate restart recovery

1. Stop Develop with `Ctrl+C` and leave the other apps and both containers running.
2. Submit a registration. Wait for the orchestrator to log `AwaitingLearningPlan` and keep the returned correlation ID. Develop's command is queued while the service is stopped.
3. Stop the orchestrator with `Ctrl+C`. Start Develop and wait for it to log that it sent `LearningPlanCreated` for that correlation ID.
4. Restart the orchestrator with its original command. It consumes the queued reply, loads the workflow instance from SQL Server and advances to `AwaitingAssessment`. The same correlation ID reaches `Completed` without another HTTP request.

To inspect durable state after the orchestrator has stopped in step 3, run this in another terminal. It shows the workflow waiting in the `AwaitingLearningPlan` state with its data so far. The row remains after the workflow completes, with its final state.

```sh
docker compose exec -T sqlserver bash -c '
  exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d RebusDemo' <<'SQL'
SELECT CorrelationId, WorkflowName, State, Data FROM dbo.WorkflowInstances ORDER BY StartedAtUtc DESC;
GO
SQL
```

## Demonstrate a transient failure

Stop Develop and restart it with this option. Leave the other applications running and submit a fresh registration.

```sh
Demo__FailFirstLearningPlan=true dotnet run --no-build --project src/Develop
```

Develop logs a simulated exception for the first `CreateLearningPlanV1` it handles. Rebus retries the same message. The successful Develop log shows the same message ID and correlation ID, and the orchestrator reaches `Completed`. The switch injects one failure per Develop process.

## Demonstrate a failed workflow

With all four services running, submit the failure sample from the repository root.

```sh
curl -i http://127.0.0.1:5080/workflows/register-learner/start \
  -H 'Content-Type: application/json' \
  --data-binary @samples/register-learner-failure.json
```

The sample sets `learningGoal` to `DEMO_FAIL_LEARNING_PLAN`. Develop throws on every delivery of that command. After five attempts, Rebus dispatches it to Develop's `IHandleMessages<IFailed<CreateLearningPlanV1>>` handler. The handler sends `LearningPlanCreationFailed` to the orchestrator and dead-letters the original command to `rebus-errors` with the exception details.

The API returns `202 Accepted` when it queues the first command. Use its `correlationId` to find the eventual outcome. The orchestrator logs `AwaitingLearningPlan -> Failed on LearningPlanCreationFailed` and saves `Failed`, the previous state, and the reason in SQL Server. `Failed` is terminal in this demo. Submit a fresh registration after correcting the cause.

| Service | Failure reply after exhausted retries |
| --- | --- |
| Manage | `LearnerCreationFailed` |
| Develop | `LearningPlanCreationFailed` |
| Take | `AssessmentSchedulingFailed` |

Inspect failed workflows in SQL Server. `FailedAtState` shows where it failed, such as `AwaitingLearningPlan`.

```sh
docker compose exec -T sqlserver bash -c '
  exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d RebusDemo' <<'SQL'
SELECT CorrelationId, WorkflowName, FailedAtState, FailureReason, UpdatedAtUtc
FROM dbo.WorkflowInstances
WHERE State = N'Failed';
GO
SQL
```

The orchestrator locks the workflow's row while it handles a reply and commits its state change after queuing the next command. If a delivery repeats, a reply that no longer fits the current state is logged and ignored, and a repeated command is harmless because service handlers derive the demo resource IDs from the correlation ID and Take derives its slot from the original request date. Every start request creates a fresh workflow, and the demo service results are generated in the handlers without separate business databases.

## Inspect archived errors

Run the failed workflow sample, then open **Archived errors** in the dashboard or look for `Archived` in the orchestrator log. Its second Rebus listener reads `rebus-errors` and saves the message ID, type, source queue, correlation ID, exception details, and timestamps in `dbo.RebusDeadLetters`. The full headers and raw message body are also saved, including bodies that couldn’t be deserialized. [Archive handler](src/Orchestrator/DeadLetters/DeadLetterMessageHandler.cs)

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
