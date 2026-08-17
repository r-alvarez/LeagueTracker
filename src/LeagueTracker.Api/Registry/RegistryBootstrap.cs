using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Auth;
using LeagueTracker.Api.Data;
using LeagueTracker.Api.Services;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Registry;

// What the registry learns at boot from outside itself: admins and owners
// named in configuration, and each account's puuid, which the old build kept
// only inside that account's own SQLite (TrackedPlayerService's cache).
public sealed class RegistryBootstrap(AccountRegistry accounts, UserStore users, AccountScopes scopes, AccountInitializer initializer, IOptions<AuthOptions> auth, ILogger<RegistryBootstrap> log)
{
    public void Run()
    {
        foreach (var email in auth.Value.AdminList) users.EnsureByEmail(email, admin: true);

        foreach (var account in accounts.All)
        {
            if (account.Owner is { Length: > 0 } ownerEmail)
            {
                var owner = users.EnsureByEmail(ownerEmail);
                if (account.OwnerUserId != owner.Id)
                {
                    accounts.Update(account, a => a.OwnerUserId = owner.Id);
                    log.LogInformation("Account {RiotId}: owner {Email} (from configuration)", account.RiotId, owner.Email);
                }
            }
            if (account.Puuid is null && initializer.IsReady(account)) HoistPuuid(account);
        }
    }

    private void HoistPuuid(Account account)
    {
        try
        {
            using var scope = scopes.Create(account);
            var db = scope.ServiceProvider.GetRequiredService<LeagueDbContext>();
            var cached = db.KeyValues.Find(TrackedPlayerService.PuuidCacheKey(account.RiotId))
                ?? account.PreviousSlugList.Select(s => db.KeyValues.Find(TrackedPlayerService.PuuidCacheKey(s.Replace('-', '#')))).FirstOrDefault(v => v is not null);
            if (cached is not { Value.Length: > 0 }) return;
            if (accounts.ByPuuid(cached.Value) is { } other && other.Id != account.Id)
            {
                log.LogWarning("Account {RiotId}: its cached puuid already belongs to {Other} - left unresolved", account.RiotId, other.RiotId);
                return;
            }
            accounts.Update(account, a => a.Puuid = cached.Value);
            log.LogInformation("Account {RiotId}: puuid hoisted into the registry", account.RiotId);
        }
        catch (Exception ex)
        {
            log.LogWarning("Account {RiotId}: could not read its cached puuid ({Message}) - the poller resolves it later", account.RiotId, ex.Message);
        }
    }
}
