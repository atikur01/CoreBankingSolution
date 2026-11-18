using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoreBanking.Tests.Web;

public sealed class WebFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string UserHeader = "X-Test-User";
    public const string RoleHeader = "X-Test-Role";
    public const string AnonymousHeader = "X-Test-Anonymous";

    private readonly string _database = $"CoreBankingWeb_{Guid.NewGuid():N}";

    public Dictionary<string, int> Ids { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:DefaultConnection",
            $"Server=(localdb)\\MSSQLLocalDB;Database={_database};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True");
        builder.UseSetting("Seed:DemoData", "true");
        builder.ConfigureLogging(l => l.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(o =>
            {
                o.DefaultScheme = TestAuthHandler.Scheme;
                o.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                o.DefaultChallengeScheme = TestAuthHandler.Scheme;
                o.DefaultForbidScheme = TestAuthHandler.Scheme;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
        });
    }

    public HttpClient CreateBankClient(string user = "admin@corebank.local", string role = Roles.Administrator)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(UserHeader, user);
        client.DefaultRequestHeaders.Add(RoleHeader, role);
        return client;
    }

    public HttpClient CreateAnonymousClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(AnonymousHeader, "1");
        return client;
    }

    public async Task<T> WithDbAsync<T>(Func<IApplicationDbContext, Task<T>> work)
    {
        using var scope = Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<IApplicationDbContext>());
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        foreach (var p in await db.UserProfiles.ToListAsync())
        {
            p.MustChangePassword = false;
            p.PasswordChangedOn = DateTime.Now;
            p.WorkFrom = null;
            p.WorkTo = null;
            p.AllowedIp = null;
        }
        await db.SaveChangesAsync();

        Ids["customer"] = (await db.Customers.AsNoTracking().OrderBy(c => c.Id).FirstAsync()).Id;
        Ids["account"] = (await db.Accounts.AsNoTracking().Where(a => a.Status == AccountStatus.Active).OrderBy(a => a.Id).FirstAsync()).Id;
        Ids["voucher"] = (await db.Vouchers.AsNoTracking().OrderBy(v => v.Id).FirstAsync()).Id;
        Ids["loan"] = (await db.Loans.AsNoTracking().OrderBy(l => l.Id).FirstAsync()).Id;
        Ids["product"] = (await db.Products.AsNoTracking().OrderBy(p => p.Id).FirstAsync()).Id;
        Ids["branch"] = (await db.Branches.AsNoTracking().OrderBy(b => b.Id).FirstAsync()).Id;
        Ids["gl"] = (await db.GlAccounts.AsNoTracking().OrderBy(g => g.Id).FirstAsync()).Id;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await LocalDb.DropAsync(_database);
    }
}

public class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Test";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.ContainsKey(WebFixture.AnonymousHeader)) return AuthenticateResult.NoResult();

        var name = Request.Headers.TryGetValue(WebFixture.UserHeader, out var u) ? u.ToString() : "admin@corebank.local";
        var role = Request.Headers.TryGetValue(WebFixture.RoleHeader, out var r) ? r.ToString() : Roles.Administrator;
        var users = Context.RequestServices.GetRequiredService<UserManager<IdentityUser>>();
        var id = (await users.FindByNameAsync(name))?.Id ?? "test-" + name;

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Role, role)],
            IdentityConstants.ApplicationScheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme));
    }
}

[CollectionDefinition(Name)]
public class WebCollection : ICollectionFixture<WebFixture>
{
    public const string Name = "web";
}
