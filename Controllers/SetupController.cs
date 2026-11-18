using CoreBankingSolution.Models.Entities;
using CoreBankingSolution.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Controllers;

[Authorize(Roles = Roles.Setup)]
public class SetupController : BankController
{
    // ---------------- Branches
    public async Task<IActionResult> Branches() => View(await Db.Branches.Include(b => b.Parent).AsNoTracking().OrderBy(b => b.Code).ToListAsync());

    [HttpGet] public async Task<IActionResult> BranchEdit(int? id)
    {
        ViewBag.Parents = new SelectList(await Db.Branches.Where(b => b.Id != id).OrderBy(b => b.Code).Select(b => new { b.Id, T = b.Code + " - " + b.Name }).ToListAsync(), "Id", "T");
        return View(id == null ? new Branch() : await Db.Branches.FindAsync(id));
    }

    [HttpPost] public async Task<IActionResult> BranchEdit(Branch m)
    {
        if (await Db.Branches.AnyAsync(b => b.Code == m.Code && b.Id != m.Id)) ModelState.AddModelError("Code", "Branch code already exists.");
        if (!ModelState.IsValid) { ViewBag.Parents = new SelectList(await Db.Branches.Where(b => b.Id != m.Id).Select(b => new { b.Id, T = b.Code + " - " + b.Name }).ToListAsync(), "Id", "T"); return View(m); }
        if (m.Id == 0) { m.BusinessDate = (await Db.Branches.MaxAsync(b => (DateTime?)b.BusinessDate)) ?? DateTime.Today; Db.Branches.Add(m); }
        else
        {
            var e = await Db.Branches.FindAsync(m.Id);
            if (e == null) return NotFound();
            e.Name = m.Name; e.Address = m.Address; e.Phone = m.Phone; e.RoutingNumber = m.RoutingNumber; e.ParentId = m.ParentId; e.Layer = m.Layer;
            e.CostCenter = m.CostCenter; e.ProfitCenter = m.ProfitCenter; e.IsActive = m.IsActive; e.DayOpen = m.DayOpen;
        }
        await Db.SaveChangesAsync(); Ok("Branch saved."); return RedirectToAction(nameof(Branches));
    }

    // ---------------- Chart of accounts (tree)
    public async Task<IActionResult> Gl()
    {
        var all = await Db.GlAccounts.AsNoTracking().OrderBy(g => g.Code).ToListAsync();
        var rows = new List<(GlAccount g, int depth)>();
        void Walk(int? parent, int depth) { foreach (var g in all.Where(x => x.ParentId == parent)) { rows.Add((g, depth)); Walk(g.Id, depth + 1); } }
        Walk(null, 0);
        return View(rows);
    }

    [HttpGet] public async Task<IActionResult> GlEdit(int? id)
    {
        ViewBag.Parents = new SelectList(await Db.GlAccounts.Where(g => g.Id != id && !g.IsPosting).OrderBy(g => g.Code).Select(g => new { g.Id, T = g.Code + " - " + g.Name }).ToListAsync(), "Id", "T");
        return View(id == null ? new GlAccount() : await Db.GlAccounts.FindAsync(id));
    }

    [HttpPost] public async Task<IActionResult> GlEdit(GlAccount m)
    {
        ModelState.Remove(nameof(GlAccount.Parent));
        if (await Db.GlAccounts.AnyAsync(g => g.Code == m.Code && g.Id != m.Id)) ModelState.AddModelError("Code", "GL code already exists.");
        if (m.Id != 0 && m.IsPosting && await Db.GlAccounts.AnyAsync(g => g.ParentId == m.Id)) ModelState.AddModelError("IsPosting", "A GL with children cannot be a posting account.");
        if (m.Id != 0 && !m.IsPosting && await Db.VoucherLines.AnyAsync(l => l.GlAccountId == m.Id)) ModelState.AddModelError("IsPosting", "GL has postings; it cannot become a group.");
        if (!ModelState.IsValid) { ViewBag.Parents = new SelectList(await Db.GlAccounts.Where(g => g.Id != m.Id && !g.IsPosting).Select(g => new { g.Id, T = g.Code + " - " + g.Name }).ToListAsync(), "Id", "T"); return View(m); }
        if (m.Id == 0) Db.GlAccounts.Add(m);
        else
        {
            var e = await Db.GlAccounts.FindAsync(m.Id);
            if (e == null) return NotFound();
            e.Name = m.Name; e.Type = m.Type; e.ParentId = m.ParentId; e.IsPosting = m.IsPosting; e.IsActive = m.IsActive; e.Remarks = m.Remarks;
        }
        await Db.SaveChangesAsync(); Ok("GL account saved."); return RedirectToAction(nameof(Gl));
    }

