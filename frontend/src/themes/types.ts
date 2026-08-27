export interface ThemeArtwork {
  src: string
  anchor?: 'bottom-left' | 'bottom-right'
  /** Size relative to the visible canvas, never the workflow's zoom. */
  heightRatio?: number
  minHeight?: number
  maxHeight?: number
  opacity?: number
  interactionOpacity?: number
}

export interface ThemeDefinition {
  id: string
  name: string
  colorScheme: 'light' | 'dark'
  background: string
  /** Omit only for the original silver-white appearance. */
  tokens?: Readonly<Record<string, string>>
  artwork?: Readonly<ThemeArtwork>
  /** Four fixed node-library corners only; no title-bar or status-bar ornaments. */
  libraryOrnament?: Readonly<{ cornerMask: string; opacity?: number }>
}
