export interface CodexStatus {
  providerId: string
  configPath: string
  configExists: boolean
  configBroken: boolean
  liveProvider: string | null
  liveProviderMatches: boolean
  liveModel: string | null
  live: {
    tableExists: boolean
    baseUrl: string | null
    wireApi: string | null
    requiresOpenaiAuth: boolean
    supportsWebSockets: boolean | null
    userAgent: string | null
    originator: string | null
    hasAuthCommand: boolean
    authCommand: string | null
    foreignKeys: string[]
    isHybridForm: boolean
    providerMatches: boolean
  } | null
  externalChanged: boolean
  authType: string
  codexRunning: boolean
  activeConnectionId: string | null
  credentialExePath: string
}

export interface CodexConnectionView {
  id: string
  name: string
  kind: 'official' | 'relay'
  baseUrl: string
  defaultModel: string
  supportsWebSockets: boolean
  userAgent: string
  originator: string
  keySet: boolean
  usageBaseUrl: string
  active: boolean
}

export interface AuthProfileView {
  id: string
  name: string
  email: string
  plan: string
  accountId: string
  createdAt: string
  updatedAt: string
  active: boolean
}

export interface AuthProfileLive {
  authType: string
  email: string
  plan: string
  importable: boolean
}

export interface ConfigRow {
  key: string
  rowKind: 'relay' | 'official-account'
  title: string
  subtitle: string
  isCurrent: boolean
  canDelete: boolean
  connectionId?: string
  profileId?: string
  keySet?: boolean
  savedUnapplied?: boolean
}

export interface RelayForm {
  name: string
  baseUrl: string
  defaultModel: string
  supportsWebSockets: boolean
  userAgent: string
  originator: string
  apiKey: string
  usageBaseUrl: string
  keySet: boolean
}

export type RelayFieldErrors = Partial<Record<keyof RelayForm, string>>
export interface CodexSnapshot {
  status: CodexStatus
  connections: CodexConnectionView[]
  profiles: { profiles: AuthProfileView[]; live: AuthProfileLive }
}
export interface WriteResult { ok: boolean; restartRequired?: boolean; note?: string; error?: string }
