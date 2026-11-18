using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CoreBanking.Domain.Features.Loans;

public class Loan
{
    public int Id { get; set; }
    [Display(Name = "Loan Account No")] public string LoanNo { get; set; } = "";
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int BranchId { get; set; }
    public Branch? Branch { get; set; }
    [Display(Name = "Settlement (deposit) Account")] public int SettlementAccountId { get; set; }
    public Account? SettlementAccount { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Loan Amount")] public decimal Principal { get; set; }
    [Column(TypeName = "decimal(9,4)"), Display(Name = "Interest Rate % p.a.")] public decimal InterestRate { get; set; }
    [Display(Name = "Term (months)")] public int TermMonths { get; set; }
    [Required, StringLength(250)] public string Purpose { get; set; } = "";
    [StringLength(500), Display(Name = "Collateral / Security")] public string? Collateral { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Collateral Value")] public decimal CollateralValue { get; set; }
    [StringLength(150)] public string? Guarantor { get; set; }
    public LoanStatus Status { get; set; }
    [DataType(DataType.Date)] public DateTime ApplicationDate { get; set; }
    [DataType(DataType.Date)] public DateTime? DisbursedOn { get; set; }
    [DataType(DataType.Date)] public DateTime? ClosedOn { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal OutstandingPrincipal { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal InterestReceivable { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal TotalInterestPaid { get; set; }
    public int OverdueDays { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal OverdueAmount { get; set; }
    public LoanClass Classification { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal ProvisionHeld { get; set; }
    public string? MadeBy { get; set; }
    public string? ApprovedBy { get; set; }
    public string? RejectReason { get; set; }
    [ConcurrencyCheck] public long Version { get; set; }
    public ICollection<LoanInstallment> Installments { get; set; } = new List<LoanInstallment>();
}

public class LoanInstallment
{
    public int Id { get; set; }
    public int LoanId { get; set; }
    public Loan? Loan { get; set; }
    public int No { get; set; }
    [DataType(DataType.Date)] public DateTime DueDate { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal PrincipalDue { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal InterestDue { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal PrincipalPaid { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal InterestPaid { get; set; }
    public bool InterestRecognized { get; set; }
    public InstallmentStatus Status { get; set; }
    [NotMapped] public decimal Total => PrincipalDue + InterestDue;
    [NotMapped] public decimal Outstanding => PrincipalDue - PrincipalPaid + InterestDue - InterestPaid;
}
