using CoreBankingSolution.Models.Entities;
using CoreBankingSolution.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Controllers;

[Authorize(Roles = Roles.Makers)]
public class CustomersController : BankController
{
    private static readonly string[] AllowedExt = [".jpg", ".jpeg", ".png", ".pdf"];

    public async Task<IActionResult> Index(string? q, KycStatus? kyc)
    {
        var qry = Db.Customers.AsNoTracking().Include(c => c.Branch).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) qry = qry.Where(c => c.Name.Contains(q) || c.CifNo.Contains(q) || (c.Nid != null && c.Nid.Contains(q)) || c.Mobile.Contains(q));
        if (kyc != null) qry = qry.Where(c => c.Kyc == kyc);
        ViewBag.Q = q; ViewBag.Kyc = kyc;
        return View(await qry.OrderByDescending(c => c.Id).Take(200).ToListAsync());
    }

    [HttpGet, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Create()
    {
        ViewBag.Branches = await BranchList(await MyBranchAsync());
        return View("Form", new Customer { BranchId = await MyBranchAsync() });
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Create(Customer m, IFormFile? photo, IFormFile? signature, IFormFile? document)
    {
        ModelState.Remove("Branch");
        if (m.Type != CustomerType.Minor && m.Type != CustomerType.Individual && string.IsNullOrWhiteSpace(m.TradeLicense) && m.Type != CustomerType.Joint && m.Type != CustomerType.Staff && m.Type != CustomerType.NonResident)
            ModelState.AddModelError("TradeLicense", "Registration / trade licence number is required for organisations.");
        if (m.Type is CustomerType.Individual or CustomerType.Staff or CustomerType.Minor && string.IsNullOrWhiteSpace(m.Nid) && string.IsNullOrWhiteSpace(m.Passport) && m.Type != CustomerType.Minor)
            ModelState.AddModelError("Nid", "NID or passport number is required.");
        if (!string.IsNullOrWhiteSpace(m.Nid) && await Db.Customers.AnyAsync(c => c.Nid == m.Nid))
            ModelState.AddModelError("Nid", "A customer with this NID already exists (duplicate CIF check).");
        if (!ModelState.IsValid) { ViewBag.Branches = await BranchList(m.BranchId); return View("Form", m); }

        try
        {
            m.PhotoPath = await SaveFile(photo); m.SignaturePath = await SaveFile(signature); m.DocumentPath = await SaveFile(document);
        }
        catch (BankingException e) { ModelState.AddModelError("", e.Message); ViewBag.Branches = await BranchList(m.BranchId); return View("Form", m); }

        var branch = await Db.Branches.FindAsync(m.BranchId);
        m.Id = 0; m.CifNo = await Svc<NumberService>().NextCifAsync(branch!.Code); m.MadeBy = Me.UserName; m.Kyc = KycStatus.Pending; m.CreatedOn = DateTime.Now;
        var hits = await Svc<SanctionService>().ScreenAsync([m.Name, m.Nid, m.Passport, m.FatherName, m.IntroducerName]);
        m.ScreeningResult = hits.Count == 0 ? "Clear (UNSCR + local list)" : "HIT: " + SanctionService.Format(hits);
        if (hits.Count > 0) m.Risk = RiskRating.High;
        Db.Customers.Add(m); await Db.SaveChangesAsync();
        Ok($"Customer created with CIF {m.CifNo}. KYC verification by a checker is required." + (hits.Count > 0 ? " WARNING: sanction screening hit!" : ""));
        return RedirectToAction(nameof(Details), new { id = m.Id });
    }

    [HttpGet, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Edit(int id)
    {
        var c = await Db.Customers.FindAsync(id); if (c == null) return NotFound();
        ViewBag.Branches = await BranchList(c.BranchId); return View("Form", c);
    }

    [HttpPost, Authorize(Roles = Roles.Operations)]
    public async Task<IActionResult> Edit(int id, Customer m, IFormFile? photo, IFormFile? signature, IFormFile? document)
    {
        ModelState.Remove("Branch");
        var c = await Db.Customers.FindAsync(id); if (c == null) return NotFound();
        if (!ModelState.IsValid) { ViewBag.Branches = await BranchList(c.BranchId); m.Id = id; m.CifNo = c.CifNo; return View("Form", m); }
        bool keyChange = c.Name != m.Name || c.Nid != m.Nid || c.Passport != m.Passport || c.DateOfBirth != m.DateOfBirth;
        try
        {
            if (photo?.Length > 0) c.PhotoPath = await SaveFile(photo);
            if (signature?.Length > 0) c.SignaturePath = await SaveFile(signature);
            if (document?.Length > 0) c.DocumentPath = await SaveFile(document);
        }
        catch (BankingException e) { Err(e.Message); return RedirectToAction(nameof(Edit), new { id }); }
        c.Type = m.Type; c.Name = m.Name; c.FatherName = m.FatherName; c.MotherName = m.MotherName; c.SpouseName = m.SpouseName; c.DateOfBirth = m.DateOfBirth; c.Gender = m.Gender;
        c.Nid = m.Nid; c.Passport = m.Passport; c.Tin = m.Tin; c.TradeLicense = m.TradeLicense; c.Mobile = m.Mobile; c.Email = m.Email; c.Address = m.Address;
        c.PermanentAddress = m.PermanentAddress; c.Profession = m.Profession; c.Sector = m.Sector; c.SourceOfFunds = m.SourceOfFunds; c.ContactPerson = m.ContactPerson;
        c.MonthlyIncome = m.MonthlyIncome; c.IntroducerName = m.IntroducerName; c.IntroducerAccount = m.IntroducerAccount; c.Risk = m.Risk; c.IsPep = m.IsPep; c.IsActive = m.IsActive;
        if (keyChange) { c.Kyc = KycStatus.Pending; c.CheckedBy = null; }
        c.MadeBy = Me.UserName;
        await Db.SaveChangesAsync(); Ok("Customer updated" + (keyChange ? "; KYC re-verification required." : "."));
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var c = await Db.Customers.Include(x => x.Branch).FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        ViewBag.Accounts = await Db.Accounts.AsNoTracking().Include(a => a.Product).Where(a => a.CustomerId == id || a.Holders.Any(h => h.CustomerId == id)).OrderBy(a => a.AccountNo).ToListAsync();
        ViewBag.Loans = await Db.Loans.AsNoTracking().Include(l => l.Product).Where(l => l.CustomerId == id).ToListAsync();
        return View(c);
    }

    [HttpPost, Authorize(Roles = Roles.Checkers)]
    public async Task<IActionResult> Verify(int id, bool approve, string? remarks)
    {
        var c = await Db.Customers.FindAsync(id); if (c == null) return NotFound();
        if (string.Equals(c.MadeBy, Me.UserName, StringComparison.OrdinalIgnoreCase)) Err("Maker-checker: you cannot verify the KYC you entered.");
        else if (approve)
        {
            var hits = await Svc<SanctionService>().ScreenAsync([c.Name, c.Nid, c.Passport]);
            if (hits.Count > 0) { c.ScreeningResult = "HIT: " + SanctionService.Format(hits); await Db.SaveChangesAsync(); Err("KYC cannot be approved - sanction screening hit: " + SanctionService.Format(hits)); }
            else if (string.IsNullOrWhiteSpace(c.PhotoPath) && c.Type is CustomerType.Individual or CustomerType.Minor) Err("Customer photograph is required for KYC.");
            else { c.Kyc = KycStatus.Verified; c.CheckedBy = Me.UserName; c.ScreeningResult = "Clear (UNSCR + local list)"; await Db.SaveChangesAsync(); Ok("KYC verified."); }
        }
        else { c.Kyc = KycStatus.Rejected; c.CheckedBy = Me.UserName; await Db.SaveChangesAsync(); Ok("KYC rejected: " + remarks); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Rescreen(int id)
    {
        var c = await Db.Customers.FindAsync(id); if (c == null) return NotFound();
        var hits = await Svc<SanctionService>().ScreenAsync([c.Name, c.Nid, c.Passport, c.FatherName]);
        c.ScreeningResult = hits.Count == 0 ? "Clear (UNSCR + local list)" : "HIT: " + SanctionService.Format(hits);
        if (hits.Count > 0) c.Risk = RiskRating.High;
        await Db.SaveChangesAsync();
        if (hits.Count > 0) Err("Sanction screening HIT: " + SanctionService.Format(hits)); else Ok("Screening clear.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> File(int id, string kind)
    {
        var c = await Db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id); if (c == null) return NotFound();
        var name = kind switch { "photo" => c.PhotoPath, "signature" => c.SignaturePath, "doc" => c.DocumentPath, _ => null };
        if (string.IsNullOrEmpty(name)) return NotFound();
        var path = Path.Combine(UploadDir(), Path.GetFileName(name));
        if (!System.IO.File.Exists(path)) return NotFound();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return PhysicalFile(path, ext == ".pdf" ? "application/pdf" : ext == ".png" ? "image/png" : "image/jpeg");
    }

    private string UploadDir()
    {
        var dir = Path.Combine(HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "App_Data", "uploads");
        Directory.CreateDirectory(dir); return dir;
    }

    private async Task<string?> SaveFile(IFormFile? f)
    {
        if (f == null || f.Length == 0) return null;
        var ext = Path.GetExtension(f.FileName).ToLowerInvariant();
        if (!AllowedExt.Contains(ext)) throw new BankingException("Only JPG, PNG or PDF files are allowed.");
        if (f.Length > 2 * 1024 * 1024) throw new BankingException("File too large (max 2 MB).");
        var name = Guid.NewGuid().ToString("N") + ext;
        await using var fs = System.IO.File.Create(Path.Combine(UploadDir(), name));
        await f.CopyToAsync(fs);
        return name;
    }
}
