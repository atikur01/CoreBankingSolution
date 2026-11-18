namespace CoreBanking.Application.Features.Ledger;

public record LineReq(int? GlAccountId, int? AccountId, decimal Debit, decimal Credit, string? Narration = null,
    int? BranchId = null, int? LoanId = null, string? LoanComponent = null);

public class VoucherReq
{
    public VoucherType Type { get; set; }
    public int BranchId { get; set; }
    public string Description { get; set; } = "";
    public DateTime? ValueDate { get; set; }
    public string? Reference { get; set; }
    public string? ChequeNo { get; set; }
    public string? CounterParty { get; set; }
    public string Maker { get; set; } = "SYSTEM";
    public UserProfile? MakerProfile { get; set; }
    public bool IsSystem { get; set; }
    public string? SourceKind { get; set; }
    public int? SourceId { get; set; }
    public int? ReversalOfId { get; set; }
    public string? Workstation { get; set; }
    public List<LineReq> Lines { get; } = [];
    public VoucherReq Dr(int? gl, int? acc, decimal amt, string? narr = null, int? branch = null, int? loan = null, string? comp = null)
    { if (amt > 0) Lines.Add(new LineReq(gl, acc, amt, 0, narr, branch, loan, comp)); return this; }
    public VoucherReq Cr(int? gl, int? acc, decimal amt, string? narr = null, int? branch = null, int? loan = null, string? comp = null)
    { if (amt > 0) Lines.Add(new LineReq(gl, acc, 0, amt, narr, branch, loan, comp)); return this; }
}

/// <summary>Callbacks so business modules can react to a voucher being authorized / rejected.</summary>
public interface IVoucherHook
{
    Task OnAuthorizedAsync(Voucher v);
    Task OnRejectedAsync(Voucher v);
}

/// <summary>
/// The posting engine: double-entry vouchers with maker/checker, back-value control, limits,
/// inter-branch auto-balancing, reversal and per-account statement lines.
/// </summary>
public class LedgerService(IApplicationDbContext db, NumberService numbers, ParamService prm, IServiceProvider sp)
{
    private static readonly VoucherType[] NonReversible =
    [
        VoucherType.LoanDisburse, VoucherType.LoanRepay, VoucherType.FdrOpen, VoucherType.AccountOpening,
        VoucherType.RemittanceIssue, VoucherType.RemittancePay, VoucherType.Clearing, VoucherType.Encash, VoucherType.Reversal
    ];

