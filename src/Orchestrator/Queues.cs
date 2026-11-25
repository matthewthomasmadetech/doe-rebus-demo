namespace RebusDemo.Orchestrator;

/// <summary>The only queue names the orchestrator needs to know. Every other destination comes from the registry.</summary>
internal static class Queues
{
    public const string Orchestrator = "orchestrator";
    public const string Errors = "rebus-errors";
}
