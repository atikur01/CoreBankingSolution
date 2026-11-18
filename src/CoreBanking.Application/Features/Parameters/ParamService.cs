namespace CoreBanking.Application.Features.Parameters;

/// <summary>Bank-wide configurable parameters (Application Administration -> Bank Parameters).</summary>
public class ParamService(IApplicationDbContext db)
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
