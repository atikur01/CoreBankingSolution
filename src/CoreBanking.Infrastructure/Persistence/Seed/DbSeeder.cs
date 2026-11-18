using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CoreBanking.Infrastructure.Persistence.Seed;

public static class DbSeeder
{
    public const string AdminEmail = "admin@corebank.local";
    public const string AdminDefaultPassword = "Admin@12345";

    public static async Task SeedAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var roleMgr = sp.GetRequiredService<RoleManager<IdentityRole>>();
        var userMgr = sp.GetRequiredService<UserManager<IdentityUser>>();

        foreach (var r in Roles.All)
            if (!await roleMgr.RoleExistsAsync(r)) await roleMgr.CreateAsync(new IdentityRole(r));

        if (!await db.Currencies.AnyAsync())
        {
            db.Currencies.AddRange(
                new Currency { Code = "BDT", Name = "Bangladeshi Taka", Symbol = "৳", IsBase = true },
                new Currency { Code = "USD", Name = "US Dollar", Symbol = "$" },
                new Currency { Code = "EUR", Name = "Euro", Symbol = "€" },
                new Currency { Code = "GBP", Name = "Pound Sterling", Symbol = "£" });
            await db.SaveChangesAsync();
        }

        foreach (var (k, v, d) in ParamService.Defaults)
            if (!await db.BankParameters.AnyAsync(p => p.Key == k)) db.BankParameters.Add(new BankParameter { Key = k, Value = v, Description = d });
        await db.SaveChangesAsync();

        if (!await db.Branches.AnyAsync())
        {
            var today = DateTime.Today;
            var ho = new Branch { Code = "001", Name = "Head Office", Layer = "HO", Address = "Dhaka", CostCenter = "CC001", ProfitCenter = "PC001", BusinessDate = today };
            db.Branches.Add(ho); await db.SaveChangesAsync();
            db.Branches.Add(new Branch { Code = "002", Name = "Motijheel Branch", Layer = "Branch", ParentId = ho.Id, Address = "Motijheel, Dhaka", CostCenter = "CC002", ProfitCenter = "PC002", BusinessDate = today });
            await db.SaveChangesAsync();
        }

        if (!await db.GlAccounts.AnyAsync()) await SeedChartOfAccounts(db);
        if (!await db.Products.AnyAsync()) await SeedProducts(db);

        if (!await db.ExciseSlabs.AnyAsync())
        {
            // Illustrative defaults - configure per current NBR rules in Setup -> Excise Duty.
            db.ExciseSlabs.AddRange(
                new ExciseSlab { MinBalance = 100000, MaxBalance = 1000000, Duty = 150 },
                new ExciseSlab { MinBalance = 1000000.01m, MaxBalance = 5000000, Duty = 500 },
                new ExciseSlab { MinBalance = 5000000.01m, MaxBalance = 10000000, Duty = 2500 },
                new ExciseSlab { MinBalance = 10000000.01m, MaxBalance = 0, Duty = 12000 });
            await db.SaveChangesAsync();
        }

        // Default administrator (must change the password at first login).
        var admin = await userMgr.FindByEmailAsync(AdminEmail);
        if (admin == null)
        {
            admin = new IdentityUser { UserName = AdminEmail, Email = AdminEmail, EmailConfirmed = true };
            var res = await userMgr.CreateAsync(admin, AdminDefaultPassword);
            if (res.Succeeded)
            {
                await userMgr.AddToRoleAsync(admin, Roles.Administrator);
                var ho = await db.Branches.FirstAsync(b => b.Code == "001");
                db.UserProfiles.Add(new UserProfile { UserId = admin.Id, FullName = "System Administrator", EmployeeId = "ADMIN", BranchId = ho.Id, Designation = "Administrator", MustChangePassword = true });
                await db.SaveChangesAsync();
            }
        }

