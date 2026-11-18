using CoreBanking.Application;
using CoreBanking.Infrastructure;
using CoreBanking.Infrastructure.Persistence;
using CoreBanking.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CoreBanking.Tests.Infrastructure;

public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string Server = "(localdb)\\MSSQLLocalDB";

    public string DatabaseName { get; } = $"CoreBankingTests_{Guid.NewGuid():N}";
    public string ConnectionString => $"Server={Server};Database={DatabaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";
    public ServiceProvider Services { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = ConnectionString,
            ["Seed:DemoData"] = "false"
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddApplication();
        services.AddInfrastructure(config);
        services.AddDefaultIdentity<IdentityUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>();
        Services = services.BuildServiceProvider();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
        await DbSeeder.SeedAsync(scope.ServiceProvider);
    }

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        await LocalDb.DropAsync(DatabaseName);
    }
}

public sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "CoreBanking.Tests";
    public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "corebanking-tests-" + Guid.NewGuid().ToString("N"));
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

public static class LocalDb
{
    public static async Task DropAsync(string databaseName)
    {
        SqlConnection.ClearAllPools();
        var master = "Server=(localdb)\\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True";
        await using var conn = new SqlConnection(master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"IF DB_ID('{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END";
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
