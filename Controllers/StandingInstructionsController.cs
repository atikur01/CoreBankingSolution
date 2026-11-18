using CoreBankingSolution.Models.Entities;
using CoreBankingSolution.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Controllers;

[Authorize(Roles = Roles.Makers)]
public class StandingInstructionsController : BankController
{
    public async Task<IActionResult> Index(string? from)
    {
        ViewBag.From = from;
        return View(await Db.StandingInstructions.AsNoTracking().Include(s => s.FromAccount).Include(s => s.ToAccount).OrderByDescending(s => s.Id).Take(200).ToListAsync());
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Create(string fromNo, string toNo, decimal amount, SiFrequency frequency, DateTime startDate, DateTime? endDate, string? narration)
    {
        await Run(async () =>
        {
            var f = await Db.Accounts.FirstOrDefaultAsync(a => a.AccountNo == fromNo.Trim()) ?? throw new BankingException("Debit account not found.");
            var t = await Db.Accounts.FirstOrDefaultAsync(a => a.AccountNo == toNo.Trim()) ?? throw new BankingException("Credit account not found.");
            if (f.Id == t.Id) throw new BankingException("Accounts must differ.");
            if (amount <= 0) throw new BankingException("Amount must be positive.");
            var bd = (await Db.Branches.FindAsync(f.BranchId))!.BusinessDate;
            if (startDate.Date < bd) throw new BankingException("Start date cannot be before the business date.");
            Db.StandingInstructions.Add(new StandingInstruction { FromAccountId = f.Id, ToAccountId = t.Id, Amount = amount, Frequency = frequency, StartDate = startDate.Date, NextRunDate = startDate.Date, EndDate = endDate?.Date, Narration = narration, MadeBy = Me.UserName });
            await Db.SaveChangesAsync();
        }, "Standing instruction registered. It runs during EOD on its due date.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> SetStatus(int id, SiStatus status)
    {
        var s = await Db.StandingInstructions.FindAsync(id);
        if (s != null && s.Status is SiStatus.Active or SiStatus.Paused && status is SiStatus.Active or SiStatus.Paused or SiStatus.Cancelled) { s.Status = status; await Db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Index));
    }
}
