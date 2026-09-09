// Optional host capabilities. The normal website never configures these.
export const reviewHost: {
  desktop: boolean
  artPrefix: string | null
  openYouTube: ((url: string) => void) | null
} = { desktop: false, artPrefix: null, openYouTube: null }

export function assetUrl(url: string): string {
  return reviewHost.artPrefix ? reviewHost.artPrefix + encodeURIComponent(url) : url
}
