using CoreBankingSolution.Models.Entities;
using CoreBankingSolution.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Controllers;

[Authorize(Roles = Roles.Operations)]
public class SanctionsController : BankController
{
    public async Task<IActionResult> Index() => View(await Db.SanctionEntries.AsNoTracking().OrderByDescending(s => s.Id).Take(500).ToListAsync());

    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> Add(SanctionEntry m)
    {
        if (string.IsNullOrWhiteSpace(m.Name)) Err("Name is required.");
        else { m.Id = 0; m.AddedOn = DateTime.Now; Db.SanctionEntries.Add(m); await Db.SaveChangesAsync(); Ok("Entry added to the sanction list."); }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> Toggle(int id)
    {
        var s = await Db.SanctionEntries.FindAsync(id); if (s != null) { s.IsActive = !s.IsActive; await Db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Check(string names)
    {
        var inputs = (names ?? "").Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hits = await Svc<SanctionService>().ScreenAsync(inputs);
        TempData[hits.Count == 0 ? "ok" : "err"] = hits.Count == 0 ? "No match found in the UNSCR / local sanction list." : "MATCH FOUND: " + SanctionService.Format(hits);
        return RedirectToAction(nameof(Index));
    }
}
