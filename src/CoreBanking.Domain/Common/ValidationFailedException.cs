namespace CoreBanking.Domain.Common;

public class ValidationFailedException(IReadOnlyList<string> errors) : BankingException(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