    // ---------------------------------------------------------------- create
    public async Task<Voucher> CreateAsync(VoucherReq req)
    {
        if (req.Lines.Count < 2) throw new BankingException("A voucher needs at least two lines (debit and credit).");
        var branch = await db.Branches.FindAsync(req.BranchId) ?? throw new BankingException("Branch not found.");
        if (!branch.IsActive) throw new BankingException($"Branch {branch.Code} is closed.");
        if (!branch.DayOpen && !req.IsSystem) throw new BankingException($"Branch {branch.Code} day is closed. Run BOD / open the next day first.");

        var txnDate = branch.BusinessDate.Date;
        var valueDate = (req.ValueDate ?? txnDate).Date;
        if (valueDate > txnDate) throw new BankingException("Value date cannot be in the future.");
        bool back = valueDate < txnDate;
        if (back && req.Type != VoucherType.Reversal && !req.IsSystem)
        {
            var max = await prm.IntAsync("BackValueMaxDays");
            if ((txnDate - valueDate).Days > max) throw new BankingException($"Back-value date is beyond the allowed {max} days.");
        }

        var ibGl = await prm.GlIdAsync("InterBranchGl");
        var lines = new List<VoucherLine>();
        int? currencyId = null;
        var baseCode = await prm.GetAsync("BaseCurrency");
        var baseCur = await db.Currencies.FirstOrDefaultAsync(c => c.Code == baseCode) ?? await db.Currencies.FirstAsync(c => c.IsBase);

        foreach (var l in req.Lines)
        {
            var debit = Money.R(l.Debit); var credit = Money.R(l.Credit);
            if ((debit > 0) == (credit > 0) || debit < 0 || credit < 0)
                throw new BankingException("Each voucher line must carry either a debit or a credit amount.");

            var vl = new VoucherLine { Debit = debit, Credit = credit, Narration = l.Narration, LoanId = l.LoanId, LoanComponent = l.LoanComponent };
            if (l.AccountId is int aid && aid > 0)
            {
                var acc = await db.Accounts.Include(a => a.Product).FirstOrDefaultAsync(a => a.Id == aid)
                          ?? throw new BankingException("Account not found.");
                ValidateAccountLine(acc, vl, req);
                if (currencyId != null && currencyId != acc.CurrencyId)
                    throw new BankingException("Cross-currency posting in one voucher is not supported.");
                currencyId = acc.CurrencyId;
                vl.AccountId = acc.Id; vl.GlAccountId = acc.Product!.GlAccountId; vl.BranchId = l.BranchId ?? acc.BranchId;
                if (debit > 0 && !req.IsSystem) await CheckDebitCapacityAsync(acc, debit, txnDate, req);
            }
            else
            {
                var gl = l.GlAccountId is int glId ? await db.GlAccounts.FindAsync(glId) : null;
                if (gl == null) throw new BankingException("GL account not found.");
                if (!gl.IsActive || !gl.IsPosting) throw new BankingException($"GL {gl.Code} is not open for posting.");
                vl.GlAccountId = gl.Id; vl.BranchId = l.BranchId ?? req.BranchId;
            }
            lines.Add(vl);
        }

        var totalDr = lines.Sum(x => x.Debit); var totalCr = lines.Sum(x => x.Credit);
        if (totalDr != totalCr)
            throw new BankingException($"Voucher is out of balance: debit {totalDr:N2} vs credit {totalCr:N2}.");
        if (totalDr <= 0) throw new BankingException("Voucher amount must be greater than zero.");

        // Auto inter-branch balancing: every branch must balance on its own through the inter-branch GL.
        foreach (var g in lines.GroupBy(x => x.BranchId).ToList())
        {
            var net = g.Sum(x => x.Debit) - g.Sum(x => x.Credit);
            if (net > 0) lines.Add(new VoucherLine { BranchId = g.Key, GlAccountId = ibGl, Credit = net, Narration = "Inter-branch adjustment" });
            else if (net < 0) lines.Add(new VoucherLine { BranchId = g.Key, GlAccountId = ibGl, Debit = -net, Narration = "Inter-branch adjustment" });
        }

        // Maker transaction limits (delegation of authority).
        if (!req.IsSystem && req.MakerProfile is { } mp)
            CheckMakerLimits(mp, lines, req.BranchId);

        var level2 = await prm.DecAsync("Level2Amount");
        var v = new Voucher
        {
            VoucherNo = await numbers.NextVoucherNoAsync(branch.Code, txnDate),
            Type = req.Type, BranchId = branch.Id, TxnDate = txnDate, ValueDate = valueDate, IsBackValue = back,
            CurrencyId = currencyId ?? baseCur.Id, TotalAmount = totalDr, Description = req.Description,
            Reference = req.Reference, ChequeNo = req.ChequeNo, CounterParty = req.CounterParty,
            RequiredLevel = (back || totalDr >= level2) ? 2 : 1,
            MakerId = req.Maker, MadeAt = DateTime.Now, Workstation = req.Workstation,
            ReversalOfId = req.ReversalOfId, SourceKind = req.SourceKind, SourceId = req.SourceId, IsSystem = req.IsSystem,
            Status = VoucherStatus.Pending, Lines = lines
        };
        db.Vouchers.Add(v);
        await db.SaveChangesAsync();

        if (req.IsSystem) await AuthorizeAsync(v.Id, "SYSTEM", system: true);
        return v;
    }

