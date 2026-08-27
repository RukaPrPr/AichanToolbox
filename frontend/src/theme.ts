import { defaultTheme } from './themes/light.ts'
import { graphitePurple } from './themes/graphitePurple.ts'
import { noirGold } from './themes/noirGold.ts'
import type { ThemeDefinition } from './themes/types.ts'

export { defaultTheme, graphitePurple, noirGold }
export type { ThemeDefinition, ThemeArtwork } from './themes/types.ts'

// Register new definitions here. Controls and the native host consume metadata,
// never individual theme IDs; the array order is the picker order.
export const themes: readonly ThemeDefinition[] = [
  defaultTheme,
  graphitePurple,
  noirGold
]

export const themeStorageKey = 'aichan:theme'

export function resolveTheme(value: unknown, catalog: readonly ThemeDefinition[] = themes): ThemeDefinition {
  return catalog.find(theme => theme.id === value) ?? defaultTheme
}

export function applyTheme(value: unknown, page: Pick<Document, 'documentElement'> = document, catalog: readonly ThemeDefinition[] = themes): ThemeDefinition {
  const theme = resolveTheme(value, catalog)
  const root = page.documentElement
  // Clear the previous palette, including variables a future theme may omit.
  for (const entry of catalog) {
    for (const key of Object.keys(entry.tokens ?? {})) root.style.removeProperty(key)
  }
  for (const [key, token] of Object.entries(theme.tokens ?? {})) root.style.setProperty(key, token)
  root.dataset.theme = theme.id
  root.style.colorScheme = theme.colorScheme
  root.style.setProperty('--app-background', theme.background)
  if (theme.tokens) root.dataset.themePalette = ''
  else delete root.dataset.themePalette
  if (theme.libraryOrnament) {
    root.dataset.themeLibraryOrnament = ''
    root.style.setProperty('--theme-library-corner-mask', `url("${theme.libraryOrnament.cornerMask}")`)
    root.style.setProperty('--theme-library-ornament-opacity', String(theme.libraryOrnament.opacity ?? .55))
  } else {
    delete root.dataset.themeLibraryOrnament
    root.style.removeProperty('--theme-library-corner-mask')
    root.style.removeProperty('--theme-library-ornament-opacity')
  }
  return theme
}

export function rememberTheme(
  theme: ThemeDefinition,
  getStorage: () => Pick<Storage, 'setItem'> = () => window.localStorage
): boolean {
  try {
    getStorage().setItem(themeStorageKey, JSON.stringify({
      id: theme.id, colorScheme: theme.colorScheme, background: theme.background
    }))
    return true
  } catch {
    // A blocked/full browser store must not prevent switching the current UI.
    return false
  }
}
