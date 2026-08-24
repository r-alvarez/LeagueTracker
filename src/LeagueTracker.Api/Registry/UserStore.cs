using LeagueTracker.Api.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeagueTracker.Api.Registry;

// Users are ours; providers only vouch for them. A login is matched by
// (issuer, subject) first, then by verified email - the link that survives a
// provider change - and only then created.
public sealed class UserStore(RegistryDatabase registry, IOptions<AuthOptions> auth, ILogger<UserStore> log)
{
    public User? ById(string? id)
    {
        if (id is not { Length: > 0 }) return null;
        using var db = registry.Open();
        return db.Users.AsNoTracking().Include(u => u.Logins).FirstOrDefault(u => u.Id == id);
    }

    public User? ByEmail(string? email)
    {
        if (Normalize(email) is not { } key) return null;
        using var db = registry.Open();
        return db.Users.AsNoTracking().Include(u => u.Logins).FirstOrDefault(u => u.Email == key);
    }

    public IReadOnlyList<User> All()
    {
        using var db = registry.Open();
        return db.Users.AsNoTracking().Include(u => u.Logins).OrderBy(u => u.Email).ToList();
    }

    // Seeding from configuration (owners, admins): a user row before its
    // person has ever signed in, joined by email on their first login.
    public User EnsureByEmail(string email, bool? admin = null)
    {
        var key = Normalize(email) ?? throw new ArgumentException("email required", nameof(email));
        using var db = registry.Open();
        var user = db.Users.FirstOrDefault(u => u.Email == key);
        if (user is null)
        {
            user = new User { Id = Ids.New(), Email = key, DisplayName = key[..key.IndexOf('@')], CreatedUtc = DateTime.UtcNow, IsAdmin = admin ?? false };
            db.Users.Add(user);
            log.LogInformation("User {Email} created from configuration{Admin}", key, user.IsAdmin ? " (admin)" : "");
        }
        else if (admin is { } a && user.IsAdmin != a)
        {
            user.IsAdmin = a;
        }
        db.SaveChanges();
        return user;
    }

    // An admin adds a person before they have ever signed in: the row is
    // theirs to be assigned things at once; the provider identity and the
    // mail follow. Null when the address is already somebody here.
    public User? Invite(string email, string? displayName, string invitedByUserId)
    {
        var key = Normalize(email) ?? throw new ArgumentException("email required", nameof(email));
        using var db = registry.Open();
        if (db.Users.Any(u => u.Email == key)) return null;
        var user = new User
        {
            Id = Ids.New(),
            Email = key,
            DisplayName = displayName is { Length: > 0 } ? displayName.Trim() : key[..key.IndexOf('@')],
            CreatedUtc = DateTime.UtcNow,
            InvitedUtc = DateTime.UtcNow,
            InvitedByUserId = invitedByUserId,
        };
        db.Users.Add(user);
        db.SaveChanges();
        log.LogInformation("User {Email} invited by {By}", key, invitedByUserId);
        return user;
    }

    public void SetProviderUserId(string userId, string providerUserId)
    {
        using var db = registry.Open();
        db.Users.Where(u => u.Id == userId).ExecuteUpdate(s => s.SetProperty(u => u.ProviderUserId, providerUserId));
    }

    public void MarkInviteSent(string userId)
    {
        using var db = registry.Open();
        db.Users.Where(u => u.Id == userId).ExecuteUpdate(s => s.SetProperty(u => u.InviteSentUtc, DateTime.UtcNow));
    }

    // Only a person who never arrived can be removed here: once they have
    // signed in they own things, and that is a different conversation.
    public bool DeleteInvited(string userId)
    {
        using var db = registry.Open();
        if (db.Users.FirstOrDefault(u => u.Id == userId) is not { IsInvitedPending: true } user) return false;
        db.UserLogins.Where(l => l.UserId == userId).ExecuteDelete();
        db.Users.Remove(user);
        db.SaveChanges();
        log.LogInformation("User {Email}: invite removed", user.Email);
        return true;
    }

