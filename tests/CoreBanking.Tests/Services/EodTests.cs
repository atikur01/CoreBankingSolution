namespace CoreBanking.Tests.Services;

[Collection(DatabaseCollection.Name)]
public class EodTests(DatabaseFixture fx)
{
    private static Task<EodService.Result> RunAsync(World w) => w.Do(s => s.Eod.RunAsync(w.Branch.Id, "eod.user"));

    [Fact]
    public async Task Pending_vouchers_block_the_day_end()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();
        await w.DepositAsync(acc, 100m, authorize: false);

        var ex = await Assert.ThrowsAsync<BankingException>(() => RunAsync(w));

        Assert.Contains("unauthorized voucher", ex.Message);
        Assert.Equal(new DateTime(2026, 3, 9), await w.CurrentDateAsync());
    }

    [Fact]
    public async Task Pending_accounts_and_unsettled_clearing_items_also_block_the_day_end()
    {
        var w = await World.CreateAsync(fx);
        var product = await w.NewProductAsync(ProductCategory.Savings, p => p.MinOpeningBalance = 0);
        var customer = await w.NewCustomerAsync();
        await w.Do(s => s.Deposits.OpenAsync(new OpenAccountInput { CustomerId = customer.Id, ProductId = product.Id, BranchId = w.Branch.Id }, World.Maker, null));

        var errors = await w.Do(s => s.Eod.PreChecksAsync(w.Branch.Id));

        Assert.Contains(errors, e => e.Contains("awaiting approval"));
    }

    [Fact]
    public async Task Day_end_moves_the_business_date_to_the_next_working_day_skipping_weekly_holidays()
    {
        var w = await World.CreateAsync(fx, new DateTime(2026, 3, 12));

        var result = await RunAsync(w);

        Assert.True(result.Success, result.Error);
        Assert.Equal(new DateTime(2026, 3, 15), result.NewBusinessDate);
        Assert.Equal(new DateTime(2026, 3, 15), await w.CurrentDateAsync());
        var logs = await w.Read(db => db.EodLogs.AsNoTracking().Where(l => l.BranchId == w.Branch.Id).ToListAsync());
        Assert.Contains(logs, l => l.Status == EodStatus.Success && l.Step.StartsWith("EOD completed"));
        Assert.DoesNotContain(logs, l => l.Status == EodStatus.Failed);
    }

    [Fact]
    public async Task Configured_holidays_are_skipped_too()
    {
        var w = await World.CreateAsync(fx, new DateTime(2026, 4, 5));
        await w.Do(async s => { s.Db.Holidays.Add(new Holiday { Date = new DateTime(2026, 4, 6), Description = "Test holiday" }); await s.Db.SaveChangesAsync(); });

        var result = await RunAsync(w);

        Assert.Equal(new DateTime(2026, 4, 7), result.NewBusinessDate);
    }

    [Fact]
    public async Task Daily_interest_accrues_on_interest_bearing_balances_and_posts_to_the_gl()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 36_500m);

        var result = await RunAsync(w);

        Assert.True(result.Success, result.Error);
        var stored = await w.AccountAsync(acc.Id);
        Assert.Equal(4.0m, Money.R(stored.AccruedInterest));
        Assert.Equal(4.0m, await w.GlBalanceAsync("5100"));
        Assert.Equal(-4.0m, await w.GlBalanceAsync("2300"));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Month_end_pays_accrued_interest_less_source_tax_for_monthly_products()
    {
        var w = await World.CreateAsync(fx, new DateTime(2026, 3, 31));
        var product = await w.NewProductAsync(ProductCategory.Savings, p => p.PayFrequency = PayFrequency.Monthly);
        var acc = await w.OpenAccountAsync(deposit: 36_500m, tweak: i => i.ProductId = product.Id);
        await w.UpdateAccountAsync(acc.Id, a => a.AccruedInterest = 100m);

        var result = await RunAsync(w);

        Assert.True(result.Success, result.Error);
        var stored = await w.AccountAsync(acc.Id);
        Assert.InRange(stored.AccruedInterest, 0m, 5m);
        Assert.Equal(36_500m + 93.6m, stored.Balance);
        Assert.Equal(new DateTime(2026, 4, 1), await w.CurrentDateAsync());
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Overdraft_balances_accrue_penal_interest()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(ProductCategory.Current, 5_000m, tweak: i => i.OdLimit = 10_000m);
        await w.WithdrawAsync(acc, 7_000m);

        var result = await RunAsync(w);

        Assert.True(result.Success, result.Error);
        var stored = await w.AccountAsync(acc.Id);
        Assert.Equal(-2_000m, stored.Balance);
        Assert.Equal(0.66m, Money.R(stored.OdInterestAccrued));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Standing_instructions_run_on_their_due_date_and_reschedule()
    {
        var w = await World.CreateAsync(fx);
        var from = await w.OpenAccountAsync(deposit: 10_000m);
        var to = await w.OpenAccountAsync(deposit: 1_000m);
        var si = await w.Do(async s =>
        {
            var e = new StandingInstruction { FromAccountId = from.Id, ToAccountId = to.Id, Amount = 2_500m, Frequency = SiFrequency.Monthly, StartDate = w.Today, NextRunDate = w.Today };
            s.Db.StandingInstructions.Add(e);
            await s.Db.SaveChangesAsync();
            return e;
        });

        var result = await RunAsync(w);

        Assert.True(result.Success, result.Error);
        Assert.Equal(7_500m, await w.BalanceAsync(from.Id));
        Assert.Equal(3_500m, await w.BalanceAsync(to.Id));
        var stored = await w.Read(db => db.StandingInstructions.AsNoTracking().FirstAsync(x => x.Id == si.Id));
        Assert.Equal(1, stored.ExecutedCount);
        Assert.Equal(w.Today.AddMonths(1), stored.NextRunDate);
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task A_standing_instruction_without_funds_is_deferred_without_blocking_the_day_end()
    {
        var w = await World.CreateAsync(fx);
        var from = await w.OpenAccountAsync(deposit: 1_000m);
        var to = await w.OpenAccountAsync(deposit: 1_000m);
        var si = await w.Do(async s =>
        {
            var e = new StandingInstruction { FromAccountId = from.Id, ToAccountId = to.Id, Amount = 5_000m, Frequency = SiFrequency.Monthly, StartDate = w.Today, NextRunDate = w.Today };
            s.Db.StandingInstructions.Add(e);
            await s.Db.SaveChangesAsync();
            return e;
        });

        var result = await RunAsync(w);

        Assert.True(result.Success, result.Error);
        var stored = await w.Read(db => db.StandingInstructions.AsNoTracking().FirstAsync(x => x.Id == si.Id));
        Assert.Equal(1, stored.FailedCount);
        Assert.Equal(0, stored.ExecutedCount);
        Assert.Equal(1_000m, await w.BalanceAsync(from.Id));
    }

    [Fact]
    public async Task A_standing_instruction_that_cannot_post_does_not_block_the_day_end()
    {
        var w = await World.CreateAsync(fx);
        var from = await w.OpenAccountAsync(ProductCategory.FDR, 50_000m);
        var to = await w.OpenAccountAsync(deposit: 1_000m);
        var si = await w.Do(async s =>
        {
            var e = new StandingInstruction { FromAccountId = from.Id, ToAccountId = to.Id, Amount = 100m, Frequency = SiFrequency.Monthly, StartDate = w.Today, NextRunDate = w.Today };
            s.Db.StandingInstructions.Add(e);
            await s.Db.SaveChangesAsync();
            return e;
        });

        var result = await RunAsync(w);

        Assert.True(result.Success, result.Error);
        var stored = await w.Read(db => db.StandingInstructions.AsNoTracking().FirstAsync(x => x.Id == si.Id));
        Assert.Equal(1, stored.FailedCount);
    }

    [Fact]
    public async Task Idle_accounts_are_flagged_dormant_and_then_inoperative()
    {
        var w = await World.CreateAsync(fx);
        var dormant = await w.OpenAccountAsync();
        var inoperative = await w.OpenAccountAsync();
        var fresh = await w.OpenAccountAsync();
        await w.UpdateAccountAsync(dormant.Id, a => a.LastTxnDate = w.Today.AddDays(-400));
        await w.UpdateAccountAsync(inoperative.Id, a => a.LastTxnDate = w.Today.AddDays(-1_200));

        await RunAsync(w);

        Assert.Equal(AccountStatus.Dormant, (await w.AccountAsync(dormant.Id)).Status);
        Assert.Equal(AccountStatus.Inoperative, (await w.AccountAsync(inoperative.Id)).Status);
        Assert.Equal(AccountStatus.Active, (await w.AccountAsync(fresh.Id)).Status);
    }

    [Fact]
    public async Task Matured_fdrs_are_flagged_or_renewed_with_interest_capitalised_net_of_tax()
    {
        var w = await World.CreateAsync(fx);
        var plain = await w.OpenAccountAsync(ProductCategory.FDR, 100_000m, tweak: i => i.TermMonths = 6);
        var renewing = await w.OpenAccountAsync(ProductCategory.FDR, 100_000m, tweak: i => { i.TermMonths = 6; i.AutoRenew = true; });
        await w.UpdateAccountAsync(plain.Id, a => a.MaturityDate = w.Today);
        await w.UpdateAccountAsync(renewing.Id, a => { a.MaturityDate = w.Today; a.AccruedInterest = 4_000m; });

        var result = await RunAsync(w);

        Assert.True(result.Success, result.Error);
        Assert.Equal(AccountStatus.Matured, (await w.AccountAsync(plain.Id)).Status);
        var renewed = await w.AccountAsync(renewing.Id);
        Assert.Equal(AccountStatus.Active, renewed.Status);
        var interest = Money.R(4_000m + 100_000m * 8m / 100m / 365m);
        var expected = 100_000m + interest - Money.R(interest * 0.10m);
        Assert.Equal(expected, renewed.Balance);
        Assert.Equal(expected, renewed.PrincipalAmount);
        Assert.Equal(w.Today.AddMonths(6), renewed.MaturityDate);
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task A_failing_step_rolls_the_whole_day_end_back()
    {
        var w = await World.CreateAsync(fx);
        var broken = await w.NewProductAsync(ProductCategory.FDR, p => p.AccrualGlId = null);
        var savings = await w.OpenAccountAsync(deposit: 36_500m);
        var fdr = await w.OpenAccountAsync(ProductCategory.FDR, 100_000m, tweak: i => { i.ProductId = broken.Id; i.TermMonths = 6; i.AutoRenew = true; });
        await w.UpdateAccountAsync(fdr.Id, a => { a.MaturityDate = w.Today; a.AccruedInterest = 1_000m; });

        var result = await RunAsync(w);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(new DateTime(2026, 3, 9), await w.CurrentDateAsync());
        Assert.Equal(0m, (await w.AccountAsync(savings.Id)).AccruedInterest);
        var logs = await w.Read(db => db.EodLogs.AsNoTracking().Where(l => l.BranchId == w.Branch.Id).ToListAsync());
        Assert.Contains(logs, l => l.Status == EodStatus.Failed);
        Assert.DoesNotContain(logs, l => l.Step == "Deposit interest accrual");
    }

    [Fact]
    public async Task Overdue_loans_are_classified_and_provisioned_at_month_end()
    {
        var w = await World.CreateAsync(fx, new DateTime(2026, 3, 9));
        var customer = await w.NewCustomerAsync();
        var settlement = await w.OpenAccountAsync(deposit: 10_000m, customer: customer);
        var product = await w.ProductAsync(ProductCategory.TermLoan);
        var loan = await w.Do(s => s.Loans.ApplyAsync(new Loan
        {
            CustomerId = customer.Id, ProductId = product.Id, BranchId = w.Branch.Id, SettlementAccountId = settlement.Id,
            Principal = 100_000m, TermMonths = 12, InterestRate = 12m, Purpose = "Business"
        }, World.Maker));
        await w.Do(s => s.Loans.ApproveAsync(loan.Id, World.Checker, true, null));
        var v = await w.Do(s => s.Loans.DisburseAsync(loan.Id, World.Maker, null));
        await w.AuthorizeAsync(v.Id);
        await w.SetBusinessDateAsync(new DateTime(2026, 6, 30));

        var result = await RunAsync(w);

        Assert.True(result.Success, result.Error);
        var stored = await w.Read(db => db.Loans.AsNoTracking().FirstAsync(l => l.Id == loan.Id));
        Assert.True(stored.OverdueDays >= 60);
        Assert.NotEqual(LoanClass.Standard, stored.Classification);
        Assert.True(stored.ProvisionHeld > 0);
        Assert.Equal(stored.ProvisionHeld, await w.GlBalanceAsync("2700") * -1);
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Year_end_closes_income_and_expense_to_retained_earnings_and_charges_excise_duty()
    {
        var w = await World.CreateAsync(fx, new DateTime(2026, 12, 31));
        var rich = await w.OpenAccountAsync(ProductCategory.Current, 200_000m);
        var (expense, income) = await w.Read(async db => (
            (await db.GlAccounts.AsNoTracking().FirstAsync(g => g.Code == "5300")).Id,
            (await db.GlAccounts.AsNoTracking().FirstAsync(g => g.Code == "4400")).Id));
        var req = new VoucherReq { Type = VoucherType.Journal, BranchId = w.Branch.Id, Description = "year activity", Maker = World.Maker };
        req.Dr(expense, null, 400m, null, w.Branch.Id);
        req.Cr(income, null, 1_000m, null, w.Branch.Id);
        req.Dr(await w.Do(s => s.Params.GlIdAsync("CashGl")), null, 600m, null, w.Branch.Id);
        var journal = await w.Do(s => s.Ledger.CreateAsync(req));
        await w.AuthorizeAsync(journal.Id);

        var result = await RunAsync(w);

        Assert.True(result.Success, result.Error);
        Assert.Equal(new DateTime(2027, 1, 3), result.NewBusinessDate);
        Assert.Equal(0m, await w.GlBalanceAsync("5300"));
        Assert.Equal(0m, await w.GlBalanceAsync("4400"));
        Assert.Equal(-600m, await w.GlBalanceAsync("3200"));
        Assert.Equal(-150m, await w.GlBalanceAsync("2630"));
        Assert.Equal(200_000m - 150m, await w.BalanceAsync(rich.Id));
        await w.AssertBooksConsistentAsync();
    }
}