    private static void ValidateAccountLine(Account acc, VoucherLine vl, VoucherReq req)
    {
        var t = req.Type;
        bool opening = t is VoucherType.AccountOpening or VoucherType.FdrOpen;
        if (vl.Debit > 0)
        {
            if (acc.Status == AccountStatus.PendingApproval || acc.Status == AccountStatus.Closed || acc.Status == AccountStatus.Blocked)
                throw new BankingException($"Account {acc.AccountNo} is {acc.Status}; debit not allowed.");
            if (acc.Status != AccountStatus.Active && !(t is VoucherType.Reversal or VoucherType.Eod)
                && !(acc.Status == AccountStatus.Matured && t == VoucherType.Encash))
                throw new BankingException($"Account {acc.AccountNo} is {acc.Status}; debit not allowed. Re-activate the account first.");
            if (acc.Product!.Category is ProductCategory.FDR or ProductCategory.DPS && t is not (VoucherType.Encash or VoucherType.Reversal or VoucherType.Eod))
                throw new BankingException($"Direct debit from a {acc.Product.Category} account is not allowed; use Encashment.");
        }
        else
        {
            if (acc.Status == AccountStatus.Closed || acc.Status == AccountStatus.Blocked)
                throw new BankingException($"Account {acc.AccountNo} is {acc.Status}; credit not allowed.");
            if (acc.Status == AccountStatus.PendingApproval && !opening)
                throw new BankingException($"Account {acc.AccountNo} is not yet approved.");
            if (acc.Product!.Category == ProductCategory.FDR && t is not (VoucherType.FdrOpen or VoucherType.Reversal or VoucherType.Eod))
                throw new BankingException("An FDR account accepts no further deposits.");
        }
    }

    private async Task CheckDebitCapacityAsync(Account acc, decimal amount, DateTime txnDate, VoucherReq req)
    {
        if (acc.Product!.Category is ProductCategory.FDR or ProductCategory.DPS) return;
        bool closing = req.SourceKind == "AccountClose";
        if (!closing && amount > acc.Available)
            throw new BankingException($"Insufficient funds in {acc.AccountNo}: available {acc.Available:N2}, requested {amount:N2}.");
        if (acc.SingleDebitLimit > 0 && amount > acc.SingleDebitLimit)
            throw new BankingException($"Amount exceeds the single-transaction debit limit ({acc.SingleDebitLimit:N2}) of {acc.AccountNo}.");
        if (acc.DailyDebitLimit > 0)
        {
            var today = await DebitTotalAsync(acc.Id, txnDate, txnDate);
            if (today + amount > acc.DailyDebitLimit)
                throw new BankingException($"Daily debit limit ({acc.DailyDebitLimit:N2}) of {acc.AccountNo} would be exceeded.");
        }
        if (acc.MonthlyDebitLimit > 0)
        {
            var first = new DateTime(txnDate.Year, txnDate.Month, 1);
            var month = await DebitTotalAsync(acc.Id, first, txnDate);
            if (month + amount > acc.MonthlyDebitLimit)
                throw new BankingException($"Monthly debit limit ({acc.MonthlyDebitLimit:N2}) of {acc.AccountNo} would be exceeded.");
        }
        if (!string.IsNullOrWhiteSpace(req.ChequeNo)) await ValidateChequeAsync(acc, req.ChequeNo);
    }

    private async Task<decimal> DebitTotalAsync(int accountId, DateTime from, DateTime to)
    {
        var posted = await db.AccountTransactions
            .Where(t => t.AccountId == accountId && t.TxnDate >= from && t.TxnDate <= to)
            .SumAsync(t => (decimal?)t.Debit) ?? 0;
        var pending = await db.VoucherLines
            .Where(l => l.AccountId == accountId && l.Debit > 0 && l.Voucher!.Status == VoucherStatus.Pending
                        && l.Voucher.TxnDate >= from && l.Voucher.TxnDate <= to)
            .SumAsync(l => (decimal?)l.Debit) ?? 0;
        return posted + pending;
    }

