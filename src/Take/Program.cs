using Rebus.Config;
using Rebus.Retry.Simple;
using Rebus.Routing.TypeBased;
using RebusDemo.Contracts;
using RebusDemo.Take;

var builder = Host.CreateApplicationBuilder(args);
var storage = builder.Configuration.GetConnectionString("AzureQueues") ?? "UseDevelopmentStorage=true";

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// Register the handler for incoming assessment commands and map outgoing assessment
// results to the orchestrator. The transport creates Take's input queue at startup.
builder.Services.AddRebusHandler<ScheduleAssessmentHandler>();
builder.Services.AddRebus(configure => configure
    .Transport(transport => transport.UseAzureStorageQueues(storage, QueueNames.Take))
    .Routing(routing => routing.TypeBased().Map<AssessmentScheduledV1>(QueueNames.Orchestrator))
    .Options(options => options.RetryStrategy(QueueNames.Errors, maxDeliveryAttempts: 5)));

await builder.Build().RunAsync();
