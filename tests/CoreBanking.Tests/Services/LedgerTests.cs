namespace CoreBanking.Tests.Services;

[Collection(DatabaseCollection.Name)]
public class LedgerTests(DatabaseFixture fx)
{
    [Fact]
    public async Task Deposit_creates_a_pending_voucher_without_moving_the_balance()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 10_000m);

        var v = await w.DepositAsync(acc, 5_000m, authorize: false);

        Assert.Equal(VoucherStatus.Pending, v.Status);
        Assert.Equal(5_000m, v.TotalAmount);
        Assert.Equal(10_000m, await w.BalanceAsync(acc.Id));
    }

    [Fact]
    public async Task Authorization_posts_balance_statement_line_and_general_ledger()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 10_000m);

        var v = await w.DepositAsync(acc, 5_000m);

        Assert.Equal(VoucherStatus.Authorized, v.Status);
        Assert.Equal(World.Checker, v.CheckerId);
        Assert.Equal(15_000m, await w.BalanceAsync(acc.Id));
        var last = await w.Read(db => db.AccountTransactions.AsNoTracking().OrderByDescending(t => t.Id).FirstAsync(t => t.AccountId == acc.Id));
        Assert.Equal(5_000m, last.Credit);
        Assert.Equal(15_000m, last.RunningBalance);
        Assert.Equal(15_000m, await w.GlBalanceAsync("1100"));
        Assert.Equal(-15_000m, await w.GlBalanceAsync("2110"));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Maker_cannot_authorize_their_own_voucher()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();
        var v = await w.DepositAsync(acc, 100m, authorize: false);

        var ex = await Assert.ThrowsAsync<BankingException>(() => w.AuthorizeAsync(v.Id, checker: World.Maker));

        Assert.Contains("Maker-checker", ex.Message);
        Assert.Equal(10_000m, await w.BalanceAsync(acc.Id));
    }

    [Fact]
    public async Task Authorizing_twice_is_rejected_and_posts_only_once()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();
        var v = await w.DepositAsync(acc, 100m);

        await Assert.ThrowsAsync<BankingException>(() => w.AuthorizeAsync(v.Id));

        Assert.Equal(10_100m, await w.BalanceAsync(acc.Id));
    }

    [Fact]
    public async Task Concurrent_authorization_of_the_same_voucher_posts_exactly_once()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();
        var v = await w.DepositAsync(acc, 700m, authorize: false);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
        {
            try { await w.AuthorizeAsync(v.Id, checker: "checker." + i); return true; }
            catch (BankingException) { return false; }
        }));

        Assert.Equal(1, attempts.Count(ok => ok));
        Assert.Equal(10_700m, await w.BalanceAsync(acc.Id));
        await w.AssertBooksConsistentAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Non_positive_amounts_are_rejected_with_a_clear_message(int amount)
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();

        var deposit = await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Transactions.DepositAsync(acc.Id, amount, null, null, World.Maker, null)));
        var withdraw = await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Transactions.WithdrawAsync(acc.Id, amount, null, null, null, World.Maker, null)));

        Assert.Contains("greater than zero", deposit.Message);
        Assert.Contains("greater than zero", withdraw.Message);
    }

    [Fact]
    public async Task Withdrawal_above_available_balance_is_refused()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 1_000m);

        var ex = await Assert.ThrowsAsync<BankingException>(() => w.WithdrawAsync(acc, 2_000m, authorize: false));

        Assert.Contains("Insufficient funds", ex.Message);
    }

    [Fact]
    public async Task Second_pending_withdrawal_fails_at_authorization_when_funds_ran_out()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 1_000m);
        var first = await w.WithdrawAsync(acc, 800m, authorize: false);
        var second = await w.WithdrawAsync(acc, 800m, authorize: false);

        await w.AuthorizeAsync(first.Id);
        var ex = await Assert.ThrowsAsync<BankingException>(() => w.AuthorizeAsync(second.Id));

        Assert.Contains("Insufficient funds", ex.Message);
        Assert.Equal(200m, await w.BalanceAsync(acc.Id));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Lien_amount_is_not_available_for_withdrawal()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 10_000m);
        await w.Do(async s => { (await s.Db.Accounts.FindAsync(acc.Id))!.LienAmount = 9_000m; await s.Db.SaveChangesAsync(); });

        await Assert.ThrowsAsync<BankingException>(() => w.WithdrawAsync(acc, 1_500m, authorize: false));
        var ok = await w.WithdrawAsync(acc, 1_000m);

        Assert.Equal(VoucherStatus.Authorized, ok.Status);
    }

    [Fact]
    public async Task Rejection_requires_a_reason_and_leaves_the_balance_untouched()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();
        var v = await w.DepositAsync(acc, 100m, authorize: false);

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Ledger.RejectAsync(v.Id, World.Checker, " ")));
        await w.Do(s => s.Ledger.RejectAsync(v.Id, World.Checker, "wrong account"));

        var stored = await w.Read(db => db.Vouchers.AsNoTracking().FirstAsync(x => x.Id == v.Id));
        Assert.Equal(VoucherStatus.Rejected, stored.Status);
        Assert.Equal("wrong account", stored.RejectReason);
        Assert.Equal(10_000m, await w.BalanceAsync(acc.Id));
        await Assert.ThrowsAsync<BankingException>(() => w.AuthorizeAsync(v.Id));
    }

    [Fact]
    public async Task Amounts_at_or_above_the_level2_threshold_need_manager_authorization()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 10_000m);

        var v = await w.DepositAsync(acc, 600_000m, authorize: false);

        Assert.Equal(2, v.RequiredLevel);
        var ex = await Assert.ThrowsAsync<BankingException>(() => w.AuthorizeAsync(v.Id, manager: false));
        Assert.Contains("Manager-level", ex.Message);
        await w.AuthorizeAsync(v.Id, manager: true);
        Assert.Equal(610_000m, await w.BalanceAsync(acc.Id));
    }

    [Fact]
    public async Task Back_dated_entries_are_limited_and_need_manager_authorization()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();

        var tooOld = await Assert.ThrowsAsync<BankingException>(() =>
            w.Do(s => s.Transactions.DepositAsync(acc.Id, 100m, null, null, World.Maker, null, w.Today.AddDays(-6))));
        var future = await Assert.ThrowsAsync<BankingException>(() =>
            w.Do(s => s.Transactions.DepositAsync(acc.Id, 100m, null, null, World.Maker, null, w.Today.AddDays(1))));
        var ok = await w.Do(s => s.Transactions.DepositAsync(acc.Id, 100m, null, null, World.Maker, null, w.Today.AddDays(-3)));

        Assert.Contains("beyond the allowed", tooOld.Message);
        Assert.Contains("future", future.Message);
        Assert.True(ok.IsBackValue);
        Assert.Equal(2, ok.RequiredLevel);
        Assert.Equal(w.Today, ok.TxnDate);
        Assert.Equal(w.Today.AddDays(-3), ok.ValueDate);
    }

    [Fact]
    public async Task Transfer_moves_money_between_accounts_and_keeps_books_balanced()
    {
        var w = await World.CreateAsync(fx);
        var a = await w.OpenAccountAsync(deposit: 5_000m);
        var b = await w.OpenAccountAsync(deposit: 1_000m);

        var v = await w.Do(s => s.Transactions.TransferAsync(a.Id, b.Id, 1_500m, null, "rent", World.Maker, null));
        await w.AuthorizeAsync(v.Id);

        Assert.Equal(3_500m, await w.BalanceAsync(a.Id));
        Assert.Equal(2_500m, await w.BalanceAsync(b.Id));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Transfer_to_the_same_account_is_refused()
    {
        var w = await World.CreateAsync(fx);
        var a = await w.OpenAccountAsync();

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Transactions.TransferAsync(a.Id, a.Id, 10m, null, null, World.Maker, null)));
    }

    [Fact]
    public async Task Debit_from_dormant_blocked_or_closed_accounts_is_refused_but_dormant_can_receive()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();
        await SetStatus(w, acc.Id, AccountStatus.Dormant);

        await Assert.ThrowsAsync<BankingException>(() => w.WithdrawAsync(acc, 10m, authorize: false));
        await w.DepositAsync(acc, 50m);
        await SetStatus(w, acc.Id, AccountStatus.Blocked);
        await Assert.ThrowsAsync<BankingException>(() => w.DepositAsync(acc, 50m, authorize: false));
    }

    [Fact]
    public async Task Reversal_restores_the_balance_and_marks_the_original_as_reversed()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 10_000m);
        var original = await w.DepositAsync(acc, 2_000m);

        var reversal = await w.Do(s => s.Ledger.CreateReversalAsync(original.Id, "entered in error", World.Maker, null));
        await w.AuthorizeAsync(reversal.Id);

        Assert.Equal(10_000m, await w.BalanceAsync(acc.Id));
        var stored = await w.Read(db => db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == original.Id));
        Assert.Equal(VoucherStatus.Reversed, stored.Status);
        Assert.Equal(reversal.Id, stored.ReversedById);
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Reversal_rules_block_double_reversal_pending_duplicates_and_missing_reason()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();
        var original = await w.DepositAsync(acc, 300m);

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Ledger.CreateReversalAsync(original.Id, "", World.Maker, null)));
        var reversal = await w.Do(s => s.Ledger.CreateReversalAsync(original.Id, "dup", World.Maker, null));
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Ledger.CreateReversalAsync(original.Id, "again", World.Maker, null)));
        await w.AuthorizeAsync(reversal.Id);
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Ledger.CreateReversalAsync(original.Id, "third", World.Maker, null)));
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Ledger.CreateReversalAsync(reversal.Id, "undo", World.Maker, null)));
    }

    [Fact]
    public async Task Pending_vouchers_cannot_be_reversed()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();
        var pending = await w.DepositAsync(acc, 300m, authorize: false);

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Ledger.CreateReversalAsync(pending.Id, "x", World.Maker, null)));
    }

    [Fact]
    public async Task Closed_business_day_blocks_manual_postings()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();
        await w.Do(async s => { (await s.Db.Branches.FindAsync(w.Branch.Id))!.DayOpen = false; await s.Db.SaveChangesAsync(); });

        var ex = await Assert.ThrowsAsync<BankingException>(() => w.DepositAsync(acc, 10m, authorize: false));

        Assert.Contains("day is closed", ex.Message);
    }

    [Fact]
    public async Task Single_debit_limit_is_enforced()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 50_000m, tweak: i => i.SingleDebitLimit = 5_000m);

        await Assert.ThrowsAsync<BankingException>(() => w.WithdrawAsync(acc, 6_000m, authorize: false));
        var ok = await w.WithdrawAsync(acc, 5_000m);

        Assert.Equal(VoucherStatus.Authorized, ok.Status);
    }

    [Fact]
    public async Task Daily_debit_limit_counts_authorized_and_pending_withdrawals()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 50_000m, tweak: i => i.DailyDebitLimit = 5_000m);

        await w.WithdrawAsync(acc, 3_000m, authorize: false);
        var ex = await Assert.ThrowsAsync<BankingException>(() => w.WithdrawAsync(acc, 3_000m, authorize: false));

        Assert.Contains("Daily debit limit", ex.Message);
    }

    [Fact]
    public async Task Monthly_debit_limit_counts_pending_withdrawals_too()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 50_000m, tweak: i => i.MonthlyDebitLimit = 5_000m);

        await w.WithdrawAsync(acc, 3_000m, authorize: false);
        var ex = await Assert.ThrowsAsync<BankingException>(() => w.WithdrawAsync(acc, 3_000m, authorize: false));

        Assert.Contains("Monthly debit limit", ex.Message);
    }

    [Fact]
    public async Task Maker_credit_and_debit_limits_from_the_user_profile_are_enforced()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 50_000m);
        var profile = new UserProfile { BranchId = w.Branch.Id, MaxCredit = 1_000m, MaxDebit = 2_000m };

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Transactions.DepositAsync(acc.Id, 1_500m, null, null, World.Maker, profile)));
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Transactions.WithdrawAsync(acc.Id, 2_500m, null, null, null, World.Maker, profile)));
        var ok = await w.Do(s => s.Transactions.DepositAsync(acc.Id, 1_000m, null, null, World.Maker, profile));

        Assert.Equal(VoucherStatus.Pending, ok.Status);
    }

    [Fact]
    public async Task Cross_branch_teller_posting_balances_each_branch_through_the_inter_branch_gl()
    {
        var home = await World.CreateAsync(fx);
        var other = await World.CreateAsync(fx);
        var acc = await home.OpenAccountAsync(deposit: 10_000m);
        var tellerAtOther = new UserProfile { BranchId = other.Branch.Id };

        var v = await home.Do(s => s.Transactions.DepositAsync(acc.Id, 2_000m, null, null, World.Maker, tellerAtOther));
        await home.AuthorizeAsync(v.Id);

        var lines = await home.Read(db => db.VoucherLines.AsNoTracking().Where(l => l.VoucherId == v.Id).ToListAsync());
        foreach (var branchLines in lines.GroupBy(l => l.BranchId))
            Assert.Equal(branchLines.Sum(l => l.Debit), branchLines.Sum(l => l.Credit));
        Assert.Contains(lines, l => l.Narration == "Inter-branch adjustment");
        Assert.Equal(2_000m, await other.GlBalanceAsync("1100"));
        await home.AssertBooksConsistentAsync();
        await other.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Journal_must_balance_and_use_open_posting_gl_accounts()
    {
        var w = await World.CreateAsync(fx);
        var (expense, income, group) = await w.Read(async db => (
            (await db.GlAccounts.AsNoTracking().FirstAsync(g => g.Code == "5300")).Id,
            (await db.GlAccounts.AsNoTracking().FirstAsync(g => g.Code == "4400")).Id,
            (await db.GlAccounts.AsNoTracking().FirstAsync(g => g.Code == "1000")).Id));

        Task<Voucher> Build(Action<VoucherReq> fill)
        {
            var r = new VoucherReq { Type = VoucherType.Journal, BranchId = w.Branch.Id, Description = "journal", Maker = World.Maker };
            fill(r);
            return w.Do(s => s.Ledger.CreateAsync(r));
        }

        await Assert.ThrowsAsync<BankingException>(() => Build(r => { r.Dr(expense, null, 100m); r.Cr(income, null, 90m); }));
        await Assert.ThrowsAsync<BankingException>(() => Build(r => { r.Dr(group, null, 100m); r.Cr(income, null, 100m); }));
        await Assert.ThrowsAsync<BankingException>(() => Build(r => r.Dr(expense, null, 100m)));
        var ok = await Build(r => { r.Dr(expense, null, 100m); r.Cr(income, null, 100m); });
        await w.AuthorizeAsync(ok.Id);

        Assert.Equal(100m, await w.GlBalanceAsync("5300"));
        Assert.Equal(-100m, await w.GlBalanceAsync("4400"));
    }

    [Fact]
    public async Task Cheque_withdrawals_require_an_issued_unstopped_unused_leaf()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(ProductCategory.Current, 50_000m);

        await Assert.ThrowsAsync<BankingException>(() => ChequeWithdraw(w, acc, "1234567", 100m));

        await w.Do(s => s.Deposits.IssueChequeBookAsync(acc.Id, 10, World.Maker, null));
        var book = await w.Read(db => db.ChequeBooks.AsNoTracking().FirstAsync(b => b.AccountId == acc.Id));
        var leaf = book.FromNo.ToString();

        var v = await ChequeWithdraw(w, acc, leaf, 100m);
        await w.AuthorizeAsync(v.Id);
        await Assert.ThrowsAsync<BankingException>(() => ChequeWithdraw(w, acc, leaf, 100m));

        var stopped = (book.FromNo + 1).ToString();
        await w.Do(s => s.Deposits.StopPaymentAsync(acc.Id, stopped, 100m, "lost", World.Maker, null));
        var ex = await Assert.ThrowsAsync<BankingException>(() => ChequeWithdraw(w, acc, stopped, 100m));
        Assert.Contains("Stop-payment", ex.Message);
        await Assert.ThrowsAsync<BankingException>(() => ChequeWithdraw(w, acc, "abc", 100m));
    }

    private static Task<Voucher> ChequeWithdraw(World w, Account acc, string chequeNo, decimal amount) =>
        w.Do(s => s.Transactions.WithdrawAsync(acc.Id, amount, chequeNo, null, null, World.Maker, null));

    private static Task SetStatus(World w, int accountId, AccountStatus status) =>
        w.Do(async s => { (await s.Db.Accounts.FindAsync(accountId))!.Status = status; await s.Db.SaveChangesAsync(); });
}