    public async Task ValidateChequeAsync(Account acc, string chequeNo)
    {
        if (!long.TryParse(chequeNo, out var no)) throw new BankingException("Cheque number must be numeric.");
        if (!await db.ChequeBooks.AnyAsync(b => b.AccountId == acc.Id && b.FromNo <= no && b.ToNo >= no))
            throw new BankingException($"Cheque {chequeNo} was not issued against account {acc.AccountNo}.");
        if (await db.StopPayments.AnyAsync(s => s.AccountId == acc.Id && s.ChequeNo == chequeNo && s.IsActive))
            throw new BankingException($"Stop-payment is active on cheque {chequeNo}.");
        if (await db.Vouchers.AnyAsync(v => v.ChequeNo == chequeNo && v.Status != VoucherStatus.Rejected && v.Status != VoucherStatus.Reversed
                                            && v.Lines.Any(l => l.AccountId == acc.Id && l.Debit > 0)))
            throw new BankingException($"Cheque {chequeNo} has already been presented/paid.");
    }

    private static void CheckMakerLimits(UserProfile p, List<VoucherLine> lines, int voucherBranch)
    {
        foreach (var l in lines.Where(x => x.AccountId != null))
        {
            bool ib = l.BranchId != voucherBranch;
            var maxDr = ib && p.MaxInterBranchDebit > 0 ? p.MaxInterBranchDebit : p.MaxDebit;
            var maxCr = ib && p.MaxInterBranchCredit > 0 ? p.MaxInterBranchCredit : p.MaxCredit;
            if (l.Debit > 0 && maxDr > 0 && l.Debit > maxDr)
                throw new BankingException($"Your debit limit is {maxDr:N2}; transaction of {l.Debit:N2} exceeds it.");
            if (l.Credit > 0 && maxCr > 0 && l.Credit > maxCr)
                throw new BankingException($"Your credit limit is {maxCr:N2}; transaction of {l.Credit:N2} exceeds it.");
        }
    }

    // ---------------------------------------------------------------- authorize
    public async Task AuthorizeAsync(int voucherId, string checker, bool isManagerLevel = false, bool system = false)
    {
        var v = await db.Vouchers.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == voucherId)
                ?? throw new BankingException("Voucher not found.");
        if (v.Status != VoucherStatus.Pending) throw new BankingException($"Voucher {v.VoucherNo} is already {v.Status}.");
        if (!system)
        {
            if (string.Equals(v.MakerId, checker, StringComparison.OrdinalIgnoreCase))
                throw new BankingException("Maker-checker violation: you cannot authorize your own entry.");
            if (v.RequiredLevel >= 2 && !isManagerLevel)
                throw new BankingException($"Voucher {v.VoucherNo} needs Manager-level authorization (amount/back-value policy).");
        }
        var branch = await db.Branches.FindAsync(v.BranchId);
        if (branch is { DayOpen: false } && !system) throw new BankingException("Branch day is closed.");

