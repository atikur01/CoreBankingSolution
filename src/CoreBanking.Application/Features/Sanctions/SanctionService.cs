namespace CoreBanking.Application.Features.Sanctions;

/// <summary>UNSCR / local sanction list screening for customers, joint holders, nominees, attorneys, beneficiaries.</summary>
public class SanctionService(IApplicationDbContext db)
{
    public record Hit(int EntryId, string Source, string Name, string Reason);

    public async Task<List<Hit>> ScreenAsync(IEnumerable<string?> namesOrIds)
    {
        var inputs = namesOrIds.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).Distinct().ToList();
        if (inputs.Count == 0) return [];
        var list = await db.SanctionEntries.AsNoTracking().Where(e => e.IsActive).ToListAsync();
        var hits = new List<Hit>();
        foreach (var e in list)
        {
            var names = new List<string> { e.Name };
            if (!string.IsNullOrWhiteSpace(e.Aliases)) names.AddRange(e.Aliases.Split(';', ',').Select(a => a.Trim()));
            foreach (var input in inputs)
            {
                if (!string.IsNullOrWhiteSpace(e.IdNumber) && string.Equals(input, e.IdNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                { hits.Add(new Hit(e.Id, e.Source.ToString(), e.Name, $"ID number match: {input}")); continue; }
                var ni = Money.Norm(input);
                if (ni.Length < 3) continue;
                foreach (var n in names)
                {
                    var nn = Money.Norm(n);
                    if (nn.Length < 3) continue;
                    if (ni == nn || TokenContain(ni, nn) || TokenContain(nn, ni))
                    { hits.Add(new Hit(e.Id, e.Source.ToString(), e.Name, $"Name match: '{input}' ~ '{n}'")); break; }
                }
            }
        }
        return hits.DistinctBy(h => (h.EntryId, h.Reason)).ToList();
    }

    // Every token of the shorter (>=2 tokens) name appears in the longer one.
    private static bool TokenContain(string a, string b)
    {
        var ta = a.Split(' ');
        var tb = b.Split(' ').ToHashSet();
        return ta.Length >= 2 && ta.All(tb.Contains);
    }

    public static string Format(List<Hit> hits) =>
        string.Join("; ", hits.Select(h => $"[{h.Source}] {h.Name} ({h.Reason})"));
}
