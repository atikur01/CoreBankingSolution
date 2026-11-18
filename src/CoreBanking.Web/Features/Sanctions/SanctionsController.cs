using CoreBanking.Web.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Web.Features.Sanctions;

[Authorize(Roles = Roles.Operations)]
public class SanctionsController(ICurrentUser me, LookupService lookups, SanctionListService sanctions) : BankController(me, lookups)
{
    public async Task<IActionResult> Index() => View(await sanctions.ListAsync());

    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> Add(SanctionEntry m)
    {
        await Run(() => sanctions.AddAsync(m), "Entry added to the sanction list.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> Toggle(int id)
    {
        await sanctions.ToggleAsync(id);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Check(string names)
    {
        var hits = await sanctions.CheckAsync(names);
        TempData[hits.Count == 0 ? "ok" : "err"] = hits.Count == 0
            ? "No match found in the UNSCR / local sanction list."
            : "MATCH FOUND: " + SanctionService.Format(hits);
        return RedirectToAction(nameof(Index));
    }
}
