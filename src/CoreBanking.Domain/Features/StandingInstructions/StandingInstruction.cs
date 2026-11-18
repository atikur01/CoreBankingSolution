using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CoreBanking.Domain.Features.StandingInstructions;

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
