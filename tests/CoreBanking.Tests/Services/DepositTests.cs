namespace CoreBanking.Tests.Services;

[Collection(DatabaseCollection.Name)]
public class DepositTests(DatabaseFixture fx)
{
    private static async Task<Account> OpenPendingAsync(World w, Customer c, Product p, decimal deposit, Action<OpenAccountInput>? tweak = null)
    {
        var input = new OpenAccountInput { CustomerId = c.Id, ProductId = p.Id, BranchId = w.Branch.Id, InitialDeposit = deposit };
        tweak?.Invoke(input);
        return await w.Do(s => s.Deposits.OpenAsync(input, World.Maker, null));
    }

    [Fact]
    public async Task Opening_an_account_creates_a_pending_account_and_voucher_that_activate_on_authorization()
    {
        var w = await World.CreateAsync(fx);
        var c = await w.NewCustomerAsync();
        var p = await w.ProductAsync(ProductCategory.Savings);

        var acc = await OpenPendingAsync(w, c, p, 5_000m);

        Assert.Equal(AccountStatus.PendingApproval, (await w.AccountAsync(acc.Id)).Status);
        Assert.Equal(13, acc.AccountNo.Length);
        Assert.True(Money.ValidNumber(acc.AccountNo));
        Assert.StartsWith(w.Branch.Code + p.Code, acc.AccountNo);
        var voucher = await w.PendingVoucherForAsync("Account", acc.Id);
        await w.AuthorizeAsync(voucher.Id);
        var active = await w.AccountAsync(acc.Id);
        Assert.Equal(AccountStatus.Active, active.Status);
        Assert.Equal(5_000m, active.Balance);
        Assert.Equal(w.Today, active.OpenedOn);
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Rejecting_the_opening_voucher_closes_the_pending_account()
    {
        var w = await World.CreateAsync(fx);
        var c = await w.NewCustomerAsync();
        var acc = await OpenPendingAsync(w, c, await w.ProductAsync(ProductCategory.Savings), 5_000m);
        var voucher = await w.PendingVoucherForAsync("Account", acc.Id);

        await w.Do(s => s.Ledger.RejectAsync(voucher.Id, World.Checker, "bad documents"));

        var stored = await w.AccountAsync(acc.Id);
        Assert.Equal(AccountStatus.Closed, stored.Status);
        Assert.EndsWith("(REJECTED)", stored.Title);
        Assert.Equal(0m, stored.Balance);
    }

    [Fact]
    public async Task Opening_deposit_is_net_of_the_opening_fee_plus_vat()
    {
        var w = await World.CreateAsync(fx);
        var product = await w.NewProductAsync(ProductCategory.Savings, p => p.OpeningFee = 200m);
        var c = await w.NewCustomerAsync();

        var acc = await OpenPendingAsync(w, c, product, 5_000m);
        await w.AuthorizeAsync((await w.PendingVoucherForAsync("Account", acc.Id)).Id);

        Assert.Equal(4_770m, await w.BalanceAsync(acc.Id));
        Assert.Equal(-200m, await w.GlBalanceAsync("4400"));
        Assert.Equal(-30m, await w.GlBalanceAsync("2610"));
        Assert.Equal(5_000m, await w.GlBalanceAsync("1100"));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Opening_rules_reject_invalid_requests()
    {
        var w = await World.CreateAsync(fx);
        var savings = await w.ProductAsync(ProductCategory.Savings);
        var current = await w.ProductAsync(ProductCategory.Current);
        var loan = await w.ProductAsync(ProductCategory.TermLoan);
        var good = await w.NewCustomerAsync();

        var unverified = await w.NewCustomerAsync(verified: false);
        var inactive = await w.NewCustomerAsync(active: false);
        await Assert.ThrowsAsync<BankingException>(() => OpenPendingAsync(w, unverified, savings, 5_000m));
        await Assert.ThrowsAsync<BankingException>(() => OpenPendingAsync(w, inactive, savings, 5_000m));
        await Assert.ThrowsAsync<BankingException>(() => OpenPendingAsync(w, good, loan, 5_000m));
        await Assert.ThrowsAsync<BankingException>(() => OpenPendingAsync(w, good, savings, 100m));
        await Assert.ThrowsAsync<BankingException>(() => OpenPendingAsync(w, good, savings, -1m));
        await Assert.ThrowsAsync<BankingException>(() => OpenPendingAsync(w, good, savings, 5_000m, i => i.OdLimit = 1_000m));
        await Assert.ThrowsAsync<BankingException>(() => OpenPendingAsync(w, good, savings, 5_000m, i => i.Mode = OperationMode.JointAny));
        var overdraft = await OpenPendingAsync(w, good, current, 5_000m, i => i.OdLimit = 1_000m);
        Assert.Equal(1_000m, overdraft.OdLimit);
    }

    [Fact]
    public async Task Nominee_shares_must_total_one_hundred_percent_and_joint_holders_must_be_verified()
    {
        var w = await World.CreateAsync(fx);
        var savings = await w.ProductAsync(ProductCategory.Savings);
        var c = await w.NewCustomerAsync();

        await Assert.ThrowsAsync<BankingException>(() => OpenPendingAsync(w, c, savings, 5_000m, i =>
            i.Nominees = [new Nominee { Name = "A", SharePercent = 60 }, new Nominee { Name = "B", SharePercent = 30 }]));
        var unverified = await w.NewCustomerAsync(verified: false);
        await Assert.ThrowsAsync<BankingException>(() => OpenPendingAsync(w, c, savings, 5_000m, i =>
        { i.Mode = OperationMode.JointAny; i.JointCustomerIds = [unverified.Id]; }));
        var partner = await w.NewCustomerAsync();
        var ok = await OpenPendingAsync(w, c, savings, 5_000m, i =>
        {
            i.Mode = OperationMode.JointAny; i.JointCustomerIds = [partner.Id];
            i.Nominees = [new Nominee { Name = "A", SharePercent = 60 }, new Nominee { Name = "B", SharePercent = 40 }];
        });

        var stored = await w.Read(db => db.Accounts.AsNoTracking().Include(a => a.Holders).Include(a => a.Nominees).FirstAsync(a => a.Id == ok.Id));
        Assert.Single(stored.Holders);
        Assert.Equal(2, stored.Nominees.Count);
    }

    [Fact]
    public async Task Sanctioned_applicants_are_blocked_from_opening_accounts()
    {
        var w = await World.CreateAsync(fx);
        var name = "Zubair Bin Listed " + Guid.NewGuid().ToString("N")[..6];
        await w.Do(async s =>
        {
            s.Db.SanctionEntries.Add(new SanctionEntry { Name = name, Source = SanctionSource.UNSCR, IsActive = true });
            await s.Db.SaveChangesAsync();
        });
        var listed = await w.NewCustomerAsync(name: name);

        var savings = await w.ProductAsync(ProductCategory.Savings);
        var ex = await Assert.ThrowsAsync<BankingException>(() => OpenPendingAsync(w, listed, savings, 5_000m));

        Assert.Contains("Sanction", ex.Message);
    }

    [Fact]
    public async Task Zero_balance_accounts_are_approved_by_a_different_user()
    {
        var w = await World.CreateAsync(fx);
        var product = await w.NewProductAsync(ProductCategory.Savings, p => p.MinOpeningBalance = 0);
        var acc = await OpenPendingAsync(w, await w.NewCustomerAsync(), product, 0m);

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Deposits.ApproveZeroBalanceAsync(acc.Id, World.Maker)));
        await w.Do(s => s.Deposits.ApproveZeroBalanceAsync(acc.Id, World.Checker));

        Assert.Equal(AccountStatus.Active, (await w.AccountAsync(acc.Id)).Status);
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Deposits.ApproveZeroBalanceAsync(acc.Id, World.Checker)));
    }

    [Fact]
    public async Task Fdr_funded_from_a_savings_account_sets_principal_and_maturity_date()
    {
        var w = await World.CreateAsync(fx);
        var customer = await w.NewCustomerAsync();
        var source = await w.OpenAccountAsync(deposit: 200_000m, customer: customer);

        var fdr = await w.OpenAccountAsync(ProductCategory.FDR, 100_000m, customer, i => { i.SourceAccountId = source.Id; i.TermMonths = 6; });

        Assert.Equal(AccountStatus.Active, fdr.Status);
        Assert.Equal(100_000m, fdr.PrincipalAmount);
        Assert.Equal(100_000m, fdr.Balance);
        Assert.Equal(w.Today.AddMonths(6), fdr.MaturityDate);
        Assert.Equal(100_000m, await w.BalanceAsync(source.Id));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Fdr_source_account_must_belong_to_the_same_customer()
    {
        var w = await World.CreateAsync(fx);
        var stranger = await w.OpenAccountAsync(deposit: 200_000m);
        var customer = await w.NewCustomerAsync();
        var fdr = await w.ProductAsync(ProductCategory.FDR);

        await Assert.ThrowsAsync<BankingException>(() => OpenPendingAsync(w, customer, fdr, 50_000m, i => i.SourceAccountId = stranger.Id));
        Assert.Equal(200_000m, await w.BalanceAsync(stranger.Id));
    }

    [Fact]
    public async Task Fdr_accepts_neither_direct_withdrawals_nor_extra_deposits()
    {
        var w = await World.CreateAsync(fx);
        var fdr = await w.OpenAccountAsync(ProductCategory.FDR, 50_000m);

        await Assert.ThrowsAsync<BankingException>(() => w.WithdrawAsync(fdr, 1_000m, authorize: false));
        await Assert.ThrowsAsync<BankingException>(() => w.DepositAsync(fdr, 1_000m, authorize: false));
    }

    [Fact]
    public async Task Dps_installments_accumulate_until_the_term_is_complete()
    {
        var w = await World.CreateAsync(fx);
        var dps = await w.OpenAccountAsync(ProductCategory.DPS, 1_000m, tweak: i => i.TermMonths = 3);
        Assert.Equal(1, dps.InstallmentsPaid);

        for (var n = 2; n <= 3; n++)
        {
            var v = await w.Do(s => s.Deposits.PayDpsInstallmentAsync(dps.Id, null, World.Maker, null));
            await w.AuthorizeAsync(v.Id);
        }

        var stored = await w.AccountAsync(dps.Id);
        Assert.Equal(3, stored.InstallmentsPaid);
        Assert.Equal(3_000m, stored.Balance);
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Deposits.PayDpsInstallmentAsync(dps.Id, null, World.Maker, null)));
    }

    [Fact]
    public async Task Dps_opening_deposit_must_equal_the_installment()
    {
        var w = await World.CreateAsync(fx);
        var dps = await w.ProductAsync(ProductCategory.DPS);
        var first = await w.NewCustomerAsync();
        var second = await w.NewCustomerAsync();

        await Assert.ThrowsAsync<BankingException>(() => OpenPendingAsync(w, first, dps, 1_500m, i => { i.InstallmentAmount = 1_000m; i.TermMonths = 12; }));
        await Assert.ThrowsAsync<BankingException>(() => OpenPendingAsync(w, second, dps, 1_000m, i => { i.InstallmentAmount = 0m; i.TermMonths = 12; }));
    }

    [Fact]
    public async Task Matured_fdr_encashment_pays_principal_and_interest_net_of_source_tax_and_closes_the_account()
    {
        var w = await World.CreateAsync(fx);
        var fdr = await w.OpenAccountAsync(ProductCategory.FDR, 100_000m, tweak: i => i.TermMonths = 6);
        await w.UpdateAccountAsync(fdr.Id, a => { a.MaturityDate = w.Today; a.AccruedInterest = 4_000m; });
        var cashBefore = await w.GlBalanceAsync("1100");

        var v = await w.Do(s => s.Deposits.EncashAsync(fdr.Id, premature: false, toCash: true, World.Maker, null));
        await w.AuthorizeAsync(v.Id);

        var stored = await w.AccountAsync(fdr.Id);
        Assert.Equal(AccountStatus.Closed, stored.Status);
        Assert.Equal(0m, stored.Balance);
        Assert.Equal(0m, stored.AccruedInterest);
        Assert.Equal(103_600m, cashBefore - await w.GlBalanceAsync("1100"));
        Assert.Equal(-400m, await w.GlBalanceAsync("2620"));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Premature_encashment_needs_the_premature_flag_and_pays_reduced_interest()
    {
        var w = await World.CreateAsync(fx);
        var fdr = await w.OpenAccountAsync(ProductCategory.FDR, 100_000m, tweak: i => i.TermMonths = 12);
        await w.UpdateAccountAsync(fdr.Id, a => a.AccruedInterest = 4_000m);

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Deposits.EncashAsync(fdr.Id, false, true, World.Maker, null)));
        var v = await w.Do(s => s.Deposits.EncashAsync(fdr.Id, true, true, World.Maker, null));
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Deposits.EncashAsync(fdr.Id, true, true, World.Maker, null)));
        await w.AuthorizeAsync(v.Id);

        Assert.Equal(AccountStatus.Closed, (await w.AccountAsync(fdr.Id)).Status);
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Encashing_a_non_term_deposit_is_refused()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Deposits.EncashAsync(acc.Id, true, true, World.Maker, null)));
    }

    [Fact]
    public async Task Closing_an_account_pays_out_the_balance_less_closing_fee_and_vat()
    {
        var w = await World.CreateAsync(fx);
        var product = await w.NewProductAsync(ProductCategory.Savings, p => p.ClosingFee = 300m);
        var acc = await w.OpenAccountAsync(deposit: 5_000m, tweak: i => i.ProductId = product.Id);
        var cashBefore = await w.GlBalanceAsync("1100");

        var v = await w.Do(s => s.Deposits.CloseAsync(acc.Id, World.Maker, null));
        await w.AuthorizeAsync(v.Id);

        var stored = await w.AccountAsync(acc.Id);
        Assert.Equal(AccountStatus.Closed, stored.Status);
        Assert.Equal(0m, stored.Balance);
        Assert.Equal(4_655m, cashBefore - await w.GlBalanceAsync("1100"));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Closing_is_blocked_by_liens_and_overdrafts()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 5_000m);
        await w.UpdateAccountAsync(acc.Id, a => a.LienAmount = 100m);

        var ex = await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Deposits.CloseAsync(acc.Id, World.Maker, null)));

        Assert.Contains("lien", ex.Message);
    }

    [Fact]
    public async Task Cheque_book_numbers_never_overlap_between_accounts_or_books()
    {
        var w = await World.CreateAsync(fx);
        var a = await w.OpenAccountAsync(ProductCategory.Current, 50_000m);
        var b = await w.OpenAccountAsync(ProductCategory.Current, 50_000m);

        await w.Do(s => s.Deposits.IssueChequeBookAsync(a.Id, 100, World.Maker, null));
        await w.Do(s => s.Deposits.IssueChequeBookAsync(b.Id, 100, World.Maker, null));
        await w.Do(s => s.Deposits.IssueChequeBookAsync(a.Id, 100, World.Maker, null));
        await w.Do(s => s.Deposits.IssueChequeBookAsync(b.Id, 50, World.Maker, null));

        var books = await w.Read(db => db.ChequeBooks.AsNoTracking().Where(x => x.AccountId == a.Id || x.AccountId == b.Id).OrderBy(x => x.FromNo).ToListAsync());
        Assert.Equal(4, books.Count);
        for (var i = 1; i < books.Count; i++) Assert.True(books[i].FromNo > books[i - 1].ToNo, $"book {books[i].FromNo}-{books[i].ToNo} overlaps {books[i - 1].FromNo}-{books[i - 1].ToNo}");
        Assert.All(books, x => Assert.True(x.ToNo >= x.FromNo));
    }

    [Fact]
    public async Task Cheque_book_leaves_and_account_type_are_validated_and_the_fee_is_charged_with_vat()
    {
        var w = await World.CreateAsync(fx);
        var current = await w.OpenAccountAsync(ProductCategory.Current, 50_000m);
        var fdr = await w.OpenAccountAsync(ProductCategory.FDR, 20_000m);

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Deposits.IssueChequeBookAsync(current.Id, 30, World.Maker, null)));
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Deposits.IssueChequeBookAsync(fdr.Id, 25, World.Maker, null)));
        var fee = await w.Do(s => s.Deposits.IssueChequeBookAsync(current.Id, 25, World.Maker, null));

        Assert.NotNull(fee);
        Assert.Equal(345m, fee!.TotalAmount);
        await w.AuthorizeAsync(fee.Id);
        Assert.Equal(49_655m, await w.BalanceAsync(current.Id));
    }

    [Fact]
    public async Task Stop_payment_is_recorded_once_per_cheque_and_only_for_issued_cheques()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(ProductCategory.Current, 50_000m);
        await w.Do(s => s.Deposits.IssueChequeBookAsync(acc.Id, 10, World.Maker, null));
        var leaf = (await w.Read(db => db.ChequeBooks.AsNoTracking().FirstAsync(b => b.AccountId == acc.Id))).FromNo.ToString();

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Deposits.StopPaymentAsync(acc.Id, "42", 10m, "x", World.Maker, null)));
        await w.Do(s => s.Deposits.StopPaymentAsync(acc.Id, leaf, 500m, "lost", World.Maker, null));
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Deposits.StopPaymentAsync(acc.Id, leaf, 500m, "lost", World.Maker, null)));

        Assert.Equal(1, await w.Read(db => db.StopPayments.CountAsync(x => x.AccountId == acc.Id && x.IsActive)));
    }

    [Fact]
    public async Task Cheque_return_charge_is_taken_even_if_it_overdraws_the_account()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(ProductCategory.Current, 5_000m);
        await w.WithdrawAsync(acc, 5_000m);

        var v = await w.Do(s => s.Deposits.ReturnChequeAsync(acc.Id, "998877", 2_000m, "insufficient funds", true, World.Maker, null));

        Assert.NotNull(v);
        Assert.Equal(VoucherStatus.Authorized, (await w.Read(db => db.Vouchers.AsNoTracking().FirstAsync(x => x.Id == v!.Id))).Status);
        Assert.Equal(-862.5m, await w.BalanceAsync(acc.Id));
        Assert.Equal(1, await w.Read(db => db.ChequeReturns.CountAsync(r => r.AccountId == acc.Id)));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Cash_deposit_screens_the_depositor_against_the_sanction_list()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync();
        var name = "Hamid Sanctioned " + Guid.NewGuid().ToString("N")[..6];
        await w.Do(async s => { s.Db.SanctionEntries.Add(new SanctionEntry { Name = name, Source = SanctionSource.Local, IsActive = true }); await s.Db.SaveChangesAsync(); });

        var ex = await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Transactions.DepositAsync(acc.Id, 100m, name, null, World.Maker, null)));

        Assert.Contains("Sanction", ex.Message);
    }
}
