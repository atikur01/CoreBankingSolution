using System.ComponentModel.DataAnnotations;
using CoreBanking.Web.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Web.Features.Password;

public class PasswordVm
{
    [Required, DataType(DataType.Password)] public string Current { get; set; } = "";
    [Required, DataType(DataType.Password)] public string NewPassword { get; set; } = "";
    [Required, DataType(DataType.Password), Compare(nameof(NewPassword))] public string Confirm { get; set; } = "";
}

public class PasswordController(
    ICurrentUser me, LookupService lookups, IPasswordService passwords, UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn)
    : BankController(me, lookups)
{
    [HttpGet]
    public IActionResult Change(bool forced = false)
    {
        ViewBag.Forced = forced;
        return View(new PasswordVm());
    }

    [HttpPost]
    public async Task<IActionResult> Change(PasswordVm m)
    {
        if (!ModelState.IsValid) return View(m);
        var user = await users.GetUserAsync(User);
        if (user == null) return Challenge();

        try
        {
            await passwords.ChangeAsync(user.Id, m.Current, m.NewPassword);
        }
        catch (ValidationFailedException e)
        {
            foreach (var error in e.Errors) ModelState.AddModelError("", error);
            return View(m);
        }
        catch (BankingException e)
        {
            ModelState.AddModelError("", e.Message);
            return View(m);
        }

        await signIn.RefreshSignInAsync(user);
        Ok("Password changed successfully.");
        return RedirectToAction("Index", "Home");
    }
}
