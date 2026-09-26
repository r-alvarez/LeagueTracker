using System.Text.Json;
using LeagueTracker.Telemetry;

namespace LeagueTracker.Api.Tests;

public class InputTelemetryTests
{
    private static InputTelemetry Read(params string[] rows)
    {
        var telemetry = new InputTelemetry();
        foreach (var row in rows)
        {
            var f = row.Split(',', 5);
            telemetry.Add(long.Parse(f[0]), f[1], f[2], f[3]);
        }
        return telemetry;
    }

    private static JsonElement Series(InputTelemetry t) => JsonSerializer.SerializeToElement(t.Series());

    [Fact]
    public void A_held_key_is_one_action_not_its_repeats()
    {
        // Tab held for the scoreboard: one press, then Windows' repeats.
        var rows = new List<string> { "0,key_down,tab,0,0" };
        for (var t = 500; t < 3000; t += 33) rows.Add($"{t},key_down,tab,0,0");
        rows.Add("3000,key_up,tab,0,0");
        rows.Add("4000,key_down,tab,0,0");

        Assert.Equal([2], Read([.. rows]).Buckets);
    }

    [Fact]
    public void A_key_whose_release_was_missed_still_counts_its_next_press()
    {
        // The key_up happened while the game was not foreground.
        Assert.Equal([2], Read("0,key_down,Q,0,0", "5000,key_down,Q,0,0").Buckets);
    }

    [Fact]
    public void Ctrl_R_levels_the_ult_and_is_not_a_cast()
    {
        var t = Read("0,key_down,ctrl,0,0", "100,key_down,R,0,0", "150,key_up,R,0,0", "200,key_up,ctrl,0,0");
        Assert.Empty(t.Ults(out var source));
        Assert.Null(source);
    }

    [Fact]
    public void Presses_close_together_are_one_ult()
    {
        var ults = Read(
            "10000,key_down,R,0,0", "10100,key_up,R,0,0",
            "12000,key_down,R,0,0", "12100,key_up,R,0,0",   // recast
            "60000,key_down,R,0,0", "60100,key_up,R,0,0").Ults(out var source);

        Assert.Equal("keys", source);
        Assert.Equal([10.0, 60.0], ults.Select(u => u.VideoSec));
        Assert.Equal([2, 1], ults.Select(u => u.Presses));
        Assert.All(ults, u => Assert.False(u.Confirmed));
    }

    [Fact]
    public void With_the_R_slot_sampled_only_presses_that_start_a_cooldown_are_casts()
    {
        var rows = new List<string>();
        // Ready (bright) all game except a cooldown from 30s to 60s.
        for (var ms = 0; ms < 90_000; ms += 250)
            rows.Add($"{ms},hud_r,,{(ms is >= 30_000 and < 60_000 ? 60 : 100)},0");
        rows.Add("29800,key_down,R,0,0");
        rows.Add("45000,key_down,R,0,0");  // mashed while on cooldown
        rows.Add("75000,key_down,R,0,0");  // pressed, nothing cast (out of range)
        var t = Read([.. rows.OrderBy(r => long.Parse(r.Split(',')[0]))]);

        var ults = t.Ults(out var source);
        Assert.Equal("hud", source);
        var cast = Assert.Single(ults);
        Assert.Equal(29.8, cast.VideoSec);
        Assert.True(cast.Confirmed);
    }

    [Fact]
    public void A_short_dark_dip_is_crowd_control_not_a_cooldown()
    {
        var rows = new List<string>();
        for (var ms = 0; ms < 60_000; ms += 250)
            rows.Add($"{ms},hud_r,,{(ms is >= 20_000 and < 21_500 ? 50 : 100)},0");
        rows.Add("19900,key_down,R,0,0");
        var ults = Read([.. rows.OrderBy(r => long.Parse(r.Split(',')[0]))]).Ults(out var source);

        // No cooldown seen at all: the presses stand on their own.
        Assert.Equal("keys", source);
        Assert.False(Assert.Single(ults).Confirmed);
    }

    [Fact]
    public void The_series_is_versioned_and_carries_the_ults()
    {
        var s = Series(Read("5000,key_down,R,0,0"));
        Assert.Equal(InputTelemetry.Version, s.GetProperty("v").GetInt32());
        Assert.Equal(5.0, s.GetProperty("ults")[0].GetProperty("videoSec").GetDouble());
        Assert.Equal("keys", s.GetProperty("ultSource").GetString());
    }
}
