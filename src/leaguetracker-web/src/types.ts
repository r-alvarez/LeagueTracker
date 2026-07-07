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
  csAt10: number | null
  laneGoldDiff10: number | null
  killParticipation: number | null
  soloKills: number
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

export interface MatchDetail {
  summary: MatchSummary
  ranksAtGameTime: boolean
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
  winRate: number
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
  winrateByLaneState: { ahead: BucketStat; even: BucketStat; behind: BucketStat }
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
