using CoreBankingSolution.Models.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Controllers;

public class PasswordVm
{
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.DataType(System.ComponentModel.DataAnnotations.DataType.Password)] public string Current { get; set; } = "";
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.DataType(System.ComponentModel.DataAnnotations.DataType.Password)] public string NewPassword { get; set; } = "";
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.DataType(System.ComponentModel.DataAnnotations.DataType.Password), System.ComponentModel.DataAnnotations.Compare("NewPassword")] public string Confirm { get; set; } = "";
}

public class PasswordController(UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn) : BankController
{
    [HttpGet] public IActionResult Change(bool forced = false) { ViewBag.Forced = forced; return View(new PasswordVm()); }

    [HttpPost]
    public async Task<IActionResult> Change(PasswordVm m)
    {
        if (!ModelState.IsValid) return View(m);
        var user = await users.GetUserAsync(User);
        if (user == null) return Challenge();
        var keep = await Svc<Services.ParamService>().IntAsync("PasswordHistoryCount");
        // Reject re-use of the last N passwords (including the current one).
        var hist = await Db.PasswordHistories.Where(h => h.UserId == user.Id).OrderByDescending(h => h.SetOn).Take(keep).ToListAsync();
        var hasher = new PasswordHasher<IdentityUser>();
        if (hist.Any(h => hasher.VerifyHashedPassword(user, h.Hash, m.NewPassword) != PasswordVerificationResult.Failed) ||
            (user.PasswordHash != null && hasher.VerifyHashedPassword(user, user.PasswordHash, m.NewPassword) != PasswordVerificationResult.Failed))
        { ModelState.AddModelError("", $"You cannot re-use any of your last {keep} passwords."); return View(m); }

        var oldHash = user.PasswordHash;
        var res = await users.ChangePasswordAsync(user, m.Current, m.NewPassword);
        if (!res.Succeeded) { foreach (var e in res.Errors) ModelState.AddModelError("", e.Description); return View(m); }
        if (oldHash != null) Db.PasswordHistories.Add(new PasswordHistory { UserId = user.Id, Hash = oldHash });
        var prof = await Db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id);
        if (prof != null) { prof.MustChangePassword = false; prof.PasswordChangedOn = DateTime.Now; }
        await Db.SaveChangesAsync();
        await signIn.RefreshSignInAsync(user);
        Ok("Password changed successfully.");
        return RedirectToAction("Index", "Home");
    }
}
