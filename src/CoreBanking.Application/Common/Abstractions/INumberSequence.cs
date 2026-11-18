namespace CoreBanking.Application.Common.Abstractions;

public interface INumberSequence
{
    Task<long> NextAsync(string key, int count = 1);
}
