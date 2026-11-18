using System.Globalization;
using System.Text;
using CoreBankingSolution.Data;
using CoreBankingSolution.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Services;

/// <summary>Business-rule violation that is safe to show to the user.</summary>
public class BankingException(string message) : Exception(message);

public static class Roles
{
    public const string Administrator = "Administrator";
    public const string Manager = "Manager";
    public const string Supervisor = "Supervisor";
    public const string Officer = "Officer";
    public const string Teller = "Teller";
    public static readonly string[] All = [Administrator, Manager, Supervisor, Officer, Teller];

    // Policy role lists
    public const string Makers = "Administrator,Manager,Supervisor,Officer,Teller";
    public const string Checkers = "Administrator,Manager,Supervisor";
    public const string Setup = "Administrator,Manager";
    public const string Operations = "Administrator,Manager,Supervisor,Officer";
}

public static class Money
{
    public static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Luhn mod-10 check digit for a numeric payload.</summary>
    public static int CheckDigit(string payload)
    {
        int sum = 0; bool dbl = true;
        for (int i = payload.Length - 1; i >= 0; i--)
        {
            int d = payload[i] - '0';
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d; dbl = !dbl;
        }
        return (10 - sum % 10) % 10;
    }

    public static bool ValidNumber(string number) =>
        number.Length > 1 && number.All(char.IsDigit) && CheckDigit(number[..^1]) == number[^1] - '0';

    public static string Words(decimal amount)
    {
        // Simple Taka formatting for printed vouchers (lakh/crore grouping)
        return amount.ToString("N2", CultureInfo.InvariantCulture);
    }

    public static string Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>Bank-wide configurable parameters (Application Administration -> Bank Parameters).</summary>
public class ParamService(ApplicationDbContext db)
{
    private Dictionary<string, string>? _cache;

    public static readonly (string Key, string Value, string Desc)[] Defaults =
    [
        ("BankName", "Core Bank PLC", "Bank name shown on reports"),
        ("BaseCurrency", "BDT", "Base / local currency code"),
        ("WeeklyHolidays", "Friday,Saturday", "Comma separated weekly holidays"),
        ("SourceTaxRate", "10", "Source tax (withholding) % on deposit interest"),
        ("VatRate", "15", "VAT % on fees and charges"),
        ("Level2Amount", "500000", "Vouchers at/above this amount (or back-valued) need Manager level authorization"),
        ("BackValueMaxDays", "5", "Maximum days a back-value entry may be dated in the past"),
        ("DormantAfterDays", "365", "Days without customer transaction before an account is flagged dormant"),
        ("InoperativeAfterDays", "1095", "Days without customer transaction before an account is flagged inoperative/unclaimed"),
        ("CtrThreshold", "1000000", "Cash Transaction Report (CTR) threshold in base currency"),
        ("InterestDayBasis", "365", "Day-count basis for interest calculation"),
        ("InterBranchGl", "1400", "GL code used for inter-branch reconciliation"),
        ("CashGl", "1100", "GL code for cash in hand (vault)"),
        ("ClearingGl", "1210", "GL code for clearing / settlement account"),
        ("FeeIncomeGl", "4400", "GL code for fees & charges income"),
        ("VatPayableGl", "2610", "GL code for VAT payable"),
        ("TaxPayableGl", "2620", "GL code for source tax payable"),
        ("ExciseDutyGl", "2630", "GL code for excise duty payable"),
        ("CommissionIncomeGl", "4500", "GL code for PO/DD/TT commission income"),
        ("ProvisionExpenseGl", "5200", "GL code for loan provision expense"),
        ("ProvisionGl", "2700", "GL code for loan loss provision"),
        ("LoanInterestIncomeGl", "4100", "GL code for loan interest income"),
        ("LoanInterestReceivableGl", "1600", "GL code for loan interest receivable"),
        ("PoPayableGl", "2200", "GL code for payment order payable"),
        ("DdPayableGl", "2210", "GL code for demand draft payable"),
        ("TtPayableGl", "2220", "GL code for telegraphic transfer payable"),
        ("FinancialYearStartMonth", "1", "Month number (1-12) on which the financial year starts"),
        ("RetainedEarningsGl", "3200", "GL code that receives the annual profit & loss transfer"),
        ("PoCommissionPct", "0.15", "Payment Order commission %"),
        ("DdCommissionPct", "0.15", "Demand Draft commission %"),
        ("TtCommissionPct", "0.25", "Telegraphic Transfer commission %"),
        ("MinRemittanceCommission", "50", "Minimum commission per remittance instrument"),
        ("PasswordExpiryDays", "30", "Passwords must be changed after this many days"),
        ("PasswordHistoryCount", "4", "Number of previous passwords that cannot be re-used"),
    ];

    private async Task<Dictionary<string, string>> LoadAsync()
    {
        if (_cache != null) return _cache;
        var rows = await db.BankParameters.AsNoTracking().ToListAsync();
        _cache = Defaults.ToDictionary(d => d.Key, d => d.Value);
        foreach (var r in rows) _cache[r.Key] = r.Value;
        return _cache;
    }

