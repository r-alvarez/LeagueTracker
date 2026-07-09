export interface RankInfo {
  queue: string
  tier: string
  division: string
  lp: number
  wins: number
  losses: number
  rankValue: number
  label: string
  asOfUtc: string
}

export interface JobStatus {
  jobName: string | null
  running: boolean
  processed: number
  total: number
  message: string
  startedUtc: string | null
}

export interface Status {
  riotId: string
  apiKeyConfigured: boolean
  matches: number
  rankedMatches: number
  deaths: number
  lpSnapshots: number
  replays: number
  patches: string[]
  dateFrom: string | null
  dateTo: string | null
  ranks: RankInfo[]
  job: JobStatus
}

export interface MatchSummary {
  id: string
  queueId: number
  queueName: string
  isRanked: boolean
  gameMode: string
  date: string
  gameEndUtc: string
  durationMin: number
  champion: string
  position: string
  win: boolean
  kills: number
  deaths: number
  assists: number
  kda: string
  cs: number
  gold: number
  damageToChampions: number
  visionScore: number
  champLevel: number
  hasTimeline: boolean
  avgAllyRank: string | null
  avgEnemyRank: string | null
  rankGapLp: number | null
  allyRanksKnown: number
  enemyRanksKnown: number
  ranksAtGameTime: boolean
  lpChange: number | null
  lpBefore: string | null
  lpAfter: string | null
  timeInEnemyHalfPct: number | null
  avgNearestAllyDist: number | null
  skillshotsHit: number | null
  skillshotsDodged: number | null
  opponentChampion: string | null
  enemyJungler: string | null
  allyJungler: string | null
  isRemake: boolean
  csAt10: number | null
  laneGoldDiff10: number | null
  killParticipation: number | null
  soloKills: number
  items: string | null
  summoner1Id: number | null
  summoner2Id: number | null
  hasReplay: boolean
}

export interface MatchPage {
  total: number
  items: MatchSummary[]
}

export interface Participant {
  participantId: number
  riotId: string
  champion: string
  position: string
  teamId: number
  isMe: boolean
  isAlly: boolean
  win: boolean
  kills: number
  deaths: number
  assists: number
  cs: number
  gold: number
  damageToChampions: number
  visionScore: number
  champLevel: number
  tier: string | null
  division: string | null
  lp: number | null
  seasonWins: number | null
  seasonLosses: number | null
  rankValue: number | null
  rankQueue: string | null
  rankLabel: string | null
  winratePct: number | null
  summoner1Id: number
  summoner2Id: number
  primaryStyleId: number
  subStyleId: number
  keystoneId: number
  items: string
  skillshotsHit: number | null
  skillshotsDodged: number | null
  skillshotDodgesLateWindow: number | null
  killParticipation: number | null
  perksJson: string
  pingsJson: string
  spell1Casts: number
  spell2Casts: number
  spell3Casts: number
  spell4Casts: number
  summoner1Casts: number
  summoner2Casts: number
}

export interface Perks {
  styles: Array<{
    description: string
    style: number
    selections: Array<{ perk: number }>
  }>
  statPerks: { offense: number; flex: number; defense: number }
}

export interface LaneDiffCheckpoint {
  min: number
  gold: number
  xp: number
  cs: number
  level: number
  myCs: number
  myLevel: number
  oppCs: number
  oppLevel: number
  myItems: number[]
  oppItems: number[]
  myKills: number
  myDeaths: number
  oppKills: number
  oppDeaths: number
}

export interface TeamObjectiveCounts {
  towers: number
  inhibitors: number
  dragons: number
  barons: number
  heralds: number
  grubs: number
  atakhan: number
}

