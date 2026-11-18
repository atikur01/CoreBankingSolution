using CoreBanking.Web.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Web.Features.Customers;

[Authorize(Roles = Roles.Makers)]
public class CustomersController(ICurrentUser me, LookupService lookups, CustomerService customers) : BankController(me, lookups)
{
    public async Task<IActionResult> Index(string? q, KycStatus? kyc)
    {
        ViewBag.Q = q;
        ViewBag.Kyc = kyc;
        return View(await customers.SearchAsync(q, kyc));
    }

    [HttpGet, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Create()
    {
        var branchId = await MyBranchAsync();
        ViewBag.Branches = await BranchList(branchId);
        return View("Form", new Customer { BranchId = branchId });
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Create(Customer m, IFormFile? photo, IFormFile? signature, IFormFile? document)
    {
        ModelState.Remove("Branch");
        AddIssues(await customers.ValidateAsync(m));
        if (!ModelState.IsValid) return await FormView(m);

        try
        {
            var (created, sanctionHit) = await customers.CreateAsync(m, Upload(photo), Upload(signature), Upload(document), Me.UserName);
            Ok($"Customer created with CIF {created.CifNo}. KYC verification by a checker is required." + (sanctionHit ? " WARNING: sanction screening hit!" : ""));
            return RedirectToAction(nameof(Details), new { id = created.Id });
        }
        catch (BankingException e)
        {
            ModelState.AddModelError("", e.Message);
            return await FormView(m);
        }
    }

    [HttpGet, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Edit(int id)
    {
        var customer = await customers.FindAsync(id);
        if (customer == null) return NotFound();
        ViewBag.Branches = await BranchList(customer.BranchId);
        return View("Form", customer);
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Edit(int id, Customer m, IFormFile? photo, IFormFile? signature, IFormFile? document)
    {
        ModelState.Remove("Branch");
        var existing = await customers.FindAsync(id);
        if (existing == null) return NotFound();
        m.Id = id;
        AddIssues(await customers.ValidateAsync(m, id));
        if (!ModelState.IsValid)
        {
            m.CifNo = existing.CifNo;
            ViewBag.Branches = await BranchList(existing.BranchId);
            return View("Form", m);
        }

        try
        {
            var keyChange = await customers.UpdateAsync(id, m, Upload(photo), Upload(signature), Upload(document), Me.UserName);
            Ok("Customer updated" + (keyChange ? "; KYC re-verification required." : "."));
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (BankingException e)
        {
            Err(e.Message);
            return RedirectToAction(nameof(Edit), new { id });
        }
    }

    public async Task<IActionResult> Details(int id)
    {
        var details = await customers.GetDetailsAsync(id);
        if (details == null) return NotFound();
        ViewBag.Accounts = details.Accounts;
        ViewBag.Loans = details.Loans;
        return View(details.Customer);
    }

    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> Verify(int id, bool approve, string? remarks)
    {
        string? message = null;
        if (await Run(async () => message = await customers.VerifyKycAsync(id, approve, remarks, Me.UserName))) Ok(message!);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Rescreen(int id)
    {
        List<SanctionService.Hit> hits = [];
        if (!await Run(async () => hits = await customers.RescreenAsync(id))) return RedirectToAction(nameof(Details), new { id });
        if (hits.Count > 0) Err("Sanction screening HIT: " + SanctionService.Format(hits)); else Ok("Screening clear.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> File(int id, string kind)
    {
        var file = await customers.GetFileAsync(id, kind);
        return file == null ? NotFound() : PhysicalFile(file.Path, file.ContentType);
    }

    private async Task<IActionResult> FormView(Customer m)
    {
        ViewBag.Branches = await BranchList(m.BranchId);
        return View("Form", m);
    }

    private static UploadedFile? Upload(IFormFile? file) =>
        file == null || file.Length == 0 ? null : new UploadedFile(file.OpenReadStream(), file.FileName, file.Length);
}
