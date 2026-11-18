using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CoreBanking.Domain.Features.Ledger;

public class Voucher
{
    public int Id { get; set; }
    [StringLength(30)] public string VoucherNo { get; set; } = "";
    public VoucherType Type { get; set; }
    [ConcurrencyCheck] public VoucherStatus Status { get; set; } = VoucherStatus.Pending;
    public int BranchId { get; set; }
    public Branch? Branch { get; set; }
    [DataType(DataType.Date)] public DateTime TxnDate { get; set; }
    [DataType(DataType.Date)] public DateTime ValueDate { get; set; }
    public bool IsBackValue { get; set; }
    public int CurrencyId { get; set; }
    [Column(TypeName = "decimal(18,6)")] public decimal ExchangeRate { get; set; } = 1;
    [Column(TypeName = "decimal(18,2)")] public decimal TotalAmount { get; set; }
    [Required, StringLength(300)] public string Description { get; set; } = "";
    [StringLength(60)] public string? Reference { get; set; }
    [StringLength(30)] public string? ChequeNo { get; set; }
    [StringLength(150)] public string? CounterParty { get; set; }
    [StringLength(30)] public string? BatchNo { get; set; }
    public int RequiredLevel { get; set; } = 1;
    public string MakerId { get; set; } = "";
    public DateTime MadeAt { get; set; } = DateTime.Now;
    [StringLength(60)] public string? Workstation { get; set; }
    public string? CheckerId { get; set; }
    public DateTime? CheckedAt { get; set; }
    public string? RejectReason { get; set; }
    public int? ReversalOfId { get; set; }
    public int? ReversedById { get; set; }
    [StringLength(30)] public string? SourceKind { get; set; }
    public int? SourceId { get; set; }
    public bool IsSystem { get; set; }
    public ICollection<VoucherLine> Lines { get; set; } = new List<VoucherLine>();
}

public class VoucherLine
{
    public int Id { get; set; }
    public int VoucherId { get; set; }
    public Voucher? Voucher { get; set; }
    public int BranchId { get; set; }
    public int GlAccountId { get; set; }
    public GlAccount? GlAccount { get; set; }
    public int? AccountId { get; set; }
    public Account? Account { get; set; }
    public int? LoanId { get; set; }
    [StringLength(20)] public string? LoanComponent { get; set; } // Principal / Interest / Fee
    [Column(TypeName = "decimal(18,2)")] public decimal Debit { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Credit { get; set; }
    [StringLength(300)] public string? Narration { get; set; }
}
