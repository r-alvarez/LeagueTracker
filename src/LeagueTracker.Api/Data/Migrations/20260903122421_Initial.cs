using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LeagueTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KeyValues",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyValues", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "KnownMatches",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnownMatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LpSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Queue = table.Column<string>(type: "text", nullable: false),
                    Tier = table.Column<string>(type: "text", nullable: false),
                    Division = table.Column<string>(type: "text", nullable: false),
                    Lp = table.Column<int>(type: "integer", nullable: false),
                    Wins = table.Column<int>(type: "integer", nullable: false),
                    Losses = table.Column<int>(type: "integer", nullable: false),
                    RankValue = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LpSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Matches",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    QueueId = table.Column<int>(type: "integer", nullable: false),
                    QueueName = table.Column<string>(type: "text", nullable: false),
                    IsRanked = table.Column<bool>(type: "boolean", nullable: false),
                    GameMode = table.Column<string>(type: "text", nullable: false),
                    GameVersion = table.Column<string>(type: "text", nullable: false),
                    GameCreationUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GameEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationSec = table.Column<double>(type: "double precision", nullable: false),
                    HasTimeline = table.Column<bool>(type: "boolean", nullable: false),
                    RawPath = table.Column<string>(type: "text", nullable: false),
                    Champion = table.Column<string>(type: "text", nullable: false),
                    Position = table.Column<string>(type: "text", nullable: false),
                    Win = table.Column<bool>(type: "boolean", nullable: false),
                    Kills = table.Column<int>(type: "integer", nullable: false),
                    Deaths = table.Column<int>(type: "integer", nullable: false),
                    Assists = table.Column<int>(type: "integer", nullable: false),
                    Cs = table.Column<int>(type: "integer", nullable: false),
                    Gold = table.Column<int>(type: "integer", nullable: false),
                    DamageToChampions = table.Column<int>(type: "integer", nullable: false),
                    VisionScore = table.Column<int>(type: "integer", nullable: false),
                    ChampLevel = table.Column<int>(type: "integer", nullable: false),
                    AvgAllyRankValue = table.Column<double>(type: "double precision", nullable: true),
                    AvgEnemyRankValue = table.Column<double>(type: "double precision", nullable: true),
                    AllyRanksKnown = table.Column<int>(type: "integer", nullable: false),
                    EnemyRanksKnown = table.Column<int>(type: "integer", nullable: false),
                    RanksAtGameTime = table.Column<bool>(type: "boolean", nullable: false),
                    LpChange = table.Column<int>(type: "integer", nullable: true),
                    LpBefore = table.Column<string>(type: "text", nullable: true),
                    LpAfter = table.Column<string>(type: "text", nullable: true),
                    TimeInEnemyHalfPct = table.Column<double>(type: "double precision", nullable: true),
                    AvgNearestAllyDist = table.Column<int>(type: "integer", nullable: true),
                    SkillshotsHit = table.Column<int>(type: "integer", nullable: true),
                    SkillshotsDodged = table.Column<int>(type: "integer", nullable: true),
                    OpponentChampion = table.Column<string>(type: "text", nullable: true),
                    EnemyJungler = table.Column<string>(type: "text", nullable: true),
                    AllyJungler = table.Column<string>(type: "text", nullable: true),
                    CsAt10 = table.Column<int>(type: "integer", nullable: true),
                    CsAt14 = table.Column<int>(type: "integer", nullable: true),
                    LaneGoldDiff10 = table.Column<int>(type: "integer", nullable: true),
                    LaneXpDiff10 = table.Column<int>(type: "integer", nullable: true),
                    LaneCsDiff10 = table.Column<int>(type: "integer", nullable: true),
                    SoloKills = table.Column<int>(type: "integer", nullable: false),
                    KillParticipation = table.Column<double>(type: "double precision", nullable: true),
                    ControlWards = table.Column<int>(type: "integer", nullable: false),
                    WardsPlaced = table.Column<int>(type: "integer", nullable: false),
                    WardsKilled = table.Column<int>(type: "integer", nullable: false),
                    DamageTakenPerMin = table.Column<double>(type: "double precision", nullable: true),
                    TripleKills = table.Column<int>(type: "integer", nullable: false),
                    QuadraKills = table.Column<int>(type: "integer", nullable: false),
                    PentaKills = table.Column<int>(type: "integer", nullable: false),
                    DpmEarly = table.Column<double>(type: "double precision", nullable: true),
                    DpmMid = table.Column<double>(type: "double precision", nullable: true),
                    DpmLate = table.Column<double>(type: "double precision", nullable: true),
                    FollowInDeaths = table.Column<int>(type: "integer", nullable: false),
                    CsAt15 = table.Column<int>(type: "integer", nullable: true),
                    LaneGoldDiff15 = table.Column<int>(type: "integer", nullable: true),
                    LaneXpDiff15 = table.Column<int>(type: "integer", nullable: true),
                    LaneCsDiff15 = table.Column<int>(type: "integer", nullable: true),
                    FirstToLevel2 = table.Column<bool>(type: "boolean", nullable: true),
                    SkillOrder = table.Column<string>(type: "text", nullable: false),
                    TotalTimeSpentDead = table.Column<int>(type: "integer", nullable: false),
                    LongestTimeSpentLiving = table.Column<int>(type: "integer", nullable: false),
                    TotalTimeCcDealt = table.Column<int>(type: "integer", nullable: false),
                    ChallengesJson = table.Column<string>(type: "text", nullable: false),
                    AvgUnspentGold = table.Column<int>(type: "integer", nullable: true),
                    MaxUnspentGold = table.Column<int>(type: "integer", nullable: true),
                    FirstWardSec = table.Column<int>(type: "integer", nullable: true),
                    FirstControlWardSec = table.Column<int>(type: "integer", nullable: true),
                    WardsFirst10 = table.Column<int>(type: "integer", nullable: false),
                    Level6LeadSec = table.Column<int>(type: "integer", nullable: true),
                    Level11LeadSec = table.Column<int>(type: "integer", nullable: true),
                    Level16LeadSec = table.Column<int>(type: "integer", nullable: true),
                    LevelSecs = table.Column<string>(type: "text", nullable: false),
                    FriendlyEpicObjectives = table.Column<int>(type: "integer", nullable: false),
                    ObjectivesPresentFor = table.Column<int>(type: "integer", nullable: false),
                    ContestedEpicsTaken = table.Column<int>(type: "integer", nullable: false),
                    TeamGoldDiff15 = table.Column<int>(type: "integer", nullable: true),
                    TeamGoldDiff20 = table.Column<int>(type: "integer", nullable: true),
                    LaneDiffsJson = table.Column<string>(type: "text", nullable: false),
                    FightsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Matches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Deaths",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MatchId = table.Column<string>(type: "text", nullable: false),
                    TimeSec = table.Column<int>(type: "integer", nullable: false),
                    X = table.Column<int>(type: "integer", nullable: false),
                    Y = table.Column<int>(type: "integer", nullable: false),
                    KilledBy = table.Column<string>(type: "text", nullable: false),
                    AssistedBy = table.Column<string>(type: "text", nullable: false),
                    DamageFrom = table.Column<string>(type: "text", nullable: false),
                    EnemiesOnYou = table.Column<int>(type: "integer", nullable: false),
                    Bounty = table.Column<int>(type: "integer", nullable: false),
                    Shutdown = table.Column<int>(type: "integer", nullable: false),
                    MyLevel = table.Column<int>(type: "integer", nullable: true),
                    MyTotalGold = table.Column<int>(type: "integer", nullable: true),
                    MyCs = table.Column<int>(type: "integer", nullable: true),
                    EnemiesNearDeath = table.Column<int>(type: "integer", nullable: true),
                    AlliesNearDeath = table.Column<int>(type: "integer", nullable: true),
                    NearestAllyDist = table.Column<int>(type: "integer", nullable: true),
                    TotalDamageReceived = table.Column<int>(type: "integer", nullable: true),
                    DamageInstanceCount = table.Column<int>(type: "integer", nullable: true),
                    TopSourceShare = table.Column<double>(type: "double precision", nullable: true),
                    TopSource = table.Column<string>(type: "text", nullable: true),
                    SecondsAfterObjective = table.Column<int>(type: "integer", nullable: true),
                    ObjectiveBefore = table.Column<string>(type: "text", nullable: true),
                    Zone = table.Column<string>(type: "text", nullable: false),
                    FollowTeammate = table.Column<string>(type: "text", nullable: true),
                    FollowTeammateRole = table.Column<string>(type: "text", nullable: true),
                    FollowTeammateCaughtBy = table.Column<string>(type: "text", nullable: true),
                    FollowSecondsAfter = table.Column<int>(type: "integer", nullable: true),
                    FollowDistance = table.Column<int>(type: "integer", nullable: true),
                    FollowAlliesDownBefore = table.Column<int>(type: "integer", nullable: true),
                    FollowPureLoss = table.Column<bool>(type: "boolean", nullable: true),
                    FollowTeamGoldDiff = table.Column<int>(type: "integer", nullable: true),
                    EnemyJunglerNear = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deaths", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Deaths_Matches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "Matches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ItemEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MatchId = table.Column<string>(type: "text", nullable: false),
                    TimeSec = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    ItemId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemEvents_Matches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "Matches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KillEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MatchId = table.Column<string>(type: "text", nullable: false),
                    TimeSec = table.Column<int>(type: "integer", nullable: false),
                    KillerParticipantId = table.Column<int>(type: "integer", nullable: false),
                    VictimParticipantId = table.Column<int>(type: "integer", nullable: false),
                    AssistIds = table.Column<string>(type: "text", nullable: false),
                    DamagePids = table.Column<string>(type: "text", nullable: false),
                    X = table.Column<int>(type: "integer", nullable: false),
                    Y = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KillEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KillEvents_Matches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "Matches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ObjectiveEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MatchId = table.Column<string>(type: "text", nullable: false),
                    TimeSec = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    SubKind = table.Column<string>(type: "text", nullable: false),
                    ByMyTeam = table.Column<bool>(type: "boolean", nullable: false),
                    KillerParticipantId = table.Column<int>(type: "integer", nullable: false),
                    X = table.Column<int>(type: "integer", nullable: false),
                    Y = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObjectiveEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ObjectiveEvents_Matches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "Matches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Participants",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MatchId = table.Column<string>(type: "text", nullable: false),
                    ParticipantId = table.Column<int>(type: "integer", nullable: false),
                    Puuid = table.Column<string>(type: "text", nullable: false),
                    RiotId = table.Column<string>(type: "text", nullable: false),
                    Champion = table.Column<string>(type: "text", nullable: false),
                    TeamId = table.Column<int>(type: "integer", nullable: false),
                    IsMe = table.Column<bool>(type: "boolean", nullable: false),
                    IsAlly = table.Column<bool>(type: "boolean", nullable: false),
                    Position = table.Column<string>(type: "text", nullable: false),
                    Win = table.Column<bool>(type: "boolean", nullable: false),
                    Kills = table.Column<int>(type: "integer", nullable: false),
                    Deaths = table.Column<int>(type: "integer", nullable: false),
                    Assists = table.Column<int>(type: "integer", nullable: false),
                    Cs = table.Column<int>(type: "integer", nullable: false),
                    Gold = table.Column<int>(type: "integer", nullable: false),
                    DamageToChampions = table.Column<int>(type: "integer", nullable: false),
                    VisionScore = table.Column<int>(type: "integer", nullable: false),
                    ChampLevel = table.Column<int>(type: "integer", nullable: false),
                    Tier = table.Column<string>(type: "text", nullable: true),
                    Division = table.Column<string>(type: "text", nullable: true),
                    Lp = table.Column<int>(type: "integer", nullable: true),
                    SeasonWins = table.Column<int>(type: "integer", nullable: true),
                    SeasonLosses = table.Column<int>(type: "integer", nullable: true),
                    RankValue = table.Column<int>(type: "integer", nullable: true),
                    RankQueue = table.Column<string>(type: "text", nullable: true),
                    SkillshotsHit = table.Column<int>(type: "integer", nullable: true),
                    SkillshotsDodged = table.Column<int>(type: "integer", nullable: true),
                    SkillshotDodgesLateWindow = table.Column<int>(type: "integer", nullable: true),
                    KillParticipation = table.Column<double>(type: "double precision", nullable: true),
                    Summoner1Id = table.Column<int>(type: "integer", nullable: false),
                    Summoner2Id = table.Column<int>(type: "integer", nullable: false),
                    PrimaryStyleId = table.Column<int>(type: "integer", nullable: false),
                    SubStyleId = table.Column<int>(type: "integer", nullable: false),
                    KeystoneId = table.Column<int>(type: "integer", nullable: false),
                    Items = table.Column<string>(type: "text", nullable: false),
                    PerksJson = table.Column<string>(type: "text", nullable: false),
                    Spell1Casts = table.Column<int>(type: "integer", nullable: false),
                    Spell2Casts = table.Column<int>(type: "integer", nullable: false),
                    Spell3Casts = table.Column<int>(type: "integer", nullable: false),
                    Spell4Casts = table.Column<int>(type: "integer", nullable: false),
                    Summoner1Casts = table.Column<int>(type: "integer", nullable: false),
                    Summoner2Casts = table.Column<int>(type: "integer", nullable: false),
                    PingsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Participants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Participants_Matches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "Matches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PositionSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MatchId = table.Column<string>(type: "text", nullable: false),
                    ParticipantId = table.Column<int>(type: "integer", nullable: false),
                    TimeSec = table.Column<int>(type: "integer", nullable: false),
                    X = table.Column<int>(type: "integer", nullable: false),
                    Y = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PositionSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PositionSamples_Matches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "Matches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeathDamages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeathId = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    SpellName = table.Column<string>(type: "text", nullable: false),
                    Physical = table.Column<int>(type: "integer", nullable: false),
                    Magic = table.Column<int>(type: "integer", nullable: false),
                    TrueDamage = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeathDamages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeathDamages_Deaths_DeathId",
                        column: x => x.DeathId,
                        principalTable: "Deaths",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeathDamages_DeathId",
                table: "DeathDamages",
                column: "DeathId");

            migrationBuilder.CreateIndex(
                name: "IX_Deaths_MatchId",
                table: "Deaths",
                column: "MatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemEvents_MatchId",
                table: "ItemEvents",
                column: "MatchId");

            migrationBuilder.CreateIndex(
                name: "IX_KillEvents_MatchId",
                table: "KillEvents",
                column: "MatchId");

            migrationBuilder.CreateIndex(
                name: "IX_LpSnapshots_Queue_TimestampUtc",
                table: "LpSnapshots",
                columns: new[] { "Queue", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Matches_GameEndUtc",
                table: "Matches",
                column: "GameEndUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ObjectiveEvents_MatchId",
                table: "ObjectiveEvents",
                column: "MatchId");

            migrationBuilder.CreateIndex(
                name: "IX_Participants_MatchId",
                table: "Participants",
                column: "MatchId");

            migrationBuilder.CreateIndex(
                name: "IX_PositionSamples_MatchId_TimeSec",
                table: "PositionSamples",
                columns: new[] { "MatchId", "TimeSec" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeathDamages");

            migrationBuilder.DropTable(
                name: "ItemEvents");

            migrationBuilder.DropTable(
                name: "KeyValues");

            migrationBuilder.DropTable(
                name: "KillEvents");

            migrationBuilder.DropTable(
                name: "KnownMatches");

            migrationBuilder.DropTable(
                name: "LpSnapshots");

            migrationBuilder.DropTable(
                name: "ObjectiveEvents");

            migrationBuilder.DropTable(
                name: "Participants");

            migrationBuilder.DropTable(
                name: "PositionSamples");

            migrationBuilder.DropTable(
                name: "Deaths");

            migrationBuilder.DropTable(
                name: "Matches");
        }
    }
}