        await Demo.DemoDataSeeder.SeedAsync(sp);
    }

    private static async Task SeedChartOfAccounts(ApplicationDbContext db)
    {
        // (code, name, type, parentCode, posting)
        var rows = new (string, string, GlType, string?, bool)[]
        {
            ("1000","ASSETS",GlType.Asset,null,false),
            ("1100","Cash in Hand (Vault/Teller)",GlType.Asset,"1000",true),
            ("1200","Balance with Bangladesh Bank & Other Banks",GlType.Asset,"1000",false),
            ("1210","Clearing / Settlement Account",GlType.Asset,"1200",true),
            ("1220","Nostro Accounts",GlType.Asset,"1200",true),
            ("1300","Loans & Advances",GlType.Asset,"1000",false),
            ("1310","Term Loans",GlType.Asset,"1300",true),
            ("1320","Demand Loans",GlType.Asset,"1300",true),
            ("1400","Inter-Branch Adjustment (Reconciliation)",GlType.Asset,"1000",true),
            ("1500","Fixed Assets",GlType.Asset,"1000",true),
            ("1600","Interest Receivable",GlType.Asset,"1000",true),
            ("1700","Sundry / Suspense Assets",GlType.Asset,"1000",true),
            ("2000","LIABILITIES",GlType.Liability,null,false),
            ("2100","Deposits",GlType.Liability,"2000",false),
            ("2110","Savings Deposits",GlType.Liability,"2100",true),
            ("2120","Current Deposits",GlType.Liability,"2100",true),
            ("2130","Special Notice Deposits (SND)",GlType.Liability,"2100",true),
            ("2140","Fixed Deposit Receipts (FDR)",GlType.Liability,"2100",true),
            ("2150","Deposit Pension Scheme (DPS)",GlType.Liability,"2100",true),
            ("2200","Payment Orders Payable",GlType.Liability,"2000",true),
            ("2210","Demand Drafts Payable",GlType.Liability,"2000",true),
            ("2220","Telegraphic Transfers Payable",GlType.Liability,"2000",true),
            ("2300","Interest Payable on Deposits",GlType.Liability,"2000",true),
            ("2400","Unclaimed / Inoperative Deposits",GlType.Liability,"2000",true),
            ("2600","Taxes & Duties Payable",GlType.Liability,"2000",false),
            ("2610","VAT Payable",GlType.Liability,"2600",true),
            ("2620","Source Tax Payable",GlType.Liability,"2600",true),
            ("2630","Excise Duty Payable",GlType.Liability,"2600",true),
            ("2700","Provision for Loans & Advances",GlType.Liability,"2000",true),
            ("2800","Sundry / Suspense Liabilities",GlType.Liability,"2000",true),
            ("3000","EQUITY",GlType.Equity,null,false),
            ("3100","Paid-up Capital",GlType.Equity,"3000",true),
            ("3200","Retained Earnings",GlType.Equity,"3000",true),
            ("4000","INCOME",GlType.Income,null,false),
            ("4100","Interest Income on Loans & Advances",GlType.Income,"4000",true),
            ("4200","Exchange Gain / Revaluation",GlType.Income,"4000",true),
            ("4400","Fees & Charges Income",GlType.Income,"4000",true),
            ("4500","Commission Income (PO/DD/TT)",GlType.Income,"4000",true),
            ("5000","EXPENSES",GlType.Expense,null,false),
            ("5100","Interest Expense on Deposits",GlType.Expense,"5000",true),
            ("5200","Provision Expense",GlType.Expense,"5000",true),
            ("5300","Administrative Expenses",GlType.Expense,"5000",true),
            ("5400","Depreciation Expense",GlType.Expense,"5000",true),
            ("6000","CONTINGENT / MEMO",GlType.Contingent,null,false),
            ("6100","Letters of Guarantee (Memo)",GlType.Contingent,"6000",true),
        };
        var map = new Dictionary<string, GlAccount>();
        foreach (var (code, name, type, parent, posting) in rows)
        {
            var g = new GlAccount { Code = code, Name = name, Type = type, IsPosting = posting, ParentId = parent == null ? null : map[parent].Id };
            db.GlAccounts.Add(g); await db.SaveChangesAsync(); map[code] = g;
        }
    }

    private static async Task SeedProducts(ApplicationDbContext db)
    {
        int Gl(string c) => db.GlAccounts.Local.FirstOrDefault(g => g.Code == c)?.Id ?? db.GlAccounts.First(g => g.Code == c).Id;
        db.Products.AddRange(
            new Product { Code = "01", Name = "Savings Deposit", Category = ProductCategory.Savings, GlAccountId = Gl("2110"), InterestGlId = Gl("5100"), AccrualGlId = Gl("2300"),
                InterestRate = 4.0m, PayFrequency = PayFrequency.HalfYearly, MinOpeningBalance = 500, MinBalance = 0, OpeningFee = 0, ChequeBookFee = 200, StopPaymentFee = 100, ChequeReturnFee = 500, ClosingFee = 300, IsInterestBearing = true },
            new Product { Code = "02", Name = "Current Deposit", Category = ProductCategory.Current, GlAccountId = Gl("2120"), InterestGlId = Gl("5100"), AccrualGlId = Gl("2300"),
                InterestRate = 0m, OverdraftRate = 12.0m, MinOpeningBalance = 5000, MinBalance = 0, ChequeBookFee = 300, StopPaymentFee = 150, ChequeReturnFee = 750, ClosingFee = 500, IsInterestBearing = false },
            new Product { Code = "03", Name = "Special Notice Deposit (SND)", Category = ProductCategory.SND, GlAccountId = Gl("2130"), InterestGlId = Gl("5100"), AccrualGlId = Gl("2300"),
                InterestRate = 5.5m, PayFrequency = PayFrequency.HalfYearly, MinOpeningBalance = 10000, ChequeBookFee = 300, StopPaymentFee = 150, ChequeReturnFee = 750, ClosingFee = 500, IsInterestBearing = true },
            new Product { Code = "04", Name = "Fixed Deposit Receipt (FDR)", Category = ProductCategory.FDR, GlAccountId = Gl("2140"), InterestGlId = Gl("5100"), AccrualGlId = Gl("2300"),
                InterestRate = 8.0m, PrematureRate = 4.0m, DefaultTermMonths = 12, PayFrequency = PayFrequency.None, MinOpeningBalance = 10000, IsInterestBearing = true },
            new Product { Code = "05", Name = "Deposit Pension Scheme (DPS)", Category = ProductCategory.DPS, GlAccountId = Gl("2150"), InterestGlId = Gl("5100"), AccrualGlId = Gl("2300"),
                InterestRate = 7.5m, PrematureRate = 3.0m, DefaultTermMonths = 60, MinOpeningBalance = 500, IsInterestBearing = true },
            new Product { Code = "61", Name = "Term Loan (EMI)", Category = ProductCategory.TermLoan, GlAccountId = Gl("1310"), InterestRate = 12.0m, ProcessingFeePct = 1.0m, DefaultTermMonths = 36 },
            new Product { Code = "62", Name = "Demand Loan", Category = ProductCategory.DemandLoan, GlAccountId = Gl("1320"), InterestRate = 13.0m, ProcessingFeePct = 0.5m, DefaultTermMonths = 6 });
        await db.SaveChangesAsync();
    }
}
