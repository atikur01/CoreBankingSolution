using System.Net;

namespace CoreBanking.Tests.Web;

[Collection(WebCollection.Name)]
public class AccessControlTests(WebFixture web)
{
    private static readonly string[] AllRoles = Roles.All;

    private static string[] Only(params string[] roles) => roles;

    public static IEnumerable<object[]> Matrix()
    {
        var makers = Only(Roles.Administrator, Roles.Manager, Roles.Supervisor, Roles.Officer, Roles.Teller);
        var operations = Only(Roles.Administrator, Roles.Manager, Roles.Supervisor, Roles.Officer);
        var checkers = Only(Roles.Administrator, Roles.Manager, Roles.Supervisor);
        var setup = Only(Roles.Administrator, Roles.Manager);
        var admin = Only(Roles.Administrator);

        foreach (var role in AllRoles)
        {
            foreach (var url in new[] { "/Customers", "/Accounts", "/Transactions/Deposit", "/Transactions/Register", "/Loans", "/Remittance", "/Clearing", "/StandingInstructions", "/Reports/TrialBalance" })
                yield return [role, url, makers.Contains(role)];
            foreach (var url in new[] { "/Customers/Create", "/Accounts/Open", "/Transactions/Journal", "/Loans/Apply", "/Sanctions" })
                yield return [role, url, operations.Contains(role)];
            foreach (var url in new[] { "/Transactions/Pending", "/Eod" })
                yield return [role, url, checkers.Contains(role)];
            foreach (var url in new[] { "/Setup/Branches", "/Setup/Gl", "/Setup/Products", "/Setup/Parameters", "/Setup/Holidays" })
                yield return [role, url, setup.Contains(role)];
            foreach (var url in new[] { "/Users", "/Users/Audit", "/Users/Create" })
                yield return [role, url, admin.Contains(role)];
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Role_permissions_match_the_policy(string role, string url, bool allowed)
    {
        using var client = web.CreateBankClient("matrix." + role.ToLowerInvariant(), role);

        var response = await client.GetAsync(url);

        if (allowed) Assert.True(response.StatusCode == HttpStatusCode.OK, $"{role} {url} -> {(int)response.StatusCode}");
        else Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{role} {url} -> {(int)response.StatusCode}");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Customers")]
    [InlineData("/Transactions/Pending")]
    [InlineData("/Setup/Parameters")]
    [InlineData("/Users")]
    [InlineData("/Reports/TrialBalance")]
    public async Task Anonymous_visitors_are_challenged(string url)
    {
        using var client = web.CreateAnonymousClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Static_assets_and_the_login_page_stay_public()
    {
        using var client = web.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Identity/Account/Login")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/css/site.css")).StatusCode);
    }

    [Fact]
    public async Task State_changing_requests_without_an_antiforgery_token_are_rejected()
    {
        using var client = web.CreateBankClient();

        var response = await client.PostAsync("/Transactions/Authorize/" + web.Ids["voucher"], new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Users_flagged_for_a_password_change_are_redirected_until_they_change_it()
    {
        var email = $"force.{Guid.NewGuid():N}@corebank.local";
        await web.WithDbAsync(async db =>
        {
            using var scope = web.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Microsoft.AspNetCore.Identity.IdentityUser>>();
            var user = new Microsoft.AspNetCore.Identity.IdentityUser { UserName = email, Email = email };
            await users.CreateAsync(user, "Str0ng!Passw0rd");
            await users.AddToRoleAsync(user, Roles.Officer);
            var branch = await db.Branches.AsNoTracking().FirstAsync();
            db.UserProfiles.Add(new UserProfile { UserId = user.Id, FullName = "Forced", BranchId = branch.Id, MustChangePassword = true });
            await db.SaveChangesAsync();
            return true;
        });
        using var client = web.CreateBankClient(email, Roles.Officer);

        var dashboard = await client.GetAsync("/");
        var change = await client.GetAsync("/Password/Change?forced=1");

        Assert.Equal(HttpStatusCode.Redirect, dashboard.StatusCode);
        Assert.Contains("/Password/Change", dashboard.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
    }

    [Fact]
    public async Task Deactivated_users_are_signed_out()
    {
        var email = $"off.{Guid.NewGuid():N}@corebank.local";
        await web.WithDbAsync(async db =>
        {
            using var scope = web.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Microsoft.AspNetCore.Identity.IdentityUser>>();
            var user = new Microsoft.AspNetCore.Identity.IdentityUser { UserName = email, Email = email };
            await users.CreateAsync(user, "Str0ng!Passw0rd");
            var branch = await db.Branches.AsNoTracking().FirstAsync();
            db.UserProfiles.Add(new UserProfile { UserId = user.Id, FullName = "Off", BranchId = branch.Id, MustChangePassword = false, IsActive = false });
            await db.SaveChangesAsync();
            return true;
        });
        using var client = web.CreateBankClient(email, Roles.Officer);

        var response = await client.GetAsync("/Customers");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("denied=1", response.Headers.Location!.ToString());
    }
}
