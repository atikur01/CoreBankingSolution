using System.Globalization;
using CoreBanking.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Identity;

namespace CoreBanking.Tests.Services;

[Collection(DatabaseCollection.Name)]
public class PlatformTests(DatabaseFixture fx)
{
    private async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = fx.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    [Fact]
    public async Task Number_sequences_are_strictly_increasing_per_key()
    {
        var key = "TEST-" + Guid.NewGuid().ToString("N");

        var values = new List<long>();
        for (var i = 0; i < 5; i++) values.Add(await InScope(sp => sp.GetRequiredService<NumberService>().NextAsync(key)));

        Assert.Equal([1L, 2L, 3L, 4L, 5L], values);
    }

    [Fact]
    public async Task Number_sequences_stay_unique_under_parallel_requests()
    {
        var key = "PARALLEL-" + Guid.NewGuid().ToString("N");

        var values = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => InScope(sp => sp.GetRequiredService<NumberService>().NextAsync(key))));

        Assert.Equal(20, values.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), values.OrderBy(v => v));
    }

    [Fact]
    public async Task Reserving_a_range_returns_the_first_number_and_moves_the_sequence_past_the_block()
    {
        var key = "RANGE-" + Guid.NewGuid().ToString("N");

        var (first, second, next) = await InScope(async sp =>
        {
            var numbers = sp.GetRequiredService<NumberService>();
            return (await numbers.ReserveRangeAsync(key, 100), await numbers.ReserveRangeAsync(key, 25), await numbers.NextAsync(key));
        });

        Assert.Equal(1, first);
        Assert.Equal(101, second);
        Assert.Equal(126, next);
    }

    [Fact]
    public async Task Generated_business_numbers_have_the_documented_shape_and_valid_check_digits()
    {
        var (account, cif, voucher, instrument) = await InScope(async sp =>
        {
            var n = sp.GetRequiredService<NumberService>();
            return (await n.NextAccountNoAsync("001", "01"), await n.NextCifAsync("001"), await n.NextVoucherNoAsync("001", new DateTime(2026, 3, 9)),
                await n.NextInstrumentNoAsync(RemittanceType.DD, "001"));
        });

        Assert.Equal(13, account.Length);
        Assert.True(Money.ValidNumber(account));
        Assert.Equal(10, cif.Length);
        Assert.True(Money.ValidNumber(cif));
        Assert.StartsWith("V001260309", voucher);
        Assert.StartsWith("DD001", instrument);
    }

    [Fact]
    public async Task Every_gl_parameter_default_points_to_an_existing_posting_account()
    {
        var keys = ParamService.Defaults.Where(d => d.Key.EndsWith("Gl")).Select(d => d.Key).ToList();

        await InScope(async sp =>
        {
            var prm = sp.GetRequiredService<ParamService>();
            var db = sp.GetRequiredService<IApplicationDbContext>();
            foreach (var key in keys)
            {
                var id = await prm.GlIdAsync(key);
                var gl = await db.GlAccounts.AsNoTracking().FirstAsync(g => g.Id == id);
                Assert.True(gl.IsPosting && gl.IsActive, $"{key} -> {gl.Code} is not an open posting account");
            }
            return true;
        });
        Assert.NotEmpty(keys);
    }

    [Fact]
    public void Parameter_defaults_have_unique_keys_and_numeric_values_where_expected()
    {
        Assert.Equal(ParamService.Defaults.Length, ParamService.Defaults.Select(d => d.Key).Distinct().Count());
        foreach (var (key, value, _) in ParamService.Defaults.Where(d => d.Key.EndsWith("Rate") || d.Key.EndsWith("Days") || d.Key.EndsWith("Amount") || d.Key.EndsWith("Pct")))
            Assert.True(decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _), key);
    }

    [Fact]
    public async Task Unknown_parameters_read_as_empty_or_zero_and_unknown_gl_codes_raise_a_business_error()
    {
        await InScope(async sp =>
        {
            var prm = sp.GetRequiredService<ParamService>();
            Assert.Equal("", await prm.GetAsync("DoesNotExist"));
            Assert.Equal(0m, await prm.DecAsync("DoesNotExist"));
            await Assert.ThrowsAsync<BankingException>(() => prm.GlIdAsync("DoesNotExist"));
            return true;
        });
    }

    [Fact]
    public async Task Weekly_and_configured_holidays_are_recognised_and_skipped()
    {
        await InScope(async sp =>
        {
            var prm = sp.GetRequiredService<ParamService>();
            Assert.True(await prm.IsHolidayAsync(new DateTime(2026, 3, 13)));
            Assert.True(await prm.IsHolidayAsync(new DateTime(2026, 3, 14)));
            Assert.False(await prm.IsHolidayAsync(new DateTime(2026, 3, 15)));
            Assert.Equal(new DateTime(2026, 3, 15), await prm.NextWorkingDayAsync(new DateTime(2026, 3, 12)));
            return true;
        });
    }

    [Fact]
    public async Task Sanction_screening_matches_ids_names_aliases_and_ignores_inactive_or_weak_matches()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var idNumber = "ID-" + tag;
        await InScope(async sp =>
        {
            var db = sp.GetRequiredService<IApplicationDbContext>();
            db.SanctionEntries.AddRange(
                new SanctionEntry { Name = $"Abdul Karim Listed {tag}", Aliases = $"A K Listed {tag}; Karim Nickname{tag}", IdNumber = idNumber, Source = SanctionSource.UNSCR, IsActive = true },
                new SanctionEntry { Name = $"Inactive Person {tag}", Source = SanctionSource.Local, IsActive = false });
            await db.SaveChangesAsync();
            return true;
        });

        await InScope(async sp =>
        {
            var s = sp.GetRequiredService<SanctionService>();
            Assert.NotEmpty(await s.ScreenAsync([idNumber]));
            Assert.NotEmpty(await s.ScreenAsync([$"abdul   KARIM listed {tag}"]));
            Assert.NotEmpty(await s.ScreenAsync([$"Karim Listed {tag}"]));
            Assert.NotEmpty(await s.ScreenAsync([$"Karim Nickname{tag}"]));
            Assert.Empty(await s.ScreenAsync([$"Inactive Person {tag}"]));
            Assert.Empty(await s.ScreenAsync([$"Totally Different {tag}"]));
            Assert.Empty(await s.ScreenAsync([null, "", "  ", "ab"]));
            Assert.Contains("UNSCR", SanctionService.Format(await s.ScreenAsync([idNumber])));
            return true;
        });
    }

    [Fact]
    public async Task Rate_lookup_uses_the_latest_approved_slab_effective_on_the_date_else_the_product_default()
    {
        var w = await World.CreateAsync(fx);
        var product = await w.NewProductAsync(ProductCategory.FDR, p => p.InterestRate = 8m);
        await w.Do(async s =>
        {
            s.Db.ProductRates.AddRange(
                new ProductRate { ProductId = product.Id, EffectiveFrom = new DateTime(2026, 1, 1), MinAmount = 0, MaxAmount = 100_000, MinTermMonths = 0, MaxTermMonths = 0, Rate = 9m, Status = ApprovalStatus.Approved },
                new ProductRate { ProductId = product.Id, EffectiveFrom = new DateTime(2026, 2, 1), MinAmount = 0, MaxAmount = 100_000, MinTermMonths = 0, MaxTermMonths = 0, Rate = 9.5m, Status = ApprovalStatus.Approved },
                new ProductRate { ProductId = product.Id, EffectiveFrom = new DateTime(2026, 3, 1), MinAmount = 0, MaxAmount = 100_000, MinTermMonths = 0, MaxTermMonths = 0, Rate = 12m, Status = ApprovalStatus.Pending },
                new ProductRate { ProductId = product.Id, EffectiveFrom = new DateTime(2026, 1, 1), MinAmount = 100_001, MaxAmount = 0, MinTermMonths = 12, MaxTermMonths = 0, Rate = 10m, Status = ApprovalStatus.Approved });
            await s.Db.SaveChangesAsync();
        });

        await w.Do(async s =>
        {
            Assert.Equal(9.5m, await s.Rates.GetRateAsync(product.Id, 50_000m, 6, new DateTime(2026, 3, 9)));
            Assert.Equal(9m, await s.Rates.GetRateAsync(product.Id, 50_000m, 6, new DateTime(2026, 1, 15)));
            Assert.Equal(10m, await s.Rates.GetRateAsync(product.Id, 500_000m, 12, new DateTime(2026, 3, 9)));
            Assert.Equal(8m, await s.Rates.GetRateAsync(product.Id, 500_000m, 6, new DateTime(2026, 3, 9)));
            Assert.Equal(8m, await s.Rates.GetRateAsync(product.Id, 50_000m, 6, new DateTime(2025, 12, 1)));
        });
    }

    [Fact]
    public async Task Every_change_is_audited_with_old_and_new_values_but_the_audit_log_itself_is_not()
    {
        var w = await World.CreateAsync(fx);
        var customer = await w.NewCustomerAsync();

        await w.UpdateAsync(db => db.Customers, customer.Id, c => c.Mobile = "01999999999");

        var logs = await w.Read(db => db.AuditLogs.AsNoTracking().Where(a => a.Entity == "Customer" && a.EntityKey == customer.Id.ToString()).OrderBy(a => a.Id).ToListAsync());
        Assert.Equal(["Create", "Update"], logs.Select(l => l.Action));
        Assert.Contains("01999999999", logs[1].Details);
        Assert.Contains("->", logs[1].Details);
        Assert.Empty(await w.Read(db => db.AuditLogs.AsNoTracking().Where(a => a.Entity == "AuditLog" || a.Entity == "NumberSequence").ToListAsync()));
    }

    [Fact]
    public async Task Seeding_is_idempotent_and_provides_roles_admin_chart_of_accounts_and_products()
    {
        var before = await InScope(async sp => await CountsAsync(sp));
        await InScope(async sp => { await DbSeeder.SeedAsync(sp); return true; });
        var after = await InScope(async sp => await CountsAsync(sp));

        Assert.Equal(before, after);
        Assert.True(after.roles >= Roles.All.Length);
        Assert.True(after.gl > 30);
        Assert.True(after.products >= 7);
        Assert.Equal(1, await InScope(async sp => await sp.GetRequiredService<IApplicationDbContext>().Currencies.CountAsync(c => c.IsBase)));
        var admin = await InScope(sp => sp.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(DbSeeder.AdminEmail));
        Assert.NotNull(admin);
    }

    private static async Task<(int roles, int gl, int products, int branches, int parameters, int users)> CountsAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<IApplicationDbContext>();
        var identity = (Microsoft.EntityFrameworkCore.DbContext)db;
        return (await identity.Set<IdentityRole>().CountAsync(), await db.GlAccounts.CountAsync(), await db.Products.CountAsync(),
            await db.Branches.CountAsync(b => b.Code == "001" || b.Code == "002"), await db.BankParameters.CountAsync(), await identity.Set<IdentityUser>().CountAsync());
    }
}
