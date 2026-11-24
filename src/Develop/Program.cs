using Rebus.Config;
using Rebus.Retry.Simple;
using Rebus.Routing.TypeBased;
using RebusDemo.Contracts;
using RebusDemo.Develop;
using RebusDemo.Logging;

var builder = Host.CreateApplicationBuilder(args);
var storage = builder.Configuration.GetConnectionString("AzureQueues") ?? "UseDevelopmentStorage=true";

builder.Logging.AddDemoConsole();

// Share the demo failure switch across handler instances so a retry can succeed.
builder.Services.AddSingleton(new DemoFailure(builder.Configuration.GetValue<bool>("Demo:FailFirstLearningPlan")));
builder.Services.AddRebusHandler<CreateLearningPlanHandler>();

// Develop listens on its own Azurite queue. Sending a LearningPlanCreatedV1 uses
// this endpoint's type-based route back to the orchestrator.
builder.Services.AddRebus(configure => configure
    .Transport(transport => transport.UseAzureStorageQueues(storage, QueueNames.Develop))
    .Routing(routing => routing.TypeBased().Map<LearningPlanCreatedV1>(QueueNames.Orchestrator))
    .Options(options => options.RetryStrategy(QueueNames.Errors, maxDeliveryAttempts: 5)));

await builder.Build().RunAsync();
