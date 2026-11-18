namespace CoreBanking.Application.Features.Deposits;

public class OpenAccountInput
{
    public int CustomerId { get; set; }
    public int ProductId { get; set; }
    public int BranchId { get; set; }
    public string? Title { get; set; }
    public OperationMode Mode { get; set; } = OperationMode.Single;
    public decimal InitialDeposit { get; set; }
    public int? SourceAccountId { get; set; }          // FDR: debit this account instead of cash
    public List<int> JointCustomerIds { get; set; } = [];
    public string? PowerOfAttorney { get; set; }
    public decimal OdLimit { get; set; }
    public decimal SingleDebitLimit { get; set; }
    public decimal DailyDebitLimit { get; set; }
    public decimal MonthlyDebitLimit { get; set; }
    public int TermMonths { get; set; }
    public decimal InstallmentAmount { get; set; }
    public int? LinkedAccountId { get; set; }
    public bool AutoRenew { get; set; }
    public List<Nominee> Nominees { get; set; } = [];
}
