using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Infrastructure.Persistence.Seed.Demo;

internal sealed record Ln(int? Gl, Account? Acc, decimal Dr, decimal Cr, string? Narr = null, Loan? Loan = null, string? Comp = null);

internal sealed class DemoState(ApplicationDbContext db)
{
    public const string Password = "Demo@12345";
    public const string ManagerUser = "manager@corebank.local";
    public const string SupervisorUser = "supervisor@corebank.local";
    public const string OfficerUser = "officer@corebank.local";
    public const string TellerUser = "teller@corebank.local";
    public const decimal VatRate = 15m;
    public const decimal Level2Amount = 500000m;

    private readonly Dictionary<string, long> _seq = [];
    private readonly List<(DateTime Date, int Order, Action Run)> _queue = [];
    private readonly List<Action> _deferred = [];

    public ApplicationDbContext Db { get; } = db;
    public DateTime Today { get; } = DateTime.Today;
    public Random Rng { get; } = new(20261118);
    public List<Branch> Branches { get; } = [];
    public Dictionary<string, Product> Products { get; } = [];
    public Dictionary<string, int> Gl { get; } = [];
    public Currency Bdt { get; set; } = null!;
    public List<Customer> Customers { get; } = [];
    public List<Account> Accounts { get; } = [];
    public List<Loan> Loans { get; } = [];
    public Dictionary<Account, decimal> InitialDeposits { get; } = [];
    public int ChequeSeq { get; set; }

    public DateTime LastMonthEnd => new DateTime(Today.Year, Today.Month, 1).AddDays(-1);

    public long Next(string key) => _seq[key] = _seq.GetValueOrDefault(key) + 1;

    public string CifNo(Branch b)
    {
        var p = $"{b.Code}{Next($"CIF-{b.Code}"):000000}";
        return p + Money.CheckDigit(p);
    }

    public string AccountNo(Branch b, string productCode)
    {
        var p = $"{b.Code}{productCode}{Next($"ACC-{b.Code}{productCode}"):0000000}";
        return p + Money.CheckDigit(p);
    }

    public string InstrumentNo(RemittanceType t, Branch b) => $"{t}{b.Code}{Next($"{t}-{b.Code}"):000000}";

    private string VoucherNo(Branch b, DateTime d) => $"V{b.Code}{d:yyMMdd}{Next($"VCH-{b.Code}-{d:yyyyMMdd}"):00000}";

    public Branch BranchOf(Account a) => Branches.First(b => b.Id == a.BranchId);

    public DateTime WorkDay(DateTime d)
    {
        while (d.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday) d = d.AddDays(-1);
        return d.Date;
    }

    public DateTime RandomDay(DateTime from, DateTime to)
    {
        var span = Math.Max(0, (to - from).Days);
        return WorkDay(from.AddDays(Rng.Next(0, span + 1)));
    }

    public string Maker() => Rng.Next(3) == 0 ? OfficerUser : TellerUser;
    public string Checker(bool managerLevel) => managerLevel || Rng.Next(3) == 0 ? ManagerUser : SupervisorUser;

    public decimal Available(Account a) => a.Balance - a.LienAmount + a.OdLimit - (a.Product?.MinBalance ?? 0);

    public void Enqueue(DateTime date, Action run) => _queue.Add((date.Date, _queue.Count, run));

    public void Defer(Action run) => _deferred.Add(run);

    public void RunQueue()
    {
        foreach (var e in _queue.OrderBy(x => x.Date).ThenBy(x => x.Order)) e.Run();
        _queue.Clear();
    }

    public void RunDeferred()
    {
        foreach (var a in _deferred) a();
        _deferred.Clear();
    }

