using LeagueTracker.Api.Accounts;
using LeagueTracker.Api.Riot;
using LeagueTracker.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace LeagueTracker.Api.Registry;

// Proof of ownership without RSO: the server names a profile icon, the
// player sets it in the League client, summoner-v4 says whether the account
// at that puuid did. The same test op.gg and u.gg run; it works on the
// personal key. Riot's third-party verification code no longer exists.
public sealed class ClaimService(RegistryDatabase registry, AccountRegistry accounts, AccountScopes scopes, ILogger<ClaimService> log)
{
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(30);
    private const int MaxAttempts = 3;
    // The starter icons every account owns from level 1 (0-28); nothing the
    // player would have to buy or unlock to complete the proof.
    private const int StarterIcons = 29;

    public sealed record ClaimView(string Id, string AccountId, string RiotId, int IconId, DateTime ExpiresUtc, int AttemptsLeft, string State);

    public IReadOnlyList<ClaimView> Mine(string userId)
    {
        using var db = registry.Open();
        var now = DateTime.UtcNow;
        return db.OwnershipClaims.AsNoTracking()
            .Where(c => c.UserId == userId && c.State == ClaimState.Pending && c.ExpiresUtc > now)
            .OrderByDescending(c => c.CreatedUtc).ToList()
            .Select(c => View(c, accounts.ById(c.AccountId)?.RiotId ?? "?")).ToList();
    }

    public async Task<(ClaimView? Claim, string? Error)> StartAsync(string userId, string accountId, CancellationToken ct)
    {
        if (accounts.ById(accountId) is not { } account) return (null, "no such account");
        if (account.IsOwned) return (null, account.OwnerUserId == userId ? "you already own this account" : "this account already has an owner");

        using var db = registry.Open();
        var now = DateTime.UtcNow;
        var open = db.OwnershipClaims.FirstOrDefault(c => c.UserId == userId && c.AccountId == accountId && c.State == ClaimState.Pending && c.ExpiresUtc > now);
        if (open is not null) return (View(open, account.RiotId), null);

        int currentIcon;
        try
        {
            currentIcon = (await SummonerAsync(account, ct)).ProfileIconId;
        }
        catch (RiotApiException ex)
        {
            return (null, $"Riot answered {ex.StatusCode} while looking the account up{(ex.IsAuthFailure ? " - the API key is invalid or expired" : "")}");
        }
        catch (RiotApiKeyMissingException)
        {
            return (null, "no Riot API key configured - claims cannot be verified right now");
        }
        // Never the icon already on the account - a proof the player did nothing for.
        var iconId = Random.Shared.Next(1, StarterIcons);
        if (iconId == currentIcon) iconId = iconId == StarterIcons - 1 ? 1 : iconId + 1;

        var claim = new OwnershipClaim
        {
            Id = Ids.New(),
            AccountId = accountId,
            UserId = userId,
            IconId = iconId,
            CreatedUtc = now,
            ExpiresUtc = now + Life,
            State = ClaimState.Pending,
        };
        db.OwnershipClaims.Add(claim);
        db.SaveChanges();
        log.LogInformation("Claim {Id}: user {User} on {RiotId}, icon {Icon}", claim.Id, userId, account.RiotId, iconId);
        return (View(claim, account.RiotId), null);
    }

    public async Task<(ClaimView? Claim, bool Verified, string? Error)> VerifyAsync(string userId, string claimId, CancellationToken ct)
    {
        using var db = registry.Open();
        var claim = db.OwnershipClaims.FirstOrDefault(c => c.Id == claimId && c.UserId == userId);
        if (claim is null) return (null, false, "no such claim");
        var account = accounts.ById(claim.AccountId);
        if (account is null) return (null, false, "the account is no longer tracked");
        if (claim.State is not ClaimState.Pending) return (View(claim, account.RiotId), false, $"this claim is {claim.State.ToString().ToLowerInvariant()}");
        if (claim.ExpiresUtc < DateTime.UtcNow)
        {
            claim.State = ClaimState.Expired;
            db.SaveChanges();
            return (View(claim, account.RiotId), false, "this claim expired - start again");
        }
        if (account.IsOwned)
        {
            claim.State = ClaimState.Failed;
            db.SaveChanges();
            return (View(claim, account.RiotId), false, "the account was claimed by someone else meanwhile");
        }

        int icon;
        try
        {
            icon = (await SummonerAsync(account, ct)).ProfileIconId;
        }
        catch (RiotApiException ex)
        {
            return (View(claim, account.RiotId), false, $"Riot answered {ex.StatusCode} - try again in a moment");
        }
        if (icon != claim.IconId)
        {
            claim.Attempts++;
            if (claim.Attempts >= MaxAttempts) claim.State = ClaimState.Failed;
            db.SaveChanges();
            return (View(claim, account.RiotId), false, claim.State is ClaimState.Failed
                ? "that is not the icon we asked for - too many tries, start a new claim"
                : $"Riot still shows icon {icon}, not {claim.IconId} - set it in the client (it can take a minute to propagate) and try again");
        }

        claim.State = ClaimState.Verified;
        foreach (var other in db.OwnershipClaims.Where(c => c.AccountId == account.Id && c.Id != claim.Id && c.State == ClaimState.Pending)) other.State = ClaimState.Failed;
        db.SaveChanges();
        accounts.Update(account, a => a.OwnerUserId = userId);
        log.LogInformation("Claim {Id} verified: {RiotId} is owned by user {User}", claim.Id, account.RiotId, userId);
        return (View(claim, account.RiotId), true, null);
    }

    private async Task<SummonerDto> SummonerAsync(Account account, CancellationToken ct)
    {
        using var scope = scopes.Create(account);
        var puuid = await scope.ServiceProvider.GetRequiredService<TrackedPlayerService>().GetPuuidAsync(ct);
        return await scope.ServiceProvider.GetRequiredService<RiotApiClient>().GetSummonerByPuuidAsync(puuid, ct);
    }

    private static ClaimView View(OwnershipClaim c, string riotId) =>
        new(c.Id, c.AccountId, riotId, c.IconId, c.ExpiresUtc, Math.Max(0, MaxAttempts - c.Attempts), c.State.ToString().ToLowerInvariant());
}
