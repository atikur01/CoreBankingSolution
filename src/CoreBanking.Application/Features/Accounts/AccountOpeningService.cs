namespace CoreBanking.Application.Features.Accounts;

public record NomineeRequest(string? Name, string? Relation, string? Nid, decimal Share);

public class AccountOpeningRequest
{
    public string Cif { get; set; } = "";
    public string? JointCifs { get; set; }
    public int ProductId { get; set; }
    public int BranchId { get; set; }
    public string? Title { get; set; }
    public OperationMode Mode { get; set; } = OperationMode.Single;
    public decimal InitialDeposit { get; set; }
    public string? SourceAccountNo { get; set; }
    public string? LinkedAccountNo { get; set; }
    public int TermMonths { get; set; }
    public decimal InstallmentAmount { get; set; }
    public bool AutoRenew { get; set; }
    public decimal OdLimit { get; set; }
    public decimal SingleDebitLimit { get; set; }
    public decimal DailyDebitLimit { get; set; }
    public decimal MonthlyDebitLimit { get; set; }
    public string? PowerOfAttorney { get; set; }
    public List<NomineeRequest> Nominees { get; set; } = [];
}

public class AccountOpeningService(LookupService lookups, DepositService deposits)
{
    public async Task<Account> OpenAsync(AccountOpeningRequest request, string maker, UserProfile? profile)
    {
        var customer = await lookups.CustomerByCifAsync(request.Cif);
        var input = new OpenAccountInput
        {
            CustomerId = customer.Id, ProductId = request.ProductId, BranchId = request.BranchId, Title = request.Title, Mode = request.Mode,
            InitialDeposit = request.InitialDeposit, PowerOfAttorney = request.PowerOfAttorney, OdLimit = request.OdLimit,
            SingleDebitLimit = request.SingleDebitLimit, DailyDebitLimit = request.DailyDebitLimit, MonthlyDebitLimit = request.MonthlyDebitLimit,
            TermMonths = request.TermMonths, InstallmentAmount = request.InstallmentAmount, AutoRenew = request.AutoRenew
        };
        foreach (var cif in (request.JointCifs ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            input.JointCustomerIds.Add((await lookups.CustomerByCifAsync(cif, $"Joint holder CIF {cif} not found.")).Id);
        input.SourceAccountId = await lookups.OptionalAccountIdAsync(request.SourceAccountNo, "Source account not found.");
        input.LinkedAccountId = await lookups.OptionalAccountIdAsync(request.LinkedAccountNo, "Linked account not found.");
        foreach (var nominee in request.Nominees.Where(n => !string.IsNullOrWhiteSpace(n.Name)))
            input.Nominees.Add(new Nominee { Name = nominee.Name!, Relation = nominee.Relation, Nid = nominee.Nid, SharePercent = nominee.Share });
        return await deposits.OpenAsync(input, maker, profile);
    }
}
