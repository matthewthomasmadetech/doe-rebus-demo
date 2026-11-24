namespace RebusDemo.Contracts;

/// <summary>
/// Queue addresses shared by the receiving endpoints and the applications that send to them.
/// </summary>
public static class QueueNames
{
    public const string Manage = "manage";
    public const string Develop = "develop";
    public const string Take = "take";
    public const string Orchestrator = "orchestrator";

    // All four endpoints forward messages here when their delivery attempts are exhausted.
    public const string Errors = "rebus-errors";
}
