using Rebus.Config;
using RebusDemo.Contracts;
using RebusDemo.Logging;
using RebusDemo.Manage;
using RebusDemo.Messaging;

var builder = WebApplication.CreateBuilder(args);
var storage = builder.Configuration.GetConnectionString("AzureQueues") ?? "UseDevelopmentStorage=true";

builder.Logging.AddDemoConsole();

// The V1 bus also supplies IBus to Manage's reminder HTTP endpoint.
builder.Services.AddTransient<LearnerCreation>();
builder.Services.AddRebusHandler<CreateLearnerHandler>();
builder.Services.AddRebus(configure => configure.UseCapabilityQueue(storage, QueueNames.CreateLearnerV1));

// Rebus hosts V2 with its own queue and handler registrations inside the same Manage process.
// A deployment with only V1 support can leave this listener off.
if (builder.Configuration.GetValue("Demo:EnableCreateLearnerV2", true))
{
    builder.Host.AddRebusService(services =>
    {
        // Register ILogger<T> in the child container. Rebus forwards the host's logger factory.
        services.AddLogging();
        services.AddTransient<LearnerCreation>();
        services.AddRebusHandler<CreateLearnerV2Handler>();
        services.AddRebus(configure => configure.UseCapabilityQueue(storage, QueueNames.CreateLearnerV2));
    });
}

var app = builder.Build();
app.MapPost("/learners/remind", SendLearnerReminderEndpoint.SendAsync);
await app.RunAsync();
