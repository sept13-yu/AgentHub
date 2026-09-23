export const font = {
  sans: '"Segoe UI Variable Text","Segoe UI","Microsoft YaHei UI","PingFang SC","Noto Sans SC","Source Han Sans SC",sans-serif',
  mono: '"IBM Plex Mono","Cascadia Mono",Consolas,ui-monospace,monospace',
} as const

export const scale = {
  fsCaption: '12px',
  fsSmall: '13px',
  fsBody: '14px',
  fsCard: '15px',
  fsTitle: '18px',
  fsPage: '20px',
  fsMetric: '22px',
  fsHero: '32px',
  sp1: '4px',
  sp2: '8px',
  sp3: '12px',
  sp4: '16px',
  sp5: '20px',
  sp6: '24px',
  sp7: '32px',
  rCard: '6px',
  rIn: '3px',
  rPill: '2px',
  hControl: '32px',
  hIconBtn: '32px',
  icon: '16px',
  hRow: '36px',
  padCard: '20px',
  padHead: '12px 20px',
  dur: '120ms',
} as const

export const tokens = {
  dark: {
    bg: '#18181C',
    bgSunken: '#101014',
    surface: '#1E1E22',
    surfaceHi: '#26262C',
    stroke: '#2E2E36',
    strokeStrong: '#3D3D48',
    text: '#FFFFFF',
    dim: 'rgba(255,255,255,.82)',
    faint: '#A6ACB4',
    accentSolid: '#2EC4B6',
    accentHover: '#3AD0C2',
    accentPressed: '#26A99D',
    onVivid: '#0B0E14',
    accentSoft: 'rgba(46,196,182,.16)',
    accentLine: 'rgba(46,196,182,.40)',
    focusRing: '#2EC4B6',
    focusRingOnAccent: '#F4F7F5',
    wash: 'rgba(255,255,255,.06)',
    washLine: 'rgba(255,255,255,.16)',
    segOnBg: 'rgba(255,255,255,.10)',
    overlay: 'rgba(0,0,0,.56)',
    disabledFg: 'rgba(255,255,255,.36)',
    disabledBg: 'rgba(255,255,255,.04)',
    disabledStroke: '#2E2E36',
    dotRing: 'transparent',
    ruleHi: 'inset 0 -1px 0 rgba(255,255,255,.05)',
    ok: '#34D399',
    okSoft: 'rgba(52,211,153,.16)',
    warn: '#F0A020',
    warnSoft: 'rgba(240,160,32,.16)',
    danger: '#E85A73',
    dangerSoft: 'rgba(232,90,115,.16)',
    info: '#4C9AFF',
    infoSoft: 'rgba(76,154,255,.16)',
    idle: '#9090A0',
    idleSoft: 'rgba(144,144,160,.16)',
    srcCodex: '#60A5FA',
    srcDsh: '#D8B4FE',
    srcCursor: '#8FD94A',
    srcWb: '#FFE44D',
    srcTrae: '#FB923C',
    srcZcode: '#E879F9',
    srcMimocode: '#FF6900',
    srcRelay: '#22D3EE',
    srcQoder: '#7C9CFF',
    srcQoderCn: '#28D858',
    srcGrok: '#F59E0B',
    srcMinimax: '#FB7185',
    srcAntigravity: '#818CF8',
    srcReasonix: '#38BDF8',
    srcDevin: '#C084FC',
    srcOpencode: '#2DD4BF',
    srcClaudeCode: '#E8A090',
    srcGeminiCli: '#7DD3FC',
    srcKiro: '#A3E635',
    srcCopilot: '#C4B5FD',
    srcKimiCode: '#34D399',
    srcCodebuddy: '#FBBF24',
    srcHermes: '#F0ABFC',
    srcOpenclaw: '#E11D48',
    srcEveryCode: '#4ADE80',
    srcAstudio: '#0EA5E9',
    srcOhMyPi: '#EA580C',
    srcOmo: '#14B8A6',
    srcPi: '#A78BFA',
    srcDots: '#F472B6',
  },
  light: {
    bg: '#F5F6F8',
    bgSunken: '#EBEEF1',
    surface: '#FFFFFF',
    surfaceHi: '#F7F8FA',
    stroke: '#E2E6EB',
    strokeStrong: '#C4CCD4',
    text: '#1B1F24',
    dim: '#4A525B',
    faint: '#626B76',
    accentSolid: '#117D76',
    accentHover: '#0E6B65',
    accentPressed: '#0A5A55',
    // 强调色加深后，压在其上的字必须转白：深字 3.95:1，白字 4.98:1
    onVivid: '#FFFFFF',
    accentSoft: 'rgba(17,125,118,.09)',
    accentLine: 'rgba(17,125,118,.32)',
    focusRing: '#117D76',
    focusRingOnAccent: '#0E6B65',
    wash: 'rgba(27,35,45,.05)',
    washLine: 'rgba(27,35,45,.16)',
    segOnBg: '#FFFFFF',
    overlay: 'rgba(18,24,32,.42)',
    disabledFg: 'rgba(27,31,36,.38)',
    disabledBg: 'rgba(27,35,45,.04)',
    disabledStroke: '#E2E6EB',
    dotRing: 'rgba(27,35,45,.22)',
    ruleHi: 'inset 0 -1px 0 rgba(27,35,45,.04)',
    ok: '#0F766E',
    okSoft: 'rgba(15,118,110,.10)',
    warn: '#B45309',
    warnSoft: 'rgba(180,83,9,.10)',
    danger: '#C81E4A',
    dangerSoft: 'rgba(200,30,74,.10)',
    info: '#1864C8',
    infoSoft: 'rgba(24,100,200,.10)',
    idle: '#626B76',
    idleSoft: 'rgba(98,107,118,.10)',
    srcCodex: '#2563EB',
    srcDsh: '#7C3AED',
    srcCursor: '#437F12',
    srcWb: '#8A6D00',
    srcTrae: '#C2410C',
    srcZcode: '#A21CAF',
    srcMimocode: '#A83A05',
    srcRelay: '#0E7490',
    srcQoder: '#4338CA',
    srcQoderCn: '#15803D',
    srcGrok: '#B45309',
    srcMinimax: '#BE123C',
    srcAntigravity: '#4338CA',
    srcReasonix: '#0369A1',
    srcDevin: '#7E22CE',
    srcOpencode: '#0F766E',
    srcClaudeCode: '#9A3412',
    srcGeminiCli: '#0369A1',
    srcKiro: '#3F6212',
    srcCopilot: '#5B21B6',
    srcKimiCode: '#047857',
    srcCodebuddy: '#A16207',
    srcHermes: '#86198F',
    srcOpenclaw: '#9F1239',
    srcEveryCode: '#166534',
    srcAstudio: '#075985',
    srcOhMyPi: '#7C2D12',
    srcOmo: '#115E59',
    srcPi: '#4C1D95',
    srcDots: '#9D174D',
  },
} as const