        var own = db.Database.CurrentTransaction == null;
        var tx = own ? await db.Database.BeginTransactionAsync() : null;
        try
        {
            foreach (var l in v.Lines.Where(x => x.AccountId != null).OrderBy(x => x.AccountId).ThenBy(x => x.Id))
            {
                var acc = await db.Accounts.Include(a => a.Product).FirstAsync(a => a.Id == l.AccountId);
                var req = new VoucherReq { Type = v.Type, ChequeNo = v.ChequeNo, IsSystem = v.IsSystem || system };
                ValidateAccountLine(acc, l, req);
                if (l.Debit > 0 && !(v.IsSystem || system) && acc.Product!.Category is not (ProductCategory.FDR or ProductCategory.DPS))
                {
                    if (v.SourceKind != "AccountClose" && l.Debit > acc.Available)
                        throw new BankingException($"Insufficient funds in {acc.AccountNo} (available {acc.Available:N2}).");
                }
                acc.Balance = Money.R(acc.Balance + l.Credit - l.Debit);
                acc.Version++;
                if (v.Type is not (VoucherType.InterestPosting or VoucherType.Eod)) acc.LastTxnDate = v.TxnDate;
                db.AccountTransactions.Add(new AccountTransaction
                {
                    AccountId = acc.Id, VoucherId = v.Id, TxnDate = v.TxnDate, ValueDate = v.ValueDate,
                    Debit = l.Debit, Credit = l.Credit, RunningBalance = acc.Balance,
                    Narration = l.Narration ?? v.Description, ChequeNo = l.Debit > 0 ? v.ChequeNo : null
                });
            }
            v.Status = VoucherStatus.Authorized;
            v.CheckerId = system ? "SYSTEM" : checker;
            v.CheckedAt = DateTime.Now;
            await db.SaveChangesAsync();

            foreach (var hook in sp.GetServices<IVoucherHook>()) await hook.OnAuthorizedAsync(v);

            if (v.ReversalOfId is int orig)
            {
                var o = await db.Vouchers.FindAsync(orig);
                if (o != null) { o.Status = VoucherStatus.Reversed; o.ReversedById = v.Id; }
            }
            await db.SaveChangesAsync();
            if (tx != null) await tx.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new BankingException("Another transaction updated the same account at the same time. Please retry.");
        }
        finally { if (tx != null) await tx.DisposeAsync(); }
    }

    public async Task RejectAsync(int voucherId, string checker, string reason)
    {
        var v = await db.Vouchers.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == voucherId)
                ?? throw new BankingException("Voucher not found.");
        if (v.Status != VoucherStatus.Pending) throw new BankingException($"Voucher {v.VoucherNo} is already {v.Status}.");
        if (string.IsNullOrWhiteSpace(reason)) throw new BankingException("A rejection reason is required.");
        v.Status = VoucherStatus.Rejected; v.CheckerId = checker; v.CheckedAt = DateTime.Now; v.RejectReason = reason;
        await db.SaveChangesAsync();
        foreach (var hook in sp.GetServices<IVoucherHook>()) await hook.OnRejectedAsync(v);
        await db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------- reverse
    public async Task<Voucher> CreateReversalAsync(int voucherId, string reason, string maker, UserProfile? profile)
    {
        var o = await db.Vouchers.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == voucherId)
                ?? throw new BankingException("Voucher not found.");
        if (o.Status != VoucherStatus.Authorized) throw new BankingException("Only authorized, un-reversed vouchers can be reversed.");
        if (NonReversible.Contains(o.Type))
            throw new BankingException($"{o.Type} vouchers cannot be reversed directly; use the module's own cancel/return function.");
        if (await db.Vouchers.AnyAsync(x => x.ReversalOfId == o.Id && x.Status == VoucherStatus.Pending))
            throw new BankingException("A reversal for this voucher is already pending authorization.");
        if (string.IsNullOrWhiteSpace(reason)) throw new BankingException("Reversal reason is required.");

        var req = new VoucherReq
        {
            Type = VoucherType.Reversal, BranchId = o.BranchId, Description = $"Reversal of {o.VoucherNo}: {reason}",
            ValueDate = o.ValueDate, Maker = maker, MakerProfile = profile, ReversalOfId = o.Id, Reference = o.VoucherNo
        };
        // Skip the automatic inter-branch lines of the original; they are regenerated.
        var ibGl = await prm.GlIdAsync("InterBranchGl");
        foreach (var l in o.Lines.Where(x => !(x.GlAccountId == ibGl && x.AccountId == null && x.Narration == "Inter-branch adjustment")))
            req.Lines.Add(new LineReq(l.AccountId == null ? l.GlAccountId : null, l.AccountId, l.Credit, l.Debit, "Reversal: " + (l.Narration ?? o.Description), l.BranchId));
        return await CreateAsync(req);
    }

    // ---------------------------------------------------------------- GL queries
    /// <summary>Posted = authorized (including those later reversed; the reversal voucher offsets them).</summary>
    public IQueryable<VoucherLine> PostedLines() =>
        db.VoucherLines.AsNoTracking().Where(l => l.Voucher!.Status == VoucherStatus.Authorized || l.Voucher.Status == VoucherStatus.Reversed);
}
