using CoreBanking.Web.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Web.Features.Setup;

[Authorize(Roles = Roles.Setup)]
public class SetupController(ICurrentUser me, LookupService lookups, SetupService setup) : BankController(me, lookups)
{
    public async Task<IActionResult> Branches() => View(await setup.BranchesAsync());

    [HttpGet]
    public async Task<IActionResult> BranchEdit(int? id)
    {
        ViewBag.Parents = ToSelect(await setup.BranchParentOptionsAsync(id));
        if (id == null) return View(new Branch());
        var branch = await setup.FindBranchAsync(id.Value);
        return branch == null ? NotFound() : View(branch);
    }

    [HttpPost]
    public async Task<IActionResult> BranchEdit(Branch m)
    {
        AddIssues(await setup.ValidateBranchAsync(m));
        if (!ModelState.IsValid)
        {
            ViewBag.Parents = ToSelect(await setup.BranchParentOptionsAsync(m.Id));
            return View(m);
        }
        if (m.Id != 0 && await setup.FindBranchAsync(m.Id) == null) return NotFound();
        await setup.SaveBranchAsync(m);
        Ok("Branch saved.");
        return RedirectToAction(nameof(Branches));
    }

    public async Task<IActionResult> Gl() => View(await setup.ChartOfAccountsAsync());

    [HttpGet]
    public async Task<IActionResult> GlEdit(int? id)
    {
        ViewBag.Parents = ToSelect(await setup.GroupGlOptionsAsync(id));
        if (id == null) return View(new GlAccount());
        var gl = await setup.FindGlAsync(id.Value);
        return gl == null ? NotFound() : View(gl);
    }

    [HttpPost]
    public async Task<IActionResult> GlEdit(GlAccount m)
    {
        ModelState.Remove(nameof(GlAccount.Parent));
        AddIssues(await setup.ValidateGlAsync(m));
        if (!ModelState.IsValid)
        {
            ViewBag.Parents = ToSelect(await setup.GroupGlOptionsAsync(m.Id));
            return View(m);
        }
        if (m.Id != 0 && await setup.FindGlAsync(m.Id) == null) return NotFound();
        await setup.SaveGlAsync(m);
        Ok("GL account saved.");
        return RedirectToAction(nameof(Gl));
    }

    public async Task<IActionResult> Products() => View(await setup.ProductsAsync());

    [HttpGet]
    public async Task<IActionResult> ProductEdit(int? id)
    {
        ViewBag.Gls = ToSelect(await setup.PostingGlOptionsAsync());
        if (id == null) return View(new Product());
        var product = await setup.FindProductAsync(id.Value);
        return product == null ? NotFound() : View(product);
    }

    [HttpPost]
    public async Task<IActionResult> ProductEdit(Product m)
    {
        foreach (var key in new[] { "GlAccount", "InterestGl", "AccrualGl" }) ModelState.Remove(key);
        AddIssues(await setup.ValidateProductAsync(m));
        if (!ModelState.IsValid)
        {
            ViewBag.Gls = ToSelect(await setup.PostingGlOptionsAsync());
            return View(m);
        }
        if (m.Id != 0 && await setup.FindProductAsync(m.Id) == null) return NotFound();
        await setup.SaveProductAsync(m);
        Ok("Product saved.");
        return RedirectToAction(nameof(Products));
    }

    public async Task<IActionResult> ProductRates(int id)
    {
        ViewBag.Product = await setup.FindProductAsync(id);
        return View(await setup.RatesAsync(id));
    }

    [HttpPost]
    public async Task<IActionResult> AddRate(ProductRate m)
    {
        ModelState.Remove("Product");
        await Run(() => setup.AddRateAsync(m, Me.UserName), "Rate submitted - it needs approval by a different user.");
        return RedirectToAction(nameof(ProductRates), new { id = m.ProductId });
    }

    [HttpPost]
    public async Task<IActionResult> DecideRate(int id, bool approve)
    {
        var productId = (await setup.FindRateAsync(id))?.ProductId;
        if (productId == null) return NotFound();
        ProductRate? rate = null;
        if (await Run(async () => rate = await setup.DecideRateAsync(id, approve, Me.UserName))) Ok("Rate " + rate!.Status);
        return RedirectToAction(nameof(ProductRates), new { id = productId });
    }

    public async Task<IActionResult> Currencies()
    {
        var page = await setup.CurrencyPageAsync();
        ViewBag.Rates = page.Rates;
        ViewBag.CurList = new SelectList(page.ForeignCurrencies, "Id", "Code");
        return View(page.Currencies);
    }

    [HttpPost]
    public async Task<IActionResult> AddCurrency(Currency m)
    {
        await Run(() => setup.AddCurrencyAsync(m), "Currency added.");
        return RedirectToAction(nameof(Currencies));
    }

    [HttpPost]
    public async Task<IActionResult> AddFx(ExchangeRate m)
    {
        ModelState.Remove("Currency");
        await Run(() => setup.AddExchangeRateAsync(m), "Spot rate saved.");
        return RedirectToAction(nameof(Currencies));
    }

    public async Task<IActionResult> Holidays() => View(await setup.HolidaysAsync());

    [HttpPost]
    public async Task<IActionResult> AddHoliday(Holiday m)
    {
        await Run(() => setup.AddHolidayAsync(m), "Holiday added.");
        return RedirectToAction(nameof(Holidays));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteHoliday(int id)
    {
        await setup.DeleteHolidayAsync(id);
        return RedirectToAction(nameof(Holidays));
    }

    public async Task<IActionResult> Excise() => View(await setup.ExciseSlabsAsync());

    [HttpPost]
    public async Task<IActionResult> AddExcise(ExciseSlab m)
    {
        await Run(() => setup.AddExciseSlabAsync(m), "Slab added.");
        return RedirectToAction(nameof(Excise));
    }

    [HttpPost]
    public async Task<IActionResult> DeleteExcise(int id)
    {
        await setup.DeleteExciseSlabAsync(id);
        return RedirectToAction(nameof(Excise));
    }

    public async Task<IActionResult> Parameters() => View(await setup.ParametersAsync());

    [HttpPost]
    public async Task<IActionResult> Parameters(Dictionary<string, string> values)
    {
        await Run(() => setup.SaveParametersAsync(values), "Parameters saved.");
        return RedirectToAction(nameof(Parameters));
    }
}
