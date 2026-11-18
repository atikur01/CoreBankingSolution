using System.Diagnostics;
using CoreBanking.Web.Common;
using CoreBanking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Web.Features.Home;

public class HomeController(ICurrentUser me, LookupService lookups, DashboardService dashboard) : BankController(me, lookups)
{
    public async Task<IActionResult> Index() => View(await dashboard.GetAsync(await Me.ProfileAsync(), Me.UserName));

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
