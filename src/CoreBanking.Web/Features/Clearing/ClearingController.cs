using CoreBanking.Web.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Web.Features.Clearing;

[Authorize(Roles = Roles.Makers)]
public class ClearingController(ICurrentUser me, LookupService lookups, ClearingService clearing) : BankController(me, lookups)
{
    public async Task<IActionResult> Index(ClearingDirection? dir, ClearingStatus? status) => View(await clearing.SearchAsync(dir, status));

    [HttpPost]
    public async Task<IActionResult> Register(ClearingItem m, string accountNo)
    {
        ModelState.Clear();
        await Run(async () =>
        {
            m.AccountId = await Lookups.AccountIdByNumberAsync(accountNo, "Account not found.");
            await clearing.RegisterAsync(m, Me.UserName, await Me.ProfileAsync());
        }, "Clearing instrument registered.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Settle(int id)
    {
        await Run(async () => await clearing.SettleAsync(id, Me.UserName, await Me.ProfileAsync()), "Settlement voucher created - awaiting authorization.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Return(int id, string reason)
    {
        await Run(async () => await clearing.ReturnAsync(id, reason, Me.UserName, await Me.ProfileAsync()), "Instrument returned.");
        return RedirectToAction(nameof(Index));
    }
}
