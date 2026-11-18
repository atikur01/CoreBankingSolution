namespace CoreBanking.Application.Features.Eod;

/// <summary>
/// Branch-wise End-of-Day / Beginning-of-Day: standing instructions, interest accrual & payment, OD interest,
/// loan interest recognition, classification & provisioning, dormancy flagging, month-end and year-end processing.
/// </summary>
public class EodService(IApplicationDbContext db, LedgerService ledger, ParamService prm, RateService rates)
{
    public record Result(bool Success, DateTime NewBusinessDate, List<string> Steps, string? Error);

    private int _branchId; private DateTime _date; private string _user = "";
    private readonly List<string> _steps = [];

    private async Task LogAsync(string step, EodStatus status, string? msg = null)
    {
        _steps.Add($"{(status == EodStatus.Failed ? "✗" : "✓")} {step}{(msg != null ? ": " + msg : "")}");
        db.EodLogs.Add(new EodLog { BranchId = _branchId, BusinessDate = _date, Step = step, Status = status, Message = msg?.Length > 480 ? msg[..480] : msg, RunBy = _user });
        await db.SaveChangesAsync();
    }

    public async Task<List<string>> PreChecksAsync(int branchId)
    {
        var errs = new List<string>();
        var b = await db.Branches.FindAsync(branchId);
        if (b == null) return ["Branch not found."];
        var pending = await db.Vouchers.CountAsync(v => v.BranchId == branchId && v.Status == VoucherStatus.Pending);
        if (pending > 0) errs.Add($"{pending} unauthorized voucher(s) exist. Authorize or reject them before closing the day.");
        var pendAcc = await db.Accounts.CountAsync(a => a.BranchId == branchId && a.Status == AccountStatus.PendingApproval);
        if (pendAcc > 0) errs.Add($"{pendAcc} account(s) are awaiting approval.");
        var pendClr = await db.ClearingItems.CountAsync(c => c.BranchId == branchId && c.Status == ClearingStatus.Registered);
        if (pendClr > 0) errs.Add($"{pendClr} clearing item(s) are not yet settled or returned.");
        return errs;
    }