    public (User User, bool Created) FromLogin(string issuer, string subject, string? email, bool emailVerified, string? displayName)
    {
        using var db = registry.Open();
        var now = DateTime.UtcNow;
        var login = db.UserLogins.FirstOrDefault(l => l.Issuer == issuer && l.Subject == subject);
        User? user = null;
        if (login is not null)
        {
            user = db.Users.Include(u => u.Logins).First(u => u.Id == login.UserId);
            login.LastUsedUtc = now;
        }
        else if (db.Users.Include(u => u.Logins).FirstOrDefault(u => u.ProviderUserId == subject) is { } invited)
        {
            // The identity we created for an invited person, arriving for the
            // first time - ours by construction, whatever the provider says
            // about the email.
            user = invited;
            db.UserLogins.Add(new UserLogin { UserId = user.Id, Issuer = issuer, Subject = subject, CreatedUtc = now, LastUsedUtc = now });
            log.LogInformation("User {Email}: invited person signed in from {Issuer}", user.Email, issuer);
        }
        else if (emailVerified && Normalize(email) is { } key && db.Users.Include(u => u.Logins).FirstOrDefault(u => u.Email == key) is { } byEmail)
        {
            // A new provider (or a config-seeded user's first login) joins the
            // existing row - never a second user for the same person.
            user = byEmail;
            db.UserLogins.Add(new UserLogin { UserId = user.Id, Issuer = issuer, Subject = subject, CreatedUtc = now, LastUsedUtc = now });
            log.LogInformation("User {Email}: linked login from {Issuer}", user.Email, issuer);
        }
        var created = false;
        if (user is null)
        {
            var key = Normalize(email) ?? $"{subject}@{new Uri(issuer).Host}";
            // The address belongs to a row we may not join (the provider has
            // not verified it): say so instead of tripping the unique index -
            // an admin-created Auth0 user is unverified until they click the
            // mail or the admin flips the flag.
            if (!emailVerified && db.Users.Any(u => u.Email == key))
                throw new UnverifiedEmailException(key);
            if (auth.Value.InviteOnly)
                throw new NotInvitedException(key);
            user = new User
            {
                Id = Ids.New(),
                Email = key,
                DisplayName = displayName is { Length: > 0 } ? displayName : key[..Math.Max(1, key.IndexOf('@'))],
                CreatedUtc = now,
            };
            db.Users.Add(user);
            db.UserLogins.Add(new UserLogin { UserId = user.Id, Issuer = issuer, Subject = subject, CreatedUtc = now, LastUsedUtc = now });
            created = true;
            log.LogInformation("User {Email} created on first login from {Issuer}", user.Email, issuer);
        }
        if (displayName is { Length: > 0 } && user.DisplayName == user.Email[..Math.Max(1, user.Email.IndexOf('@'))]) user.DisplayName = displayName;
        user.LastSeenUtc = now;
        db.SaveChanges();
        return (user, created);
    }

    public bool SetAdmin(string userId, bool admin)
    {
        using var db = registry.Open();
        var user = db.Users.FirstOrDefault(u => u.Id == userId);
        if (user is null) return false;
        user.IsAdmin = admin;
        db.SaveChanges();
        return true;
    }

    public void Touch(string userId)
    {
        using var db = registry.Open();
        db.Users.Where(u => u.Id == userId).ExecuteUpdate(s => s.SetProperty(u => u.LastSeenUtc, DateTime.UtcNow));
    }

    private static string? Normalize(string? email)
    {
        var trimmed = email?.Trim().ToLowerInvariant();
        return trimmed is { Length: > 3 } && trimmed.Contains('@') ? trimmed : null;
    }
}

public sealed class UnverifiedEmailException(string email) : Exception($"The email {email} is registered here but your identity provider has not verified it yet")
{
    public string Email { get; } = email;
}

public sealed class NotInvitedException(string email) : Exception($"{email} has not been invited to this tracker")
{
    public string Email { get; } = email;
}
