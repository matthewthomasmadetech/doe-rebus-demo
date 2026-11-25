using Rebus.Config;
using RebusDemo.Contracts;
using RebusDemo.Logging;
using RebusDemo.Messaging;
using RebusDemo.Take;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging => logging.AddDemoConsole())
    .ConfigureServices((context, services) =>
    {
        AddAssessmentQueue(services, context.Configuration, QueueNames.ScheduleAssessmentV1);
        services.AddRebusHandler<ScheduleAssessmentHandler>();
    });

// Each listener resolves only the handler for its contract version. The alternate V2 listener
// lets us change the destination in SQL while Take and the orchestrator are still running.
foreach (var queue in new[] { QueueNames.ScheduleAssessmentV2, QueueNames.ScheduleAssessmentV2Alternate })
{
    builder.AddRebusService((context, services) =>
    {
        services.AddLogging();
        AddAssessmentQueue(services, context.Configuration, queue);
        services.AddRebusHandler<ScheduleAssessmentV2Handler>();
    });
}

await builder.Build().RunAsync();

// Pass the queue name to the handler so its log shows where the message actually arrived.
static void AddAssessmentQueue(IServiceCollection services, IConfiguration configuration, string queue)
{
    var storage = configuration.GetConnectionString("AzureQueues") ?? "UseDevelopmentStorage=true";
    services.AddSingleton(new AssessmentQueue(queue));
    services.AddTransient<AssessmentScheduling>();
    services.AddRebus(configure => configure.UseCapabilityQueue(storage, queue));
}
