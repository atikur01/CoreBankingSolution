using CoreBankingSolution.Models.Entities;
using CoreBankingSolution.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Controllers;

[Authorize(Roles = Roles.Checkers)]
public class EodController : BankController
{
    public async Task<IActionResult> Index(int? branchId)
    {
        var branches = await Db.Branches.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.Code).ToListAsync();
        var eod = Svc<EodService>();
        var checks = new Dictionary<int, List<string>>();
        foreach (var b in branches) checks[b.Id] = await eod.PreChecksAsync(b.Id);
        ViewBag.Checks = checks;
        var logs = Db.EodLogs.AsNoTracking().Include(l => l.Branch).AsQueryable();
        if (branchId != null) logs = logs.Where(l => l.BranchId == branchId);
        ViewBag.Logs = await logs.OrderByDescending(l => l.Id).Take(80).ToListAsync();
        return View(branches);
    }

    [HttpPost]
    public async Task<IActionResult> Run(int branchId)
    {
        try
        {
            var r = await Svc<EodService>().RunAsync(branchId, Me.UserName);
            if (r.Success) Ok($"EOD completed. New business date: {r.NewBusinessDate:dd-MMM-yyyy}.");
            else Err("EOD failed and was rolled back: " + r.Error);
        }
        catch (BankingException e) { Err(e.Message); }
        return RedirectToAction(nameof(Index));
    }
}
