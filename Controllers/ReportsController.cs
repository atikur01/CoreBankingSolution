using CoreBankingSolution.Models.Entities;
using CoreBankingSolution.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Controllers;

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
public class ReportsController : BankController
{
    private static string N(decimal v) => v.ToString("N2");
    private static string D(DateTime d) => d.ToString("dd-MMM-yyyy");

    public IActionResult Index() => View();

    private async Task<(DateTime from, DateTime to, DateTime bd)> Dates(DateTime? from, DateTime? to, int days = 30)
    {
        var bd = (await Db.Branches.MaxAsync(b => (DateTime?)b.BusinessDate)) ?? DateTime.Today;
        return ((from ?? bd.AddDays(-days)).Date, (to ?? bd).Date, bd);
    }

    private async Task<ReportVm> Base(string title, string action, bool f, bool t, bool br, DateTime from, DateTime to, int? branchId, string? extra, string? extraLabel = null, string? desc = null)
        => new() { Title = title, Action = action, ShowFrom = f, ShowTo = t, ShowBranch = br, From = from, To = to, BranchId = branchId, Extra = extra, ExtraLabel = extraLabel, Description = desc, Branches = br ? await BranchList(branchId) : null };

    private IActionResult Out(ReportVm vm, bool csv)
    {
        if (csv) return Csv(vm.Action, vm.Columns, vm.Rows.Select(r => (IEnumerable<object?>)r).Concat(vm.Totals != null ? [vm.Totals] : []));
        return View("Table", vm);
    }

