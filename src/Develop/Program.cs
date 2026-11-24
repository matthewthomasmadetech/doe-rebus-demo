using Rebus.Config;
using Rebus.Retry.Simple;
using RebusDemo.Contracts;
using RebusDemo.Develop;
using RebusDemo.Logging;
using RebusDemo.Messaging;

var builder = Host.CreateApplicationBuilder(args);
var storage = builder.Configuration.GetConnectionString("AzureQueues") ?? "UseDevelopmentStorage=true";

builder.Logging.AddDemoConsole();

// Share the demo failure switch across handler instances so a retry can succeed.
builder.Services.AddSingleton(new DemoFailure(builder.Configuration.GetValue<bool>("Demo:FailFirstLearningPlan")));
builder.Services.AddRebusHandler<CreateLearningPlanHandler>();
builder.Services.AddRebusHandler<SendLearnerReminderHandler>();

// Both Develop handlers consume messages from the same input queue.
builder.Services.AddRebus(configure => configure
    .Transport(transport => transport.UseAzureStorageQueues(storage, QueueNames.Develop))
    .Routing(RebusRoutes.Configure)
    .Options(options =>
    {
        options.RetryStrategy(QueueNames.Errors, maxDeliveryAttempts: 5,
            secondLevelRetriesEnabled: true, errorDetailsHeaderMaxLength: 4096);
        options.ValidateMessageContracts();
    }));

await builder.Build().RunAsync();
