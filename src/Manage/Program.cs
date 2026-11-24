using Rebus.Config;
using Rebus.Retry.Simple;
using Rebus.Routing.TypeBased;
using RebusDemo.Contracts;
using RebusDemo.Manage;

var builder = Host.CreateApplicationBuilder(args);
var storage = builder.Configuration.GetConnectionString("AzureQueues") ?? "UseDevelopmentStorage=true";

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// Register the handler with dependency injection. Rebus dispatches messages to it using
// the message type declared in IHandleMessages<T>.
builder.Services.AddRebusHandler<CreateLearnerHandler>();

// AddRebus ties the bus lifecycle to the .NET host. Transport picks this service's input
// queue, and TypeBased maps the outgoing completion message to the orchestrator's queue.
builder.Services.AddRebus(configure => configure
    .Transport(transport => transport.UseAzureStorageQueues(storage, QueueNames.Manage))
    .Routing(routing => routing.TypeBased().Map<LearnerCreatedV1>(QueueNames.Orchestrator))
    .Options(options => options.RetryStrategy(QueueNames.Errors, maxDeliveryAttempts: 5)));

await builder.Build().RunAsync();
