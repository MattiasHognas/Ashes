using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Ashes.Registry.Storage;

/// <summary>
/// EF Core / SQLite implementation of <see cref="IAccountStore"/>. Token secrets are random 256-bit
/// values shown once at mint time; only their SHA-256 hash is persisted, so a database read never
/// discloses a usable token.
/// </summary>
internal sealed class EfAccountStore(RegistryDbContext db) : IAccountStore
{
    public async Task<Account> CreateAccountAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var entity = new AccountEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Accounts.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToAccount(entity);
    }

    public async Task<Account?> GetByNameAsync(string name, CancellationToken ct)
    {
        var e = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name, ct);
        return e is null ? null : ToAccount(e);
    }

    public async Task<(Account Account, string Secret)> CreateTokenAsync(string accountId, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw new InvalidOperationException($"No account with id '{accountId}'.");

        var secret = GenerateSecret();
        db.Tokens.Add(new ApiTokenEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            AccountId = accountId,
            HashedSecret = Hash(secret),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);
        return (ToAccount(account), secret);
    }

    public async Task<Account?> ResolveTokenAsync(string presentedSecret, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(presentedSecret))
        {
            return null;
        }

        var hashed = Hash(presentedSecret);
        var token = await db.Tokens.FirstOrDefaultAsync(t => t.HashedSecret == hashed, ct);
        if (token is null)
        {
            return null;
        }

        token.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == token.AccountId, ct);
        return account is null ? null : ToAccount(account);
    }

    private static Account ToAccount(AccountEntity e) => new(e.Id, e.Name, e.CreatedAt);

    private static string GenerateSecret() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
