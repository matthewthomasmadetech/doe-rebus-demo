using Rebus.Config;
using Rebus.Retry.Simple;
using Rebus.Sagas.Idempotent;
using RebusDemo.Contracts;
using RebusDemo.Logging;
using RebusDemo.Messaging;
using RebusDemo.Orchestrator;

var builder = WebApplication.CreateBuilder(args);
var storage = builder.Configuration.GetConnectionString("AzureQueues") ?? "UseDevelopmentStorage=true";
var sql = builder.Configuration.GetConnectionString("RebusSqlServer")
    ?? throw new InvalidOperationException("Configure the RebusSqlServer connection string.");

builder.Logging.AddDemoConsole();

// Rebus discovers all the saga's message handlers from this one DI registration.
builder.Services.AddRebusHandler<LearnerRegistrationSaga>();
builder.Services.AddRebus(configure => configure
    // The orchestrator reads both registration commands and service replies from this queue.
    .Transport(transport => transport.UseAzureStorageQueues(storage, QueueNames.Orchestrator))
    .Routing(RebusRoutes.Configure)
    // Rebus creates both tables at startup. Sagas holds serialized workflow state and SagaIndex
    // maps a correlation value to that state so replies can resume a workflow after a restart.
    .Sagas(sagas => sagas.StoreInSqlServer(sql, "Sagas", "SagaIndex"))
    .Options(options =>
    {
        // Rebus retries failed deliveries and forwards exhausted messages to the shared error queue.
        options.RetryStrategy(QueueNames.Errors, maxDeliveryAttempts: 5);
        // With IdempotentSaga<T>, Rebus stores handled message IDs and outgoing commands with the active saga.
        // If the same message is redelivered, Rebus can replay its outgoing commands from that history.
        options.EnableIdempotentSagas();
    }));

var app = builder.Build();
app.MapPost("/learners/register", RegisterLearnerEndpoint.RegisterAsync);
await app.RunAsync();
