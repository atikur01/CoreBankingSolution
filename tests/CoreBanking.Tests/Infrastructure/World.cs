namespace CoreBanking.Tests.Infrastructure;

public sealed class World
{
    public const string Maker = "maker.user";
    public const string Checker = "checker.user";
    public const string Other = "other.user";

    private static int _branchSeq = 100;
    private static long _customerSeq = 1_000_000_000;
    private static int _productSeq = 9;
    private readonly DatabaseFixture _fx;

    private World(DatabaseFixture fx) => _fx = fx;

    public Branch Branch { get; private set; } = null!;
    public DateTime Today => Branch.BusinessDate;

    public static async Task<World> CreateAsync(DatabaseFixture fx, DateTime? businessDate = null)
    {
        var w = new World(fx);
        var code = Interlocked.Increment(ref _branchSeq).ToString("000");
        w.Branch = await w.Do(async s =>
        {
            var b = new Branch { Code = code, Name = "Test Branch " + code, Layer = "Branch", BusinessDate = businessDate ?? new DateTime(2026, 3, 9), DayOpen = true, IsActive = true };
            s.Db.Branches.Add(b);
            await s.Db.SaveChangesAsync();
            return b;
        });
        return w;
    }

    public Svc Open() => new(_fx.Services.CreateScope());

    public async Task<T> Do<T>(Func<Svc, Task<T>> work)
    {
        using var s = Open();
        return await work(s);
    }

    public async Task Do(Func<Svc, Task> work)
    {
        using var s = Open();
        await work(s);
    }

    public Task<T> Read<T>(Func<IApplicationDbContext, Task<T>> query) => Do(s => query(s.Db));

    public Task<Product> ProductAsync(ProductCategory category) =>
        Read(db => db.Products.AsNoTracking().FirstAsync(p => p.Category == category));

    public Task<Product> NewProductAsync(ProductCategory category, Action<Product>? tweak = null) =>
        Do(async s =>
        {
            var template = await s.Db.Products.AsNoTracking().FirstAsync(p => p.Category == category);
            var seq = Interlocked.Increment(ref _productSeq);
            var product = new Product
            {
                Code = seq.ToString("00"), Name = $"Test {category} {seq}", Category = category, GlAccountId = template.GlAccountId,
                InterestGlId = template.InterestGlId, AccrualGlId = template.AccrualGlId, InterestRate = template.InterestRate,
                OverdraftRate = template.OverdraftRate, PrematureRate = template.PrematureRate, PayFrequency = template.PayFrequency,
                MinOpeningBalance = template.MinOpeningBalance, DefaultTermMonths = template.DefaultTermMonths, ProcessingFeePct = template.ProcessingFeePct,
                IsInterestBearing = template.IsInterestBearing, ChequeBookFee = template.ChequeBookFee, StopPaymentFee = template.StopPaymentFee,
                ChequeReturnFee = template.ChequeReturnFee, ClosingFee = template.ClosingFee
            };
            tweak?.Invoke(product);
            s.Db.Products.Add(product);
            await s.Db.SaveChangesAsync();
            return product;
        });

    public Task UpdateAsync<T>(Func<IApplicationDbContext, DbSet<T>> set, int id, Action<T> change) where T : class =>
        Do(async s =>
        {
            var entity = await set(s.Db).FindAsync(id) ?? throw new InvalidOperationException("Entity not found.");
            change(entity);
            await s.Db.SaveChangesAsync();
        });

    public Task UpdateAccountAsync(int id, Action<Account> change) => UpdateAsync(db => db.Accounts, id, change);

    public Task SetBusinessDateAsync(DateTime date) =>
        UpdateAsync(db => db.Branches, Branch.Id, b => b.BusinessDate = date).ContinueWith(_ => Branch.BusinessDate = date);

    public async Task<DateTime> CurrentDateAsync() => (await Read(db => db.Branches.AsNoTracking().FirstAsync(b => b.Id == Branch.Id))).BusinessDate;

