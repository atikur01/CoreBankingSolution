namespace CoreBanking.Tests.Services;

[Collection(DatabaseCollection.Name)]
public class LoanTests(DatabaseFixture fx)
{
    private static async Task<(Customer customer, Account settlement)> BorrowerAsync(World w)
    {
        var customer = await w.NewCustomerAsync();
        var account = await w.OpenAccountAsync(deposit: 10_000m, customer: customer);
        return (customer, account);
    }

    private static async Task<Loan> ApplyAsync(World w, Customer customer, Account settlement, decimal principal = 100_000m, int months = 12,
        ProductCategory category = ProductCategory.TermLoan, decimal rate = 12m)
    {
        var product = await w.ProductAsync(category);
        return await w.Do(s => s.Loans.ApplyAsync(new Loan
        {
            CustomerId = customer.Id, ProductId = product.Id, BranchId = w.Branch.Id, SettlementAccountId = settlement.Id,
            Principal = principal, TermMonths = months, InterestRate = rate, Purpose = "Business"
        }, World.Maker));
    }

    private static async Task<Loan> RunningLoanAsync(World w, decimal principal = 100_000m, int months = 12, ProductCategory category = ProductCategory.TermLoan, decimal rate = 12m)
    {
        var (customer, settlement) = await BorrowerAsync(w);
        var loan = await ApplyAsync(w, customer, settlement, principal, months, category, rate);
        await w.Do(s => s.Loans.ApproveAsync(loan.Id, World.Checker, true, null));
        var v = await w.Do(s => s.Loans.DisburseAsync(loan.Id, World.Maker, null));
        await w.AuthorizeAsync(v.Id);
        return await LoadAsync(w, loan.Id);
    }

    private static Task<Loan> LoadAsync(World w, int id) =>
        w.Read(db => db.Loans.AsNoTracking().Include(l => l.Installments).Include(l => l.Product).FirstAsync(l => l.Id == id));

    [Fact]
    public async Task Application_is_recorded_with_a_valid_loan_number_and_the_product_rate_when_none_is_given()
    {
        var w = await World.CreateAsync(fx);
        var (customer, settlement) = await BorrowerAsync(w);

        var loan = await ApplyAsync(w, customer, settlement, rate: 0m);

        Assert.Equal(LoanStatus.Applied, loan.Status);
        Assert.True(Money.ValidNumber(loan.LoanNo));
        Assert.Equal(12m, loan.InterestRate);
        Assert.Equal(w.Today, loan.ApplicationDate);
    }

    [Fact]
    public async Task Application_rules_reject_unverified_borrowers_foreign_settlement_accounts_and_bad_terms()
    {
        var w = await World.CreateAsync(fx);
        var (customer, settlement) = await BorrowerAsync(w);
        var stranger = await w.OpenAccountAsync();
        var unverified = await w.NewCustomerAsync(verified: false);
        var strangerAccountOfUnverified = settlement;

        await Assert.ThrowsAsync<BankingException>(() => ApplyAsync(w, unverified, strangerAccountOfUnverified));
        await Assert.ThrowsAsync<BankingException>(() => ApplyAsync(w, customer, stranger));
        await Assert.ThrowsAsync<BankingException>(() => ApplyAsync(w, customer, settlement, principal: 0m));
        await Assert.ThrowsAsync<BankingException>(() => ApplyAsync(w, customer, settlement, months: 0));
        await w.UpdateAccountAsync(settlement.Id, a => a.Status = AccountStatus.Dormant);
        await Assert.ThrowsAsync<BankingException>(() => ApplyAsync(w, customer, settlement));
    }