    // ---------------- Products
    public async Task<IActionResult> Products() => View(await Db.Products.Include(p => p.GlAccount).AsNoTracking().OrderBy(p => p.Code).ToListAsync());

    private async Task GlLists()
    {
        var gl = await Db.GlAccounts.Where(g => g.IsPosting && g.IsActive).OrderBy(g => g.Code).Select(g => new { g.Id, T = g.Code + " - " + g.Name }).ToListAsync();
        ViewBag.Gls = new SelectList(gl, "Id", "T");
    }

    [HttpGet] public async Task<IActionResult> ProductEdit(int? id) { await GlLists(); return View(id == null ? new Product() : await Db.Products.FindAsync(id)); }

    [HttpPost] public async Task<IActionResult> ProductEdit(Product m)
    {
        foreach (var k in new[] { "GlAccount", "InterestGl", "AccrualGl" }) ModelState.Remove(k);
        if (await Db.Products.AnyAsync(p => p.Code == m.Code && p.Id != m.Id)) ModelState.AddModelError("Code", "Product code already exists.");
        if (!ModelState.IsValid) { await GlLists(); return View(m); }
        if (m.Id == 0) Db.Products.Add(m);
        else
        {
            var e = await Db.Products.FindAsync(m.Id);
            if (e == null) return NotFound();
            if (await Db.Accounts.AnyAsync(a => a.ProductId == e.Id) && e.Category != m.Category) { Err("Category cannot change once accounts exist."); await GlLists(); return View(m); }
            Db.Entry(e).CurrentValues.SetValues(m);
        }
        await Db.SaveChangesAsync(); Ok("Product saved."); return RedirectToAction(nameof(Products));
    }

