using CoreBankingSolution.Models.Entities;
using CoreBankingSolution.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Controllers;

[Authorize(Roles = Roles.Makers)]
public class ClearingController : BankController
{
    public async Task<IActionResult> Index(ClearingDirection? dir, ClearingStatus? status)
    {
        var qry = Db.ClearingItems.AsNoTracking().Include(c => c.Account).AsQueryable();
        if (dir != null) qry = qry.Where(c => c.Direction == dir);
        if (status != null) qry = qry.Where(c => c.Status == status);
        return View(await qry.OrderByDescending(c => c.Id).Take(200).ToListAsync());
    }

    [HttpPost]
    public async Task<IActionResult> Register(ClearingItem m, string accountNo)
    {
        ModelState.Clear();
        await Run(async () =>
        {
            var acc = await Db.Accounts.FirstOrDefaultAsync(a => a.AccountNo == accountNo.Trim()) ?? throw new BankingException("Account not found.");
            m.AccountId = acc.Id;
            await Svc<ClearingService>().RegisterAsync(m, Me.UserName, await Me.ProfileAsync());
        }, "Clearing instrument registered.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Settle(int id) { await Run(async () => await Svc<ClearingService>().SettleAsync(id, Me.UserName, await Me.ProfileAsync()), "Settlement voucher created - awaiting authorization."); return RedirectToAction(nameof(Index)); }

    [HttpPost]
    public async Task<IActionResult> Return(int id, string reason) { await Run(async () => await Svc<ClearingService>().ReturnAsync(id, reason, Me.UserName, await Me.ProfileAsync()), "Instrument returned."); return RedirectToAction(nameof(Index)); }
}
