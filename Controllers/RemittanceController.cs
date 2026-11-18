using CoreBankingSolution.Models.Entities;
using CoreBankingSolution.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Controllers;

[Authorize(Roles = Roles.Makers)]
public class RemittanceController : BankController
{
    public async Task<IActionResult> Index(RemittanceType? type, RemittanceStatus? status, string? q)
    {
        var qry = Db.Remittances.AsNoTracking().Include(r => r.Branch).Include(r => r.SourceAccount).AsQueryable();
        if (type != null) qry = qry.Where(r => r.Type == type);
        if (status != null) qry = qry.Where(r => r.Status == status);
        if (!string.IsNullOrWhiteSpace(q)) qry = qry.Where(r => r.InstrumentNo.Contains(q) || r.BeneficiaryName.Contains(q) || r.ApplicantName.Contains(q));
        ViewBag.Q = q;
        return View(await qry.OrderByDescending(r => r.Id).Take(200).ToListAsync());
    }

    [HttpGet] public IActionResult Issue() => View(new RemittanceInput { Type = RemittanceType.PO });

    [HttpPost]
    public async Task<IActionResult> Issue(RemittanceInput m, string? sourceAccountNo)
    {
        Remittance? r = null;
        var ok = await Run(async () =>
        {
            if (!string.IsNullOrWhiteSpace(sourceAccountNo)) m.SourceAccountId = (await Db.Accounts.FirstOrDefaultAsync(a => a.AccountNo == sourceAccountNo.Trim()) ?? throw new BankingException("Source account not found.")).Id;
            r = await Svc<RemittanceService>().IssueAsync(m, Me.UserName, await Me.ProfileAsync());
        });
        if (ok) { Ok($"{r!.Type} {r.InstrumentNo} issued; voucher awaits authorization. Commission incl. VAT: {r.Commission:N2}"); return RedirectToAction(nameof(Index)); }
        ViewBag.Src = sourceAccountNo; return View(m);
    }

    [HttpPost]
    public async Task<IActionResult> Pay(int id, string? accountNo)
    {
        await Run(async () =>
        {
            int? acc = null;
            if (!string.IsNullOrWhiteSpace(accountNo)) acc = (await Db.Accounts.FirstOrDefaultAsync(a => a.AccountNo == accountNo.Trim()) ?? throw new BankingException("Account not found.")).Id;
            await Svc<RemittanceService>().PayAsync(id, acc, Me.UserName, await Me.ProfileAsync());
        }, "Payment voucher created - awaiting authorization.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Cancel(int id) { await Run(async () => await Svc<RemittanceService>().CancelAsync(id, Me.UserName, await Me.ProfileAsync()), "Cancellation voucher created - awaiting authorization."); return RedirectToAction(nameof(Index)); }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Stop(int id)
    {
        var r = await Db.Remittances.FindAsync(id);
        if (r is { Status: RemittanceStatus.Issued }) { r.Status = RemittanceStatus.Stopped; await Db.SaveChangesAsync(); Ok("Payment stopped on instrument " + r.InstrumentNo); }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Release(int id)
    {
        var r = await Db.Remittances.FindAsync(id);
        if (r is { Status: RemittanceStatus.Stopped }) { r.Status = RemittanceStatus.Issued; await Db.SaveChangesAsync(); Ok("Stop released."); }
        return RedirectToAction(nameof(Index));
    }
}