    // ------------------------------------------------------------ General ledger
    public async Task<IActionResult> TrialBalance(DateTime? to, int? branchId, string? extra, bool csv = false)
    {
        var (_, t, _) = await Dates(null, to);
        var vm = await Base("Trial balance", nameof(TrialBalance), false, true, true, t, t, branchId, extra);
        var q = Svc<LedgerService>().PostedLines().Where(l => l.Voucher!.TxnDate <= t);
        if (branchId != null) q = q.Where(l => l.BranchId == branchId);
        var sums = await q.GroupBy(l => l.GlAccountId).Select(g => new { g.Key, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) }).ToListAsync();
        var gls = await Db.GlAccounts.AsNoTracking().OrderBy(g => g.Code).ToListAsync();
        vm.Columns = ["Code", "GL account", "Type", "Debit", "Credit", "Balance Dr/(Cr)"]; vm.Numeric = [false, false, false, true, true, true];
        decimal td = 0, tc = 0;
        foreach (var g in gls.Where(g => g.IsPosting))
        {
            var s = sums.FirstOrDefault(x => x.Key == g.Id); if (s == null) continue;
            vm.Rows.Add([g.Code, g.Name, g.Type.ToString(), N(s.Dr), N(s.Cr), N(s.Dr - s.Cr)]); td += s.Dr; tc += s.Cr;
        }
        vm.Totals = ["", "TOTAL", "", N(td), N(tc), N(td - tc)];
        vm.Description = "As on " + D(t) + (td == tc ? " - books are balanced ✔" : " - OUT OF BALANCE");
        return Out(vm, csv);
    }

    public async Task<IActionResult> GlStatement(DateTime? from, DateTime? to, int? branchId, string? extra, bool csv = false)
    {
        var (f, t, _) = await Dates(from, to);
        var vm = await Base("General ledger statement", nameof(GlStatement), true, true, true, f, t, branchId, extra, "GL code");
        vm.Columns = ["Date", "Voucher", "Branch", "Narration", "Debit", "Credit", "Running balance"]; vm.Numeric = [false, false, false, false, true, true, true];
        if (string.IsNullOrWhiteSpace(extra)) { vm.Description = "Enter a GL code (e.g. 1100)."; return Out(vm, false); }
        var gl = await Db.GlAccounts.FirstOrDefaultAsync(g => g.Code == extra.Trim());
        if (gl == null) { vm.Description = "GL not found."; return Out(vm, false); }
        var ids = gl.IsPosting ? [gl.Id] : await Db.GlAccounts.Where(g => g.Code.StartsWith(gl.Code)).Select(g => g.Id).ToListAsync();
        var q = Svc<LedgerService>().PostedLines().Where(l => ids.Contains(l.GlAccountId));
        if (branchId != null) q = q.Where(l => l.BranchId == branchId);
        var opening = await q.Where(l => l.Voucher!.TxnDate < f).SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0;
        var rows = await q.Where(l => l.Voucher!.TxnDate >= f && l.Voucher.TxnDate <= t).OrderBy(l => l.Voucher!.TxnDate).ThenBy(l => l.Id).Select(l => new { l.Voucher!.TxnDate, l.Voucher.VoucherNo, l.BranchId, l.Narration, l.Debit, l.Credit }).ToListAsync();
        var run = opening;
        vm.Rows.Add(["", "", "", "Opening balance", "", "", N(run)]);
        foreach (var r in rows) { run += r.Debit - r.Credit; vm.Rows.Add([D(r.TxnDate), r.VoucherNo, r.BranchId.ToString(), r.Narration ?? "", r.Debit == 0 ? "" : N(r.Debit), r.Credit == 0 ? "" : N(r.Credit), N(run)]); }
        vm.Totals = ["", "", "", "Closing / totals", N(rows.Sum(x => x.Debit)), N(rows.Sum(x => x.Credit)), N(run)];
        vm.Description = $"{gl.Code} - {gl.Name} (positive = debit balance)";
        return Out(vm, csv);
    }

    public async Task<IActionResult> DayBook(DateTime? from, int? branchId, string? extra, bool csv = false)
    {
        var (_, _, bd) = await Dates(null, null);
        var d = (from ?? bd).Date;
        var vm = await Base("Day book", nameof(DayBook), true, false, true, d, d, branchId, extra);
        var q = Svc<LedgerService>().PostedLines().Where(l => l.Voucher!.TxnDate == d);
        if (branchId != null) q = q.Where(l => l.BranchId == branchId);
        var rows = await q.OrderBy(l => l.Voucher!.VoucherNo).ThenBy(l => l.Id).Select(l => new { l.Voucher!.VoucherNo, Type = l.Voucher.Type.ToString(), l.BranchId, Gl = l.GlAccount!.Code + " " + l.GlAccount.Name, Acc = l.Account != null ? l.Account.AccountNo : "", l.Narration, l.Debit, l.Credit }).ToListAsync();
        vm.Columns = ["Voucher", "Type", "Br", "GL", "Account", "Narration", "Debit", "Credit"]; vm.Numeric = [false, false, false, false, false, false, true, true];
        vm.Rows = rows.Select(r => new[] { r.VoucherNo, r.Type, r.BranchId.ToString(), r.Gl, r.Acc, r.Narration ?? "", r.Debit == 0 ? "" : N(r.Debit), r.Credit == 0 ? "" : N(r.Credit) }).ToList();
        vm.Totals = ["", "", "", "", "", "Total", N(rows.Sum(x => x.Debit)), N(rows.Sum(x => x.Credit))];
        return Out(vm, csv);
    }

    public async Task<IActionResult> BalanceSheet(DateTime? to, int? branchId, string? extra, bool csv = false)
    {
        var (_, t, _) = await Dates(null, to);
        var vm = await Base("Balance sheet", nameof(BalanceSheet), false, true, true, t, t, branchId, extra);
        var q = Svc<LedgerService>().PostedLines().Where(l => l.Voucher!.TxnDate <= t);
        if (branchId != null) q = q.Where(l => l.BranchId == branchId);
        var sums = await q.GroupBy(l => new { l.GlAccountId }).Select(g => new { g.Key.GlAccountId, Net = g.Sum(x => x.Debit - x.Credit) }).ToListAsync();
        var gls = await Db.GlAccounts.AsNoTracking().OrderBy(g => g.Code).ToListAsync();
        decimal Sum(Func<GlAccount, bool> f) => sums.Where(s => f(gls.First(g => g.Id == s.GlAccountId))).Sum(s => s.Net);
        vm.Columns = ["Code", "Particulars", "Amount"]; vm.Numeric = [false, false, true];
        void Section(string title, GlType type, int sign)
        {
            vm.Rows.Add(["", title.ToUpper(), ""]);
            foreach (var g in gls.Where(x => x.IsPosting && x.Type == type)) { var s = sums.FirstOrDefault(z => z.GlAccountId == g.Id); if (s != null && s.Net != 0) vm.Rows.Add([g.Code, g.Name, N(sign * s.Net)]); }
            vm.Rows.Add(["", "Total " + title, N(sign * Sum(g => g.Type == type))]);
        }
        Section("Assets", GlType.Asset, 1); Section("Liabilities", GlType.Liability, -1); Section("Equity", GlType.Equity, -1);
        var pl = -Sum(g => g.Type is GlType.Income or GlType.Expense);
        vm.Rows.Add(["", "Profit / (loss) for the period not yet transferred", N(pl)]);
        var assets = Sum(g => g.Type == GlType.Asset); var le = -Sum(g => g.Type is GlType.Liability or GlType.Equity) + pl;
        vm.Totals = ["", $"Assets {N(assets)}  vs  Liabilities + Equity + P&L {N(le)}", N(assets - le)];
        vm.Description = "As on " + D(t) + (assets == le ? " - balanced ✔" : " - difference shown in the last row");
        return Out(vm, csv);
    }

    public async Task<IActionResult> IncomeStatement(DateTime? from, DateTime? to, int? branchId, string? extra, bool csv = false)
    {
        var (f, t, _) = await Dates(from, to, 365);
        var vm = await Base("Income statement (profit & loss)", nameof(IncomeStatement), true, true, true, f, t, branchId, extra);
        var q = Svc<LedgerService>().PostedLines().Where(l => l.Voucher!.TxnDate >= f && l.Voucher.TxnDate <= t && !(l.Voucher.Type == VoucherType.Eod && l.Voucher.Description.StartsWith("Year-end transfer")));
        if (branchId != null) q = q.Where(l => l.BranchId == branchId);
        var sums = await q.Where(l => l.GlAccount!.Type == GlType.Income || l.GlAccount.Type == GlType.Expense).GroupBy(l => new { l.GlAccount!.Code, l.GlAccount.Name, l.GlAccount.Type }).Select(g => new { g.Key.Code, g.Key.Name, g.Key.Type, Net = g.Sum(x => x.Credit - x.Debit) }).OrderBy(x => x.Code).ToListAsync();
        vm.Columns = ["Code", "Particulars", "Amount"]; vm.Numeric = [false, false, true];
        vm.Rows.Add(["", "INCOME", ""]); foreach (var s in sums.Where(x => x.Type == GlType.Income)) vm.Rows.Add([s.Code, s.Name, N(s.Net)]);
        var inc = sums.Where(x => x.Type == GlType.Income).Sum(x => x.Net); vm.Rows.Add(["", "Total income", N(inc)]);
        vm.Rows.Add(["", "EXPENSES", ""]); foreach (var s in sums.Where(x => x.Type == GlType.Expense)) vm.Rows.Add([s.Code, s.Name, N(-s.Net)]);
        var exp = -sums.Where(x => x.Type == GlType.Expense).Sum(x => x.Net); vm.Rows.Add(["", "Total expenses", N(exp)]);
        vm.Totals = ["", "NET PROFIT / (LOSS)", N(inc - exp)];
        return Out(vm, csv);
    }

    public async Task<IActionResult> InterBranch(DateTime? to, int? branchId, string? extra, bool csv = false)
    {
        var (_, t, _) = await Dates(null, to);
        var vm = await Base("Inter-branch reconciliation", nameof(InterBranch), false, true, false, t, t, null, extra, desc: "Balance of the inter-branch adjustment GL by branch. The bank-wide total must be zero; branch balances show the amount each branch owes / is owed.");
        var ib = await Svc<ParamService>().GetAsync("InterBranchGl");
        var rows = await Svc<LedgerService>().PostedLines().Where(l => l.GlAccount!.Code == ib && l.Voucher!.TxnDate <= t).GroupBy(l => l.BranchId).Select(g => new { g.Key, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) }).ToListAsync();
        var br = await Db.Branches.AsNoTracking().ToListAsync();
        vm.Columns = ["Branch", "Debit (receivable)", "Credit (payable)", "Net Dr/(Cr)"]; vm.Numeric = [false, true, true, true];
        vm.Rows = rows.Select(r => new[] { br.First(b => b.Id == r.Key).ToString(), N(r.Dr), N(r.Cr), N(r.Dr - r.Cr) }).ToList();
        vm.Totals = ["BANK-WIDE", N(rows.Sum(x => x.Dr)), N(rows.Sum(x => x.Cr)), N(rows.Sum(x => x.Dr - x.Cr)) + (rows.Sum(x => x.Dr - x.Cr) == 0 ? "  ✔ reconciled" : "  ✗ UNRECONCILED")];
        return Out(vm, csv);
    }

    // ------------------------------------------------------------ Deposits
    public async Task<IActionResult> Deposits(int? branchId, string? extra, bool csv = false)
    {
        var vm = await Base("Deposit portfolio", nameof(Deposits), false, false, true, DateTime.Today, DateTime.Today, branchId, extra, "Status (Active/Dormant/…)");
        var q = Db.Accounts.AsNoTracking().Include(a => a.Product).Include(a => a.Customer).Include(a => a.Branch).Where(a => a.Status != AccountStatus.Closed);
        if (branchId != null) q = q.Where(a => a.BranchId == branchId);
        if (Enum.TryParse<AccountStatus>(extra, true, out var st)) q = q.Where(a => a.Status == st);
        var rows = await q.OrderBy(a => a.Product!.Code).ThenBy(a => a.AccountNo).Take(5000).ToListAsync();
        vm.Columns = ["Account", "Title", "CIF", "Product", "Branch", "Status", "Rate %", "Accrued int.", "Balance"]; vm.Numeric = [false, false, false, false, false, false, true, true, true];
        vm.Rows = rows.Select(a => new[] { a.AccountNo, a.Title, a.Customer!.CifNo, a.Product!.Name, a.Branch!.Code, a.Status.ToString(), a.Product.IsInterestBearing ? (a.InterestRate > 0 ? a.InterestRate : a.Product.InterestRate).ToString("0.##") : "", N(a.AccruedInterest), N(a.Balance) }).ToList();
        vm.Totals = ["", "", "", "", "", "", "", "Total", N(rows.Sum(a => a.Balance))];
        return Out(vm, csv);
    }

    public async Task<IActionResult> Dormant(int? branchId, string? extra, bool csv = false)
    {
        var vm = await Base("Dormant & inoperative (unclaimed) accounts", nameof(Dormant), false, false, true, DateTime.Today, DateTime.Today, branchId, extra);
        var q = Db.Accounts.AsNoTracking().Include(a => a.Product).Include(a => a.Customer).Where(a => a.Status == AccountStatus.Dormant || a.Status == AccountStatus.Inoperative);
        if (branchId != null) q = q.Where(a => a.BranchId == branchId);
        var rows = await q.OrderBy(a => a.LastTxnDate).ToListAsync();
        vm.Columns = ["Account", "Title", "Customer mobile", "Product", "Status", "Last txn", "Balance"]; vm.Numeric = [false, false, false, false, false, false, true];
        vm.Rows = rows.Select(a => new[] { a.AccountNo, a.Title, a.Customer!.Mobile, a.Product!.Name, a.Status.ToString(), a.LastTxnDate == null ? "-" : D(a.LastTxnDate.Value), N(a.Balance) }).ToList();
        vm.Totals = ["", "", "", "", "", "Total", N(rows.Sum(a => a.Balance))];
        return Out(vm, csv);
    }

    public async Task<IActionResult> Maturity(DateTime? to, int? branchId, string? extra, bool csv = false)
    {
        var bd = (await Db.Branches.MaxAsync(b => (DateTime?)b.BusinessDate)) ?? DateTime.Today;
        var t = (to ?? bd.AddDays(30)).Date;
        var vm = await Base("FDR / DPS maturity schedule", nameof(Maturity), false, true, true, t, t, branchId, extra);
        var q = Db.Accounts.AsNoTracking().Include(a => a.Product).Include(a => a.Customer).Where(a => (a.Product!.Category == ProductCategory.FDR || a.Product.Category == ProductCategory.DPS) && (a.Status == AccountStatus.Active || a.Status == AccountStatus.Matured) && a.MaturityDate <= t);
        if (branchId != null) q = q.Where(a => a.BranchId == branchId);
        var rows = await q.OrderBy(a => a.MaturityDate).ToListAsync();
        vm.Columns = ["Account", "Customer", "Product", "Opened", "Maturity", "Rate %", "Principal/Balance", "Accrued interest"]; vm.Numeric = [false, false, false, false, false, true, true, true];
        vm.Rows = rows.Select(a => new[] { a.AccountNo, a.Title, a.Product!.Name, D(a.OpenedOn), D(a.MaturityDate!.Value), a.InterestRate.ToString("0.##"), N(a.Balance), N(a.AccruedInterest) }).ToList();
        vm.Totals = ["", "", "", "", "", "Total", N(rows.Sum(a => a.Balance)), N(rows.Sum(a => a.AccruedInterest))];
        vm.Description = "Maturing on or before " + D(t);
        return Out(vm, csv);
    }

    // ------------------------------------------------------------ Loans
    public async Task<IActionResult> LoanPortfolio(int? branchId, string? extra, bool csv = false)
    {
        var vm = await Base("Loan portfolio, classification & provision", nameof(LoanPortfolio), false, false, true, DateTime.Today, DateTime.Today, branchId, extra, "Classification (Standard/SMA/SubStandard/Doubtful/BadLoss)");
        var q = Db.Loans.AsNoTracking().Include(l => l.Customer).Include(l => l.Product).Where(l => l.Status == LoanStatus.Disbursed);
        if (branchId != null) q = q.Where(l => l.BranchId == branchId);
        if (Enum.TryParse<LoanClass>(extra, true, out var cl)) q = q.Where(l => l.Classification == cl);
        var rows = await q.OrderByDescending(l => l.OverdueDays).ToListAsync();
        vm.Columns = ["Loan", "Borrower", "Product", "Disbursed", "Principal", "Outstanding", "Overdue amt", "Days", "Class", "Provision"]; vm.Numeric = [false, false, false, false, true, true, true, true, false, true];
        vm.Rows = rows.Select(l => new[] { l.LoanNo, l.Customer!.Name, l.Product!.Name, l.DisbursedOn == null ? "" : D(l.DisbursedOn.Value), N(l.Principal), N(l.OutstandingPrincipal), N(l.OverdueAmount), l.OverdueDays.ToString(), l.Classification.ToString(), N(l.ProvisionHeld) }).ToList();
        vm.Totals = ["", "", "", "Total", N(rows.Sum(l => l.Principal)), N(rows.Sum(l => l.OutstandingPrincipal)), N(rows.Sum(l => l.OverdueAmount)), "", "", N(rows.Sum(l => l.ProvisionHeld))];
        vm.Description = "Classification by overdue days: <60 Standard · 60-89 SMA · 90-269 Sub-standard · 270-359 Doubtful · ≥360 Bad/Loss. Provision: 1% / 1% / 20% / 50% / 100% of outstanding (simplified; collateral not netted).";
        return Out(vm, csv);
    }

    // ------------------------------------------------------------ Control & compliance
    public async Task<IActionResult> Unauthorized(DateTime? from, DateTime? to, int? branchId, string? extra, bool csv = false)
    {
        var (f, t, _) = await Dates(from, to, 7);
        var vm = await Base("Unauthorized / rejected transactions (branch-wise)", nameof(Unauthorized), true, true, true, f, t, branchId, extra);
        var q = Db.Vouchers.AsNoTracking().Include(v => v.Branch).Where(v => (v.Status == VoucherStatus.Pending || v.Status == VoucherStatus.Rejected) && v.TxnDate >= f && v.TxnDate <= t);
        if (branchId != null) q = q.Where(v => v.BranchId == branchId);
        var rows = await q.OrderBy(v => v.BranchId).ThenBy(v => v.Id).ToListAsync();
        vm.Columns = ["Branch", "Voucher", "Date", "Type", "Description", "Amount", "Status", "Maker", "Reason"]; vm.Numeric = [false, false, false, false, false, true, false, false, false];
        vm.Rows = rows.Select(v => new[] { v.Branch!.Code, v.VoucherNo, D(v.TxnDate), v.Type.ToString(), v.Description, N(v.TotalAmount), v.Status.ToString(), v.MakerId, v.RejectReason ?? "" }).ToList();
        return Out(vm, csv);
    }

    public async Task<IActionResult> Ctr(DateTime? from, DateTime? to, int? branchId, string? extra, bool csv = false)
    {
        var (f, t, _) = await Dates(from, to, 7);
        var thr = await Svc<ParamService>().DecAsync("CtrThreshold");
        var vm = await Base("Cash Transaction Report (CTR)", nameof(Ctr), true, true, true, f, t, branchId, extra, desc: $"Cash deposits / withdrawals of {thr:N0} or more (threshold set in Bank Parameters: CtrThreshold).");
        var q = Db.Vouchers.AsNoTracking().Include(v => v.Branch).Include(v => v.Lines).ThenInclude(l => l.Account).Where(v => v.Status == VoucherStatus.Authorized && v.TxnDate >= f && v.TxnDate <= t && (v.Type == VoucherType.CashDeposit || v.Type == VoucherType.CashWithdrawal || v.Type == VoucherType.AccountOpening) && v.TotalAmount >= thr);
        if (branchId != null) q = q.Where(v => v.BranchId == branchId);
        var rows = await q.OrderBy(v => v.TxnDate).ToListAsync();
        vm.Columns = ["Date", "Voucher", "Branch", "Type", "Account", "Account title", "Party", "Amount"]; vm.Numeric = [false, false, false, false, false, false, false, true];
        vm.Rows = rows.Select(v => { var a = v.Lines.FirstOrDefault(l => l.Account != null)?.Account; return new[] { D(v.TxnDate), v.VoucherNo, v.Branch!.Code, v.Type.ToString(), a?.AccountNo ?? "", a?.Title ?? "", v.CounterParty ?? "", N(v.TotalAmount) }; }).ToList();
        vm.Totals = ["", "", "", "", "", "", "Total", N(rows.Sum(v => v.TotalAmount))];
        return Out(vm, csv);
    }

    public async Task<IActionResult> Cheques(DateTime? from, DateTime? to, int? branchId, string? extra, bool csv = false)
    {
        var (f, t, _) = await Dates(from, to, 90);
        var vm = await Base("Cheque return & stop-payment register", nameof(Cheques), true, true, true, f, t, branchId, extra);
        var rets = await Db.ChequeReturns.AsNoTracking().Include(r => r.Account).Where(r => r.ReturnDate >= f && r.ReturnDate <= t && (branchId == null || r.Account!.BranchId == branchId)).ToListAsync();
        var stops = await Db.StopPayments.AsNoTracking().Include(r => r.Account).Where(r => r.InstructionTime >= f && r.InstructionTime < t.AddDays(1) && (branchId == null || r.Account!.BranchId == branchId)).ToListAsync();
        vm.Columns = ["Register", "Date", "Account", "Cheque", "Amount", "Reason", "Charge / state"]; vm.Numeric = [false, false, false, false, true, false, false];
        vm.Rows = rets.Select(r => new[] { "RETURN", D(r.ReturnDate), r.Account!.AccountNo, r.ChequeNo, N(r.Amount), r.Reason, N(r.ChargeApplied) })
            .Concat(stops.Select(s => new[] { "STOP PAYMENT", s.InstructionTime.ToString("dd-MMM-yyyy HH:mm"), s.Account!.AccountNo, s.ChequeNo, N(s.Amount), s.Reason, s.IsActive ? "Active" : "Released" })).ToList();
        return Out(vm, csv);
    }

    public async Task<IActionResult> UserActivity(DateTime? from, DateTime? to, int? branchId, string? extra, bool csv = false)
    {
        var (f, t, _) = await Dates(from, to, 7);
        var vm = await Base("User-wise activity summary", nameof(UserActivity), true, true, true, f, t, branchId, extra);
        var q = Db.Vouchers.AsNoTracking().Where(v => v.TxnDate >= f && v.TxnDate <= t);
        if (branchId != null) q = q.Where(v => v.BranchId == branchId);
        var made = await q.GroupBy(v => v.MakerId).Select(g => new { U = g.Key, N = g.Count(), A = g.Sum(x => x.TotalAmount) }).ToListAsync();
        var chk = await q.Where(v => v.CheckerId != null).GroupBy(v => v.CheckerId!).Select(g => new { U = g.Key, N = g.Count() }).ToListAsync();
        var users = made.Select(m => m.U).Union(chk.Select(c => c.U)).Distinct().OrderBy(u => u);
        vm.Columns = ["User", "Vouchers made", "Amount made", "Vouchers authorized"]; vm.Numeric = [false, true, true, true];
        vm.Rows = users.Select(u => new[] { u, (made.FirstOrDefault(m => m.U == u)?.N ?? 0).ToString(), N(made.FirstOrDefault(m => m.U == u)?.A ?? 0), (chk.FirstOrDefault(c => c.U == u)?.N ?? 0).ToString() }).ToList();
        return Out(vm, csv);
    }

    public async Task<IActionResult> Rates(bool csv = false)
    {
        var vm = await Base("Product interest rates in force", nameof(Rates), false, false, false, DateTime.Today, DateTime.Today, null, null);
        var rows = await Db.Products.AsNoTracking().OrderBy(p => p.Code).ToListAsync();
        vm.Columns = ["Code", "Product", "Category", "Default rate %", "Overdraft/penal %", "Premature %", "Interest payment"]; vm.Numeric = [false, false, false, true, true, true, false];
        vm.Rows = rows.Select(p => new[] { p.Code, p.Name, p.Category.ToString(), p.InterestRate.ToString("0.####"), p.OverdraftRate.ToString("0.####"), p.PrematureRate.ToString("0.####"), p.PayFrequency.ToString() }).ToList();
        return Out(vm, csv);
    }
}
