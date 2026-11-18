using CoreBankingSolution.Data;
using CoreBankingSolution.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddHttpContextAccessor();
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddDefaultIdentity<IdentityUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        // Password policy (BB CBS guideline 10.1.3) - history & expiry are enforced by PasswordPolicyMiddleware.
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddRazorPages();
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});
builder.Services.ConfigureApplicationCookie(o =>
{
    o.ExpireTimeSpan = TimeSpan.FromMinutes(30);   // idle session timeout
    o.SlidingExpiration = true;
    o.Cookie.HttpOnly = true;
});

builder.Services.AddScoped<ParamService>();
builder.Services.AddScoped<NumberService>();
builder.Services.AddScoped<SanctionService>();
builder.Services.AddScoped<RateService>();
builder.Services.AddScoped<LedgerService>();
builder.Services.AddScoped<DepositService>();
builder.Services.AddScoped<TransactionService>();
builder.Services.AddScoped<LoanService>();
builder.Services.AddScoped<RemittanceService>();
builder.Services.AddScoped<ClearingService>();
builder.Services.AddScoped<EodService>();
builder.Services.AddScoped<IVoucherHook, DepositHook>();
builder.Services.AddScoped<IVoucherHook, LoanHook>();
builder.Services.AddScoped<IVoucherHook, RemittanceHook>();
builder.Services.AddScoped<IVoucherHook, ClearingHook>();
builder.Services.AddScoped<UserContext>();

var app = builder.Build();

// Create / update the database schema and reference data.
using (var scope = app.Services.CreateScope())
{
    var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        // Database:Mode = Migrate (default, applies EF migrations) | EnsureCreated (dev/test only) | None
        var mode = app.Configuration.GetValue("Database:Mode", "Migrate");
        if (mode == "Migrate") await db.Database.MigrateAsync();
        else if (mode == "EnsureCreated") await db.Database.EnsureCreatedAsync();
        await DbSeeder.SeedAsync(scope.ServiceProvider);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Database initialisation failed. Check the connection string / network access.");
        if (app.Environment.IsDevelopment()) throw;
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Users are created by administrators only - public self-registration is disabled.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/Identity/Account/Register", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/Identity/Account/ExternalLogin", StringComparison.OrdinalIgnoreCase))
    { ctx.Response.StatusCode = 404; return; }
    await next();
});

app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<PasswordPolicyMiddleware>();
app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();
app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}").WithStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();

public partial class Program { }
