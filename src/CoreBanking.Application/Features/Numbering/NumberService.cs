namespace CoreBanking.Application.Features.Numbering;

public class NumberService(INumberSequence sequence)
{
    public Task<long> NextAsync(string key) => sequence.NextAsync(key);

    public async Task<long> ReserveRangeAsync(string key, int count) => await sequence.NextAsync(key, count) - count + 1;

    public async Task<string> NextAccountNoAsync(string branchCode, string productCode)
    {
        var n = await NextAsync($"ACC-{branchCode}{productCode}");
        var p = $"{branchCode}{productCode}{n:0000000}";
        return p + Money.CheckDigit(p);
    }

    public async Task<string> NextCifAsync(string branchCode)
    {
        var n = await NextAsync($"CIF-{branchCode}");
        var p = $"{branchCode}{n:000000}";
        return p + Money.CheckDigit(p);
    }

    public async Task<string> NextVoucherNoAsync(string branchCode, DateTime date)
    {
        var n = await NextAsync($"VCH-{branchCode}-{date:yyyyMMdd}");
        return $"V{branchCode}{date:yyMMdd}{n:00000}";
    }

    public async Task<string> NextInstrumentNoAsync(RemittanceType t, string branchCode)
    {
        var n = await NextAsync($"{t}-{branchCode}");
        return $"{t}{branchCode}{n:000000}";
    }
}
