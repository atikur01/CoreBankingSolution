using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoreBanking.Web.Common;

[Authorize]
public abstract class BankController(ICurrentUser me, LookupService lookups) : Controller
{
    protected ICurrentUser Me => me;
    protected LookupService Lookups => lookups;

    protected void Ok(string message) => TempData["ok"] = message;
    protected void Err(string message) => TempData["err"] = message;

    protected async Task<bool> Run(Func<Task> work, string? success = null)
    {
        try
        {
            await work();
            if (success != null) Ok(success);
            return true;
        }
        catch (BankingException e) { Err(e.Message); }
        catch (DbUpdateConcurrencyException) { Err("The record was changed by someone else. Please reload and retry."); }
        catch (DbUpdateException e) { Err("Database error: " + (e.InnerException?.Message ?? e.Message)); }
        return false;
    }

    protected static SelectList ToSelect(IEnumerable<LookupItem> items, object? selected = null) =>
        new(items, nameof(LookupItem.Id), nameof(LookupItem.Text), selected);

    protected async Task<SelectList> BranchList(object? selected = null) => ToSelect(await lookups.BranchesAsync(), selected);

    protected async Task<SelectList> ProductList(bool? loans = null, object? selected = null) =>
        ToSelect((await lookups.ActiveProductsAsync(loans)).Select(p => new LookupItem(p.Id, p.Code + " - " + p.Name)), selected);

    protected Task<int> MyBranchAsync() => me.BranchIdAsync();

    protected void AddIssues(IEnumerable<ValidationIssue> issues)
    {
        foreach (var issue in issues) ModelState.AddModelError(issue.Field, issue.Message);
    }

    protected FileContentResult Csv(string name, IEnumerable<string> header, IEnumerable<IEnumerable<object?>> rows)
    {
        static string Escape(object? value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (text.Length > 0 && "=+-@".Contains(text[0]) && !decimal.TryParse(text, out _)) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", header.Select(Escape)));
        foreach (var row in rows) sb.AppendLine(string.Join(",", row.Select(Escape)));
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", name + ".csv");
    }
}
