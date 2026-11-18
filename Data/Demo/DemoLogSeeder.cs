using CoreBankingSolution.Models.Entities;
using CoreBankingSolution.Services;

namespace CoreBankingSolution.Data.Demo;

internal static class DemoLogSeeder
{
    public static void PostAccruals(DemoState s)
    {
        foreach (var a in s.Accounts.Where(x => x.Status is AccountStatus.Active or AccountStatus.Matured && x.Balance > 0))
        {
            var cat = a.Product!.Category;
            var days = Math.Max(0, (s.Today - a.OpenedOn).Days);
            a.AccruedInterest = cat switch
            {
                ProductCategory.FDR => Math.Round(a.Balance * a.InterestRate / 100m / 365m * Math.Min(days, a.TermMonths * 30), 4),
                ProductCategory.DPS => Math.Round(a.Balance * a.InterestRate / 100m / 365m * Math.Min(days, 365) / 2m, 4),
                ProductCategory.Savings or ProductCategory.SND => Math.Round(a.Balance * a.InterestRate / 100m / 365m * Math.Min(days, 30), 4),
                _ => 0
            };
        }
        foreach (var g in s.Accounts.Where(a => a.AccruedInterest > 0).GroupBy(a => a.BranchId))
        {
            var branch = s.Branches.First(b => b.Id == g.Key);
            var total = Money.R(g.Sum(a => a.AccruedInterest));
            if (total <= 0) continue;
            s.Post(VoucherType.Eod, branch, s.LastMonthEnd, "Deposit interest accrual",
                [new(s.Gl["5100"], null, total, 0, "Interest expense"), new(s.Gl["2300"], null, 0, total, "Interest payable")], system: true);
        }
    }

    public static void AddEodLogs(DemoState s)
    {
        var steps = new[] { "Pre-checks", "Deposit interest accrual", "Loan interest recognition", "Loan classification", "Business date advance" };
        foreach (var br in s.Branches)
            for (int back = 3; back >= 1; back--)
            {
                var d = s.WorkDay(s.Today.AddDays(-back));
                for (int i = 0; i < steps.Length; i++)
                    s.Db.EodLogs.Add(new EodLog
                    {
                        Branch = br, BusinessDate = d, Step = steps[i], Status = EodStatus.Success, Message = "Completed", At = d.AddHours(18).AddMinutes(i * 2), RunBy = DemoState.SupervisorUser
                    });
            }
        var last = s.Branches[^1];
        s.Db.EodLogs.Add(new EodLog { Branch = last, BusinessDate = s.WorkDay(s.Today.AddDays(-1)), Step = "Pre-checks", Status = EodStatus.Info, Message = "1 voucher pending authorization (resolved before day close)", At = s.Today.AddDays(-1).AddHours(17), RunBy = DemoState.SupervisorUser });
    }

    public static void AddAuditEntries(DemoState s)
    {
        var users = DemoCatalog.Users.Select(u => u.Email).Prepend("admin@corebank.local").ToArray();
        var ips = new[] { "10.10.4.21", "10.10.4.37", "10.10.2.15", "10.10.7.88" };
        for (int i = 0; i < 24; i++)
        {
            var at = s.Today.AddDays(-s.Rng.Next(0, 14)).AddHours(8 + s.Rng.Next(0, 9)).AddMinutes(s.Rng.Next(0, 60));
            var action = i % 6 == 5 ? "LoginFailed" : i % 3 == 2 ? "Logout" : "Login";
            s.Db.AuditLogs.Add(new AuditLog
            {
                At = at, UserName = users[s.Rng.Next(users.Length)], Ip = ips[s.Rng.Next(ips.Length)], Action = action, Entity = "Session",
                Details = action == "LoginFailed" ? "Invalid password" : null
            });
        }
    }
}
