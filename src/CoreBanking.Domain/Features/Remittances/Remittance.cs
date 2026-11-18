using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CoreBanking.Domain.Features.Remittances;

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
