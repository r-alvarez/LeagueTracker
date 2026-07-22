import type { ContestVerdict, MatchReview, ReviewVerdict } from './types'

// The five tiers, harsh at both ends on purpose: the verdict only stays
// trustworthy if it can say "you got run over" as plainly as "you dominated".
// It scores the contest, never the game - the result is a separate fact.
export const CONTEST_LABEL: Record<Exclude<ContestVerdict, null>, string> = {
  dominated: 'Dominated the contest',
  won: 'Won the contest',
  split: 'Split contest',
  lost: 'Lost the contest',
  runover: 'Got run over',
}

/** Terse variants that fit the match-row meta column on one line. */
export const CONTEST_SHORT: Record<Exclude<ContestVerdict, null>, string> = {
  dominated: 'Dominated',
  won: 'Contest won',
  split: 'Contest split',
  lost: 'Contest lost',
  runover: 'Run over',
}

/** Row tint bucket: won-side, split, lost-side. */
export const contestSide = (c: ContestVerdict) =>
  c === 'dominated' || c === 'won' ? 'cwin' : c === 'split' ? 'csplit' : c ? 'closs' : null

/** The one honest sentence, composed from whatever the questions decided.
 *  Mixed verdicts stay out - the sentence only carries what was decisive. */
export function contestSentence(r: MatchReview): string {
  const pos: string[] = []
  const neg: string[] = []
  const put = (v: ReviewVerdict | undefined, yes: string, no: string) => {
    if (v === 'yes') pos.push(yes)
    else if (v === 'no') neg.push(no)
  }
  put(r.laneDuel?.verdict, 'you out-dueled your lane', 'your lane out-dueled you')
  put(r.fights.verdict, 'you left your fights alive', 'you overstayed and paid in bodies')
  put(r.discipline.verdict, 'you stepped with your eyes open', 'you stepped in blind')
  if (r.stewardship) {
    // The stewardship summary already carries its own direction ("lead grew",
    // "deficit grew"); it just needs a subject to read as a clause.
    const s = r.stewardship.detail.summary
    const phrase = s.startsWith('lead') || s.startsWith('deficit') ? `your ${s}` : `you ${s}`
    put(r.stewardship.verdict, phrase, phrase)
  }
  if (pos.length === 0 && neg.length === 0) return "Nothing decisive either way — that's the game."
  const body = pos.length && neg.length
    ? `${pos.join(', ')}, but ${neg.join(', ')}`
    : (pos.length ? pos : neg).join(', ')
  return body.charAt(0).toUpperCase() + body.slice(1) + " — that's the game."
}
