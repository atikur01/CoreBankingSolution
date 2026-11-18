using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Infrastructure.Persistence.Seed.Demo;

public static class DemoDataSeeder
{
    public static async Task SeedAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        if (!sp.GetRequiredService<IConfiguration>().GetValue("Seed:DemoData", true)) return;
        if (await db.Customers.AnyAsync() || await db.Vouchers.AnyAsync()) return;

        await using var tx = await db.Database.BeginTransactionAsync();
        var state = new DemoState(db);

        await DemoReferenceSeeder.RunAsync(state, sp);
        DemoCustomerSeeder.Run(state);
        await db.SaveChangesAsync();

        var loans = new DemoLoanSeeder(state);
        loans.Build();
        await db.SaveChangesAsync();

        new DemoActivitySeeder(state).Run();
        state.RunQueue();
        loans.PostProvisions();
        DemoLogSeeder.PostAccruals(state);
        DemoLogSeeder.AddEodLogs(state);
        DemoLogSeeder.AddAuditEntries(state);
        await db.SaveChangesAsync();

        state.RunDeferred();
        await state.PersistSequencesAsync();
        await tx.CommitAsync();
    }
}
