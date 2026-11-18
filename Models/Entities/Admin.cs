using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CoreBankingSolution.Models.Entities;

public class UserProfile
{
    public int Id { get; set; }
    [Required] public string UserId { get; set; } = "";
    [Required, StringLength(120), Display(Name = "Full Name")] public string FullName { get; set; } = "";
    [StringLength(30), Display(Name = "Employee ID")] public string? EmployeeId { get; set; }
    public int BranchId { get; set; }
    public Branch? Branch { get; set; }
    [StringLength(60)] public string? Designation { get; set; }
    [StringLength(60), Display(Name = "Prime Menu")] public string? PrimeMenu { get; set; }
    [StringLength(45), Display(Name = "Allowed Terminal IP (blank = any)")] public string? AllowedIp { get; set; }
    [Display(Name = "Operating hours from")] public TimeSpan? WorkFrom { get; set; }
    [Display(Name = "Operating hours to")] public TimeSpan? WorkTo { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Max single credit (maker)")] public decimal MaxCredit { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Max single debit (maker)")] public decimal MaxDebit { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Max inter-branch credit")] public decimal MaxInterBranchCredit { get; set; }
    [Column(TypeName = "decimal(18,2)"), Display(Name = "Max inter-branch debit")] public decimal MaxInterBranchDebit { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; } = true;
    public DateTime PasswordChangedOn { get; set; } = DateTime.Now;
    public string? DelegatedToUserId { get; set; }
    [DataType(DataType.Date)] public DateTime? DelegationUntil { get; set; }
}

public class PasswordHistory
{
    public int Id { get; set; }
    [Required] public string UserId { get; set; } = "";
    [Required] public string Hash { get; set; } = "";
    public DateTime SetOn { get; set; } = DateTime.Now;
}

public class AuditLog
{
    public long Id { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
    [StringLength(256)] public string? UserName { get; set; }
    [StringLength(45)] public string? Ip { get; set; }
    [StringLength(20)] public string Action { get; set; } = "";
    [StringLength(80)] public string Entity { get; set; } = "";
    [StringLength(60)] public string? EntityKey { get; set; }
    public string? Details { get; set; }
}

public class EodLog
{
    public int Id { get; set; }
    public int BranchId { get; set; }
    public Branch? Branch { get; set; }
    [DataType(DataType.Date)] public DateTime BusinessDate { get; set; }
    [StringLength(80)] public string Step { get; set; } = "";
    public EodStatus Status { get; set; }
    [StringLength(500)] public string? Message { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
    public string? RunBy { get; set; }
}
