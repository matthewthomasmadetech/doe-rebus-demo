using Rebus.Config;
using RebusDemo.Contracts;
using RebusDemo.Logging;
using RebusDemo.Messaging;
using RebusDemo.Take;

var builder = Host.CreateApplicationBuilder(args);
var storage = builder.Configuration.GetConnectionString("AzureQueues") ?? "UseDevelopmentStorage=true";

builder.Logging.AddDemoConsole();

// Register the handler for incoming assessment commands. Rebus creates the capability queue at startup.
builder.Services.AddRebusHandler<ScheduleAssessmentHandler>();
builder.Services.AddRebus(configure => configure.UseCapabilityQueue(storage, QueueNames.ScheduleAssessmentV1));

await builder.Build().RunAsync();
