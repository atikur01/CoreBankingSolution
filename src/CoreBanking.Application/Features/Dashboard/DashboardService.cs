namespace CoreBanking.Application.Features.Dashboard;

public class DashboardSummary
{
    public int Customers, ActiveAccounts, PendingVouchers, PendingAccounts, PendingLoans, PendingKyc, TodayTxns, CtrToday, NplLoans, RunningLoans;
    public decimal TotalDeposits, LoanOutstanding, TodayDebit, TodayCredit, Provision;
    public List<(string Name, decimal Amount)> DepositByProduct = [];
    public List<Branch> Branches = [];
    public List<Voucher> Recent = [];
    public List<(DateTime Date, decimal Credit, decimal Debit)> Trend = [];
    public List<(LoanClass Class, int Count, decimal Amount)> LoanMix = [];
    public string UserName = "";
    public string BranchName = "";
    public DateTime BusinessDate;
}

public class DashboardService(IApplicationDbContext db, ParamService prm)
{
    private static readonly AccountStatus[] Excluded = [AccountStatus.Closed, AccountStatus.PendingApproval];

    public async Task<DashboardSummary> GetAsync(UserProfile? profile, string userName)
    {
        var vm = new DashboardSummary();
        var branch = profile?.Branch ?? await db.Branches.FirstOrDefaultAsync();
        var d = branch?.BusinessDate ?? DateTime.Today;

        vm.UserName = profile?.FullName ?? userName;
        vm.BranchName = branch == null ? "-" : $"{branch.Code} - {branch.Name}";
        vm.BusinessDate = d;

        vm.Customers = await db.Customers.CountAsync(c => c.IsActive);
        vm.ActiveAccounts = await db.Accounts.CountAsync(a => a.Status == AccountStatus.Active);
        vm.PendingVouchers = await db.Vouchers.CountAsync(v => v.Status == VoucherStatus.Pending);
        vm.PendingAccounts = await db.Accounts.CountAsync(a => a.Status == AccountStatus.PendingApproval);
        vm.PendingLoans = await db.Loans.CountAsync(l => l.Status == LoanStatus.Applied);
        vm.PendingKyc = await db.Customers.CountAsync(c => c.Kyc == KycStatus.Pending);
        vm.TotalDeposits = await db.Accounts.Where(a => !Excluded.Contains(a.Status)).SumAsync(a => (decimal?)a.Balance) ?? 0;
        vm.LoanOutstanding = await db.Loans.Where(l => l.Status == LoanStatus.Disbursed).SumAsync(l => (decimal?)l.OutstandingPrincipal) ?? 0;
        vm.RunningLoans = await db.Loans.CountAsync(l => l.Status == LoanStatus.Disbursed);
        vm.NplLoans = await db.Loans.CountAsync(l => l.Status == LoanStatus.Disbursed && l.Classification >= LoanClass.SubStandard);
        vm.Provision = await db.Loans.SumAsync(l => (decimal?)l.ProvisionHeld) ?? 0;

        vm.TodayTxns = await db.Vouchers.CountAsync(v => v.TxnDate == d && v.Status == VoucherStatus.Authorized);
        vm.TodayDebit = await db.AccountTransactions.Where(t => t.TxnDate == d).SumAsync(t => (decimal?)t.Debit) ?? 0;
        vm.TodayCredit = await db.AccountTransactions.Where(t => t.TxnDate == d).SumAsync(t => (decimal?)t.Credit) ?? 0;
        var ctr = await prm.DecAsync("CtrThreshold");
        vm.CtrToday = await db.Vouchers.CountAsync(v => v.TxnDate == d && v.Status == VoucherStatus.Authorized
            && (v.Type == VoucherType.CashDeposit || v.Type == VoucherType.CashWithdrawal) && v.TotalAmount >= ctr);

        vm.DepositByProduct = (await db.Accounts.Where(a => !Excluded.Contains(a.Status))
            .GroupBy(a => a.Product!.Name).Select(g => new { g.Key, Total = g.Sum(x => x.Balance) }).ToListAsync())
            .Select(x => (x.Key, x.Total)).ToList();
        vm.Branches = await db.Branches.AsNoTracking().OrderBy(b => b.Code).ToListAsync();
        vm.Recent = await db.Vouchers.AsNoTracking().OrderByDescending(v => v.Id).Take(8).ToListAsync();

        var since = d.AddDays(-13);
        var daily = await db.AccountTransactions.AsNoTracking().Where(t => t.TxnDate >= since && t.TxnDate <= d)
            .GroupBy(t => t.TxnDate).Select(g => new { g.Key, Cr = g.Sum(x => x.Credit), Dr = g.Sum(x => x.Debit) }).ToListAsync();
        vm.Trend = Enumerable.Range(0, 14).Select(i => since.AddDays(i))
            .Select(day => (day, daily.FirstOrDefault(x => x.Key == day)?.Cr ?? 0, daily.FirstOrDefault(x => x.Key == day)?.Dr ?? 0)).ToList();

        var mix = await db.Loans.AsNoTracking().Where(l => l.Status == LoanStatus.Disbursed)
            .GroupBy(l => l.Classification).Select(g => new { g.Key, N = g.Count(), A = g.Sum(x => x.OutstandingPrincipal) }).ToListAsync();
        vm.LoanMix = Enum.GetValues<LoanClass>()
            .Select(c => (c, mix.FirstOrDefault(x => x.Key == c)?.N ?? 0, mix.FirstOrDefault(x => x.Key == c)?.A ?? 0)).ToList();
        return vm;
    }
}
