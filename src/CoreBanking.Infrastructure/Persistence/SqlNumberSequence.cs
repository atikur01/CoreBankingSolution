namespace CoreBanking.Infrastructure.Persistence;

public class SqlNumberSequence(ApplicationDbContext db) : INumberSequence
{
    public async Task<long> NextAsync(string key, int count = 1)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        var own = db.Database.CurrentTransaction == null;
        var tx = own ? await db.Database.BeginTransactionAsync() : null;
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO NumberSequences ([Key], LastValue) SELECT {0}, 0 WHERE NOT EXISTS (SELECT 1 FROM NumberSequences WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = {0})", key);
            await db.Database.ExecuteSqlRawAsync("UPDATE NumberSequences SET LastValue = LastValue + {1} WHERE [Key] = {0}", key, count);
            var value = await db.Database.SqlQueryRaw<long>("SELECT LastValue AS Value FROM NumberSequences WHERE [Key] = {0}", key).FirstAsync();
            if (tx != null) await tx.CommitAsync();
            return value;
        }
        finally
        {
            if (tx != null) await tx.DisposeAsync();
        }
    }
}
