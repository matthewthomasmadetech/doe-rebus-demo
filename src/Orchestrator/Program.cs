using Rebus.Bus;
using Rebus.Config;
using Rebus.Pipeline;
using Rebus.Pipeline.Receive;
using Rebus.Retry;
using Rebus.Retry.Simple;
using Rebus.Routing.TransportMessages;
using Rebus.Serialization;
using RebusDemo.Logging;
using RebusDemo.Orchestrator;
using RebusDemo.Orchestrator.Dashboard;
using RebusDemo.Orchestrator.DeadLetters;
using RebusDemo.Orchestrator.Registry;
using RebusDemo.Orchestrator.Workflows;

var builder = WebApplication.CreateBuilder(args);
var storage = builder.Configuration.GetConnectionString("AzureQueues") ?? "UseDevelopmentStorage=true";
var sql = builder.Configuration.GetConnectionString("RebusSqlServer")
    ?? throw new InvalidOperationException("Configure the RebusSqlServer connection string.");

builder.Logging.AddDemoConsole();
builder.Services.AddRazorPages();
builder.Services.AddSingleton(new DashboardStore(sql));

// Message routes and workflow definitions live in SQL Server. Configuration only seeds them.
var registry = new RouteRegistry(sql);
var workflows = new WorkflowStore(sql);
var seedRoutes = builder.Configuration.GetSection("Registry:Routes").Get<RouteRegistration[]>() ?? [];
var seedWorkflows = builder.Configuration.GetSection("Workflows").Get<WorkflowSeed[]>() ?? [];
builder.Services.AddSingleton(registry);
builder.Services.AddSingleton(workflows);
builder.Services.AddSingleton<WorkflowEngine>();

builder.Services.AddRebus((configure, services) => configure
    // Services push every message to this queue: workflow starts and replies, and messages to forward.
    .Transport(transport => transport.UseAzureStorageQueues(storage, Queues.Orchestrator))
    .Routing(routing =>
    {
        // Commands the orchestrator sends are routed from the registry. A message arriving from a service is
        // forwarded to its registered queue, or continues to the workflow engine when none is registered.
        routing.Register(_ => new RegistryRouter(registry));
        // A message that fails its registered contract is moved to the error queue instead of being forwarded.
        routing.AddTransportMessageForwarder(message => RegistryRouter.ForwardRegisteredAsync(registry, message),
            ErrorBehavior.Normal);
    })
    // The orchestrator sends registered message types as raw JSON, so it needs no compiled contract types.
    .Serialization(serialization => serialization.Decorate(context => new RawJsonSerializer(context.Get<ISerializer>())))
    .Options(options =>
    {
        // Rebus retries failed deliveries and forwards exhausted messages to the shared error queue.
        options.RetryStrategy(Queues.Errors, maxDeliveryAttempts: 5);

        // Replies that workflows wait for are handled on the raw JSON, before Rebus would deserialize them.
        // Resolve the bus when a message arrives, because it does not exist yet while it is being configured.
        var step = new WorkflowMessageStep(workflows, services.GetRequiredService<WorkflowEngine>(),
            () => services.GetRequiredService<IBus>());
        options.Decorate<IPipeline>(context => new PipelineStepInjector(context.Get<IPipeline>())
            .OnReceive(step, PipelineRelativePosition.Before, typeof(DeserializeIncomingMessageStep)));
    }));

// This second bus consumes the shared error queue independently of the orchestrator's input queue.
builder.Services.AddSingleton(new DeadLetterStore(sql));
builder.Services.AddSingleton<DeadLetterMessageHandler>();
builder.Services.AddRebus((configure, services) =>
{
    var handler = services.GetRequiredService<DeadLetterMessageHandler>();
    return configure
        .Transport(transport => transport.UseAzureStorageQueues(storage, Queues.Errors))
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
await registry.EnsureTableExistsAsync(seedRoutes);
await workflows.EnsureTablesAsync(seedWorkflows);
app.UseStaticFiles();
app.MapRazorPages();
app.MapPost("/workflows/{name}/start", StartWorkflowEndpoint.StartAsync);
await app.RunAsync();
