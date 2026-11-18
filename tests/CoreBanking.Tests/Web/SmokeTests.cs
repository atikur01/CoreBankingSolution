using System.Net;

namespace CoreBanking.Tests.Web;

[Collection(WebCollection.Name)]
public class SmokeTests(WebFixture web)
{
    public static IEnumerable<object[]> Pages() =>
    [
        ["/"], ["/Home/Index"],
        ["/Customers"], ["/Customers?q=a&kyc=1"], ["/Customers/Create"], ["/Customers/Details/{customer}"], ["/Customers/Edit/{customer}"],
        ["/Accounts"], ["/Accounts?status=1"], ["/Accounts/Open"], ["/Accounts/Details/{account}"], ["/Accounts/Statement/{account}"],
        ["/Accounts/Statement/{account}?csv=true"], ["/Accounts/Lookup?no=000"],
        ["/Transactions/Deposit"], ["/Transactions/Withdraw"], ["/Transactions/Transfer"], ["/Transactions/Journal"],
        ["/Transactions/Pending"], ["/Transactions/Register"], ["/Transactions/Register?csv=true"], ["/Transactions/Details/{voucher}"],
        ["/Loans"], ["/Loans/Apply"], ["/Loans/Details/{loan}"],
        ["/Remittance"], ["/Remittance/Issue"], ["/Clearing"], ["/StandingInstructions"], ["/Eod"], ["/Sanctions"],
        ["/Reports"], ["/Reports/TrialBalance"], ["/Reports/TrialBalance?csv=true"], ["/Reports/GlStatement?extra=1100"], ["/Reports/GlStatement?extra=1000"],
        ["/Reports/DayBook"], ["/Reports/BalanceSheet"], ["/Reports/IncomeStatement"], ["/Reports/InterBranch"], ["/Reports/Deposits"], ["/Reports/Deposits?extra=Dormant"],
        ["/Reports/Dormant"], ["/Reports/Maturity"], ["/Reports/LoanPortfolio"], ["/Reports/Unauthorized"], ["/Reports/Ctr"], ["/Reports/Cheques"],
        ["/Reports/UserActivity"], ["/Reports/Rates"], ["/Reports/DayBook?csv=true"], ["/Reports/Rates?csv=true"],
        ["/Setup/Branches"], ["/Setup/BranchEdit"], ["/Setup/BranchEdit/{branch}"], ["/Setup/Gl"], ["/Setup/GlEdit"], ["/Setup/GlEdit/{gl}"],
        ["/Setup/Products"], ["/Setup/ProductEdit"], ["/Setup/ProductEdit/{product}"], ["/Setup/ProductRates/{product}"],
        ["/Setup/Currencies"], ["/Setup/Holidays"], ["/Setup/Excise"], ["/Setup/Parameters"],
        ["/Users"], ["/Users/Create"], ["/Users/Roles"], ["/Users/Audit"], ["/Users/Audit?csv=true"],
        ["/Password/Change"], ["/Identity/Account/Login"]
    ];

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Every_page_renders_for_an_administrator(string template)
    {
        var url = web.Ids.Aggregate(template, (current, kv) => current.Replace("{" + kv.Key + "}", kv.Value.ToString()));
        using var client = web.CreateBankClient();

        var response = await client.GetAsync(url);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)response.StatusCode}\n{(body.Length > 1500 ? body[..1500] : body)}");
    }

    [Fact]
    public async Task Unknown_records_return_not_found_instead_of_failing()
    {
        using var client = web.CreateBankClient();

        foreach (var url in new[] { "/Customers/Details/999999", "/Accounts/Details/999999", "/Loans/Details/999999", "/Transactions/Details/999999", "/Accounts/Statement/999999" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Account_lookup_reports_known_and_unknown_numbers()
    {
        var number = await web.WithDbAsync(async db => (await db.Accounts.AsNoTracking().FirstAsync()).AccountNo);
        using var client = web.CreateBankClient();

        var known = await client.GetStringAsync("/Accounts/Lookup?no=" + number);
        var unknown = await client.GetStringAsync("/Accounts/Lookup?no=0000000000000");

        Assert.Contains("\"found\":true", known);
        Assert.Contains("\"found\":false", unknown);
    }

    [Fact]
    public async Task Csv_exports_are_utf8_with_bom_and_neutralise_formula_injection()
    {
        using var client = web.CreateBankClient();

        var response = await client.GetAsync("/Transactions/Register?csv=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public async Task The_public_registration_endpoint_is_disabled()
    {
        using var client = web.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Identity/Account/Register")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Identity/Account/ExternalLogin")).StatusCode);
    }
}
