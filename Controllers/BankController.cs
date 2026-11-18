using CoreBankingSolution.Data;
using CoreBankingSolution.Models.Entities;
using CoreBankingSolution.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Controllers;

[Authorize]
public abstract class BankController : Controller
{
    protected ApplicationDbContext Db => HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
    protected UserContext Me => HttpContext.RequestServices.GetRequiredService<UserContext>();
    protected T Svc<T>() where T : notnull => HttpContext.RequestServices.GetRequiredService<T>();

    protected void Ok(string m) => TempData["ok"] = m;
    protected void Err(string m) => TempData["err"] = m;

    /// <summary>Runs a business operation, converting rule violations into a visible message.</summary>
    protected async Task<bool> Run(Func<Task> work, string? success = null)
    {
        try { await work(); if (success != null) Ok(success); return true; }
        catch (BankingException e) { Err(e.Message); }
        catch (DbUpdateConcurrencyException) { Err("The record was changed by someone else. Please reload and retry."); }
        catch (DbUpdateException e) { Err("Database error: " + (e.InnerException?.Message ?? e.Message)); }
        return false;
    }

    protected async Task<SelectList> BranchList(object? selected = null) =>
        new(await Db.Branches.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.Code).Select(b => new { b.Id, Text = b.Code + " - " + b.Name }).ToListAsync(), "Id", "Text", selected);

    protected async Task<SelectList> ProductList(Func<Product, bool>? filter = null, object? selected = null)
    {
        var l = (await Db.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Code).ToListAsync()).Where(p => filter?.Invoke(p) ?? true);
        return new SelectList(l.Select(p => new { p.Id, Text = p.Code + " - " + p.Name }), "Id", "Text", selected);
    }

    protected async Task<int> MyBranchAsync() => await Me.BranchIdAsync();

    protected FileContentResult Csv(string name, IEnumerable<string> header, IEnumerable<IEnumerable<object?>> rows)
    {
        static string Esc(object? v)
        {
            var s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (s.Length > 0 && "=+-@".Contains(s[0]) && !decimal.TryParse(s, out _)) s = "'" + s;   // CSV-injection guard
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(",", header.Select(Esc)));
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(Esc)));
        return File(new System.Text.UTF8Encoding(true).GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", name + ".csv");
    }
}
