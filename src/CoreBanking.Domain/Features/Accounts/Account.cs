using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CoreBanking.Domain.Features.Accounts;

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
