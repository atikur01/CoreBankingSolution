using System.Linq.Expressions;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace CoreBanking.Web.Helpers;

public static class FormHelpers
{
    private static IHtmlContent Wrap<TM, TP>(IHtmlHelper<TM> h, Expression<Func<TM, TP>> e, int col, IHtmlContent input, string? hint, bool label = true)
    {
        var sb = new HtmlContentBuilder();
        sb.AppendHtml($"<div class=\"col-md-{col}\">");
        if (label) { sb.AppendHtml("<label class=\"form-label\" for=\"" + h.IdFor(e) + "\">"); sb.AppendHtml(h.DisplayNameFor(e)); sb.AppendHtml("</label>"); }
        sb.AppendHtml(input);
        sb.AppendHtml(h.ValidationMessageFor(e, "", new { @class = "text-danger small" }));
        if (hint != null) sb.AppendHtml($"<div class=\"form-text\">{System.Net.WebUtility.HtmlEncode(hint)}</div>");
        sb.AppendHtml("</div>");
        return sb;
    }

    /// <summary>Bootstrap form field chosen from the property type (text, number, date, checkbox, enum select, textarea).</summary>
    public static IHtmlContent Field<TM, TP>(this IHtmlHelper<TM> h, Expression<Func<TM, TP>> e, int col = 4, string? hint = null, bool area = false, bool readOnly = false, string? type = null)
    {
        var t = Nullable.GetUnderlyingType(typeof(TP)) ?? typeof(TP);
        var attrs = new Dictionary<string, object> { ["class"] = "form-control" };
        if (readOnly) attrs["readonly"] = "readonly";
        IHtmlContent input;
        if (t == typeof(bool))
        {
            var sb = new HtmlContentBuilder();
            sb.AppendHtml("<div class=\"form-check mt-4\">");
            sb.AppendHtml(h.CheckBoxFor((Expression<Func<TM, bool>>)(object)e, new { @class = "form-check-input" }));
            sb.AppendHtml(" <label class=\"form-check-label\" for=\"" + h.IdFor(e) + "\">"); sb.AppendHtml(h.DisplayNameFor(e)); sb.AppendHtml("</label></div>");
            return Wrap(h, e, col, sb, hint, label: false);
        }
        if (t.IsEnum) { attrs["class"] = "form-select"; input = h.DropDownListFor(e, h.GetEnumSelectList(t), attrs); }
        else if (t == typeof(DateTime)) { attrs["type"] = "date"; input = h.TextBoxFor(e, "{0:yyyy-MM-dd}", attrs); }
        else if (t == typeof(TimeSpan)) { attrs["type"] = "time"; input = h.TextBoxFor(e, "{0:hh\\:mm}", attrs); }
        else if (t == typeof(decimal)) { attrs["type"] = "number"; attrs["step"] = "0.01"; input = h.TextBoxFor(e, "{0:0.##}", attrs); }
        else if (t == typeof(int) || t == typeof(long)) { attrs["type"] = "number"; input = h.TextBoxFor(e, attrs); }
        else if (area) { attrs["rows"] = 3; input = h.TextAreaFor(e, attrs); }
        else { if (type != null) attrs["type"] = type; input = h.TextBoxFor(e, attrs); }
        return Wrap(h, e, col, input, hint);
    }

    public static IHtmlContent Select<TM, TP>(this IHtmlHelper<TM> h, Expression<Func<TM, TP>> e, IEnumerable<SelectListItem> items, int col = 4, string? optionLabel = null, string? hint = null, object? attrs = null)
    {
        var dict = new RouteValueDictionary(attrs) { ["class"] = "form-select" };
        return Wrap(h, e, col, h.DropDownListFor(e, items, optionLabel, dict), hint);
    }

    public static string Badge(object? status)
    {
        var s = status?.ToString() ?? "";
        var cls = s switch
        {
            "Active" or "Authorized" or "Approved" or "Verified" or "Paid" or "Settled" or "Disbursed" or "Success" or "Standard" => "success",
            "Pending" or "PendingApproval" or "Applied" or "Registered" or "Issued" or "Started" or "Matured" or "SMA" => "warning text-dark",
            "Rejected" or "Closed" or "Cancelled" or "Returned" or "Failed" or "Blocked" or "BadLoss" or "Stopped" => "danger",
            "Reversed" or "Dormant" or "Inoperative" or "Frozen" or "SubStandard" or "Doubtful" or "Paused" => "secondary",
            _ => "info text-dark"
        };
        return $"<span class=\"badge bg-{cls} badge-status\">{System.Net.WebUtility.HtmlEncode(s)}</span>";
    }
}