export interface DeathEvent {
  timeSec: number
  gameTime: string
  x: number
  y: number
  killedBy: string
  assistedBy: string
  damageFrom: string
  enemiesOnYou: number
  bounty: number
  shutdown: number
  myLevel: number | null
  myTotalGold: number | null
  myCs: number | null
  enemiesNearDeath: number | null
  alliesNearDeath: number | null
  nearestAllyDist: number | null
  totalDamageReceived: number | null
  damageInstanceCount: number | null
  topSource: string | null
  topSourceShare: number | null
  secondsAfterObjective: number | null
  objectiveBefore: string | null
  zone: string
  followTeammate: string | null
  followTeammateRole: string | null
  followTeammateCaughtBy: string | null
  followSecondsAfter: number | null
  followDistance: number | null
  followAlliesDownBefore: number | null
  followPureLoss: boolean | null
  followTeamGoldDiff: number | null
  damageInstances: DamageInstance[]
}

export interface DamageInstance {
  source: string
  spellName: string
  physical: number
  magic: number
  trueDamage: number
  total: number
}

export interface ObjectiveEventDto {
  timeSec: number
  gameTime: string
  kind: string
  subKind: string
  byMyTeam: boolean
  killer: string | null
}

export interface MatchMacro {
  avgUnspentGold: number | null
  maxUnspentGold: number | null
  firstWardSec: number | null
  firstControlWardSec: number | null
  wardsFirst10: number
  level6LeadSec: number | null
  level11LeadSec: number | null
  level16LeadSec: number | null
  friendlyEpicObjectives: number
  objectivesPresentFor: number
}

export interface MatchDetail {
  summary: MatchSummary
  ranksAtGameTime: boolean
  macro: MatchMacro
  mySide: string
  teamObjectives: { ally: TeamObjectiveCounts; enemy: TeamObjectiveCounts }
  skillOrder: number[]
  laning: {
    csAt10: number | null
    csAt15: number | null
    laneGoldDiff10: number | null
    laneXpDiff10: number | null
    laneCsDiff10: number | null
    laneGoldDiff15: number | null
    laneXpDiff15: number | null
    laneCsDiff15: number | null
    firstToLevel2: boolean | null
    checkpoints: LaneDiffCheckpoint[] | null
  }
  wards: { wardsPlaced: number; wardsKilled: number; controlWards: number }
  participants: Participant[]
  deaths: DeathEvent[]
  objectives: ObjectiveEventDto[]
  itemEvents: { timeSec: number; kind: string; itemId: number }[]
}

export interface AnalyticsSummary {
  games: number
  totalDeaths: number
  deathsPerGame: number
  collapseDeaths: number
  isolatedDeaths: number
  postObjectiveDeaths: number
  burstDeaths: number
  avgEnemiesNearDeath: number | null
  avgNearestAllyDistAtDeath: number | null
  avgTimeInEnemyHalfPct: number
  avgNearestAllyDistOverall: number
  avgSkillshotsHit: number
  avgSkillshotsDodged: number
}

export interface LpPoint {
  timestampUtc: string
  queue: string
  tier: string
  division: string
  lp: number
  wins: number
  losses: number
  rankValue: number
  label: string
}

export interface CountedItem {
  key: string
  count: number
  share: number
}

export interface BucketStat {
  games: number
  wins: number
  winRate: number
}

export interface LaneStateStats {
  ahead: BucketStat
  even: BucketStat
  behind: BucketStat
  at15: { ahead: BucketStat; even: BucketStat; behind: BucketStat }
  trajectory: {
    leadsHeldAt20: { held: number; of: number }
    deficitsRecoveredAt20: { recovered: number; of: number }
    leadsAt20Won: { won: number; of: number }
  }
}

export interface ProfileMetric {
  key: string
  label: string
  category: string
  unit: string
  higherIsBetter: boolean
  description: string
  avg: number
  avgWins: number | null
  avgLosses: number | null
  separationPct: number | null
  games: number
  recent: number[]
}

export interface ProfileGroup {
  games: number
  wins: number
  metrics: ProfileMetric[]
}

export interface ChallengeRow {
  id: number
  name: string
  description: string
  level: string
  levelRank: number
  percentile: number | null
  value: number | null
  levelShare: number | null
  nextLevel: string | null
  nextLevelShare: number | null
}

