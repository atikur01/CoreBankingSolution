using CoreBanking.Web.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Web.Features.Remittances;

[Authorize(Roles = Roles.Makers)]
public class RemittanceController(ICurrentUser me, LookupService lookups, RemittanceQueries queries, RemittanceService remittances)
    : BankController(me, lookups)
{
    public async Task<IActionResult> Index(RemittanceType? type, RemittanceStatus? status, string? q)
    {
        ViewBag.Q = q;
        return View(await queries.SearchAsync(type, status, q));
    }

    [HttpGet] public IActionResult Issue() => View(new RemittanceInput { Type = RemittanceType.PO });

    [HttpPost]
    public async Task<IActionResult> Issue(RemittanceInput m, string? sourceAccountNo)
    {
        Remittance? issued = null;
        var ok = await Run(async () =>
        {
            m.SourceAccountId = await Lookups.OptionalAccountIdAsync(sourceAccountNo, "Source account not found.");
            issued = await remittances.IssueAsync(m, Me.UserName, await Me.ProfileAsync());
        });
        if (ok)
        {
            Ok($"{issued!.Type} {issued.InstrumentNo} issued; voucher awaits authorization. Commission incl. VAT: {issued.Commission:N2}");
            return RedirectToAction(nameof(Index));
        }
        ViewBag.Src = sourceAccountNo;
        return View(m);
    }

    [HttpPost]
    public Task<IActionResult> Pay(int id, string? accountNo) =>
        ToIndex(async () =>
        {
            var account = await Lookups.OptionalAccountIdAsync(accountNo, "Account not found.");
            await remittances.PayAsync(id, account, Me.UserName, await Me.ProfileAsync());
        }, "Payment voucher created - awaiting authorization.");

    [HttpPost]
    public Task<IActionResult> Cancel(int id) =>
        ToIndex(async () => await remittances.CancelAsync(id, Me.UserName, await Me.ProfileAsync()), "Cancellation voucher created - awaiting authorization.");

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public Task<IActionResult> Stop(int id) => ToIndex(() => remittances.StopAsync(id), "Payment stopped on the instrument.");

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public Task<IActionResult> Release(int id) => ToIndex(() => remittances.ReleaseAsync(id), "Stop released.");

    private async Task<IActionResult> ToIndex(Func<Task> work, string success)
    {
        await Run(work, success);
        return RedirectToAction(nameof(Index));
    }
}
