using LeagueTracker.Api.Services;

namespace LeagueTracker.Api.Tests;

public class ClipServiceFightWindowsTests
{
    private const double GameLength = 1800;

    private static readonly List<ClipService.Fighter> Fighters =
    [
        new(2, "Ally#EUW", "Ahri"),
        new(3, "Mate#EUW", "Leona"),
        new(7, "Foe#EUW", "Zed"),
    ];

    private static TimelineAnalyzer.Fight Fight(
        int start, int end, string kind = "teamfight", string result = "lost", int allies = 3, int enemies = 4,
        int allyKills = 0, int enemyKills = 1, bool converted = false, int camera = 2, bool participated = false) =>
        new(start, end, kind, result, participated, allies, enemies, allyKills, enemyKills, 0, converted, camera);

    private static List<ClipWindow> Plan(List<TimelineAnalyzer.Fight> fights, List<ClipService.ObjectiveTake>? objectives = null, int nextIndex = 0) =>
        ClipService.FightWindows(fights, Fighters, objectives ?? [], GameLength, nextIndex);

    [Fact]
    public void Teamfights_get_the_longer_rolls_and_skirmishes_keep_the_moment_rolls()
    {
        var windows = Plan([Fight(600, 610), Fight(900, 905, kind: "skirmish", allies: 2, enemies: 2, allyKills: 1, enemyKills: 1)]);

        Assert.Equal((570, 630), (windows[0].StartSec, windows[0].EndSec));
        Assert.Equal((880, 915), (windows[1].StartSec, windows[1].EndSec));
    }

    [Fact]
    public void A_converted_objective_extends_the_clip_to_the_winners_take()
    {
        var objectives = new List<ClipService.ObjectiveTake> { new(1230, ByMyTeam: false), new(1240, ByMyTeam: true) };

        var window = Plan([Fight(1200, 1200, result: "lost", converted: true)], objectives).Single();

        Assert.Equal(1235, window.EndSec);
    }

    [Fact]
    public void An_objective_taken_after_the_conversion_window_does_not_stretch_the_clip()
    {
        var objectives = new List<ClipService.ObjectiveTake> { new(1250, ByMyTeam: false) };

        var window = Plan([Fight(1200, 1200, result: "lost", converted: true)], objectives).Single();

        Assert.Equal(1220, window.EndSec);
    }

    [Fact]
    public void Overlapping_fight_windows_fold_into_one_clip_labelled_from_the_union()
    {
        var windows = Plan(
        [
            Fight(1445, 1445, allies: 3, enemies: 4, allyKills: 0, enemyKills: 1, result: "lost", camera: 2),
            Fight(1490, 1497, allies: 4, enemies: 4, allyKills: 2, enemyKills: 2, result: "draw", camera: 3),
        ]);

        var clip = Assert.Single(windows);
        Assert.Equal((1415, 1517), (clip.StartSec, clip.EndSec));
        Assert.Equal("teamfight 4v4 · lost", clip.Label);
        Assert.Equal(new[] { 1445, 1490 }, clip.Events.Select(e => e.TimeSec).ToArray());
    }

    [Fact]
    public void A_folded_clip_films_from_the_biggest_fights_camera_and_the_later_one_on_a_tie()
    {
        var biggest = Plan([Fight(1000, 1000, allyKills: 3, enemyKills: 0, camera: 3), Fight(1040, 1040, camera: 7)]).Single();
        var tie = Plan([Fight(1000, 1000, camera: 3), Fight(1040, 1040, camera: 7)]).Single();

        Assert.Equal("Leona", biggest.CameraChampion);
        Assert.Equal("Zed", tie.CameraChampion);
    }

    [Fact]
    public void Fights_further_apart_than_their_rolls_stay_separate_clips_with_running_indexes()
    {
        var windows = Plan([Fight(1000, 1000), Fight(1051, 1051)], nextIndex: 4);

        Assert.Equal(new[] { 4, 5 }, windows.Select(w => w.Index).ToArray());
        Assert.Equal(new[] { (970, 1020), (1021, 1071) }, windows.Select(w => (w.StartSec, w.EndSec)).ToArray());
    }

    [Fact]
    public void Gate_skips_the_players_own_fights_and_single_kill_skirmishes()
    {
        var windows = Plan(
        [
            Fight(300, 300, participated: true),
            Fight(600, 600, kind: "skirmish", allies: 2, enemies: 2, allyKills: 0, enemyKills: 1),
            Fight(900, 900, kind: "skirmish", allies: 2, enemies: 2, allyKills: 1, enemyKills: 1),
        ]);

        Assert.Equal(900, Assert.Single(windows).Events.Single().TimeSec);
    }

    [Fact]
    public void The_clip_never_runs_past_the_end_of_the_game()
    {
        var window = Plan([Fight((int)GameLength - 5, (int)GameLength - 5)]).Single();

        Assert.Equal((int)GameLength, window.EndSec);
    }
}
