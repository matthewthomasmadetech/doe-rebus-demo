using Rebus.Config;
using RebusDemo.Contracts;
using RebusDemo.Develop;
using RebusDemo.Logging;
using RebusDemo.Messaging;

var builder = WebApplication.CreateBuilder(args);
var storage = builder.Configuration.GetConnectionString("AzureQueues") ?? "UseDevelopmentStorage=true";

builder.Logging.AddDemoConsole();

// Share the demo failure switch across plan handlers. This bus also sends the HTTP demo commands.
builder.Services.AddSingleton(new DemoFailure(builder.Configuration.GetValue<bool>("Demo:FailFirstLearningPlan")));
builder.Services.AddRebusHandler<CreateLearningPlanHandler>();
builder.Services.AddRebus(configure => configure.UseCapabilityQueue(storage, QueueNames.CreateLearningPlanV1));

// Give reminder delivery its own handler registrations. Rebus forwards the host's logger factory.
builder.Host.AddRebusService(services =>
{
    services.AddLogging();
    services.AddRebusHandler<SendLearnerReminderHandler>();
    services.AddRebus(configure => configure.UseCapabilityQueue(storage, QueueNames.SendLearnerReminderV1));
});

var app = builder.Build();
app.MapPost("/assessments/schedule/{version}", ScheduleAssessmentEndpoint.ScheduleAsync);
await app.RunAsync();
