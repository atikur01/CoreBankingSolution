namespace CoreBanking.Application.Features.Reports;

public record ReportQuery(DateTime? From, DateTime? To, int? BranchId, string? Extra);

public class ReportTable
{
    public string? Description { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public string[] Columns { get; set; } = [];
    public bool[] Numeric { get; set; } = [];
    public List<string[]> Rows { get; set; } = [];
    public string[]? Totals { get; set; }
    public bool AllowExport { get; set; } = true;
}

public class ReportService(IApplicationDbContext db, LedgerService ledger, ParamService prm)
{
    private static string N(decimal v) => v.ToString("N2");
    private static string D(DateTime d) => d.ToString("dd-MMM-yyyy");

    private async Task<(DateTime from, DateTime to, DateTime businessDate)> DatesAsync(DateTime? from, DateTime? to, int days = 30)
    {
        var bd = await db.Branches.MaxAsync(b => (DateTime?)b.BusinessDate) ?? DateTime.Today;
        return ((from ?? bd.AddDays(-days)).Date, (to ?? bd).Date, bd);
    }

    private async Task<List<int>> DescendantIdsAsync(int rootId)
    {
        var nodes = await db.GlAccounts.AsNoTracking().Select(g => new { g.Id, g.ParentId }).ToListAsync();
        var children = nodes.ToLookup(n => n.ParentId, n => n.Id);
        var result = new List<int> { rootId };
        for (var i = 0; i < result.Count; i++) result.AddRange(children[result[i]]);
        return result;
    }

    public async Task<DateTime> BusinessDateAsync() => (await DatesAsync(null, null)).businessDate;

    public async Task<ReportTable> TrialBalanceAsync(ReportQuery query)
    {
        var (_, t, _) = await DatesAsync(null, query.To);
        var q = ledger.PostedLines().Where(l => l.Voucher!.TxnDate <= t);
        if (query.BranchId != null) q = q.Where(l => l.BranchId == query.BranchId);
        var sums = await q.GroupBy(l => l.GlAccountId).Select(g => new { g.Key, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) }).ToListAsync();
        var gls = await db.GlAccounts.AsNoTracking().OrderBy(g => g.Code).ToListAsync();

        var table = new ReportTable
        {
            From = t, To = t,
            Columns = ["Code", "GL account", "Type", "Debit", "Credit", "Balance Dr/(Cr)"],
            Numeric = [false, false, false, true, true, true]
        };
        decimal td = 0, tc = 0;
        foreach (var g in gls.Where(g => g.IsPosting))
        {
            var s = sums.FirstOrDefault(x => x.Key == g.Id);
            if (s == null) continue;
            table.Rows.Add([g.Code, g.Name, g.Type.ToString(), N(s.Dr), N(s.Cr), N(s.Dr - s.Cr)]);
            td += s.Dr; tc += s.Cr;
        }
        table.Totals = ["", "TOTAL", "", N(td), N(tc), N(td - tc)];
        table.Description = "As on " + D(t) + (td == tc ? " - books are balanced ✔" : " - OUT OF BALANCE");
        return table;
    }

