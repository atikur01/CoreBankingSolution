using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CoreBanking.Domain.Features.Sanctions;

public class SanctionEntry
{
    public int Id { get; set; }
    public SanctionSource Source { get; set; } = SanctionSource.UNSCR;
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [StringLength(200), Display(Name = "Alias / Alternate names")] public string? Aliases { get; set; }
    [StringLength(30), Display(Name = "NID / Passport / ID")] public string? IdNumber { get; set; }
    [DataType(DataType.Date)] public DateTime? DateOfBirth { get; set; }
    [StringLength(300)] public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime AddedOn { get; set; } = DateTime.Now;
}
