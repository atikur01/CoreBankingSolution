using System.ComponentModel.DataAnnotations;

namespace CoreBankingSolution.Models.Entities;

public class Customer
{
    public int Id { get; set; }
    [Display(Name = "CIF No")] public string CifNo { get; set; } = "";
    public CustomerType Type { get; set; } = CustomerType.Individual;
    [Required, StringLength(150), Display(Name = "Full Name / Entity Name")] public string Name { get; set; } = "";
    [StringLength(150), Display(Name = "Father's Name")] public string? FatherName { get; set; }
    [StringLength(150), Display(Name = "Mother's Name")] public string? MotherName { get; set; }
    [StringLength(150), Display(Name = "Spouse's Name")] public string? SpouseName { get; set; }
    [DataType(DataType.Date), Display(Name = "Date of Birth / Incorporation")] public DateTime? DateOfBirth { get; set; }
    [StringLength(10)] public string? Gender { get; set; }
    [StringLength(20), Display(Name = "NID No")] public string? Nid { get; set; }
    [StringLength(20), Display(Name = "Passport No")] public string? Passport { get; set; }
    [StringLength(20), Display(Name = "TIN / e-TIN")] public string? Tin { get; set; }
    [StringLength(30), Display(Name = "Trade License / Reg. No")] public string? TradeLicense { get; set; }
    [Required, StringLength(20), Display(Name = "Mobile")] public string Mobile { get; set; } = "";
    [EmailAddress, StringLength(120)] public string? Email { get; set; }
    [Required, StringLength(300), Display(Name = "Present Address")] public string Address { get; set; } = "";
    [StringLength(300), Display(Name = "Permanent Address")] public string? PermanentAddress { get; set; }
    [StringLength(100)] public string? Profession { get; set; }
    [StringLength(100), Display(Name = "Sector / Sub-sector / Business type")] public string? Sector { get; set; }
    [StringLength(100), Display(Name = "Source of Funds")] public string? SourceOfFunds { get; set; }
    [StringLength(150), Display(Name = "Contact Person (corporate)")] public string? ContactPerson { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "decimal(18,2)"), Display(Name = "Monthly Income / Turnover")] public decimal MonthlyIncome { get; set; }
    [StringLength(150), Display(Name = "Introducer Name")] public string? IntroducerName { get; set; }
    [StringLength(30), Display(Name = "Introducer Account No")] public string? IntroducerAccount { get; set; }
    public RiskRating Risk { get; set; } = RiskRating.Low;
    public KycStatus Kyc { get; set; } = KycStatus.Pending;
    [Display(Name = "Sanction screening result")] public string? ScreeningResult { get; set; }
    public bool IsPep { get; set; }
    public string? PhotoPath { get; set; }
    public string? SignaturePath { get; set; }
    public string? DocumentPath { get; set; }
    public int BranchId { get; set; }
    public Branch? Branch { get; set; }
    public bool IsActive { get; set; } = true;
    public string? MadeBy { get; set; }
    public string? CheckedBy { get; set; }
    public DateTime CreatedOn { get; set; } = DateTime.Now;
    public ICollection<Account> Accounts { get; set; } = new List<Account>();
}

public class Nominee
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    [Required, StringLength(150)] public string Name { get; set; } = "";
    [StringLength(60)] public string? Relation { get; set; }
    [StringLength(20)] public string? Nid { get; set; }
    [DataType(DataType.Date)] public DateTime? DateOfBirth { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "decimal(5,2)"), Display(Name = "Share %")] public decimal SharePercent { get; set; } = 100;
    [StringLength(300)] public string? Address { get; set; }
}

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
