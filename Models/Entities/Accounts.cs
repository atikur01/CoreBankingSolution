using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CoreBankingSolution.Models.Entities;

public class Account
{
    public int Id { get; set; }
    [Display(Name = "Account No")] public string AccountNo { get; set; } = "";
    [Required, StringLength(200), Display(Name = "Account Title")] public string Title { get; set; } = "";
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int BranchId { get; set; }
    public Branch? Branch { get; set; }
    public int CurrencyId { get; set; }
    public Currency? Currency { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Balance { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Lien / Hold Amount")] public decimal LienAmount { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Overdraft Limit")] public decimal OdLimit { get; set; }
    [Column(TypeName = "decimal(18,4)")] public decimal OdInterestAccrued { get; set; }
    [Column(TypeName = "decimal(18,4)")] public decimal AccruedInterest { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.PendingApproval;
    [Display(Name = "Mode of Operation")] public OperationMode Mode { get; set; } = OperationMode.Single;
    [DataType(DataType.Date)] public DateTime OpenedOn { get; set; }
    [DataType(DataType.Date)] public DateTime? ClosedOn { get; set; }
    [DataType(DataType.Date)] public DateTime? LastTxnDate { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Single Debit Limit (0 = none)")] public decimal SingleDebitLimit { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Daily Debit Limit (0 = none)")] public decimal DailyDebitLimit { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Monthly Debit Limit (0 = none)")] public decimal MonthlyDebitLimit { get; set; }
    [StringLength(150), Display(Name = "Power of Attorney Holder")] public string? PowerOfAttorney { get; set; }
    // Term deposits (FDR / DPS)
    [Column(TypeName = "decimal(18,2)")] public decimal PrincipalAmount { get; set; }
    [Column(TypeName = "decimal(9,4)")] public decimal InterestRate { get; set; }
    public int TermMonths { get; set; }
    [DataType(DataType.Date)] public DateTime? MaturityDate { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal InstallmentAmount { get; set; }
    public int InstallmentsPaid { get; set; }
    public bool AutoRenew { get; set; }
    public int? LinkedAccountId { get; set; }
    public string? MadeBy { get; set; }
    public string? CheckedBy { get; set; }
    [ConcurrencyCheck] public long Version { get; set; }
    public ICollection<AccountHolder> Holders { get; set; } = new List<AccountHolder>();
    public ICollection<Nominee> Nominees { get; set; } = new List<Nominee>();
    [NotMapped] public decimal Available => Balance - LienAmount + OdLimit - (Product?.MinBalance ?? 0);
}

public class AccountHolder
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
}

public class AccountTransaction
{
    public long Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public int VoucherId { get; set; }
    public Voucher? Voucher { get; set; }
    [DataType(DataType.Date)] public DateTime TxnDate { get; set; }
    [DataType(DataType.Date)] public DateTime ValueDate { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Debit { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Credit { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal RunningBalance { get; set; }
    [StringLength(300)] public string? Narration { get; set; }
    [StringLength(30)] public string? ChequeNo { get; set; }
    public DateTime PostedAt { get; set; } = DateTime.Now;
}

public class ChequeBook
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public long FromNo { get; set; }
    public long ToNo { get; set; }
    [DataType(DataType.Date)] public DateTime IssuedOn { get; set; }
    public string? IssuedBy { get; set; }
}

public class StopPayment
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    [Required, StringLength(30)] public string ChequeNo { get; set; } = "";
    [Column(TypeName = "decimal(18,2)")] public decimal Amount { get; set; }
    [Required, StringLength(250)] public string Reason { get; set; } = "";
    public DateTime InstructionTime { get; set; } = DateTime.Now;
    public bool IsActive { get; set; } = true;
    public DateTime? ReleasedOn { get; set; }
    public string? MadeBy { get; set; }
}

public class ChequeReturn
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    [Required, StringLength(30)] public string ChequeNo { get; set; } = "";
    [Column(TypeName = "decimal(18,2)")] public decimal Amount { get; set; }
    [Required, StringLength(250)] public string Reason { get; set; } = "";
    [DataType(DataType.Date)] public DateTime ReturnDate { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal ChargeApplied { get; set; }
}

public class StandingInstruction
{
    public int Id { get; set; }
    public int FromAccountId { get; set; }
    public Account? FromAccount { get; set; }
    public int ToAccountId { get; set; }
    public Account? ToAccount { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Amount { get; set; }
    public SiFrequency Frequency { get; set; } = SiFrequency.Monthly;
    [DataType(DataType.Date)] public DateTime StartDate { get; set; }
    [DataType(DataType.Date)] public DateTime NextRunDate { get; set; }
    [DataType(DataType.Date)] public DateTime? EndDate { get; set; }
    public SiStatus Status { get; set; } = SiStatus.Active;
    [StringLength(200)] public string? Narration { get; set; }
    public int ExecutedCount { get; set; }
    public int FailedCount { get; set; }
    public string? LastResult { get; set; }
    public string? MadeBy { get; set; }
}

public class ClearingItem
{
    public int Id { get; set; }
    public ClearingDirection Direction { get; set; }
    [Required, StringLength(30)] public string InstrumentNo { get; set; } = "";
    [StringLength(100)] public string? BankName { get; set; }
    [StringLength(100)] public string? BranchName { get; set; }
    [StringLength(20)] public string? RoutingNo { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Amount { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public int BranchId { get; set; }
    [DataType(DataType.Date)] public DateTime RegisteredOn { get; set; }
    [DataType(DataType.Date)] public DateTime? SettledOn { get; set; }
    public ClearingStatus Status { get; set; }
    [StringLength(250)] public string? ReturnReason { get; set; }
    public int? VoucherId { get; set; }
    public string? MadeBy { get; set; }
}

public class Remittance
{
    public int Id { get; set; }
    public RemittanceType Type { get; set; }
    [Required, StringLength(30)] public string InstrumentNo { get; set; } = "";
    [Column(TypeName = "decimal(18,2)")] public decimal Amount { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Commission { get; set; }
    [Required, StringLength(150)] public string ApplicantName { get; set; } = "";
    [Required, StringLength(150)] public string BeneficiaryName { get; set; } = "";
    [StringLength(100)] public string? BeneficiaryBank { get; set; }
    [StringLength(100)] public string? BeneficiaryBranch { get; set; }
    [StringLength(30)] public string? BeneficiaryAccountNo { get; set; }
    public int? SourceAccountId { get; set; }
    public Account? SourceAccount { get; set; }
    public int BranchId { get; set; }
    public Branch? Branch { get; set; }
    [DataType(DataType.Date)] public DateTime IssueDate { get; set; }
    [DataType(DataType.Date)] public DateTime? PaidDate { get; set; }
    public RemittanceStatus Status { get; set; }
    public int? IssueVoucherId { get; set; }
    public int? PayVoucherId { get; set; }
    public string? MadeBy { get; set; }
}
