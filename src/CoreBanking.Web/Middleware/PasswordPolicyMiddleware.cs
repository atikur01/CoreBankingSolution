using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace CoreBanking.Web.Middleware;

public class PasswordPolicyMiddleware(RequestDelegate next)
{
    private static readonly string[] ExemptPrefixes = ["/Password", "/Identity/Account/Logout", "/lib", "/css", "/js", "/favicon"];

    public async Task InvokeAsync(HttpContext ctx, UserAccessPolicy policy)
    {
        if (ctx.User.Identity?.IsAuthenticated == true && !IsExempt(ctx.Request.Path.Value ?? ""))
        {
            var userId = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            switch (await policy.EvaluateAsync(userId, ctx.Connection.RemoteIpAddress?.ToString(), DateTime.Now))
            {
                case AccessDecision.SignOut:
                    await ctx.SignOutAsync(IdentityConstants.ApplicationScheme);
                    ctx.Response.Redirect("/Identity/Account/Login?denied=1");
                    return;
                case AccessDecision.ChangePassword:
                    ctx.Response.Redirect("/Password/Change?forced=1");
                    return;
            }
        }
        await next(ctx);
    }

    private static bool IsExempt(string path) => ExemptPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