    public async Task<ReportTable> GlStatementAsync(ReportQuery query)
    {
        var (f, t, _) = await DatesAsync(query.From, query.To);
        var table = new ReportTable
        {
            From = f, To = t,
            Columns = ["Date", "Voucher", "Branch", "Narration", "Debit", "Credit", "Running balance"],
            Numeric = [false, false, false, false, true, true, true],
            AllowExport = false
        };
        if (string.IsNullOrWhiteSpace(query.Extra)) { table.Description = "Enter a GL code (e.g. 1100)."; return table; }
        var code = query.Extra.Trim();
        var gl = await db.GlAccounts.FirstOrDefaultAsync(g => g.Code == code);
        if (gl == null) { table.Description = "GL not found."; return table; }

        var ids = gl.IsPosting ? [gl.Id] : await DescendantIdsAsync(gl.Id);
        var q = ledger.PostedLines().Where(l => ids.Contains(l.GlAccountId));
        if (query.BranchId != null) q = q.Where(l => l.BranchId == query.BranchId);
        var opening = await q.Where(l => l.Voucher!.TxnDate < f).SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0;
        var rows = await q.Where(l => l.Voucher!.TxnDate >= f && l.Voucher.TxnDate <= t).OrderBy(l => l.Voucher!.TxnDate).ThenBy(l => l.Id)
            .Select(l => new { l.Voucher!.TxnDate, l.Voucher.VoucherNo, l.BranchId, l.Narration, l.Debit, l.Credit }).ToListAsync();

        var run = opening;
        table.AllowExport = true;
        table.Rows.Add(["", "", "", "Opening balance", "", "", N(run)]);
        foreach (var r in rows)
        {
            run += r.Debit - r.Credit;
            table.Rows.Add([D(r.TxnDate), r.VoucherNo, r.BranchId.ToString(), r.Narration ?? "", r.Debit == 0 ? "" : N(r.Debit), r.Credit == 0 ? "" : N(r.Credit), N(run)]);
        }
        table.Totals = ["", "", "", "Closing / totals", N(rows.Sum(x => x.Debit)), N(rows.Sum(x => x.Credit)), N(run)];
        table.Description = $"{gl.Code} - {gl.Name} (positive = debit balance)";
        return table;
    }

    public async Task<ReportTable> DayBookAsync(ReportQuery query)
    {
        var (_, _, bd) = await DatesAsync(null, null);
        var d = (query.From ?? bd).Date;
        var q = ledger.PostedLines().Where(l => l.Voucher!.TxnDate == d);
        if (query.BranchId != null) q = q.Where(l => l.BranchId == query.BranchId);
        var rows = await q.OrderBy(l => l.Voucher!.VoucherNo).ThenBy(l => l.Id)
            .Select(l => new
            {
                l.Voucher!.VoucherNo, Type = l.Voucher.Type.ToString(), l.BranchId, Gl = l.GlAccount!.Code + " " + l.GlAccount.Name,
                Acc = l.Account != null ? l.Account.AccountNo : "", l.Narration, l.Debit, l.Credit
            }).ToListAsync();

        return new ReportTable
        {
            From = d, To = d,
            Columns = ["Voucher", "Type", "Br", "GL", "Account", "Narration", "Debit", "Credit"],
            Numeric = [false, false, false, false, false, false, true, true],
            Rows = rows.Select(r => new[] { r.VoucherNo, r.Type, r.BranchId.ToString(), r.Gl, r.Acc, r.Narration ?? "", r.Debit == 0 ? "" : N(r.Debit), r.Credit == 0 ? "" : N(r.Credit) }).ToList(),
            Totals = ["", "", "", "", "", "Total", N(rows.Sum(x => x.Debit)), N(rows.Sum(x => x.Credit))]
        };
    }

    public async Task<ReportTable> BalanceSheetAsync(ReportQuery query)
    {
        var (_, t, _) = await DatesAsync(null, query.To);
        var q = ledger.PostedLines().Where(l => l.Voucher!.TxnDate <= t);
        if (query.BranchId != null) q = q.Where(l => l.BranchId == query.BranchId);
        var sums = await q.GroupBy(l => new { l.GlAccountId }).Select(g => new { g.Key.GlAccountId, Net = g.Sum(x => x.Debit - x.Credit) }).ToListAsync();
        var gls = await db.GlAccounts.AsNoTracking().OrderBy(g => g.Code).ToListAsync();
        var typeById = gls.ToDictionary(g => g.Id, g => g.Type);
        decimal Sum(Func<GlType, bool> f) => sums.Where(s => f(typeById[s.GlAccountId])).Sum(s => s.Net);

        var table = new ReportTable { From = t, To = t, Columns = ["Code", "Particulars", "Amount"], Numeric = [false, false, true] };
        void Section(string title, GlType type, int sign)
        {
            table.Rows.Add(["", title.ToUpper(), ""]);
            foreach (var g in gls.Where(x => x.IsPosting && x.Type == type))
            {
                var s = sums.FirstOrDefault(z => z.GlAccountId == g.Id);
                if (s != null && s.Net != 0) table.Rows.Add([g.Code, g.Name, N(sign * s.Net)]);
            }
            table.Rows.Add(["", "Total " + title, N(sign * Sum(x => x == type))]);
        }
        Section("Assets", GlType.Asset, 1);
        Section("Liabilities", GlType.Liability, -1);
        Section("Equity", GlType.Equity, -1);
        var pl = -Sum(x => x is GlType.Income or GlType.Expense);
        table.Rows.Add(["", "Profit / (loss) for the period not yet transferred", N(pl)]);
        var assets = Sum(x => x == GlType.Asset);
        var liabilitiesAndEquity = -Sum(x => x is GlType.Liability or GlType.Equity) + pl;
        table.Totals = ["", $"Assets {N(assets)}  vs  Liabilities + Equity + P&L {N(liabilitiesAndEquity)}", N(assets - liabilitiesAndEquity)];
        table.Description = "As on " + D(t) + (assets == liabilitiesAndEquity ? " - balanced ✔" : " - difference shown in the last row");
        return table;
    }