    public Task<Customer> NewCustomerAsync(bool verified = true, CustomerType type = CustomerType.Individual, string? name = null, bool active = true) =>
        Do(async s =>
        {
            var n = Interlocked.Increment(ref _customerSeq);
            var c = new Customer
            {
                CifNo = await s.Numbers.NextCifAsync(Branch.Code), Type = type, Name = name ?? $"Test Customer {n}", Nid = n.ToString(),
                Mobile = "017" + (n % 100_000_000).ToString("00000000"), Address = "Dhaka", BranchId = Branch.Id,
                Kyc = verified ? KycStatus.Verified : KycStatus.Pending, IsActive = active, MadeBy = Maker,
                TradeLicense = type is CustomerType.Individual ? null : "TL-" + n
            };
            s.Db.Customers.Add(c);
            await s.Db.SaveChangesAsync();
            return c;
        });

    public Task<Voucher> AuthorizeAsync(int voucherId, bool manager = true, string checker = Checker) =>
        Do(async s =>
        {
            await s.Ledger.AuthorizeAsync(voucherId, checker, manager);
            return await s.Db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == voucherId);
        });

    public Task<Voucher> PendingVoucherForAsync(string sourceKind, int sourceId) =>
        Read(db => db.Vouchers.AsNoTracking().OrderByDescending(v => v.Id)
            .FirstAsync(v => v.SourceKind == sourceKind && v.SourceId == sourceId && v.Status == VoucherStatus.Pending));

    public async Task<Account> OpenAccountAsync(ProductCategory category = ProductCategory.Savings, decimal deposit = 10_000m, Customer? customer = null, Action<OpenAccountInput>? tweak = null)
    {
        customer ??= await NewCustomerAsync();
        var product = await ProductAsync(category);
        var created = await Do(s =>
        {
            var input = new OpenAccountInput { CustomerId = customer.Id, ProductId = product.Id, BranchId = Branch.Id, InitialDeposit = deposit };
            if (category == ProductCategory.DPS) input.InstallmentAmount = deposit;
            tweak?.Invoke(input);
            return s.Deposits.OpenAsync(input, Maker, null);
        });
        var voucher = await PendingVoucherForAsync("Account", created.Id);
        await AuthorizeAsync(voucher.Id);
        return await AccountAsync(created.Id);
    }

    public Task<Account> AccountAsync(int id) =>
        Read(db => db.Accounts.AsNoTracking().Include(a => a.Product).FirstAsync(a => a.Id == id));

    public async Task<decimal> BalanceAsync(int accountId) => (await AccountAsync(accountId)).Balance;

    public async Task<Voucher> DepositAsync(Account a, decimal amount, bool authorize = true)
    {
        var v = await Do(s => s.Transactions.DepositAsync(a.Id, amount, null, null, Maker, null));
        return authorize ? await AuthorizeAsync(v.Id) : v;
    }

    public async Task<Voucher> WithdrawAsync(Account a, decimal amount, bool authorize = true)
    {
        var v = await Do(s => s.Transactions.WithdrawAsync(a.Id, amount, null, null, null, Maker, null));
        return authorize ? await AuthorizeAsync(v.Id) : v;
    }

    public Task<decimal> GlBalanceAsync(string glCode) =>
        Read(async db => await db.VoucherLines.AsNoTracking()
            .Where(l => l.BranchId == Branch.Id && l.GlAccount!.Code == glCode
                        && (l.Voucher!.Status == VoucherStatus.Authorized || l.Voucher.Status == VoucherStatus.Reversed))
            .SumAsync(l => (decimal?)(l.Debit - l.Credit)) ?? 0);

    public async Task AssertBooksConsistentAsync()
    {
        await Read(async db =>
        {
            var lines = await db.VoucherLines.AsNoTracking()
                .Where(l => l.BranchId == Branch.Id && (l.Voucher!.Status == VoucherStatus.Authorized || l.Voucher.Status == VoucherStatus.Reversed))
                .Select(l => new { l.Debit, l.Credit }).ToListAsync();
            Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));

            var accounts = await db.Accounts.AsNoTracking().Where(a => a.BranchId == Branch.Id).ToListAsync();
            foreach (var a in accounts)
            {
                var txns = await db.AccountTransactions.AsNoTracking().Where(t => t.AccountId == a.Id).OrderBy(t => t.Id).ToListAsync();
                Assert.Equal(txns.Sum(t => t.Credit - t.Debit), a.Balance);
                decimal running = 0;
                foreach (var t in txns)
                {
                    running += t.Credit - t.Debit;
                    Assert.Equal(running, t.RunningBalance);
                }
            }
            return true;
        });
    }
}