    public async Task<Result> RunAsync(int branchId, string user)
    {
        var branch = await db.Branches.FindAsync(branchId) ?? throw new BankingException("Branch not found.");
        _branchId = branchId; _date = branch.BusinessDate.Date; _user = user; _steps.Clear();
        if (!branch.IsActive) throw new BankingException("Branch is closed.");
        var errs = await PreChecksAsync(branchId);
        if (errs.Count > 0) throw new BankingException("Cannot close the day: " + string.Join(" ", errs));

        await LogAsync("EOD started", EodStatus.Started);
        using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var basis = await prm.DecAsync("InterestDayBasis");
            var next = await prm.NextWorkingDayAsync(_date);
            bool monthEnd = next.Month != _date.Month;
            int fyStart = await prm.IntAsync("FinancialYearStartMonth"); if (fyStart is < 1 or > 12) fyStart = 1;
            bool yearEnd = monthEnd && next.Month == fyStart;

            await StandingInstructionsAsync();
            await DepositInterestAccrualAsync(basis);
            await OverdraftInterestAsync(basis);
            await LoanInterestRecognitionAsync();
            await LoanClassificationAsync(monthEnd);
            await MaturityAndRenewalAsync();
            if (monthEnd) await InterestPaymentAsync(_date.Month);
            if (monthEnd) await CollectOverdraftInterestAsync();
            await DormancyAsync();
            if (yearEnd) { await ExciseDutyAsync(); await ProfitTransferAsync(fyStart); }

            branch.BusinessDate = next; branch.DayOpen = true;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            await LogAsync($"EOD completed - BOD done, new business date {next:dd-MMM-yyyy}", EodStatus.Success);
            return new Result(true, next, _steps.ToList(), null);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            var msg = ex is BankingException ? ex.Message : ex.GetBaseException().Message;
            await LogAsync("EOD FAILED - rolled back", EodStatus.Failed, msg);
            return new Result(false, _date, _steps.ToList(), msg);
        }
    }

    // ------------------------------------------------------------------ steps
    private async Task StandingInstructionsAsync()
    {
        var due = await db.StandingInstructions.Include(s => s.FromAccount).Include(s => s.ToAccount)
            .Where(s => s.Status == SiStatus.Active && s.NextRunDate <= _date && s.FromAccount!.BranchId == _branchId).ToListAsync();
        int ok = 0, fail = 0;
        foreach (var si in due)
        {
            if (si.EndDate != null && si.NextRunDate > si.EndDate) { si.Status = SiStatus.Completed; continue; }
            var from = await db.Accounts.Include(a => a.Product).FirstAsync(a => a.Id == si.FromAccountId);
            if (from.Status != AccountStatus.Active || si.Amount > from.Available || si.ToAccount!.Status is AccountStatus.Closed or AccountStatus.Blocked or AccountStatus.PendingApproval)
            {
                si.FailedCount++; si.LastResult = $"{_date:dd-MMM}: failed (insufficient funds / account status)"; fail++; continue;
            }
            var req = new VoucherReq { Type = VoucherType.StandingInstruction, BranchId = _branchId, IsSystem = true, Maker = "SYSTEM",
                Description = si.Narration ?? $"Standing instruction #{si.Id}", SourceKind = "SI", SourceId = si.Id };
            req.Dr(null, si.FromAccountId, si.Amount, $"SI to {si.ToAccount.AccountNo}");
            req.Cr(null, si.ToAccountId, si.Amount, $"SI from {from.AccountNo}");
            try { await ledger.CreateAsync(req); }
            catch (BankingException ex)
            {
                si.FailedCount++; si.LastResult = $"{_date:dd-MMM}: failed ({ex.Message})"; fail++;
                continue;
            }
            si.ExecutedCount++; si.LastResult = $"{_date:dd-MMM}: executed";
            si.NextRunDate = si.Frequency == SiFrequency.Monthly ? si.NextRunDate.AddMonths(1)
                : si.Frequency == SiFrequency.Quarterly ? si.NextRunDate.AddMonths(3)
                : si.Frequency == SiFrequency.HalfYearly ? si.NextRunDate.AddMonths(6)
                : si.Frequency == SiFrequency.Yearly ? si.NextRunDate.AddYears(1) : si.NextRunDate.AddDays((int)si.Frequency);
            if (si.EndDate != null && si.NextRunDate > si.EndDate) si.Status = SiStatus.Completed;
            ok++;
        }
        await db.SaveChangesAsync();
        await LogAsync("Standing instructions", EodStatus.Success, $"{ok} executed, {fail} failed/deferred");
    }

    private async Task DepositInterestAccrualAsync(decimal basis)
    {
        var allRates = await db.ProductRates.AsNoTracking().Where(r => r.Status == ApprovalStatus.Approved && r.EffectiveFrom <= _date).ToListAsync();
        var accounts = await db.Accounts.Include(a => a.Product)
            .Where(a => a.BranchId == _branchId && a.Product!.IsInterestBearing && (a.Status == AccountStatus.Active || a.Status == AccountStatus.Dormant || a.Status == AccountStatus.Matured)
                        && a.Balance > 0).ToListAsync();
        var perProduct = new Dictionary<Product, decimal>();
        foreach (var a in accounts)
        {
            decimal rate = a.InterestRate;
            if (a.Product!.Category is ProductCategory.Savings or ProductCategory.SND or ProductCategory.Current)
            {
                var r = allRates.Where(x => x.ProductId == a.ProductId && x.MinAmount <= a.Balance && (x.MaxAmount == 0 || x.MaxAmount >= a.Balance))
                    .OrderByDescending(x => x.EffectiveFrom).ThenByDescending(x => x.Id).FirstOrDefault();
                rate = r?.Rate ?? a.Product.InterestRate;
            }
            if (a.Product.Category == ProductCategory.FDR && a.Status == AccountStatus.Matured && a.MaturityDate < _date) continue; // no interest after maturity
            var before = Money.R(a.AccruedInterest);
            a.AccruedInterest += a.Balance * rate / 100m / basis;
            var delta = Money.R(a.AccruedInterest) - before;
            if (delta > 0) perProduct[a.Product] = perProduct.GetValueOrDefault(a.Product) + delta;
            a.Version++;
        }
        await db.SaveChangesAsync();
        foreach (var (p, amt) in perProduct)
        {
            if (p.InterestGlId == null || p.AccrualGlId == null) continue;
            var req = new VoucherReq { Type = VoucherType.Eod, BranchId = _branchId, IsSystem = true, Maker = "SYSTEM", Description = $"Daily interest accrual - {p.Name}" };
            req.Dr(p.InterestGlId, null, amt, "Interest expense", _branchId);
            req.Cr(p.AccrualGlId, null, amt, "Interest payable", _branchId);
            await ledger.CreateAsync(req);
        }
        await LogAsync("Deposit interest accrual", EodStatus.Success, $"{accounts.Count} accounts, {perProduct.Values.Sum():N2}");
    }

    private async Task OverdraftInterestAsync(decimal basis)
    {
        var accs = await db.Accounts.Include(a => a.Product).Where(a => a.BranchId == _branchId && a.Balance < 0 && a.Status != AccountStatus.Closed).ToListAsync();
        decimal total = 0;
        foreach (var a in accs)
        {
            var before = Money.R(a.OdInterestAccrued);
            a.OdInterestAccrued += -a.Balance * a.Product!.OverdraftRate / 100m / basis;
            total += Money.R(a.OdInterestAccrued) - before; a.Version++;
        }
        await db.SaveChangesAsync();
        if (total > 0)
        {
            var req = new VoucherReq { Type = VoucherType.Eod, BranchId = _branchId, IsSystem = true, Maker = "SYSTEM", Description = "Overdraft interest accrual" };
            req.Dr(await prm.GlIdAsync("LoanInterestReceivableGl"), null, total, "OD interest receivable", _branchId);
            req.Cr(await prm.GlIdAsync("LoanInterestIncomeGl"), null, total, "OD interest income", _branchId);
            await ledger.CreateAsync(req);
        }
        await LogAsync("Overdraft interest accrual", EodStatus.Success, $"{accs.Count} accounts, {total:N2}");
    }

    private async Task CollectOverdraftInterestAsync()
    {
        var accs = await db.Accounts.Where(a => a.BranchId == _branchId && a.OdInterestAccrued >= 0.005m && a.Status != AccountStatus.Closed).ToListAsync();
        var recv = await prm.GlIdAsync("LoanInterestReceivableGl");
        int n = 0;
        foreach (var a in accs)
        {
            var amt = Money.R(a.OdInterestAccrued);
            if (amt <= 0) continue;
            var req = new VoucherReq { Type = VoucherType.Eod, BranchId = _branchId, IsSystem = true, Maker = "SYSTEM", Description = $"Overdraft interest debit {a.AccountNo}" };
            req.Dr(null, a.Id, amt, "Monthly overdraft interest");
            req.Cr(recv, null, amt, "OD interest receivable settled", _branchId);
            await ledger.CreateAsync(req);
            a.OdInterestAccrued -= amt; n++;
        }
        await db.SaveChangesAsync();
        await LogAsync("Month-end overdraft interest collection", EodStatus.Success, $"{n} accounts");
    }

    private async Task LoanInterestRecognitionAsync()
    {
        var items = await db.LoanInstallments.Include(i => i.Loan)
            .Where(i => i.Loan!.BranchId == _branchId && i.Loan.Status == LoanStatus.Disbursed && !i.InterestRecognized && i.DueDate <= _date).ToListAsync();
        decimal total = 0;
        foreach (var i in items)
        {
            var amt = i.InterestDue - i.InterestPaid;
            i.InterestRecognized = true;
            if (amt > 0) { total += amt; i.Loan!.InterestReceivable += amt; i.Loan.Version++; }
        }
        await db.SaveChangesAsync();
        if (total > 0)
        {
            var req = new VoucherReq { Type = VoucherType.Eod, BranchId = _branchId, IsSystem = true, Maker = "SYSTEM", Description = "Loan interest due recognition" };
            req.Dr(await prm.GlIdAsync("LoanInterestReceivableGl"), null, total, "Interest receivable", _branchId);
            req.Cr(await prm.GlIdAsync("LoanInterestIncomeGl"), null, total, "Interest income", _branchId);
            await ledger.CreateAsync(req);
        }
        await LogAsync("Loan interest recognition", EodStatus.Success, $"{items.Count} installments, {total:N2}");
    }

    private async Task LoanClassificationAsync(bool monthEnd)
    {
        var loans = await db.Loans.Include(l => l.Installments).Where(l => l.BranchId == _branchId && (l.Status == LoanStatus.Disbursed || l.ProvisionHeld > 0)).ToListAsync();
        foreach (var l in loans)
        {
            if (l.Status != LoanStatus.Disbursed) { l.OverdueDays = 0; l.OverdueAmount = 0; continue; }
            var overdue = l.Installments.Where(i => i.Status == InstallmentStatus.Due && i.DueDate < _date).ToList();
            l.OverdueDays = overdue.Count == 0 ? 0 : (_date - overdue.Min(i => i.DueDate)).Days;
            l.OverdueAmount = overdue.Sum(i => i.Outstanding);
            l.Classification = LoanCalculator.Classify(l.OverdueDays).cls;
        }
        await db.SaveChangesAsync();
        decimal delta = 0;
        if (monthEnd)
        {
            foreach (var l in loans)
            {
                var req = l.Status == LoanStatus.Disbursed ? Money.R(l.OutstandingPrincipal * LoanCalculator.Classify(l.OverdueDays).pct / 100m) : 0;
                delta += req - l.ProvisionHeld; l.ProvisionHeld = req;
            }
            await db.SaveChangesAsync();
            if (delta != 0)
            {
                var v = new VoucherReq { Type = VoucherType.Eod, BranchId = _branchId, IsSystem = true, Maker = "SYSTEM", Description = "Month-end loan loss provision" };
                var exp = await prm.GlIdAsync("ProvisionExpenseGl"); var prov = await prm.GlIdAsync("ProvisionGl");
                if (delta > 0) { v.Dr(exp, null, delta, "Provision charge", _branchId); v.Cr(prov, null, delta, "Provision held", _branchId); }
                else { v.Dr(prov, null, -delta, "Provision release", _branchId); v.Cr(exp, null, -delta, "Provision write-back", _branchId); }
                await ledger.CreateAsync(v);
            }
        }
        await LogAsync("Loan classification" + (monthEnd ? " & provisioning" : ""), EodStatus.Success, $"{loans.Count} loans" + (monthEnd ? $", provision change {delta:N2}" : ""));
    }

    private async Task MaturityAndRenewalAsync()
    {
        var fdrs = await db.Accounts.Include(a => a.Product).Where(a => a.BranchId == _branchId && a.Status == AccountStatus.Active
            && (a.Product!.Category == ProductCategory.FDR || a.Product.Category == ProductCategory.DPS) && a.MaturityDate != null && a.MaturityDate <= _date).ToListAsync();
        int renewed = 0;
        var taxPct = await prm.DecAsync("SourceTaxRate");
        foreach (var a in fdrs)
        {
            if (a.Product!.Category == ProductCategory.FDR && a.AutoRenew)
            {
                var interest = Money.R(a.AccruedInterest); var tax = Money.R(interest * taxPct / 100m);
                if (interest > 0)
                {
                    var req = new VoucherReq { Type = VoucherType.Eod, BranchId = _branchId, IsSystem = true, Maker = "SYSTEM", Description = $"FDR auto-renewal {a.AccountNo}" };
                    req.Dr(a.Product.AccrualGlId, null, interest, "Accrued interest capitalised", _branchId);
                    req.Cr(await prm.GlIdAsync("TaxPayableGl"), null, tax, "Source tax", _branchId);
                    req.Cr(null, a.Id, interest - tax, "Interest added to principal");
                    await ledger.CreateAsync(req);
                    a.AccruedInterest -= interest;
                }
                a.OpenedOn = _date; a.MaturityDate = _date.AddMonths(a.TermMonths); a.PrincipalAmount = a.Balance;
                a.InterestRate = await rates.GetRateAsync(a.ProductId, a.Balance, a.TermMonths, _date);
                renewed++;
            }
            else a.Status = AccountStatus.Matured;
            a.Version++;
        }
        await db.SaveChangesAsync();
        await LogAsync("FDR/DPS maturity & renewal", EodStatus.Success, $"{fdrs.Count} matured, {renewed} auto-renewed");
    }

    private async Task InterestPaymentAsync(int month)
    {
        var taxPct = await prm.DecAsync("SourceTaxRate");
        var taxGl = await prm.GlIdAsync("TaxPayableGl");
        var accs = await db.Accounts.Include(a => a.Product).Where(a => a.BranchId == _branchId && a.Product!.IsInterestBearing
            && a.Product.PayFrequency != PayFrequency.None && a.AccruedInterest >= 0.5m
            && (a.Status == AccountStatus.Active || a.Status == AccountStatus.Dormant)).ToListAsync();
        int n = 0;
        foreach (var g in accs.Where(a => month % (int)a.Product!.PayFrequency == 0).GroupBy(a => a.ProductId))
        {
            var prod = g.First().Product!;
            var req = new VoucherReq { Type = VoucherType.InterestPosting, BranchId = _branchId, IsSystem = true, Maker = "SYSTEM", Description = $"Interest payment - {prod.Name}" };
            decimal totalPay = 0, totalTax = 0;
            foreach (var a in g)
            {
                var target = prod.Category is ProductCategory.FDR or ProductCategory.DPS ? a.LinkedAccountId : a.Id;
                if (target == null) continue;
                var pay = Money.R(a.AccruedInterest); if (pay <= 0) continue;
                var tax = Money.R(pay * taxPct / 100m);
                req.Cr(null, target, pay - tax, $"Interest {a.AccountNo}");
                totalPay += pay; totalTax += tax; a.AccruedInterest -= pay; a.Version++; n++;
            }
            if (totalPay <= 0) continue;
            req.Dr(prod.AccrualGlId, null, totalPay, "Interest payable settled", _branchId);
            req.Cr(taxGl, null, totalTax, "Source tax on interest", _branchId);
            await ledger.CreateAsync(req);
        }
        await db.SaveChangesAsync();
        await LogAsync("Periodic interest payment", EodStatus.Success, $"{n} accounts");
    }

    private async Task DormancyAsync()
    {
        int dormantDays = await prm.IntAsync("DormantAfterDays"), inopDays = await prm.IntAsync("InoperativeAfterDays");
        var accs = await db.Accounts.Include(a => a.Product).Where(a => a.BranchId == _branchId && (a.Status == AccountStatus.Active || a.Status == AccountStatus.Dormant)
            && (a.Product!.Category == ProductCategory.Savings || a.Product.Category == ProductCategory.Current || a.Product.Category == ProductCategory.SND)).ToListAsync();
        int d = 0, i = 0;
        foreach (var a in accs)
        {
            var idle = (_date - (a.LastTxnDate ?? a.OpenedOn)).Days;
            if (idle >= inopDays && a.Status != AccountStatus.Inoperative) { a.Status = AccountStatus.Inoperative; i++; a.Version++; }
            else if (idle >= dormantDays && a.Status == AccountStatus.Active) { a.Status = AccountStatus.Dormant; d++; a.Version++; }
        }
        await db.SaveChangesAsync();
        await LogAsync("Dormant / inoperative account flagging", EodStatus.Success, $"{d} dormant, {i} inoperative");
    }

    private async Task ExciseDutyAsync()
    {
        var slabs = await db.ExciseSlabs.AsNoTracking().ToListAsync();
        if (slabs.Count == 0) { await LogAsync("Year-end excise duty", EodStatus.Info, "no slabs configured"); return; }
        var gl = await prm.GlIdAsync("ExciseDutyGl");
        var accs = await db.Accounts.Include(a => a.Product).Where(a => a.BranchId == _branchId && a.Status == AccountStatus.Active
            && a.Balance > 0 && a.Product!.Category != ProductCategory.TermLoan).ToListAsync();
        int n = 0;
        foreach (var a in accs)
        {
            var slab = slabs.Where(s => a.Balance >= s.MinBalance && (s.MaxBalance == 0 || a.Balance <= s.MaxBalance)).OrderByDescending(s => s.MinBalance).FirstOrDefault();
            if (slab == null || slab.Duty <= 0 || slab.Duty > a.Balance) continue;
            var req = new VoucherReq { Type = VoucherType.Eod, BranchId = _branchId, IsSystem = true, Maker = "SYSTEM", Description = $"Annual excise duty {a.AccountNo}" };
            req.Dr(null, a.Id, slab.Duty, "Excise duty");
            req.Cr(gl, null, slab.Duty, "Excise duty payable", _branchId);
            await ledger.CreateAsync(req); n++;
        }
        await LogAsync("Year-end excise duty", EodStatus.Success, $"{n} accounts");
    }

    private async Task ProfitTransferAsync(int fyStart)
    {
        var fyBegin = new DateTime(_date.Month >= fyStart && fyStart > 1 ? _date.Year : (fyStart == 1 ? _date.Year : _date.Year - 1), fyStart, 1);
        var rows = await ledger.PostedLines()
            .Where(l => l.BranchId == _branchId && l.Voucher!.TxnDate >= fyBegin && l.Voucher.TxnDate <= _date
                        && (l.GlAccount!.Type == GlType.Income || l.GlAccount.Type == GlType.Expense))
            .GroupBy(l => l.GlAccountId).Select(g => new { Gl = g.Key, Dr = g.Sum(x => x.Debit), Cr = g.Sum(x => x.Credit) }).ToListAsync();
        var req = new VoucherReq { Type = VoucherType.Eod, BranchId = _branchId, IsSystem = true, Maker = "SYSTEM", Description = $"Year-end transfer of profit & loss to retained earnings (FY {fyBegin:yyyy})" };
        decimal net = 0;
        foreach (var r in rows)
        {
            var bal = r.Cr - r.Dr; // positive for income
            if (bal > 0) req.Dr(r.Gl, null, bal, "Close income", _branchId);
            else if (bal < 0) req.Cr(r.Gl, null, -bal, "Close expense", _branchId);
            net += bal;
        }
        var re = await prm.GlIdAsync("RetainedEarningsGl");
        if (net > 0) req.Cr(re, null, net, "Net profit for the year", _branchId);
        else if (net < 0) req.Dr(re, null, -net, "Net loss for the year", _branchId);
        if (req.Lines.Count >= 2) await ledger.CreateAsync(req);
        await LogAsync("Year-end profit & loss transfer", EodStatus.Success, $"net {net:N2}");
    }
}
