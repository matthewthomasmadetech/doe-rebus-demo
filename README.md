# Rebus learner registration demo

Four .NET 10 applications run independently on the host. Rebus routes every command and reply through Azure Storage Queues in Azurite. SQL Server 2022 stores the orchestrator's in-progress saga state and correlation index.

| Application | Input queue | Role |
| --- | --- | --- |
| Orchestrator | `orchestrator` | Accepts registrations over HTTP and coordinates the saga |
| Manage | `manage` | Creates a demo learner registration |
| Develop | `develop` | Builds a learning plan around the learner's goal |
| Take | `take` | Schedules an online diagnostic assessment |

The `Contracts` project defines the versioned commands and replies shared by the four applications.

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

The API returns `202 Accepted` with a `correlationId` once `RegisterLearnerV1` is queued. Search the JSON logs from the four terminals for that ID to follow the full workflow.

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