    public async Task<ReportTable> IncomeStatementAsync(ReportQuery query)
    {
        var (f, t, _) = await DatesAsync(query.From, query.To, 365);
        var q = ledger.PostedLines().Where(l => l.Voucher!.TxnDate >= f && l.Voucher.TxnDate <= t
            && !(l.Voucher.Type == VoucherType.Eod && l.Voucher.Description.StartsWith("Year-end transfer")));
        if (query.BranchId != null) q = q.Where(l => l.BranchId == query.BranchId);
        var sums = await q.Where(l => l.GlAccount!.Type == GlType.Income || l.GlAccount.Type == GlType.Expense)
            .GroupBy(l => new { l.GlAccount!.Code, l.GlAccount.Name, l.GlAccount.Type })
            .Select(g => new { g.Key.Code, g.Key.Name, g.Key.Type, Net = g.Sum(x => x.Credit - x.Debit) }).OrderBy(x => x.Code).ToListAsync();

        var table = new ReportTable { From = f, To = t, Columns = ["Code", "Particulars", "Amount"], Numeric = [false, false, true] };
        table.Rows.Add(["", "INCOME", ""]);
        foreach (var s in sums.Where(x => x.Type == GlType.Income)) table.Rows.Add([s.Code, s.Name, N(s.Net)]);
        var income = sums.Where(x => x.Type == GlType.Income).Sum(x => x.Net);
        table.Rows.Add(["", "Total income", N(income)]);
        table.Rows.Add(["", "EXPENSES", ""]);
        foreach (var s in sums.Where(x => x.Type == GlType.Expense)) table.Rows.Add([s.Code, s.Name, N(-s.Net)]);
        var expense = -sums.Where(x => x.Type == GlType.Expense).Sum(x => x.Net);
        table.Rows.Add(["", "Total expenses", N(expense)]);
        table.Totals = ["", "NET PROFIT / (LOSS)", N(income - expense)];
        return table;
    }

