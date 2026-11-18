using CoreBanking.Web.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Web.Features.Reports;

public class ReportVm
{
    public string Title { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Description { get; set; }
    public bool ShowFrom, ShowTo, ShowBranch;
    public string? ExtraLabel;
    public DateTime From, To;
    public int? BranchId;
    public string? Extra;
    public SelectList? Branches;
    public string[] Columns { get; set; } = [];
    public bool[] Numeric { get; set; } = [];
    public List<string[]> Rows { get; set; } = [];
    public string[]? Totals { get; set; }
}

[Authorize(Roles = Roles.Makers)]
public class ReportsController(ICurrentUser me, LookupService lookups, ReportService reports) : BankController(me, lookups)
{
    private sealed record Meta(string Title, bool ShowFrom, bool ShowTo, bool ShowBranch, string? ExtraLabel = null);

    public IActionResult Index() => View();

    private async Task<IActionResult> Render(string action, Meta meta, Func<ReportQuery, Task<ReportTable>> build, DateTime? from, DateTime? to, int? branchId, string? extra, bool csv)
    {
        var table = await build(new ReportQuery(from, to, branchId, extra));
        var vm = new ReportVm
        {
            Title = meta.Title, Action = action, ShowFrom = meta.ShowFrom, ShowTo = meta.ShowTo, ShowBranch = meta.ShowBranch, ExtraLabel = meta.ExtraLabel,
            From = table.From, To = table.To, BranchId = branchId, Extra = extra, Description = table.Description,
            Branches = meta.ShowBranch ? await BranchList(branchId) : null,
            Columns = table.Columns, Numeric = table.Numeric, Rows = table.Rows, Totals = table.Totals
        };
        if (csv && table.AllowExport)
            return Csv(action, vm.Columns, vm.Rows.Select(r => (IEnumerable<object?>)r).Concat(vm.Totals != null ? [vm.Totals] : []));
        return View("Table", vm);
    }

    public Task<IActionResult> TrialBalance(DateTime? to, int? branchId, string? extra, bool csv = false) =>
        Render(nameof(TrialBalance), new("Trial balance", false, true, true), reports.TrialBalanceAsync, null, to, branchId, extra, csv);

    public Task<IActionResult> GlStatement(DateTime? from, DateTime? to, int? branchId, string? extra, bool csv = false) =>
        Render(nameof(GlStatement), new("General ledger statement", true, true, true, "GL code"), reports.GlStatementAsync, from, to, branchId, extra, csv);

    public Task<IActionResult> DayBook(DateTime? from, int? branchId, string? extra, bool csv = false) =>
        Render(nameof(DayBook), new("Day book", true, false, true), reports.DayBookAsync, from, null, branchId, extra, csv);

    public Task<IActionResult> BalanceSheet(DateTime? to, int? branchId, string? extra, bool csv = false) =>
        Render(nameof(BalanceSheet), new("Balance sheet", false, true, true), reports.BalanceSheetAsync, null, to, branchId, extra, csv);

    public Task<IActionResult> IncomeStatement(DateTime? from, DateTime? to, int? branchId, string? extra, bool csv = false) =>
        Render(nameof(IncomeStatement), new("Income statement (profit & loss)", true, true, true), reports.IncomeStatementAsync, from, to, branchId, extra, csv);

    public Task<IActionResult> InterBranch(DateTime? to, int? branchId, string? extra, bool csv = false) =>
        Render(nameof(InterBranch), new("Inter-branch reconciliation", false, true, false), reports.InterBranchAsync, null, to, null, extra, csv);

    public Task<IActionResult> Deposits(int? branchId, string? extra, bool csv = false) =>
        Render(nameof(Deposits), new("Deposit portfolio", false, false, true, "Status (Active/Dormant/…)"), reports.DepositsAsync, null, null, branchId, extra, csv);

    public Task<IActionResult> Dormant(int? branchId, string? extra, bool csv = false) =>
        Render(nameof(Dormant), new("Dormant & inoperative (unclaimed) accounts", false, false, true), reports.DormantAsync, null, null, branchId, extra, csv);

    public Task<IActionResult> Maturity(DateTime? to, int? branchId, string? extra, bool csv = false) =>
        Render(nameof(Maturity), new("FDR / DPS maturity schedule", false, true, true), reports.MaturityAsync, null, to, branchId, extra, csv);

    public Task<IActionResult> LoanPortfolio(int? branchId, string? extra, bool csv = false) =>
        Render(nameof(LoanPortfolio), new("Loan portfolio, classification & provision", false, false, true, "Classification (Standard/SMA/SubStandard/Doubtful/BadLoss)"),
            reports.LoanPortfolioAsync, null, null, branchId, extra, csv);

    public Task<IActionResult> Unauthorized(DateTime? from, DateTime? to, int? branchId, string? extra, bool csv = false) =>
        Render(nameof(Unauthorized), new("Unauthorized / rejected transactions (branch-wise)", true, true, true), reports.UnauthorizedAsync, from, to, branchId, extra, csv);

    public Task<IActionResult> Ctr(DateTime? from, DateTime? to, int? branchId, string? extra, bool csv = false) =>
        Render(nameof(Ctr), new("Cash Transaction Report (CTR)", true, true, true), reports.CtrAsync, from, to, branchId, extra, csv);

    public Task<IActionResult> Cheques(DateTime? from, DateTime? to, int? branchId, string? extra, bool csv = false) =>
        Render(nameof(Cheques), new("Cheque return & stop-payment register", true, true, true), reports.ChequesAsync, from, to, branchId, extra, csv);

    public Task<IActionResult> UserActivity(DateTime? from, DateTime? to, int? branchId, string? extra, bool csv = false) =>
        Render(nameof(UserActivity), new("User-wise activity summary", true, true, true), reports.UserActivityAsync, from, to, branchId, extra, csv);

    public Task<IActionResult> Rates(bool csv = false) =>
        Render(nameof(Rates), new("Product interest rates in force", false, false, false), _ => reports.RatesAsync(), null, null, null, null, csv);
}
