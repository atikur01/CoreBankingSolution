using CoreBanking.Web.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Web.Features.StandingInstructions;

[Authorize(Roles = Roles.Makers)]
public class StandingInstructionsController(ICurrentUser me, LookupService lookups, StandingInstructionService instructions) : BankController(me, lookups)
{
    public async Task<IActionResult> Index(string? from)
    {
        ViewBag.From = from;
        return View(await instructions.ListAsync());
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Create(string fromNo, string toNo, decimal amount, SiFrequency frequency, DateTime startDate, DateTime? endDate, string? narration)
    {
        await Run(() => instructions.CreateAsync(new StandingInstructionRequest(fromNo, toNo, amount, frequency, startDate, endDate, narration), Me.UserName),
            "Standing instruction registered. It runs during EOD on its due date.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> SetStatus(int id, SiStatus status)
    {
        await instructions.SetStatusAsync(id, status);
        return RedirectToAction(nameof(Index));
    }
}