    [Fact]
    public async Task Approval_follows_maker_checker_and_rejection_needs_a_reason()
    {
        var w = await World.CreateAsync(fx);
        var (customer, settlement) = await BorrowerAsync(w);
        var loan = await ApplyAsync(w, customer, settlement);

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Loans.ApproveAsync(loan.Id, World.Maker, true, null)));
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Loans.ApproveAsync(loan.Id, World.Checker, false, " ")));
        await w.Do(s => s.Loans.ApproveAsync(loan.Id, World.Checker, false, "weak collateral"));

        var stored = await LoadAsync(w, loan.Id);
        Assert.Equal(LoanStatus.Rejected, stored.Status);
        Assert.Equal("weak collateral", stored.RejectReason);
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Loans.ApproveAsync(loan.Id, World.Checker, true, null)));
    }

    [Fact]
    public async Task Only_approved_loans_can_be_disbursed_and_only_once()
    {
        var w = await World.CreateAsync(fx);
        var (customer, settlement) = await BorrowerAsync(w);
        var loan = await ApplyAsync(w, customer, settlement);

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Loans.DisburseAsync(loan.Id, World.Maker, null)));
        await w.Do(s => s.Loans.ApproveAsync(loan.Id, World.Checker, true, null));
        var v = await w.Do(s => s.Loans.DisburseAsync(loan.Id, World.Maker, null));
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Loans.DisburseAsync(loan.Id, World.Maker, null)));
        await w.AuthorizeAsync(v.Id);
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Loans.DisburseAsync(loan.Id, World.Maker, null)));
    }

    [Fact]
    public async Task Disbursement_credits_the_net_proceeds_and_builds_the_repayment_schedule()
    {
        var w = await World.CreateAsync(fx);
        var (customer, settlement) = await BorrowerAsync(w);
        var loan = await ApplyAsync(w, customer, settlement);
        await w.Do(s => s.Loans.ApproveAsync(loan.Id, World.Checker, true, null));

        var v = await w.Do(s => s.Loans.DisburseAsync(loan.Id, World.Maker, null));
        await w.AuthorizeAsync(v.Id);

        var stored = await LoadAsync(w, loan.Id);
        Assert.Equal(LoanStatus.Disbursed, stored.Status);
        Assert.Equal(w.Today, stored.DisbursedOn);
        Assert.Equal(100_000m, stored.OutstandingPrincipal);
        Assert.Equal(12, stored.Installments.Count);
        Assert.Equal(100_000m, stored.Installments.Sum(i => i.PrincipalDue));
        Assert.Equal(10_000m + 100_000m - 1_000m - 150m, await w.BalanceAsync(settlement.Id));
        Assert.Equal(100_000m, await w.GlBalanceAsync("1310"));
        Assert.Equal(-1_000m, await w.GlBalanceAsync("4400"));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Demand_loans_get_a_single_bullet_installment()
    {
        var w = await World.CreateAsync(fx);

        var loan = await RunningLoanAsync(w, 60_000m, 6, ProductCategory.DemandLoan, 12m);

        var inst = Assert.Single(loan.Installments);
        Assert.Equal(60_000m, inst.PrincipalDue);
        Assert.Equal(3_600m, inst.InterestDue);
    }

    [Fact]
    public async Task Repayment_cannot_exceed_the_remaining_dues_and_must_be_positive()
    {
        var w = await World.CreateAsync(fx);
        var loan = await RunningLoanAsync(w);
        var payoff = await w.Do(s => s.Loans.PayoffAmountAsync(loan.Id));

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Loans.RepayAsync(loan.Id, 0m, null, World.Maker, null)));
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Loans.RepayAsync(loan.Id, payoff + 0.01m, null, World.Maker, null)));
    }

    [Fact]
    public async Task First_installment_repayment_reduces_principal_and_marks_the_installment_paid()
    {
        var w = await World.CreateAsync(fx);
        var loan = await RunningLoanAsync(w);
        var first = loan.Installments.OrderBy(i => i.No).First();

        var v = await w.Do(s => s.Loans.RepayAsync(loan.Id, first.PrincipalDue + first.InterestDue, null, World.Maker, null));
        await w.AuthorizeAsync(v.Id);

        var stored = await LoadAsync(w, loan.Id);
        var paid = stored.Installments.Single(i => i.No == 1);
        Assert.Equal(InstallmentStatus.Paid, paid.Status);
        Assert.Equal(100_000m - first.PrincipalDue, stored.OutstandingPrincipal);
        Assert.Equal(first.InterestDue, stored.TotalInterestPaid);
        Assert.Equal(LoanStatus.Disbursed, stored.Status);
        Assert.Equal(-first.InterestDue, await w.GlBalanceAsync("4100"));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Partial_payments_settle_interest_first_and_keep_the_installment_open()
    {
        var w = await World.CreateAsync(fx);
        var loan = await RunningLoanAsync(w);

        var v = await w.Do(s => s.Loans.RepayAsync(loan.Id, 500m, null, World.Maker, null));
        await w.AuthorizeAsync(v.Id);

        var stored = await LoadAsync(w, loan.Id);
        var first = stored.Installments.Single(i => i.No == 1);
        Assert.Equal(InstallmentStatus.Due, first.Status);
        Assert.Equal(500m, first.InterestPaid);
        Assert.Equal(0m, first.PrincipalPaid);
        Assert.Equal(100_000m, stored.OutstandingPrincipal);
    }

    [Fact]
    public async Task Paying_off_the_full_balance_closes_the_loan()
    {
        var w = await World.CreateAsync(fx);
        var loan = await RunningLoanAsync(w, 50_000m, 6);
        var payoff = await w.Do(s => s.Loans.PayoffAmountAsync(loan.Id));

        var v = await w.Do(s => s.Loans.RepayAsync(loan.Id, payoff, null, World.Maker, null));
        await w.AuthorizeAsync(v.Id);

        var stored = await LoadAsync(w, loan.Id);
        Assert.Equal(LoanStatus.Closed, stored.Status);
        Assert.Equal(0m, stored.OutstandingPrincipal);
        Assert.Equal(w.Today, stored.ClosedOn);
        Assert.All(stored.Installments, i => Assert.Equal(InstallmentStatus.Paid, i.Status));
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Loans.RepayAsync(loan.Id, 1m, null, World.Maker, null)));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Repayment_can_be_debited_from_a_deposit_account()
    {
        var w = await World.CreateAsync(fx);
        var loan = await RunningLoanAsync(w);
        var source = await w.Read(db => db.Accounts.AsNoTracking().FirstAsync(a => a.Id == loan.SettlementAccountId));
        var before = source.Balance;

        var v = await w.Do(s => s.Loans.RepayAsync(loan.Id, 2_000m, loan.SettlementAccountId, World.Maker, null));
        await w.AuthorizeAsync(v.Id);

        Assert.Equal(before - 2_000m, await w.BalanceAsync(loan.SettlementAccountId));
    }

    [Fact]
    public async Task A_second_pending_repayment_is_refused_at_authorization_once_the_schedule_moved_on()
    {
        var w = await World.CreateAsync(fx);
        var loan = await RunningLoanAsync(w);
        var first = loan.Installments.OrderBy(i => i.No).First();
        var amount = first.PrincipalDue + first.InterestDue;

        var a = await w.Do(s => s.Loans.RepayAsync(loan.Id, amount, null, World.Maker, null));
        var b = await w.Do(s => s.Loans.RepayAsync(loan.Id, amount, null, World.Maker, null));
        await w.AuthorizeAsync(a.Id);
        var ex = await Assert.ThrowsAsync<BankingException>(() => w.AuthorizeAsync(b.Id));

        Assert.Contains("schedule changed", ex.Message);
        var stored = await LoadAsync(w, loan.Id);
        Assert.Equal(InstallmentStatus.Paid, stored.Installments.Single(i => i.No == 1).Status);
        Assert.Equal(InstallmentStatus.Due, stored.Installments.Single(i => i.No == 2).Status);
        Assert.Equal(100_000m - first.PrincipalDue, stored.OutstandingPrincipal);
        var pending = await w.Read(db => db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == b.Id));
        Assert.Equal(VoucherStatus.Pending, pending.Status);
        await w.AssertBooksConsistentAsync();
    }
}
