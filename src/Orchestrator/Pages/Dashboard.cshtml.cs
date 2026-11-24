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
    internal IReadOnlyList<SagaRow> Sagas { get; private set; } = [];
    internal IReadOnlyList<DeadLetterRow> DeadLetters { get; private set; } = [];
    internal DeadLetterDetails? SelectedError { get; private set; }
    internal bool HasNextPage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellation)
    {
        Response.Headers.CacheControl = "no-store";
        Tab = ShowingErrors ? "errors" : "sagas";
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
            var rows = await store.GetSagasAsync(Query, Status, offset, PageSize + 1, cancellation);
            HasNextPage = rows.Count > PageSize;
            Sagas = rows.Take(PageSize).ToList();
        }
    }

    internal static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("dd MMM yyyy, HH:mm:ss", CultureInfo.InvariantCulture);

    internal static string StageLabel(RegistrationStage stage) => stage switch
    {
        RegistrationStage.New => "New",
        RegistrationStage.AwaitingLearner => "Awaiting learner",
        RegistrationStage.AwaitingLearningPlan => "Awaiting learning plan",
        RegistrationStage.AwaitingAssessment => "Awaiting assessment",
        RegistrationStage.Completed => "Completed",
        RegistrationStage.Failed => "Failed",
        _ => $"Stage {(int)stage}"
    };

    internal static string StageClass(RegistrationStage stage) => stage switch
    {
        RegistrationStage.Completed => "completed",
        RegistrationStage.Failed => "failed",
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
