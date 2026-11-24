using Rebus.Config;
using Rebus.Retry.Simple;
using RebusDemo.Contracts;
using RebusDemo.Logging;
using RebusDemo.Manage;
using RebusDemo.Messaging;

var builder = WebApplication.CreateBuilder(args);
var storage = builder.Configuration.GetConnectionString("AzureQueues") ?? "UseDevelopmentStorage=true";

builder.Logging.AddDemoConsole();

// Register the handler with dependency injection. Rebus dispatches messages to it using
// the message type declared in IHandleMessages<T>.
builder.Services.AddRebusHandler<CreateLearnerHandler>();

// Manage reads its own input queue and uses the shared map for outgoing commands and replies.
builder.Services.AddRebus(configure => configure
    .Transport(transport => transport.UseAzureStorageQueues(storage, QueueNames.Manage))
    .Routing(RebusRoutes.Configure)
    .Options(options => options.RetryStrategy(QueueNames.Errors, maxDeliveryAttempts: 5,
        secondLevelRetriesEnabled: true, errorDetailsHeaderMaxLength: 4096)));

var app = builder.Build();
app.MapPost("/learners/remind", SendLearnerReminderEndpoint.SendAsync);
await app.RunAsync();
