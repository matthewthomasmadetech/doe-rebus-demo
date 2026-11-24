using Rebus.Config;
using RebusDemo.Contracts;
using RebusDemo.Develop;
using RebusDemo.Logging;
using RebusDemo.Messaging;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging => logging.AddDemoConsole())
    .ConfigureServices((context, services) =>
    {
        var storage = context.Configuration.GetConnectionString("AzureQueues") ?? "UseDevelopmentStorage=true";

        // Share the demo failure switch across learning-plan handler instances so a retry can succeed.
        services.AddSingleton(new DemoFailure(context.Configuration.GetValue<bool>("Demo:FailFirstLearningPlan")));
        services.AddRebusHandler<CreateLearningPlanHandler>();
        services.AddRebus(configure => configure.UseCapabilityQueue(storage, QueueNames.CreateLearningPlanV1));
    })
    .AddRebusService((context, services) =>
    {
        var storage = context.Configuration.GetConnectionString("AzureQueues") ?? "UseDevelopmentStorage=true";

        // This listener has its own container, so it only resolves the reminder handler.
        // AddLogging registers ILogger<T> using the logger factory forwarded by Rebus.
        services.AddLogging();
        services.AddRebusHandler<SendLearnerReminderHandler>();
        services.AddRebus(configure => configure.UseCapabilityQueue(storage, QueueNames.SendLearnerReminderV1));
    });

await builder.Build().RunAsync();
