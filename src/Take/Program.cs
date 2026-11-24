using Rebus.Config;
using Rebus.Retry.Simple;
using RebusDemo.Contracts;
using RebusDemo.Logging;
using RebusDemo.Messaging;
using RebusDemo.Take;

var builder = Host.CreateApplicationBuilder(args);
var storage = builder.Configuration.GetConnectionString("AzureQueues") ?? "UseDevelopmentStorage=true";

builder.Logging.AddDemoConsole();

// Register the handler for incoming assessment commands. Rebus creates Take's input queue at startup.
builder.Services.AddRebusHandler<ScheduleAssessmentHandler>();
builder.Services.AddRebus(configure => configure
    .Transport(transport => transport.UseAzureStorageQueues(storage, QueueNames.Take))
    .Routing(RebusRoutes.Configure)
    .Options(options => options.RetryStrategy(QueueNames.Errors, maxDeliveryAttempts: 5,
        secondLevelRetriesEnabled: true, errorDetailsHeaderMaxLength: 4096)));

await builder.Build().RunAsync();