export interface ChallengeBenchmark {
  asOfUtc: string | null
  challenges: ChallengeRow[]
}

export interface ClipEvent {
  kind: 'kill' | 'death'
  timeSec: number
}

export interface ClipInfo {
  index: number
  label: string
  startSec: number
  endSec: number
  events: ClipEvent[]
  url: string
  ready: boolean
}

export interface RenderQueueRow {
  matchId: string
  champion: string
  gameEndUtc: string
  kind: 'clips' | 'full'
  status: 'pending' | 'rendering' | 'done' | 'failed' | 'no-events'
  error: string | null
}

export interface FullGameStatus {
  state: 'none' | 'requested' | 'rendering' | 'done' | 'failed'
  keep: boolean
  sizeMb: number | null
  renderedUtc: string | null
  error: string | null
}

export interface StorageInfo {
  rawGamesMb: number
  replaysMb: number
  clipsMb: number
  fullGamesMb: number
  databaseMb: number
}

export interface LiveParticipant {
  championId: number
  teamId: number
  riotId: string | null
  isMe: boolean
}

export interface LiveGame {
  matchId: string
  queueId: number
  queue: string
  startedUtc: string | null
  detectedUtc: string
  myChampionId: number
  myTeamId: number
  participants: LiveParticipant[]
}

export interface MatchupRow {
  opponent: string
  games: number
  winRate: number
  laneGoldAt10: number | null
  kda: number
}

export interface SplitRow {
  key: string
  games: number
  wins: number
  winRate: number
  kda: number
  kp: number
  csPerMin: number
  dpm: number
  laneGoldAt10: number | null
  deathsPerGame: number
  detail: {
    avgKills: number
    avgDeaths: number
    avgAssists: number
    csAt10: number
    soloKillsPerGame: number
    visionPerMin: number
    skillshotsDodgedPerGame: number
    matchups: MatchupRow[]
  } | null
}

export interface SeriesPoint {
  id: string
  date: string
  win: boolean
  n: number
  rollingWinRate10: number
  laneGoldAt10: number | null
  csAt10: number | null
}

export interface Stats {
  scope: {
    games: number
    wins: number
    losses: number
    winRate: number
    dateFrom: string | null
    dateTo: string | null
    champions: number
  }
  overall: {
    kda: number
    kp: number
    dpm: number
    gpm: number
    csPerMin: number
    csAt10: number
    laneGoldAt10: number | null
    laneCsAt10: number | null
    laneGoldAt10ByRole: { role: string; avg: number }[]
    visionPerMin: number
    controlWardsPerGame: number
    deathsPerGame: number
    deathsPre10: number
    deaths10To20: number
    deathsPost20: number
    soloKillsPerGame: number
    dpmEarly: number
    dpmMid: number
    dpmLate: number
    damageTakenPerMin: number
    triples: number
    quadras: number
    pentas: number
    skillshotsHitPerGame: number
    skillshotsDodgedPerGame: number
  }
  winrateByLaneState: LaneStateStats
  deathZones: CountedItem[]
  topKillers: CountedItem[]
  followIn: {
    totalDeaths: number
    followIns: number
    rate: number
    pureLoss: number
    twoPlusDown: number
    byRole: CountedItem[]
    goldState: { behind: number; even: number; ahead: number }
  }
  profile: {
    all: ProfileGroup
    evenBehind: ProfileGroup
    ahead: ProfileGroup
  }
  byChampion: SplitRow[]
  byRole: SplitRow[]
  series: SeriesPoint[]
  lpDeltas: { queue: string; last7: number | null; last30: number | null }[]
  observations: string[]
}

export interface LpPerGame {
  id: string
  gameEndUtc: string
  queueName: string
  champion: string
  position: string
  win: boolean
  kda: string
  lpBefore: string | null
  lpAfter: string | null
  lpChange: number | null
}
