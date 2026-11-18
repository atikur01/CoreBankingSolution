using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CoreBanking.Domain.Features.Clearing;

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