    public async Task<IActionResult> ProductRates(int id)
    {
        ViewBag.Product = await Db.Products.FindAsync(id);
        return View(await Db.ProductRates.AsNoTracking().Where(r => r.ProductId == id).OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.Id).ToListAsync());
    }

    [HttpPost] public async Task<IActionResult> AddRate(ProductRate m)
    {
        ModelState.Remove("Product");
        if (m.Rate < 0 || m.Rate > 100) Err("Rate must be between 0 and 100.");
        else
        {
            m.Id = 0; m.Status = ApprovalStatus.Pending; m.MadeBy = Me.UserName;
            Db.ProductRates.Add(m); await Db.SaveChangesAsync(); Ok("Rate submitted - it needs approval by a different user.");
        }
        return RedirectToAction(nameof(ProductRates), new { id = m.ProductId });
    }

    [HttpPost] public async Task<IActionResult> DecideRate(int id, bool approve)
    {
        var r = await Db.ProductRates.FindAsync(id);
        if (r == null) return NotFound();
        if (r.MadeBy == Me.UserName) Err("Maker-checker: you cannot approve a rate you entered.");
        else if (r.Status != ApprovalStatus.Pending) Err("Rate is not pending.");
        else { r.Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected; r.CheckedBy = Me.UserName; await Db.SaveChangesAsync(); Ok("Rate " + r.Status); }
        return RedirectToAction(nameof(ProductRates), new { id = r.ProductId });
    }

    // ---------------- Currencies & FX
    public async Task<IActionResult> Currencies()
    {
        ViewBag.Rates = await Db.ExchangeRates.Include(r => r.Currency).AsNoTracking().OrderByDescending(r => r.RateDate).ThenBy(r => r.Currency!.Code).Take(60).ToListAsync();
        ViewBag.CurList = new SelectList(await Db.Currencies.Where(c => !c.IsBase).ToListAsync(), "Id", "Code");
        return View(await Db.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync());
    }

    [HttpPost] public async Task<IActionResult> AddCurrency(Currency m)
    {
        m.Code = (m.Code ?? "").ToUpperInvariant();
        if (m.Code.Length != 3 || string.IsNullOrWhiteSpace(m.Name)) Err("Enter a 3-letter code and a name.");
        else if (await Db.Currencies.AnyAsync(c => c.Code == m.Code)) Err("Currency exists.");
        else { m.Id = 0; m.IsBase = false; Db.Currencies.Add(m); await Db.SaveChangesAsync(); Ok("Currency added."); }
        return RedirectToAction(nameof(Currencies));
    }

    [HttpPost] public async Task<IActionResult> AddFx(ExchangeRate m)
    {
        ModelState.Remove("Currency");
        if (m.BuyRate <= 0 || m.SellRate <= 0) Err("Rates must be positive.");
        else { m.Id = 0; Db.ExchangeRates.Add(m); await Db.SaveChangesAsync(); Ok("Spot rate saved."); }
        return RedirectToAction(nameof(Currencies));
    }

    // ---------------- Holidays
    public async Task<IActionResult> Holidays() => View(await Db.Holidays.AsNoTracking().OrderBy(h => h.Date).ToListAsync());

    [HttpPost] public async Task<IActionResult> AddHoliday(Holiday m)
    {
        if (string.IsNullOrWhiteSpace(m.Description)) Err("Description required.");
        else if (await Db.Holidays.AnyAsync(h => h.Date == m.Date.Date)) Err("Holiday already defined.");
        else { m.Id = 0; m.Date = m.Date.Date; Db.Holidays.Add(m); await Db.SaveChangesAsync(); Ok("Holiday added."); }
        return RedirectToAction(nameof(Holidays));
    }

    [HttpPost] public async Task<IActionResult> DeleteHoliday(int id)
    {
        var h = await Db.Holidays.FindAsync(id); if (h != null) { Db.Holidays.Remove(h); await Db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Holidays));
    }

    // ---------------- Excise duty slabs
    public async Task<IActionResult> Excise() => View((await Db.ExciseSlabs.AsNoTracking().ToListAsync()).OrderBy(s => s.MinBalance).ToList());

    [HttpPost] public async Task<IActionResult> AddExcise(ExciseSlab m)
    {
        if (m.Duty <= 0) Err("Duty must be positive."); else { m.Id = 0; Db.ExciseSlabs.Add(m); await Db.SaveChangesAsync(); Ok("Slab added."); }
        return RedirectToAction(nameof(Excise));
    }

    [HttpPost] public async Task<IActionResult> DeleteExcise(int id)
    {
        var s = await Db.ExciseSlabs.FindAsync(id); if (s != null) { Db.ExciseSlabs.Remove(s); await Db.SaveChangesAsync(); }
        return RedirectToAction(nameof(Excise));
    }

    // ---------------- Parameters
    public async Task<IActionResult> Parameters() => View(await Db.BankParameters.AsNoTracking().OrderBy(p => p.Key).ToListAsync());

    [HttpPost] public async Task<IActionResult> Parameters(Dictionary<string, string> values)
    {
        var rows = await Db.BankParameters.ToListAsync();
        foreach (var r in rows) if (values.TryGetValue(r.Key, out var v) && !string.IsNullOrWhiteSpace(v)) r.Value = v.Trim();
        await Db.SaveChangesAsync(); Ok("Parameters saved."); return RedirectToAction(nameof(Parameters));
    }
}
