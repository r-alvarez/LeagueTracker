namespace LeagueTracker.Api.Services;

// Approximate Summoner's Rift zone classifier for death/kill coordinates.
// Riot coords run ~0..14870 with (0,0) at the blue (team 100) base corner and
// (14870,14870) at the red (team 200) base corner. Mid lane is the main diagonal;
// the river is the anti-diagonal. Buckets are coarse but stable - good enough for
// "most of your deaths happen in X".
public static class MapZones
{
    private const double Max = 14870.0;
    private static readonly double Sqrt2 = Math.Sqrt(2);

    public static string Classify(int xi, int yi)
    {
        double x = xi, y = yi;
        double s = x + y;                              // 0 (blue corner) .. 2*Max (red corner)
        double d = x - y;                              // >0 bot side, <0 top side
        double offMain = Math.Abs(d) / Sqrt2;          // distance from mid-lane diagonal
        double offAnti = Math.Abs(s - Max) / Sqrt2;    // distance from river diagonal

        if (s < 3600) return "Blue base";
        if (s > 2 * Max - 3600) return "Red base";

        if (offMain < 1200) return s < Max ? "Mid lane (blue side)" : "Mid lane (red side)";
        if (offAnti < 1100) return d < 0 ? "Top river" : "Bot river";

        bool topLane = (x < 2600 && y > 2600) || (y > Max - 2600 && x < Max - 2600);
        bool botLane = (y < 2600 && x > 2600) || (x > Max - 2600 && y < Max - 2600);
        if (topLane) return "Top lane";
        if (botLane) return "Bot lane";

        string side = s < Max ? "Blue" : "Red";
        string half = d < 0 ? "top" : "bot";
        return $"{side} jungle ({half} side)";
    }
}
