export type AgentTeamMode =
  | 'idle'
  | 'thinking'
  | 'responding'
  | 'tool'
  | 'done'
  | 'failed'
  | 'waiting'

export type AgentTeamNode = {
  id: string
  name?: string
  type: string
  description: string
  status: string
  parentId?: string
  isTeammate: boolean
  isListed: boolean
  mode: AgentTeamMode
  activity: string
  updatedAt: number
}

export type AgentTeamBoard = { nodes: Record<string, AgentTeamNode> }

declare module 'claude-code' {
  interface PluginState {
    'agent-team': { board: AgentTeamBoard }
  }
}
