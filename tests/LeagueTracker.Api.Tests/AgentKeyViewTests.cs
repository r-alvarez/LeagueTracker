using LeagueTracker.Api.Auth;
using LeagueTracker.Api.Registry;
using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

// /api/me/agents lists every renderer to every signed-in person; the row
// must not carry the machine's address, owner or logs to a stranger (audit D5).
public class AgentKeyViewTests
{
    private static readonly AgentKeyRecord Renderer = new()
    {
        Id = "render-box", Name = "render-box", Machine = "NAS-PC", Role = AgentRole.Renderer, Status = AgentKeyStatus.Approved,
        OwnerUserId = "owner", LastIp = "203.0.113.5", Note = "in the garage",
    };

    private static readonly AgentLive Live = new("render-box", "render-box", "1.0", "renderer", false, "idle", null, null, true, null, "NAS-PC", "WINDOWS-USER", "owner", false, DateTime.UtcNow, Online: true);

    private static readonly List<AgentLogInfo> Logs = [new("agent-1.log", DateTime.UtcNow, 1024)];

    [Fact]
    public void A_stranger_sees_only_that_the_renderer_exists_and_is_up()
    {
        var view = ManagementEndpoints.VisibleKeyView(Renderer, "stranger", admin: false, "owner@example.com", Live, Logs, accounts: null);

        var shared = Assert.IsType<ManagementEndpoints.SharedKey>(view);
        Assert.Equal(new ManagementEndpoints.SharedKey("render-box", "render-box", "renderer", "approved", Online: true), shared);
        Assert.Equal(["Id", "Name", "Role", "Status", "Online"], shared.GetType().GetProperties().Select(p => p.Name));
    }

    [Fact]
    public void The_owner_and_an_admin_still_get_the_full_row()
    {
        var mine = ManagementEndpoints.VisibleKeyView(Renderer, "owner", admin: false, null, Live, Logs, accounts: null);
        var admins = ManagementEndpoints.VisibleKeyView(Renderer, "someone-else", admin: true, "owner@example.com", Live, Logs, accounts: null);

        Assert.Equal("203.0.113.5", Property(mine, "LastIp"));
        Assert.Equal(true, Property(mine, "Mine"));
        Assert.Equal("203.0.113.5", Property(admins, "LastIp"));
        Assert.Equal("owner@example.com", Property(admins, "OwnerEmail"));
        Assert.Same(Logs, Property(admins, "Logs"));
    }

    [Fact]
    public void An_unbound_key_is_nobodys_but_an_admins()
    {
        var unbound = new AgentKeyRecord { Id = "legacy", Name = "legacy", Role = AgentRole.Renderer, Status = AgentKeyStatus.Approved };

        Assert.IsType<ManagementEndpoints.SharedKey>(ManagementEndpoints.VisibleKeyView(unbound, "stranger", admin: false, null, null, [], accounts: null));
        Assert.IsNotType<ManagementEndpoints.SharedKey>(ManagementEndpoints.VisibleKeyView(unbound, "stranger", admin: true, null, null, [], accounts: null));
    }

    private static object? Property(object view, string name) => view.GetType().GetProperty(name)!.GetValue(view);
}
