using System.Net;

namespace CoreBanking.Tests.Web;

[Collection(WebCollection.Name)]
public class FlowTests(WebFixture web)
{
    private async Task<(string number, decimal balance, int id)> ActiveAccountAsync() =>
        await web.WithDbAsync(async db =>
        {
            var a = await db.Accounts.AsNoTracking().Include(x => x.Product)
                .Where(x => x.Status == AccountStatus.Active && x.Product!.Category == ProductCategory.Savings && x.Balance > 1_000)
                .OrderByDescending(x => x.Id).FirstAsync();
            return (a.AccountNo, a.Balance, a.Id);
        });

    private static int VoucherId(HttpResponseMessage response) =>
        int.Parse(response.Headers.Location!.ToString().Split('/').Last().Split('?')[0]);

    [Fact]
    public async Task A_deposit_flows_from_maker_entry_to_checker_authorization()
    {
        var (number, before, id) = await ActiveAccountAsync();
        using var maker = web.CreateBankClient("flow.maker", Roles.Officer);
        using var checker = web.CreateBankClient("flow.checker", Roles.Manager);

        var created = await maker.PostFormAsync("/Transactions/Deposit", "/Transactions/Deposit", new Dictionary<string, string> { ["AccountNo"] = number, ["Amount"] = "250" });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var voucherId = VoucherId(created);
        var pending = await web.WithDbAsync(db => db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == voucherId));
        Assert.Equal(VoucherStatus.Pending, pending.Status);
        Assert.Equal("flow.maker", pending.MakerId);

        await maker.PostFormAsync($"/Transactions/Details/{voucherId}", $"/Transactions/Authorize/{voucherId}", new Dictionary<string, string>());
        Assert.Equal(VoucherStatus.Pending, (await web.WithDbAsync(db => db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == voucherId))).Status);
        Assert.Equal(before, await web.WithDbAsync(async db => (await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == id)).Balance));

        var authorized = await checker.PostFormAsync($"/Transactions/Details/{voucherId}", $"/Transactions/Authorize/{voucherId}", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, authorized.StatusCode);
        Assert.Equal(VoucherStatus.Authorized, (await web.WithDbAsync(db => db.Vouchers.AsNoTracking().FirstAsync(v => v.Id == voucherId))).Status);
        Assert.Equal(before + 250m, await web.WithDbAsync(async db => (await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == id)).Balance));
    }

    [Fact]
    public async Task Invalid_cash_forms_are_redisplayed_without_creating_vouchers()
    {
        var (number, _, _) = await ActiveAccountAsync();
        var before = await web.WithDbAsync(db => db.Vouchers.CountAsync());
        using var maker = web.CreateBankClient("flow.maker2", Roles.Teller);

        var zero = await maker.PostFormAsync("/Transactions/Deposit", "/Transactions/Deposit", new Dictionary<string, string> { ["AccountNo"] = number, ["Amount"] = "0" });
        var unknown = await maker.PostFormAsync("/Transactions/Deposit", "/Transactions/Deposit", new Dictionary<string, string> { ["AccountNo"] = "0000000000000", ["Amount"] = "10" });

        Assert.Equal(HttpStatusCode.OK, zero.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal(before, await web.WithDbAsync(db => db.Vouchers.CountAsync()));
    }

    [Fact]
    public async Task A_withdrawal_above_the_balance_is_refused_with_a_message_instead_of_an_error_page()
    {
        var (number, balance, _) = await ActiveAccountAsync();
        using var maker = web.CreateBankClient("flow.maker3", Roles.Teller);

        var response = await maker.PostFormAsync("/Transactions/Withdraw", "/Transactions/Withdraw",
            new Dictionary<string, string> { ["AccountNo"] = number, ["Amount"] = (balance + 1_000_000m).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Insufficient funds", html);
    }

    [Fact]
    public async Task A_new_customer_can_be_created_and_duplicate_nids_are_refused()
    {
        var branchId = web.Ids["branch"];
        var nid = "9" + Random.Shared.NextInt64(100_000_000, 999_999_999);
        var form = new Dictionary<string, string>
        {
            ["Type"] = "1", ["Name"] = "Flow Test Customer", ["Mobile"] = "01712345678", ["Address"] = "Dhaka", ["Nid"] = nid,
            ["BranchId"] = branchId.ToString(), ["Risk"] = "1", ["Kyc"] = "0", ["IsActive"] = "true"
        };
        using var officer = web.CreateBankClient("flow.officer", Roles.Officer);

        var created = await officer.PostMultipartAsync("/Customers/Create", "/Customers/Create", form);
        var duplicate = await officer.PostMultipartAsync("/Customers/Create", "/Customers/Create", form);

        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var stored = await web.WithDbAsync(db => db.Customers.AsNoTracking().SingleAsync(c => c.Nid == nid));
        Assert.Equal(KycStatus.Pending, stored.Kyc);
        Assert.Equal("flow.officer", stored.MadeBy);
        Assert.Equal(10, stored.CifNo.Length);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(1, await web.WithDbAsync(db => db.Customers.CountAsync(c => c.Nid == nid)));
    }

    [Fact]
    public async Task Kyc_cannot_be_verified_by_the_maker_who_entered_the_customer()
    {
        var nid = "8" + Random.Shared.NextInt64(100_000_000, 999_999_999);
        using var officer = web.CreateBankClient("flow.officer2", Roles.Manager);
        await officer.PostMultipartAsync("/Customers/Create", "/Customers/Create", new Dictionary<string, string>
        {
            ["Type"] = "1", ["Name"] = "KYC Test", ["Mobile"] = "01712345679", ["Address"] = "Dhaka", ["Nid"] = nid,
            ["BranchId"] = web.Ids["branch"].ToString(), ["Risk"] = "1", ["IsActive"] = "true"
        });
        var customer = await web.WithDbAsync(db => db.Customers.AsNoTracking().SingleAsync(c => c.Nid == nid));

        await officer.PostFormAsync($"/Customers/Details/{customer.Id}", $"/Customers/Verify/{customer.Id}", new Dictionary<string, string> { ["approve"] = "true" });

        Assert.Equal(KycStatus.Pending, (await web.WithDbAsync(db => db.Customers.AsNoTracking().SingleAsync(c => c.Id == customer.Id))).Kyc);
    }

    [Fact]
    public async Task Bank_parameters_reject_values_that_would_break_postings()
    {
        using var admin = web.CreateBankClient();
        var original = await web.WithDbAsync(async db => (await db.BankParameters.AsNoTracking().FirstAsync(p => p.Key == "CashGl")).Value);
        var vat = await web.WithDbAsync(async db => (await db.BankParameters.AsNoTracking().FirstAsync(p => p.Key == "VatRate")).Value);

        await admin.PostFormAsync("/Setup/Parameters", "/Setup/Parameters", new Dictionary<string, string> { ["values[CashGl]"] = "9999", ["values[VatRate]"] = "abc" });

        Assert.Equal(original, await web.WithDbAsync(async db => (await db.BankParameters.AsNoTracking().FirstAsync(p => p.Key == "CashGl")).Value));
        Assert.Equal(vat, await web.WithDbAsync(async db => (await db.BankParameters.AsNoTracking().FirstAsync(p => p.Key == "VatRate")).Value));
    }
}
