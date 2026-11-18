namespace CoreBanking.Application.Features.StandingInstructions;

public record StandingInstructionRequest(
    string FromAccountNo, string ToAccountNo, decimal Amount, SiFrequency Frequency, DateTime StartDate, DateTime? EndDate, string? Narration);

public class StandingInstructionService(IApplicationDbContext db, LookupService lookups)
{
    public Task<List<StandingInstruction>> ListAsync() =>
        db.StandingInstructions.AsNoTracking().Include(s => s.FromAccount).Include(s => s.ToAccount)
            .OrderByDescending(s => s.Id).Take(200).ToListAsync();

    public async Task<StandingInstruction> CreateAsync(StandingInstructionRequest request, string maker)
    {
        var from = await lookups.AccountByNumberAsync(request.FromAccountNo, "Debit account not found.");
        var to = await lookups.AccountByNumberAsync(request.ToAccountNo, "Credit account not found.");
        if (from.Id == to.Id) throw new BankingException("Accounts must differ.");
        if (request.Amount <= 0) throw new BankingException("Amount must be positive.");
        var businessDate = (await db.Branches.FindAsync(from.BranchId))!.BusinessDate;
        if (request.StartDate.Date < businessDate) throw new BankingException("Start date cannot be before the business date.");
        if (request.EndDate != null && request.EndDate.Value.Date < request.StartDate.Date)
            throw new BankingException("End date cannot be before the start date.");

        var instruction = new StandingInstruction
        {
            FromAccountId = from.Id, ToAccountId = to.Id, Amount = Money.R(request.Amount), Frequency = request.Frequency,
            StartDate = request.StartDate.Date, NextRunDate = request.StartDate.Date, EndDate = request.EndDate?.Date,
            Narration = request.Narration, MadeBy = maker
        };
        db.StandingInstructions.Add(instruction);
        await db.SaveChangesAsync();
        return instruction;
    }

    public async Task SetStatusAsync(int id, SiStatus status)
    {
        var instruction = await db.StandingInstructions.FindAsync(id);
        var current = instruction?.Status is SiStatus.Active or SiStatus.Paused;
        var target = status is SiStatus.Active or SiStatus.Paused or SiStatus.Cancelled;
        if (instruction == null || !current || !target) return;
        instruction.Status = status;
        await db.SaveChangesAsync();
    }
}
