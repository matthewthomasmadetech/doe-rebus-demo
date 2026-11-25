using Rebus.Config;
using Rebus.Pipeline;
using Rebus.Retry;
using Rebus.Retry.Simple;
using Rebus.Routing.TransportMessages;
using Rebus.Sagas.Idempotent;
using RebusDemo.Contracts;
using RebusDemo.Logging;
using RebusDemo.Messaging;
using RebusDemo.Orchestrator;
using RebusDemo.Orchestrator.Dashboard;
using RebusDemo.Orchestrator.DeadLetters;
using RebusDemo.Orchestrator.Routing;

var builder = WebApplication.CreateBuilder(args);
var storage = builder.Configuration.GetConnectionString("AzureQueues") ?? "UseDevelopmentStorage=true";
var sql = builder.Configuration.GetConnectionString("RebusSqlServer")
    ?? throw new InvalidOperationException("Configure the RebusSqlServer connection string.");
var routeRefreshSeconds = builder.Configuration.GetValue("Routing:RefreshIntervalSeconds", 5);
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(routeRefreshSeconds);

builder.Logging.AddDemoConsole();
builder.Services.AddRazorPages();
builder.Services.AddSingleton(new DashboardStore(sql));
builder.Services.AddSingleton(services => new CapabilityRouteCache(sql,
    TimeSpan.FromSeconds(routeRefreshSeconds), services.GetRequiredService<ILogger<CapabilityRouteCache>>()));
builder.Services.AddHostedService(services => services.GetRequiredService<CapabilityRouteCache>());
builder.Services.AddSingleton<CapabilityMessageForwarder>();

// Rebus discovers all the saga's message handlers from this one DI registration.
builder.Services.AddRebusHandler<LearnerRegistrationSaga>();
builder.Services.AddRebus((configure, services) => configure
    // This is the entry queue for worker commands, saga initiation, and saga status updates.
    .Transport(transport => transport.UseAzureStorageQueues(storage, QueueNames.Orchestrator))
    .Routing(routing =>
    {
        RebusRoutes.Configure(routing);
        var forwarder = services.GetRequiredService<CapabilityMessageForwarder>();
        routing.AddTransportMessageForwarder(forwarder.RouteAsync, ErrorBehavior.Normal);
    })
    // Rebus creates both tables at startup. Sagas holds serialized workflow state and SagaIndex
    // maps a correlation value to that state so replies can resume a workflow after a restart.
    .Sagas(sagas => sagas.StoreInSqlServer(sql, "Sagas", "SagaIndex"))
    .Options(options =>
    {
        // Rebus retries failed deliveries and forwards exhausted messages to the shared error queue.
        options.RetryStrategy(QueueNames.Errors, maxDeliveryAttempts: 5);
        options.ValidateMessageContracts();
        // With IdempotentSaga<T>, Rebus stores handled message IDs and outgoing commands with the active saga.
        // If the same message is redelivered, Rebus can replay its outgoing commands from that history.
        options.EnableIdempotentSagas();
    }));

// This second bus consumes the shared error queue independently of the saga's input queue.
builder.Services.AddSingleton(new DeadLetterStore(sql));
builder.Services.AddSingleton<DeadLetterMessageHandler>();
builder.Services.AddRebus((configure, services) =>
{
    var handler = services.GetRequiredService<DeadLetterMessageHandler>();
    return configure
        .Transport(transport => transport.UseAzureStorageQueues(storage, QueueNames.Errors))
        .Options(options =>
        {
            options.SetNumberOfWorkers(1);
            options.SetMaxParallelism(1);
            options.RetryStrategy(maxDeliveryAttempts: 1);
            options.Register<IErrorHandler>(_ => handler);

            // Run inside Rebus's retry step and capture the raw envelope before it reaches application handlers.
            options.Decorate<IPipeline>(context => new PipelineStepInjector(context.Get<IPipeline>())
                .OnReceive(handler, PipelineRelativePosition.After, typeof(DefaultRetryStep)));
        });
}, isDefaultBus: false);

var app = builder.Build();
await app.Services.GetRequiredService<DeadLetterStore>().EnsureTableExistsAsync();
await app.Services.GetRequiredService<DashboardStore>().EnsureTableExistsAsync();
await app.Services.GetRequiredService<CapabilityRouteCache>().InitializeAsync(app.Lifetime.ApplicationStopping);
app.UseStaticFiles();
app.MapRazorPages();
app.MapPost("/learners/register", RegisterLearnerEndpoint.RegisterAsync);
await app.RunAsync();