    public async Task<string> GetAsync(string key) => (await LoadAsync()).GetValueOrDefault(key) ?? "";
    public async Task<decimal> DecAsync(string key) =>
        decimal.TryParse(await GetAsync(key), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    public async Task<int> IntAsync(string key) => (int)await DecAsync(key);
    public void Reset() => _cache = null;

    public async Task<int> GlIdAsync(string paramKey)
    {
        var code = await GetAsync(paramKey);
        var id = await db.GlAccounts.Where(g => g.Code == code).Select(g => (int?)g.Id).FirstOrDefaultAsync();
        return id ?? throw new BankingException($"GL account '{code}' (parameter {paramKey}) is not configured in the chart of accounts.");
    }

    public async Task<bool> IsHolidayAsync(DateTime d)
    {
        var weekly = (await GetAsync("WeeklyHolidays")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (weekly.Any(w => string.Equals(w, d.DayOfWeek.ToString(), StringComparison.OrdinalIgnoreCase))) return true;
        var day = d.Date;
        return await db.Holidays.AnyAsync(h => h.Date == day);
    }

    public async Task<DateTime> NextWorkingDayAsync(DateTime from)
    {
        var d = from.Date.AddDays(1);
        for (int i = 0; i < 31 && await IsHolidayAsync(d); i++) d = d.AddDays(1);
        return d;
    }
}

/// <summary>Gap-free-ish number sequences with check digits (CIF, account, loan, voucher, instruments).</summary>
public class NumberService(ApplicationDbContext db)
{
    public async Task<long> NextAsync(string key)
    {
        var own = db.Database.CurrentTransaction == null;
        var tx = own ? await db.Database.BeginTransactionAsync() : null;
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO NumberSequences ([Key], LastValue) SELECT {0}, 0 WHERE NOT EXISTS (SELECT 1 FROM NumberSequences WHERE [Key] = {0})", key);
            await db.Database.ExecuteSqlRawAsync("UPDATE NumberSequences SET LastValue = LastValue + 1 WHERE [Key] = {0}", key);
            var v = await db.Database.SqlQueryRaw<long>("SELECT LastValue AS Value FROM NumberSequences WHERE [Key] = {0}", key).FirstAsync();
            if (tx != null) await tx.CommitAsync();
            return v;
        }
        finally { if (tx != null) await tx.DisposeAsync(); }
    }

    /// <summary>13 digits: branch(3) + product(2) + serial(7) + check digit(1).</summary>
    public async Task<string> NextAccountNoAsync(string branchCode, string productCode)
    {
        var n = await NextAsync($"ACC-{branchCode}{productCode}");
        var p = $"{branchCode}{productCode}{n:0000000}";
        return p + Money.CheckDigit(p);
    }

    /// <summary>10 digits: branch(3) + serial(6) + check digit(1).</summary>
    public async Task<string> NextCifAsync(string branchCode)
    {
        var n = await NextAsync($"CIF-{branchCode}");
        var p = $"{branchCode}{n:000000}";
        return p + Money.CheckDigit(p);
    }

    public async Task<string> NextVoucherNoAsync(string branchCode, DateTime date)
    {
        var n = await NextAsync($"VCH-{branchCode}-{date:yyyyMMdd}");
        return $"V{branchCode}{date:yyMMdd}{n:00000}";
    }

    public async Task<string> NextInstrumentNoAsync(RemittanceType t, string branchCode)
    {
        var n = await NextAsync($"{t}-{branchCode}");
        return $"{t}{branchCode}{n:000000}";
    }
}

/// <summary>UNSCR / local sanction list screening for customers, joint holders, nominees, attorneys, beneficiaries.</summary>
public class SanctionService(ApplicationDbContext db)
{
    public record Hit(int EntryId, string Source, string Name, string Reason);

    public async Task<List<Hit>> ScreenAsync(IEnumerable<string?> namesOrIds)
    {
        var inputs = namesOrIds.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).Distinct().ToList();
        if (inputs.Count == 0) return [];
        var list = await db.SanctionEntries.AsNoTracking().Where(e => e.IsActive).ToListAsync();
        var hits = new List<Hit>();
        foreach (var e in list)
        {
            var names = new List<string> { e.Name };
            if (!string.IsNullOrWhiteSpace(e.Aliases)) names.AddRange(e.Aliases.Split(';', ',').Select(a => a.Trim()));
            foreach (var input in inputs)
            {
                if (!string.IsNullOrWhiteSpace(e.IdNumber) && string.Equals(input, e.IdNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                { hits.Add(new Hit(e.Id, e.Source.ToString(), e.Name, $"ID number match: {input}")); continue; }
                var ni = Money.Norm(input);
                if (ni.Length < 3) continue;
                foreach (var n in names)
                {
                    var nn = Money.Norm(n);
                    if (nn.Length < 3) continue;
                    if (ni == nn || TokenContain(ni, nn) || TokenContain(nn, ni))
                    { hits.Add(new Hit(e.Id, e.Source.ToString(), e.Name, $"Name match: '{input}' ~ '{n}'")); break; }
                }
            }
        }
        return hits.DistinctBy(h => (h.EntryId, h.Reason)).ToList();
    }

    // Every token of the shorter (>=2 tokens) name appears in the longer one.
    private static bool TokenContain(string a, string b)
    {
        var ta = a.Split(' ');
        var tb = b.Split(' ').ToHashSet();
        return ta.Length >= 2 && ta.All(tb.Contains);
    }

    public static string Format(List<Hit> hits) =>
        string.Join("; ", hits.Select(h => $"[{h.Source}] {h.Name} ({h.Reason})"));
}
