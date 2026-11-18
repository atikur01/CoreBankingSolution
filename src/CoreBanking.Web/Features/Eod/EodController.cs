using CoreBanking.Web.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Web.Features.Eod;

[Authorize(Roles = Roles.Checkers)]
public class EodController(ICurrentUser me, LookupService lookups, EodQueries queries, EodService eod) : BankController(me, lookups)
{
    public async Task<IActionResult> Index(int? branchId)
    {
        var overview = await queries.GetAsync(branchId);
        ViewBag.Checks = overview.Checks;
        ViewBag.Logs = overview.Logs;
        return View(overview.Branches);
    }

    [HttpPost]
    public async Task<IActionResult> Run(int branchId)
    {
        try
        {
            var result = await eod.RunAsync(branchId, Me.UserName);
            if (result.Success) Ok($"EOD completed. New business date: {result.NewBusinessDate:dd-MMM-yyyy}.");
            else Err("EOD failed and was rolled back: " + result.Error);
        }
        catch (BankingException e) { Err(e.Message); }
        return RedirectToAction(nameof(Index));
    }
}
