namespace CoreBanking.Tests.Services;

[Collection(DatabaseCollection.Name)]
public class RemittanceTests(DatabaseFixture fx)
{
    private static RemittanceInput Input(decimal amount, RemittanceType type = RemittanceType.PO, int? source = null) => new()
    {
        Type = type, Amount = amount, ApplicantName = "Applicant Rahim", BeneficiaryName = "Beneficiary Karim " + Guid.NewGuid().ToString("N")[..5],
        BeneficiaryBank = "Other Bank", SourceAccountId = source
    };

    private static async Task<Remittance> IssueAsync(World w, RemittanceInput input, bool authorize = true)
    {
        var profile = new UserProfile { BranchId = w.Branch.Id };
        var r = await w.Do(s => s.Remittances.IssueAsync(input, World.Maker, profile));
        if (authorize) await w.AuthorizeAsync(r.IssueVoucherId!.Value);
        return r;
    }

    private static Task<Remittance> LoadAsync(World w, int id) => w.Read(db => db.Remittances.AsNoTracking().FirstAsync(r => r.Id == id));

    [Fact]
    public async Task Issue_for_cash_charges_commission_and_vat_and_credits_the_payable_gl()
    {
        var w = await World.CreateAsync(fx);

        var r = await IssueAsync(w, Input(100_000m));

        Assert.Equal(172.5m, r.Commission);
        Assert.Equal(RemittanceStatus.Issued, r.Status);
        Assert.StartsWith("PO" + w.Branch.Code, r.InstrumentNo);
        Assert.Equal(100_172.5m, await w.GlBalanceAsync("1100"));
        Assert.Equal(-100_000m, await w.GlBalanceAsync("2200"));
        Assert.Equal(-150m, await w.GlBalanceAsync("4500"));
        Assert.Equal(-22.5m, await w.GlBalanceAsync("2610"));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Small_instruments_pay_the_minimum_commission()
    {
        var w = await World.CreateAsync(fx);

        var r = await IssueAsync(w, Input(1_000m, RemittanceType.DD));

        Assert.Equal(57.5m, r.Commission);
        Assert.Equal(-1_000m, await w.GlBalanceAsync("2210"));
    }

    [Fact]
    public async Task Issue_against_a_deposit_account_debits_the_account()
    {
        var w = await World.CreateAsync(fx);
        var acc = await w.OpenAccountAsync(deposit: 50_000m);

        var r = await IssueAsync(w, Input(10_000m, RemittanceType.TT, acc.Id));

        Assert.Equal(50_000m - 10_000m - r.Commission, await w.BalanceAsync(acc.Id));
        Assert.Equal(-10_000m, await w.GlBalanceAsync("2220"));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task Non_positive_amounts_and_sanctioned_parties_are_refused()
    {
        var w = await World.CreateAsync(fx);
        var profile = new UserProfile { BranchId = w.Branch.Id };
        var name = "Rashed Blocked " + Guid.NewGuid().ToString("N")[..6];
        await w.Do(async s => { s.Db.SanctionEntries.Add(new SanctionEntry { Name = name, Source = SanctionSource.Local, IsActive = true }); await s.Db.SaveChangesAsync(); });

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Remittances.IssueAsync(Input(0m), World.Maker, profile)));
        var bad = Input(5_000m);
        bad.BeneficiaryName = name;
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Remittances.IssueAsync(bad, World.Maker, profile)));
    }

    [Fact]
    public async Task Paying_an_instrument_needs_an_authorized_issue_voucher_and_then_marks_it_paid()
    {
        var w = await World.CreateAsync(fx);
        var profile = new UserProfile { BranchId = w.Branch.Id };
        var r = await IssueAsync(w, Input(10_000m), authorize: false);

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Remittances.PayAsync(r.Id, null, World.Maker, profile)));
        await w.AuthorizeAsync(r.IssueVoucherId!.Value);
        var pay = await w.Do(s => s.Remittances.PayAsync(r.Id, null, World.Maker, profile));
        await w.AuthorizeAsync(pay.Id);

        var stored = await LoadAsync(w, r.Id);
        Assert.Equal(RemittanceStatus.Paid, stored.Status);
        Assert.Equal(w.Today, stored.PaidDate);
        Assert.Equal(pay.Id, stored.PayVoucherId);
        Assert.Equal(0m, await w.GlBalanceAsync("2200"));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task An_instrument_cannot_be_paid_twice_even_before_the_first_payment_is_authorized()
    {
        var w = await World.CreateAsync(fx);
        var profile = new UserProfile { BranchId = w.Branch.Id };
        var r = await IssueAsync(w, Input(10_000m));

        var first = await w.Do(s => s.Remittances.PayAsync(r.Id, null, World.Maker, profile));
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Remittances.PayAsync(r.Id, null, World.Maker, profile)));
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Remittances.CancelAsync(r.Id, World.Maker, profile)));
        await w.AuthorizeAsync(first.Id);
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Remittances.PayAsync(r.Id, null, World.Maker, profile)));

        Assert.Equal(0m, await w.GlBalanceAsync("2200"));
    }

    [Fact]
    public async Task Racing_pay_and_cancel_vouchers_cannot_both_be_authorized()
    {
        var w = await World.CreateAsync(fx);
        var r = await IssueAsync(w, Input(10_000m));
        var payable = await w.Read(db => db.GlAccounts.AsNoTracking().FirstAsync(g => g.Code == "2200"));
        var cash = await w.Read(db => db.GlAccounts.AsNoTracking().FirstAsync(g => g.Code == "1100"));

        Voucher Forge(string kind)
        {
            var req = new VoucherReq
            {
                Type = VoucherType.RemittancePay, BranchId = w.Branch.Id, Maker = World.Maker, Description = "forged " + kind,
                SourceKind = kind, SourceId = r.Id
            };
            req.Dr(payable.Id, null, 10_000m, null, w.Branch.Id);
            req.Cr(cash.Id, null, 10_000m, null, w.Branch.Id);
            return w.Do(s => s.Ledger.CreateAsync(req)).GetAwaiter().GetResult();
        }

        var pay = Forge("Remittance");
        var cancel = Forge("RemittanceCancel");
        await w.AuthorizeAsync(pay.Id);

        await Assert.ThrowsAsync<BankingException>(() => w.AuthorizeAsync(cancel.Id));
        Assert.Equal(RemittanceStatus.Paid, (await LoadAsync(w, r.Id)).Status);
    }

    [Fact]
    public async Task Cancelling_refunds_the_principal_but_not_the_commission()
    {
        var w = await World.CreateAsync(fx);
        var profile = new UserProfile { BranchId = w.Branch.Id };
        var acc = await w.OpenAccountAsync(deposit: 50_000m);
        var r = await IssueAsync(w, Input(10_000m, RemittanceType.PO, acc.Id));

        var cancel = await w.Do(s => s.Remittances.CancelAsync(r.Id, World.Maker, profile));
        await w.AuthorizeAsync(cancel.Id);

        Assert.Equal(RemittanceStatus.Cancelled, (await LoadAsync(w, r.Id)).Status);
        Assert.Equal(50_000m - r.Commission, await w.BalanceAsync(acc.Id));
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Remittances.PayAsync(r.Id, null, World.Maker, profile)));
        await w.AssertBooksConsistentAsync();
    }

    [Fact]
    public async Task An_instrument_cannot_be_cancelled_before_its_issue_voucher_is_authorized()
    {
        var w = await World.CreateAsync(fx);
        var profile = new UserProfile { BranchId = w.Branch.Id };
        var r = await IssueAsync(w, Input(10_000m), authorize: false);

        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Remittances.CancelAsync(r.Id, World.Maker, profile)));
    }

    [Fact]
    public async Task Rejecting_the_issue_voucher_cancels_the_instrument()
    {
        var w = await World.CreateAsync(fx);
        var profile = new UserProfile { BranchId = w.Branch.Id };
        var r = await IssueAsync(w, Input(10_000m), authorize: false);

        await w.Do(s => s.Ledger.RejectAsync(r.IssueVoucherId!.Value, World.Checker, "mistake"));

        Assert.Equal(RemittanceStatus.Cancelled, (await LoadAsync(w, r.Id)).Status);
        await Assert.ThrowsAsync<BankingException>(() => w.Do(s => s.Remittances.PayAsync(r.Id, null, World.Maker, profile)));
    }

    [Fact]
    public async Task Pay_into_an_account_credits_it()
    {
        var w = await World.CreateAsync(fx);
        var profile = new UserProfile { BranchId = w.Branch.Id };
        var acc = await w.OpenAccountAsync(deposit: 1_000m);
        var r = await IssueAsync(w, Input(4_000m));

        var pay = await w.Do(s => s.Remittances.PayAsync(r.Id, acc.Id, World.Maker, profile));
        await w.AuthorizeAsync(pay.Id);

        Assert.Equal(5_000m, await w.BalanceAsync(acc.Id));
    }
}