    public async Task<ReportTable> InterBranchAsync(ReportQuery query)
    {
        var (_, t, _) = await DatesAsync(null, query.To);
        var interBranchGl = await prm.GetAsync("InterBranchGl");
        var rows = await ledger.PostedLines().Where(l => l.GlAccount!.Code == interBranchGl && l.Voucher!.TxnDate <= t)
            .GroupBy(l => l.BranchId).Select(g => new { g.Key, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) }).ToListAsync();
        var branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id);
        var net = rows.Sum(x => x.Dr - x.Cr);
        return new ReportTable
        {
            From = t, To = t,
            Description = "Balance of the inter-branch adjustment GL by branch. The bank-wide total must be zero; branch balances show the amount each branch owes / is owed.",
            Columns = ["Branch", "Debit (receivable)", "Credit (payable)", "Net Dr/(Cr)"],
            Numeric = [false, true, true, true],
            Rows = rows.Select(r => new[] { branches[r.Key].ToString(), N(r.Dr), N(r.Cr), N(r.Dr - r.Cr) }).ToList(),
            Totals = ["BANK-WIDE", N(rows.Sum(x => x.Dr)), N(rows.Sum(x => x.Cr)), N(net) + (net == 0 ? "  ✔ reconciled" : "  ✗ UNRECONCILED")]
        };
    }

    public async Task<ReportTable> DepositsAsync(ReportQuery query)
    {
        var q = db.Accounts.AsNoTracking().Include(a => a.Product).Include(a => a.Customer).Include(a => a.Branch).Where(a => a.Status != AccountStatus.Closed);
        if (query.BranchId != null) q = q.Where(a => a.BranchId == query.BranchId);
        if (Enum.TryParse<AccountStatus>(query.Extra, true, out var status)) q = q.Where(a => a.Status == status);
        var rows = await q.OrderBy(a => a.Product!.Code).ThenBy(a => a.AccountNo).Take(5000).ToListAsync();
        return new ReportTable
        {
            From = DateTime.Today, To = DateTime.Today,
            Columns = ["Account", "Title", "CIF", "Product", "Branch", "Status", "Rate %", "Accrued int.", "Balance"],
            Numeric = [false, false, false, false, false, false, true, true, true],
            Rows = rows.Select(a => new[]
            {
                a.AccountNo, a.Title, a.Customer!.CifNo, a.Product!.Name, a.Branch!.Code, a.Status.ToString(),
                a.Product.IsInterestBearing ? (a.InterestRate > 0 ? a.InterestRate : a.Product.InterestRate).ToString("0.##") : "",
                N(a.AccruedInterest), N(a.Balance)
            }).ToList(),
            Totals = ["", "", "", "", "", "", "", "Total", N(rows.Sum(a => a.Balance))]
        };
    }

    public async Task<ReportTable> DormantAsync(ReportQuery query)
    {
        var q = db.Accounts.AsNoTracking().Include(a => a.Product).Include(a => a.Customer)
            .Where(a => a.Status == AccountStatus.Dormant || a.Status == AccountStatus.Inoperative);
        if (query.BranchId != null) q = q.Where(a => a.BranchId == query.BranchId);
        var rows = await q.OrderBy(a => a.LastTxnDate).ToListAsync();
        return new ReportTable
        {
            From = DateTime.Today, To = DateTime.Today,
            Columns = ["Account", "Title", "Customer mobile", "Product", "Status", "Last txn", "Balance"],
            Numeric = [false, false, false, false, false, false, true],
            Rows = rows.Select(a => new[] { a.AccountNo, a.Title, a.Customer!.Mobile, a.Product!.Name, a.Status.ToString(), a.LastTxnDate == null ? "-" : D(a.LastTxnDate.Value), N(a.Balance) }).ToList(),
            Totals = ["", "", "", "", "", "Total", N(rows.Sum(a => a.Balance))]
        };
    }

    public async Task<ReportTable> MaturityAsync(ReportQuery query)
    {
        var bd = await BusinessDateAsync();
        var t = (query.To ?? bd.AddDays(30)).Date;
        var q = db.Accounts.AsNoTracking().Include(a => a.Product).Include(a => a.Customer)
            .Where(a => (a.Product!.Category == ProductCategory.FDR || a.Product.Category == ProductCategory.DPS)
                        && (a.Status == AccountStatus.Active || a.Status == AccountStatus.Matured) && a.MaturityDate <= t);
        if (query.BranchId != null) q = q.Where(a => a.BranchId == query.BranchId);
        var rows = await q.OrderBy(a => a.MaturityDate).ToListAsync();
        return new ReportTable
        {
            From = t, To = t, Description = "Maturing on or before " + D(t),
            Columns = ["Account", "Customer", "Product", "Opened", "Maturity", "Rate %", "Principal/Balance", "Accrued interest"],
            Numeric = [false, false, false, false, false, true, true, true],
            Rows = rows.Select(a => new[] { a.AccountNo, a.Title, a.Product!.Name, D(a.OpenedOn), D(a.MaturityDate!.Value), a.InterestRate.ToString("0.##"), N(a.Balance), N(a.AccruedInterest) }).ToList(),
            Totals = ["", "", "", "", "", "Total", N(rows.Sum(a => a.Balance)), N(rows.Sum(a => a.AccruedInterest))]
        };
    }

    public async Task<ReportTable> LoanPortfolioAsync(ReportQuery query)
    {
        var q = db.Loans.AsNoTracking().Include(l => l.Customer).Include(l => l.Product).Where(l => l.Status == LoanStatus.Disbursed);
        if (query.BranchId != null) q = q.Where(l => l.BranchId == query.BranchId);
        if (Enum.TryParse<LoanClass>(query.Extra, true, out var cls)) q = q.Where(l => l.Classification == cls);
        var rows = await q.OrderByDescending(l => l.OverdueDays).ToListAsync();
        return new ReportTable
        {
            From = DateTime.Today, To = DateTime.Today,
            Description = "Classification by overdue days: <60 Standard · 60-89 SMA · 90-269 Sub-standard · 270-359 Doubtful · ≥360 Bad/Loss. Provision: 1% / 1% / 20% / 50% / 100% of outstanding (simplified; collateral not netted).",
            Columns = ["Loan", "Borrower", "Product", "Disbursed", "Principal", "Outstanding", "Overdue amt", "Days", "Class", "Provision"],
            Numeric = [false, false, false, false, true, true, true, true, false, true],
            Rows = rows.Select(l => new[]
            {
                l.LoanNo, l.Customer!.Name, l.Product!.Name, l.DisbursedOn == null ? "" : D(l.DisbursedOn.Value), N(l.Principal), N(l.OutstandingPrincipal),
                N(l.OverdueAmount), l.OverdueDays.ToString(), l.Classification.ToString(), N(l.ProvisionHeld)
            }).ToList(),
            Totals = ["", "", "", "Total", N(rows.Sum(l => l.Principal)), N(rows.Sum(l => l.OutstandingPrincipal)), N(rows.Sum(l => l.OverdueAmount)), "", "", N(rows.Sum(l => l.ProvisionHeld))]
        };
    }

    public async Task<ReportTable> UnauthorizedAsync(ReportQuery query)
    {
        var (f, t, _) = await DatesAsync(query.From, query.To, 7);
        var q = db.Vouchers.AsNoTracking().Include(v => v.Branch)
            .Where(v => (v.Status == VoucherStatus.Pending || v.Status == VoucherStatus.Rejected) && v.TxnDate >= f && v.TxnDate <= t);
        if (query.BranchId != null) q = q.Where(v => v.BranchId == query.BranchId);
        var rows = await q.OrderBy(v => v.BranchId).ThenBy(v => v.Id).ToListAsync();
        return new ReportTable
        {
            From = f, To = t,
            Columns = ["Branch", "Voucher", "Date", "Type", "Description", "Amount", "Status", "Maker", "Reason"],
            Numeric = [false, false, false, false, false, true, false, false, false],
            Rows = rows.Select(v => new[] { v.Branch!.Code, v.VoucherNo, D(v.TxnDate), v.Type.ToString(), v.Description, N(v.TotalAmount), v.Status.ToString(), v.MakerId, v.RejectReason ?? "" }).ToList()
        };
    }

    public async Task<ReportTable> CtrAsync(ReportQuery query)
    {
        var (f, t, _) = await DatesAsync(query.From, query.To, 7);
        var threshold = await prm.DecAsync("CtrThreshold");
        var q = db.Vouchers.AsNoTracking().Include(v => v.Branch).Include(v => v.Lines).ThenInclude(l => l.Account)
            .Where(v => v.Status == VoucherStatus.Authorized && v.TxnDate >= f && v.TxnDate <= t
                        && (v.Type == VoucherType.CashDeposit || v.Type == VoucherType.CashWithdrawal || v.Type == VoucherType.AccountOpening)
                        && v.TotalAmount >= threshold);
        if (query.BranchId != null) q = q.Where(v => v.BranchId == query.BranchId);
        var rows = await q.OrderBy(v => v.TxnDate).ToListAsync();
        return new ReportTable
        {
            From = f, To = t,
            Description = $"Cash deposits / withdrawals of {threshold:N0} or more (threshold set in Bank Parameters: CtrThreshold).",
            Columns = ["Date", "Voucher", "Branch", "Type", "Account", "Account title", "Party", "Amount"],
            Numeric = [false, false, false, false, false, false, false, true],
            Rows = rows.Select(v =>
            {
                var a = v.Lines.FirstOrDefault(l => l.Account != null)?.Account;
                return new[] { D(v.TxnDate), v.VoucherNo, v.Branch!.Code, v.Type.ToString(), a?.AccountNo ?? "", a?.Title ?? "", v.CounterParty ?? "", N(v.TotalAmount) };
            }).ToList(),
            Totals = ["", "", "", "", "", "", "Total", N(rows.Sum(v => v.TotalAmount))]
        };
    }

    public async Task<ReportTable> ChequesAsync(ReportQuery query)
    {
        var (f, t, _) = await DatesAsync(query.From, query.To, 90);
        var branchId = query.BranchId;
        var returns = await db.ChequeReturns.AsNoTracking().Include(r => r.Account)
            .Where(r => r.ReturnDate >= f && r.ReturnDate <= t && (branchId == null || r.Account!.BranchId == branchId)).ToListAsync();
        var stops = await db.StopPayments.AsNoTracking().Include(r => r.Account)
            .Where(r => r.InstructionTime >= f && r.InstructionTime < t.AddDays(1) && (branchId == null || r.Account!.BranchId == branchId)).ToListAsync();
        return new ReportTable
        {
            From = f, To = t,
            Columns = ["Register", "Date", "Account", "Cheque", "Amount", "Reason", "Charge / state"],
            Numeric = [false, false, false, false, true, false, false],
            Rows = returns.Select(r => new[] { "RETURN", D(r.ReturnDate), r.Account!.AccountNo, r.ChequeNo, N(r.Amount), r.Reason, N(r.ChargeApplied) })
                .Concat(stops.Select(s => new[] { "STOP PAYMENT", s.InstructionTime.ToString("dd-MMM-yyyy HH:mm"), s.Account!.AccountNo, s.ChequeNo, N(s.Amount), s.Reason, s.IsActive ? "Active" : "Released" }))
                .ToList()
        };
    }

    public async Task<ReportTable> UserActivityAsync(ReportQuery query)
    {
        var (f, t, _) = await DatesAsync(query.From, query.To, 7);
        var q = db.Vouchers.AsNoTracking().Where(v => v.TxnDate >= f && v.TxnDate <= t);
        if (query.BranchId != null) q = q.Where(v => v.BranchId == query.BranchId);
        var made = await q.GroupBy(v => v.MakerId).Select(g => new { U = g.Key, N = g.Count(), A = g.Sum(x => x.TotalAmount) }).ToListAsync();
        var authorized = await q.Where(v => v.CheckerId != null).GroupBy(v => v.CheckerId!).Select(g => new { U = g.Key, N = g.Count() }).ToListAsync();
        var users = made.Select(m => m.U).Union(authorized.Select(c => c.U)).Distinct().OrderBy(u => u);
        return new ReportTable
        {
            From = f, To = t,
            Columns = ["User", "Vouchers made", "Amount made", "Vouchers authorized"],
            Numeric = [false, true, true, true],
            Rows = users.Select(u => new[]
            {
                u, (made.FirstOrDefault(m => m.U == u)?.N ?? 0).ToString(), N(made.FirstOrDefault(m => m.U == u)?.A ?? 0),
                (authorized.FirstOrDefault(c => c.U == u)?.N ?? 0).ToString()
            }).ToList()
        };
    }

    public async Task<ReportTable> RatesAsync()
    {
        var rows = await db.Products.AsNoTracking().OrderBy(p => p.Code).ToListAsync();
        return new ReportTable
        {
            From = DateTime.Today, To = DateTime.Today,
            Columns = ["Code", "Product", "Category", "Default rate %", "Overdraft/penal %", "Premature %", "Interest payment"],
            Numeric = [false, false, false, true, true, true, false],
            Rows = rows.Select(p => new[] { p.Code, p.Name, p.Category.ToString(), p.InterestRate.ToString("0.####"), p.OverdraftRate.ToString("0.####"), p.PrematureRate.ToString("0.####"), p.PayFrequency.ToString() }).ToList()
        };
    }
}