export type ThemeName = keyof typeof tokens

export const cssAliases = {
  accentFg: 'on-vivid',
  loadingTrack: 'wash',
  loadingBar: 'accent-solid',
  emptyFg: 'faint',
  emptyIcon: 'idle',
  errorFg: 'danger',
  errorSoft: 'danger-soft',
} as const

const toastAlias = {
  dark: { toastBg: 'surface-hi', toastStroke: 'stroke-strong', toastFg: 'text' },
  light: { toastBg: 'surface', toastStroke: 'stroke-strong', toastFg: 'text' },
} as const

export type NaiveOverrides = {
  common: Record<string, string | number>
  Button: Record<string, string>
}

function kebab(name: string): string {
  return name
    .replace(/[A-Z]/g, (ch) => `-${ch.toLowerCase()}`)
    .replace(/([a-z])(\d)/g, '$1-$2')
}

export function cssVarMap(theme: ThemeName): Record<string, string> {
  const set = tokens[theme]
  const out: Record<string, string> = {}
  for (const [key, value] of Object.entries(scale)) {
    out[`--${kebab(key)}`] = value
  }
  out['--font'] = font.sans
  out['--mono'] = font.mono
  for (const [key, value] of Object.entries(set)) {
    out[`--${kebab(key)}`] = value
  }
  for (const [key, ref] of Object.entries(cssAliases)) {
    out[`--${kebab(key)}`] = `var(--${ref})`
  }
  for (const [key, ref] of Object.entries(toastAlias[theme])) {
    out[`--${kebab(key)}`] = `var(--${ref})`
  }
  return out
}

export function applyCssVars(el: HTMLElement, theme: ThemeName): void {
  el.dataset.theme = theme
  const map = cssVarMap(theme)
  for (const [name, value] of Object.entries(map)) {
    el.style.setProperty(name, value)
  }
}

function toneSeries(color: string) {
  return {
    Color: color,
    ColorHover: color,
    ColorPressed: color,
    ColorSuppl: color,
  }
}

export function naiveOverrides(theme: ThemeName): NaiveOverrides {
  const t = tokens[theme]
  return {
    common: {
      fontFamily: font.sans,
      fontFamilyMono: font.mono,
      fontSize: scale.fsBody,
      fontSizeSmall: scale.fsSmall,
      fontSizeTiny: scale.fsCaption,
      heightMedium: scale.hControl,
      borderRadius: scale.rIn,
      borderRadiusSmall: scale.rPill,
      primaryColor: t.accentSolid,
      primaryColorHover: t.accentHover,
      primaryColorPressed: t.accentPressed,
      primaryColorSuppl: t.accentHover,
      ...prefixSeries('success', toneSeries(t.ok)),
      ...prefixSeries('warning', toneSeries(t.warn)),
      ...prefixSeries('error', toneSeries(t.danger)),
      ...prefixSeries('info', toneSeries(t.info)),
      textColorBase: t.text,
      textColor2: t.dim,
      textColor3: t.faint,
      bodyColor: t.bg,
      cardColor: t.surface,
      modalColor: t.surface,
      popoverColor: t.surface,
      borderColor: t.stroke,
      dividerColor: t.stroke,
      hoverColor: t.wash,
      pressedColor: t.segOnBg,
      inputColor: t.surfaceHi,
      tableColor: t.surface,
      boxShadow1: 'none',
      boxShadow2: 'none',
      boxShadow3: 'none',
    },
    Button: {
      textColorPrimary: t.onVivid,
      textColorHoverPrimary: t.onVivid,
      textColorPressedPrimary: t.onVivid,
      textColorFocusPrimary: t.onVivid,
      textColorDisabledPrimary: t.disabledFg,
      colorDisabledPrimary: t.disabledBg,
      borderDisabledPrimary: `1px solid ${t.disabledStroke}`,
      textColorError: '#FFFFFF',
      textColorHoverError: '#FFFFFF',
      textColorPressedError: '#FFFFFF',
      textColorFocusError: '#FFFFFF',
      textColorDisabledError: t.disabledFg,
      colorDisabledError: t.disabledBg,
      borderDisabledError: `1px solid ${t.disabledStroke}`,
      opacityDisabled: '1',
    },
  }
}

function prefixSeries(prefix: string, series: ReturnType<typeof toneSeries>): Record<string, string> {
  return {
    [`${prefix}Color`]: series.Color,
    [`${prefix}ColorHover`]: series.ColorHover,
    [`${prefix}ColorPressed`]: series.ColorPressed,
    [`${prefix}ColorSuppl`]: series.ColorSuppl,
  }
}