    public Voucher Post(VoucherType type, Branch br, DateTime date, string desc, IEnumerable<Ln> lines,
        VoucherStatus status = VoucherStatus.Authorized, string? sourceKind = null, int? sourceId = null, string? chequeNo = null,
        string? counterParty = null, bool system = false, string? rejectReason = null, string? maker = null)
    {
        var ls = lines.Where(l => l.Dr + l.Cr > 0).ToList();
        var total = ls.Sum(l => l.Dr);
        var level = total >= Level2Amount ? 2 : 1;
        var v = new Voucher
        {
            VoucherNo = VoucherNo(br, date), Type = type, Status = status, BranchId = br.Id, TxnDate = date, ValueDate = date,
            CurrencyId = Bdt.Id, TotalAmount = total, Description = desc, ChequeNo = chequeNo, CounterParty = counterParty,
            RequiredLevel = level, MakerId = system ? "SYSTEM" : maker ?? Maker(), MadeAt = date.AddHours(9).AddMinutes(Rng.Next(0, 480)),
            Workstation = $"WS-{br.Code}", SourceKind = sourceKind, SourceId = sourceId, IsSystem = system, RejectReason = rejectReason
        };
        if (status != VoucherStatus.Pending)
        {
            v.CheckerId = system ? "SYSTEM" : Checker(level == 2);
            v.CheckedAt = v.MadeAt.AddMinutes(Rng.Next(5, 120));
        }
        Db.Vouchers.Add(v);
        foreach (var l in ls)
        {
            v.Lines.Add(new VoucherLine
            {
                BranchId = l.Acc?.BranchId ?? br.Id,
                GlAccountId = l.Acc != null ? l.Acc.Product!.GlAccountId : l.Gl!.Value,
                AccountId = l.Acc?.Id, LoanId = l.Loan?.Id, LoanComponent = l.Comp, Debit = l.Dr, Credit = l.Cr, Narration = l.Narr
            });
            if (l.Acc == null || status != VoucherStatus.Authorized) continue;
            var a = l.Acc;
            a.Balance = Money.R(a.Balance + l.Cr - l.Dr);
            if (type is not (VoucherType.InterestPosting or VoucherType.Eod)) a.LastTxnDate = date;
            Db.AccountTransactions.Add(new AccountTransaction
            {
                Account = a, Voucher = v, TxnDate = date, ValueDate = date, Debit = l.Dr, Credit = l.Cr,
                RunningBalance = a.Balance, Narration = l.Narr ?? desc, ChequeNo = l.Dr > 0 ? chequeNo : null, PostedAt = v.CheckedAt ?? v.MadeAt
            });
        }
        return v;
    }

    public Voucher CashIn(Account a, DateTime date, decimal amount, string desc, VoucherType type = VoucherType.CashDeposit,
        string? sourceKind = null, int? sourceId = null, string? depositor = null, VoucherStatus status = VoucherStatus.Authorized) =>
        Post(type, BranchOf(a), date, desc,
        [new(Gl["1100"], null, amount, 0, "Cash received"), new(null, a, 0, amount, desc)],
        status, sourceKind, sourceId, counterParty: depositor);

    public Voucher CashOut(Account a, DateTime date, decimal amount, string desc, string? chequeNo = null, string? sourceKind = null,
        int? sourceId = null, string? withdrawer = null, VoucherStatus status = VoucherStatus.Authorized, string? rejectReason = null) =>
        Post(VoucherType.CashWithdrawal, BranchOf(a), date, desc,
        [new(null, a, amount, 0, desc), new(Gl["1100"], null, 0, amount, "Cash paid")],
        status, sourceKind, sourceId, chequeNo, withdrawer, rejectReason: rejectReason);

    public void Fund(Account a, DateTime date, decimal needed)
    {
        var gap = needed - Available(a);
        if (gap <= 0) return;
        CashIn(a, date, Math.Ceiling((gap + 1000m) / 1000m) * 1000m, "Cash deposit " + a.AccountNo);
    }

    public Voucher Charge(Account a, DateTime date, string desc, decimal fee)
    {
        fee = Money.R(fee);
        var vat = Money.R(fee * VatRate / 100m);
        Fund(a, date, fee + vat);
        return Post(VoucherType.Charge, BranchOf(a), date, desc,
        [
            new(null, a, fee + vat, 0, desc), new(Gl["4400"], null, 0, fee, desc, null, null), new(Gl["2610"], null, 0, vat, "VAT on " + desc)
        ]);
    }

    public async Task PersistSequencesAsync()
    {
        _seq["CHQ-GLOBAL"] = ChequeSeq;
        var existing = await Db.NumberSequences.ToDictionaryAsync(n => n.Key);
        foreach (var (k, v) in _seq)
        {
            if (existing.TryGetValue(k, out var row)) row.LastValue = Math.Max(row.LastValue, v);
            else Db.NumberSequences.Add(new NumberSequence { Key = k, LastValue = v });
        }
        await Db.SaveChangesAsync();
    }
}
