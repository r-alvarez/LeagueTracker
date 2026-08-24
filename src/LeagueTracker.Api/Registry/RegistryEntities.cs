using System.ComponentModel.DataAnnotations;

namespace LeagueTracker.Api.Registry;

// A person the app knows. Ours: the id is minted here, never a provider's
// subject, so a provider swap (or a second provider) never renames anyone.
public sealed class User
{
    [Key] public string Id { get; set; } = "";
    // Lowercased. Unique. The link that survives a provider change: a login
    // from a new issuer with the same verified email joins this user.
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsAdmin { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    // Set when an admin added this person from the People page (as opposed
    // to configuration or a first login); the row waits for them.
    public DateTime? InvitedUtc { get; set; }
    public string? InvitedByUserId { get; set; }
    public DateTime? InviteSentUtc { get; set; }
    // The identity we created for them at the provider (Auth0's user_id,
    // which is also the id_token subject). Their first sign-in joins on it,
    // so it does not matter whether the provider has verified the email yet.
    public string? ProviderUserId { get; set; }
    public List<UserLogin> Logins { get; set; } = [];

    public bool IsInvitedPending => InvitedUtc is not null && LastSeenUtc is null;
}

// One provider identity of a user (issuer + subject from the id_token). One
// user, many logins - Auth0 today, "Login with Riot" tomorrow.
public sealed class UserLogin
{
    [Key] public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
}

public enum AgentRole { Recorder, Renderer }

// A short-lived, single-use code an owner mints on their Data page; the
// agent presents it at enrolment and its key is born owned by that user.
public sealed class JoinCode
{
    [Key] public string Code { get; set; } = "";
    public string OwnerUserId { get; set; } = "";
    public AgentRole Role { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
    public string? UsedByKeyId { get; set; }
}

public enum ClaimState { Pending, Verified, Expired, Failed }

// "Set your profile icon to N, then press Verify" - Riot's summoner-v4
// answers whether the player at that puuid did, which is the proof.
public sealed class OwnershipClaim
{
    [Key] public string Id { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string UserId { get; set; } = "";
    public int IconId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public int Attempts { get; set; }
    public ClaimState State { get; set; }
}
