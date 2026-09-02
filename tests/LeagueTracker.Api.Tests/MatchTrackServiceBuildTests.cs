using LeagueTracker.Api.Data;
using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

public class MatchTrackServiceBuildTests
{
    private static Match Game() => new()
    {
        Id = "EUW1_1",
        DurationSec = 1800,
        Participants =
        [
            new() { ParticipantId = 1, Champion = "Ahri", TeamId = 100, IsMe = true, IsAlly = true, Position = "MIDDLE" },
            new() { ParticipantId = 6, Champion = "Zed", TeamId = 200, IsAlly = false, Position = "MIDDLE" },
        ],
    };

    private static PositionSample At(int t, int pid, int x, int y) => new() { MatchId = "EUW1_1", TimeSec = t, ParticipantId = pid, X = x, Y = y };

    [Fact]
    public void Frames_are_ordered_by_time_and_slotted_by_participant()
    {
        var track = MatchTrackService.Build(Game(),
            [At(120, 6, 9000, 9000), At(60, 1, 7000, 7000), At(60, 6, 8000, 8000), At(120, 1, 7500, 7500)],
            [], []);

        Assert.Equal([60, 120], track.Frames.Select(f => f.T));
        Assert.Equal(10, track.Frames[0].P.Length);
        Assert.Equal([7000, 7000], track.Frames[0].P[0]!);
        Assert.Equal([8000, 8000], track.Frames[0].P[5]!);
        Assert.Null(track.Frames[0].P[2]);
        Assert.Equal(1, track.MyPid);
        Assert.Equal(["Ahri", "Zed"], track.Participants.Select(p => p.Champion));
    }

    [Fact]
    public void A_frame_missing_a_player_leaves_only_that_slot_empty()
    {
        var track = MatchTrackService.Build(Game(), [At(60, 1, 1, 1), At(120, 6, 2, 2)], [], []);

        Assert.Null(track.Frames[0].P[5]);
        Assert.Null(track.Frames[1].P[0]);
        Assert.Equal([2, 2], track.Frames[1].P[5]!);
    }

    [Fact]
    public void Kill_ledgers_parse_in_time_order_and_skip_blanks()
    {
        var kills = new[]
        {
            new KillEvent { MatchId = "EUW1_1", TimeSec = 900, KillerParticipantId = 6, VictimParticipantId = 1, AssistIds = "7, 8", DamagePids = "6,7,,x", X = 5, Y = 6 },
            new KillEvent { MatchId = "EUW1_1", TimeSec = 300, KillerParticipantId = 1, VictimParticipantId = 6, AssistIds = "", DamagePids = "1", X = 3, Y = 4 },
        };

        var track = MatchTrackService.Build(Game(), [At(60, 1, 0, 0)], kills, []);

        Assert.Equal([300, 900], track.Kills.Select(k => k.T));
        Assert.Empty(track.Kills[0].Assists);
        Assert.Equal([7, 8], track.Kills[1].Assists);
        Assert.Equal([6, 7], track.Kills[1].Damage);
        Assert.Equal((5, 6), (track.Kills[1].X, track.Kills[1].Y));
    }

    [Fact]
    public void Objectives_keep_their_position_and_side()
    {
        var objectives = new[]
        {
            new ObjectiveEvent { MatchId = "EUW1_1", TimeSec = 1500, Kind = "BARON", ByMyTeam = false, KillerParticipantId = 7, X = 5007, Y = 10471 },
            new ObjectiveEvent { MatchId = "EUW1_1", TimeSec = 600, Kind = "DRAGON", SubKind = "FIRE", ByMyTeam = true, KillerParticipantId = 2, X = 9866, Y = 4414 },
        };

        var track = MatchTrackService.Build(Game(), [At(60, 1, 0, 0)], [], objectives);

        Assert.Equal(["DRAGON", "BARON"], track.Objectives.Select(o => o.Kind));
        Assert.True(track.Objectives[0].ByMyTeam);
        Assert.Equal("FIRE", track.Objectives[0].SubKind);
        Assert.Equal((5007, 10471), (track.Objectives[1].X, track.Objectives[1].Y));
    }
}
