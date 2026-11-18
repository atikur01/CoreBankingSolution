using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Infrastructure.Persistence.Seed.Demo;

internal static class DemoReferenceSeeder
{
    public static async Task RunAsync(DemoState s, IServiceProvider sp)
    {
        await SeedBranchesAsync(s);
        await SeedLookupsAsync(s);
        await SeedUsersAsync(s, sp);
    }

    private static async Task SeedBranchesAsync(DemoState s)
    {
        var db = s.Db;
        var existing = await db.Branches.ToDictionaryAsync(b => b.Code);
        foreach (var d in DemoCatalog.Branches)
        {
            if (!existing.TryGetValue(d.Code, out var b))
            {
                b = new Branch { Code = d.Code, Name = d.Name, Layer = d.Layer, CostCenter = $"CC{d.Code}", ProfitCenter = $"PC{d.Code}" };
                if (d.Parent.Length > 0) b.ParentId = existing[d.Parent].Id;
                db.Branches.Add(b);
                await db.SaveChangesAsync();
                existing[d.Code] = b;
            }
            b.Address = d.Address;
            b.Phone = d.Phone;
            b.RoutingNumber = d.Routing;
            b.IsActive = d.Active;
            b.BusinessDate = s.Today;
            b.DayOpen = true;
        }
        await db.SaveChangesAsync();
        s.Branches.AddRange(existing.Values.Where(b => b.IsActive).OrderBy(b => b.Code));
    }

    private static async Task SeedLookupsAsync(DemoState s)
    {
        var db = s.Db;
        s.Bdt = await db.Currencies.FirstAsync(c => c.IsBase);
        foreach (var g in await db.GlAccounts.ToListAsync()) s.Gl[g.Code] = g.Id;
        foreach (var p in await db.Products.ToListAsync()) s.Products[p.Code] = p;

        var fx = new (string Code, decimal Buy, decimal Sell)[] { ("USD", 121.20m, 122.00m), ("EUR", 130.80m, 132.10m), ("GBP", 153.40m, 155.20m) };
        var currencies = await db.Currencies.ToDictionaryAsync(c => c.Code);
        foreach (var (code, buy, sell) in fx)
            for (int d = 9; d >= 0; d--)
            {
                var drift = (decimal)(s.Rng.NextDouble() - 0.5) * 0.6m;
                db.ExchangeRates.Add(new ExchangeRate { CurrencyId = currencies[code].Id, RateDate = s.Today.AddDays(-d), BuyRate = Math.Round(buy + drift, 4), SellRate = Math.Round(sell + drift, 4) });
            }

        var y = s.Today.Year;
        db.Holidays.AddRange(
            new Holiday { Date = new DateTime(y, 2, 21), Description = "International Mother Language Day" },
            new Holiday { Date = new DateTime(y, 3, 26), Description = "Independence Day" },
            new Holiday { Date = new DateTime(y, 4, 14), Description = "Pohela Boishakh (Bengali New Year)" },
            new Holiday { Date = new DateTime(y, 5, 1), Description = "May Day" },
            new Holiday { Date = new DateTime(y, 12, 16), Description = "Victory Day" },
            new Holiday { Date = new DateTime(y, 12, 25), Description = "Christmas Day" });

        var start = s.Today.AddDays(-500);
        ProductRate Rate(string code, decimal min, decimal max, int minT, int maxT, decimal rate, ApprovalStatus st, DateTime from) =>
            new() { ProductId = s.Products[code].Id, MinAmount = min, MaxAmount = max, MinTermMonths = minT, MaxTermMonths = maxT, Rate = rate, Status = st, EffectiveFrom = from, MadeBy = DemoState.OfficerUser, CheckedBy = st == ApprovalStatus.Pending ? null : DemoState.ManagerUser };
        db.ProductRates.AddRange(
            Rate("01", 0, 0, 0, 0, 4.00m, ApprovalStatus.Approved, start),
            Rate("03", 0, 999999.99m, 0, 0, 5.50m, ApprovalStatus.Approved, start),
            Rate("03", 1000000, 0, 0, 0, 6.00m, ApprovalStatus.Approved, start),
            Rate("04", 0, 999999.99m, 0, 0, 8.00m, ApprovalStatus.Approved, start),
            Rate("04", 1000000, 0, 0, 0, 8.50m, ApprovalStatus.Approved, start),
            Rate("04", 1000000, 0, 0, 0, 8.75m, ApprovalStatus.Pending, s.Today.AddDays(7)),
            Rate("04", 0, 0, 36, 0, 9.25m, ApprovalStatus.Rejected, s.Today.AddDays(-30)),
            Rate("05", 0, 0, 0, 0, 7.50m, ApprovalStatus.Approved, start),
            Rate("61", 0, 0, 0, 0, 12.00m, ApprovalStatus.Approved, start),
            Rate("62", 0, 0, 0, 0, 13.00m, ApprovalStatus.Approved, start));

        foreach (var (name, aliases, id, notes, source) in DemoCatalog.Sanctions)
            db.SanctionEntries.Add(new SanctionEntry { Name = name, Aliases = aliases.Length == 0 ? null : aliases, IdNumber = id, Notes = notes, Source = source, AddedOn = s.Today.AddDays(-s.Rng.Next(30, 400)) });

        await db.SaveChangesAsync();
    }

    private static async Task SeedUsersAsync(DemoState s, IServiceProvider sp)
    {
        var users = sp.GetRequiredService<UserManager<IdentityUser>>();
        var branches = s.Branches.ToDictionary(b => b.Code);
        foreach (var u in DemoCatalog.Users)
        {
            if (await users.FindByEmailAsync(u.Email) != null) continue;
            var user = new IdentityUser { UserName = u.Email, Email = u.Email, EmailConfirmed = true };
            if (!(await users.CreateAsync(user, DemoState.Password)).Succeeded) continue;
            await users.AddToRoleAsync(user, u.Role);
            s.Db.UserProfiles.Add(new UserProfile
            {
                UserId = user.Id, FullName = u.FullName, EmployeeId = u.EmployeeId, BranchId = branches[u.BranchCode].Id, Designation = u.Designation,
                MaxCredit = u.MaxCredit, MaxDebit = u.MaxDebit, MaxInterBranchCredit = u.MaxCredit, MaxInterBranchDebit = u.MaxDebit,
                WorkFrom = new TimeSpan(9, 0, 0), WorkTo = new TimeSpan(17, 0, 0), MustChangePassword = false, PasswordChangedOn = s.Today.AddDays(-3)
            });
            s.Db.PasswordHistories.Add(new PasswordHistory { UserId = user.Id, Hash = user.PasswordHash ?? "", SetOn = s.Today.AddDays(-3) });
        }
        await s.Db.SaveChangesAsync();
    }
}
