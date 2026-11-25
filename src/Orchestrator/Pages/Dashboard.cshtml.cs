using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RebusDemo.Orchestrator.Dashboard;

namespace RebusDemo.Orchestrator.Pages;

public sealed class DashboardModel(DashboardStore store) : PageModel
{
    private const int PageSize = 25;

    [BindProperty(SupportsGet = true)] public string? Tab { get; set; }
    [BindProperty(SupportsGet = true)] public string? Query { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int Number { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public string? Error { get; set; }

    internal bool ShowingErrors => Tab == "errors";
    internal DashboardTotals Totals { get; private set; } = new(0, 0, 0, 0);
    internal IReadOnlyList<WorkflowRow> Workflows { get; private set; } = [];
    internal IReadOnlyList<DeadLetterRow> DeadLetters { get; private set; } = [];
    internal DeadLetterDetails? SelectedError { get; private set; }
    internal bool HasNextPage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellation)
    {
        Response.Headers.CacheControl = "no-store";
        Tab = ShowingErrors ? "errors" : "workflows";
        Status = Status is "active" or "completed" or "failed" ? Status : "";
        Query = Query?.Trim() ?? "";
        Query = Query[..Math.Min(Query.Length, 128)];
        Number = Math.Clamp(Number, 1, int.MaxValue / PageSize);
        var offset = (Number - 1) * PageSize;

        Totals = await store.GetTotalsAsync(cancellation);
        if (ShowingErrors)
        {
            var rows = await store.GetDeadLettersAsync(Query, offset, PageSize + 1, cancellation);
            HasNextPage = rows.Count > PageSize;
            DeadLetters = rows.Take(PageSize).ToList();

            var key = new byte[32];
            if (Error?.Length == 64 && Convert.FromHexString(Error, key, out _, out _) == System.Buffers.OperationStatus.Done)
                SelectedError = await store.GetDeadLetterAsync(key, cancellation);
        }
        else
        {
            var rows = await store.GetWorkflowsAsync(Query, Status, offset, PageSize + 1, cancellation);
            HasNextPage = rows.Count > PageSize;
            Workflows = rows.Take(PageSize).ToList();
        }
    }

    internal static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("dd MMM yyyy, HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Turns a state name such as AwaitingLearningPlan into "Awaiting learning plan".</summary>
    internal static string StateLabel(string state)
    {
        var words = System.Text.RegularExpressions.Regex.Replace(state, "(?<=[a-z])(?=[A-Z])", " ");
        return char.ToUpperInvariant(words[0]) + words[1..].ToLowerInvariant();
    }

    internal static string StateClass(string state) => state switch
    {
        "Completed" => "completed",
        "Failed" => "failed",
        _ => "active"
    };

    internal static string ShortType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unknown type";

        var typeName = value.Split(',', 2)[0];
        return typeName[(typeName.LastIndexOf('.') + 1)..];
    }
}
