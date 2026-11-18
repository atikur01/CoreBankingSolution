using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CoreBanking.Domain.Features.Setup;

public class Branch
{
    public int Id { get; set; }
    [Required, StringLength(3, MinimumLength = 3), Display(Name = "Branch Code (3 digits)")]
    [RegularExpression("^[0-9]{3}$", ErrorMessage = "Branch code must be exactly 3 digits")]
    public string Code { get; set; } = "";
    [Required, StringLength(120)] public string Name { get; set; } = "";
    [StringLength(250)] public string? Address { get; set; }
    [StringLength(30)] public string? Phone { get; set; }
    [StringLength(20), Display(Name = "Routing No")] public string? RoutingNumber { get; set; }
    [Display(Name = "Parent (layer)")] public int? ParentId { get; set; }
    public Branch? Parent { get; set; }
    [StringLength(30), Display(Name = "Layer")] public string Layer { get; set; } = "Branch"; // HO/Division/Region/Cluster/Branch/SubBranch/Booth
    [Display(Name = "Cost Center")] public string? CostCenter { get; set; }
    [Display(Name = "Profit Center")] public string? ProfitCenter { get; set; }
    public bool IsActive { get; set; } = true;
    [DataType(DataType.Date), Display(Name = "Business Date")] public DateTime BusinessDate { get; set; } = DateTime.Today;
    public bool DayOpen { get; set; } = true;
    public override string ToString() => $"{Code} - {Name}";
}

public class Currency
{
    public int Id { get; set; }
    [Required, StringLength(3, MinimumLength = 3)] public string Code { get; set; } = "";
    [Required, StringLength(60)] public string Name { get; set; } = "";
    [StringLength(5)] public string? Symbol { get; set; }
    public bool IsBase { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ExchangeRate
{
    public int Id { get; set; }
    public int CurrencyId { get; set; }
    public Currency? Currency { get; set; }
    [DataType(DataType.Date)] public DateTime RateDate { get; set; } = DateTime.Today;
    [Column(TypeName = "decimal(18,6)")] public decimal BuyRate { get; set; }
    [Column(TypeName = "decimal(18,6)")] public decimal SellRate { get; set; }
}

public class Holiday
{
    public int Id { get; set; }
    [DataType(DataType.Date)] public DateTime Date { get; set; }
    [Required, StringLength(120)] public string Description { get; set; } = "";
}

public class BankParameter
{
    [Key, StringLength(60)] public string Key { get; set; } = "";
    [Required, StringLength(250)] public string Value { get; set; } = "";
    [StringLength(250)] public string? Description { get; set; }
}

public class GlAccount
{
    public int Id { get; set; }
    [Required, StringLength(20)] public string Code { get; set; } = "";
    [Required, StringLength(150)] public string Name { get; set; } = "";
    public GlType Type { get; set; }
    public int? ParentId { get; set; }
    public GlAccount? Parent { get; set; }
    public ICollection<GlAccount> Children { get; set; } = new List<GlAccount>();
    [Display(Name = "Posting allowed (leaf)")] public bool IsPosting { get; set; } = true;
    public bool IsActive { get; set; } = true;
    [Display(Name = "Nostro/Vostro/Suspense/Memo note")] public string? Remarks { get; set; }
    public override string ToString() => $"{Code} - {Name}";
}

public class Product
{
    public int Id { get; set; }
    [Required, StringLength(2, MinimumLength = 2), RegularExpression("^[0-9]{2}$", ErrorMessage = "2 digits")]
    [Display(Name = "Product Code (2 digits)")] public string Code { get; set; } = "";
    [Required, StringLength(120)] public string Name { get; set; } = "";
    public ProductCategory Category { get; set; }
    [Display(Name = "Principal GL")] public int GlAccountId { get; set; }
    public GlAccount? GlAccount { get; set; }
    [Display(Name = "Interest Expense/Income GL")] public int? InterestGlId { get; set; }
    public GlAccount? InterestGl { get; set; }
    [Display(Name = "Interest Payable/Receivable GL")] public int? AccrualGlId { get; set; }
    public GlAccount? AccrualGl { get; set; }
    [Column(TypeName = "decimal(9,4)"), Display(Name = "Default Interest Rate % p.a.")] public decimal InterestRate { get; set; }
    [Column(TypeName = "decimal(9,4)"), Display(Name = "Overdraft/Penal Rate % p.a.")] public decimal OverdraftRate { get; set; }
    [Column(TypeName = "decimal(9,4)"), Display(Name = "Premature Encashment Rate %")] public decimal PrematureRate { get; set; }
    [Display(Name = "Interest Payment")] public PayFrequency PayFrequency { get; set; } = PayFrequency.None;
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Min. Opening Balance")] public decimal MinOpeningBalance { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Min. Maintained Balance")] public decimal MinBalance { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Account Opening Fee")] public decimal OpeningFee { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Cheque Book Fee")] public decimal ChequeBookFee { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Stop Payment Fee")] public decimal StopPaymentFee { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Cheque Return (dishonour) Fee")] public decimal ChequeReturnFee { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Account Closing Fee")] public decimal ClosingFee { get; set; }
    [Column(TypeName = "decimal(9,4)"), Display(Name = "Loan Processing Fee %")] public decimal ProcessingFeePct { get; set; }
    [Display(Name = "Default Term (months)")] public int DefaultTermMonths { get; set; }
    public bool IsInterestBearing { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsLoan => Category >= ProductCategory.TermLoan;
    public override string ToString() => $"{Code} - {Name}";
}

public class ProductRate
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    [DataType(DataType.Date), Display(Name = "Effective From (may be back-dated)")] public DateTime EffectiveFrom { get; set; } = DateTime.Today;
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Amount From (slab)")] public decimal MinAmount { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Amount To (0 = no limit)")] public decimal MaxAmount { get; set; }
    [Display(Name = "Term From (months)")] public int MinTermMonths { get; set; }
    [Display(Name = "Term To (0 = no limit)")] public int MaxTermMonths { get; set; }
    [Column(TypeName = "decimal(9,4)")] public decimal Rate { get; set; }
    public ApprovalStatus Status { get; set; }
    public string? MadeBy { get; set; }
    public string? CheckedBy { get; set; }
}

public class ExciseSlab
{
    public int Id { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Balance From")] public decimal MinBalance { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Balance To (0 = no limit)")] public decimal MaxBalance { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Duty Amount")] public decimal Duty { get; set; }
}

public class NumberSequence
{
    [Key, StringLength(60)] public string Key { get; set; } = "";
    public long LastValue { get; set; }
}
