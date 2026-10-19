import { atom, read, update } from 'claude-code'
import type { Register, EngineInterface } from 'claude-code'

import type { AgentTeamBoard, AgentTeamMode, AgentTeamNode } from '../types'

const PANE = 'agent-team'
const MAIN = 'main'
const COMMAND = 'agent-team'

const board = atom({ plugin: 'agent-team', key: 'board' } as const, {
  nodes: {},
} as AgentTeamBoard)

// ---------------------------------------------------------------------------
// State helpers
// ---------------------------------------------------------------------------

function leadNode(now: number): AgentTeamNode {
  return {
    id: MAIN,
    name: 'Claude',
    type: 'lead',
    description: 'main conversation',
    status: 'lead',
    isTeammate: false,
    isListed: true,
    mode: 'idle',
    activity: 'waiting for you',
    updatedAt: now,
  }
}

function preview(text: string, max = 70): string {
  const flat = text.replace(/\s+/g, ' ').trim()
  return flat.length > max ? flat.slice(0, max - 1) + '…' : flat
}

async function setNode(
  $: EngineInterface,
  id: string,
  change: (node: AgentTeamNode) => Partial<AgentTeamNode>,
): Promise<void> {
  const now = await $.clock.now()
  await update($, board, b => {
    const existing =
      b.nodes[id] ??
      (id === MAIN
        ? leadNode(now)
        : {
            id,
            type: 'agent',
            description: '',
            status: 'running',
            isTeammate: false,
            isListed: false,
            mode: 'idle' as AgentTeamMode,
            activity: '',
            updatedAt: now,
          })
    return {
      ...b,
      nodes: { ...b.nodes, [id]: { ...existing, ...change(existing), updatedAt: now } },
    }
  })
}

function modeForStatus(status: string): AgentTeamMode {
  switch (status) {
    case 'running':
      return 'thinking'
    case 'waiting':
      return 'waiting'
    case 'completed':
      return 'done'
    case 'failed':
    case 'killed':
      return 'failed'
    default:
      return 'idle'
  }
}

async function syncFromList($: EngineInterface): Promise<void> {
  const list = await $.agent.list()
  const now = await $.clock.now()
  await update($, board, b => {
    const nodes: Record<string, AgentTeamNode> = { ...b.nodes }
    if (!nodes[MAIN]) nodes[MAIN] = leadNode(now)
    const seen = new Set<string>([MAIN])
    let changed = !b.nodes[MAIN]

    for (const info of list) {
      seen.add(info.id)
      const old = nodes[info.id]
      const merged: AgentTeamNode = {
        id: info.id,
        name: info.name,
        type: info.type,
        description: info.description,
        status: info.status,
        parentId: info.parentId,
        isTeammate: info.teammateId !== undefined,
        isListed: true,
        mode: old?.mode ?? modeForStatus(info.status),
        activity: old?.activity ?? (info.status === 'pending' ? 'starting' : ''),
        updatedAt: old?.updatedAt ?? now,
      }
      // An ended agent that our events never closed out gets its final mode.
      if (
        (info.status === 'completed' || info.status === 'failed' || info.status === 'killed') &&
        old &&
        old.mode !== 'done' &&
        old.mode !== 'failed'
      ) {
        merged.mode = modeForStatus(info.status)
        merged.activity = info.status
        merged.updatedAt = now
      }
      if (!old || JSON.stringify(old) !== JSON.stringify(merged)) {
        nodes[info.id] = merged
        changed = true
      }
    }

    for (const id of Object.keys(nodes)) {
      if (!seen.has(id) && nodes[id]!.isListed) {
        nodes[id] = { ...nodes[id]!, isListed: false, updatedAt: now }
        changed = true
      }
    }

    return changed ? { ...b, nodes } : b
  })
}

// ---------------------------------------------------------------------------
// Rebuilding the board from the agent-feed record after a restart
// ---------------------------------------------------------------------------

type FeedLine = Record<string, unknown>

async function readFeed($: EngineInterface): Promise<FeedLine[]> {
  const home =
    (await $.env.get('USERPROFILE')) ?? (await $.env.get('HOME')) ?? $.plugin.root.replace(/[\\/]\.claude[\\/].*$/, '')
  const dir = `${home.replace(/[\\/]+$/, '')}/.claude/agent-feed/${await $.session.id()}`
  if (!(await $.fs.exists(dir))) return []
  const chunks = (await $.fs.list(dir))
    .map(e => /^(\d{6})\.jsonl$/.exec(e.name)?.[1])
    .filter((n): n is string => n !== undefined)
    .sort()
  const out: FeedLine[] = []
  for (const n of chunks) {
    let text: string
    try {
      text = await $.fs.read(`${dir}/${n}.jsonl`)
    } catch {
      continue
    }
    for (const line of text.split(String.fromCharCode(10))) {
      if (line.length === 0) continue
      try {
        const ev = JSON.parse(line) as FeedLine
        if (ev && typeof ev.k === 'string') out.push(ev)
      } catch {
        // skip malformed lines
      }
    }
  }
  return out
}

function str(v: unknown): string | undefined {
  return typeof v === 'string' ? v : undefined
}

async function rebuildFromFeed($: EngineInterface): Promise<void> {
  const events = await readFeed($)
  if (events.length === 0) return
  const now = await $.clock.now()
  const found = new Map<string, AgentTeamNode>()
  for (const ev of events) {
    if (ev.k === 'agent') {
      const id = str(ev.id)
      if (!id || id === MAIN) continue
      const old = found.get(id)
      const isTeammate = ev.isTeammate === true
      const status = str(ev.status) ?? 'idle'
      const finished = status === 'completed' || status === 'failed' || status === 'killed'
      const parentId = str(ev.parentId)
      found.set(id, {
        id,
        name: str(ev.name) ?? old?.name,
        type: str(ev.type) ?? old?.type ?? 'agent',
        description: str(ev.description) ?? old?.description ?? '',
        // Not running in this process: teammates can be resumed, so they sit idle.
        status: finished && !isTeammate ? status : 'idle',
        parentId: parentId && parentId !== MAIN ? parentId : undefined,
        isTeammate,
        isListed: false,
        mode: old?.mode ?? 'idle',
        activity: old?.activity ?? '',
        updatedAt: now,
      })
      continue
    }
    const agent = str(ev.agent)
    const node = agent ? found.get(agent) : undefined
    if (!node) continue
    if (ev.k === 'turn' && ev.phase === 'end') {
      const ok = ev.reason === 'answer'
      node.mode = ok ? 'done' : 'failed'
      node.activity = ok ? `answered: ${preview(str(ev.answer) ?? '', 50)}` : `ended: ${str(ev.reason) ?? 'unknown'}`
    } else if (ev.k === 'turn' && ev.phase === 'start') {
      node.mode = 'idle'
      node.activity = `last turn: ${preview(str(ev.text) ?? '', 50)}`
    } else if (ev.k === 'tool' && ev.phase === 'start') {
      // Not running now whatever the record says: keep it as idle.
      node.mode = 'idle'
      node.activity = `last: ${preview(str(ev.summary) ?? str(ev.name) ?? '', 60)}`
    }
  }
  if (found.size === 0) return
  await update($, board, b => {
    const nodes: Record<string, AgentTeamNode> = { ...b.nodes }
    for (const [id, node] of found) if (!nodes[id]) nodes[id] = node
    return { ...b, nodes }
  })
}

// ---------------------------------------------------------------------------
// Describing a tool call in a few words
// ---------------------------------------------------------------------------

function describeTool(tool: string, args: Record<string, unknown>): string {
  const s = (k: string) => (typeof args[k] === 'string' ? (args[k] as string) : undefined)
  switch (tool) {
    case 'Bash':
    case 'PowerShell':
      return `${tool}: ${s('description') ?? s('command') ?? ''}`
    case 'Read':
      return `reading ${s('file_path') ?? ''}`
    case 'Edit':
      return `editing ${s('file_path') ?? ''}`
    case 'Write':
      return `writing ${s('file_path') ?? ''}`
    case 'Glob':
      return `globbing ${s('pattern') ?? ''}`
    case 'Grep':
      return `searching ${s('pattern') ?? ''}`
    case 'WebFetch':
      return `opening ${s('url') ?? ''}`
    case 'WebSearch':
      return `searching web: ${s('query') ?? ''}`
    case 'Agent':
      return `spawning agent: ${s('name') ?? s('description') ?? ''}`
    case 'SendMessage':
      return `messaging ${s('to') ?? ''}`
    case 'Skill':
      return `skill ${s('skill') ?? ''}`
    default: {
      const firstString = Object.entries(args).find(
        ([k, v]) => typeof v === 'string' && k !== 'tool' && k !== 'tool_use_id' && k !== 'agentId',
      )
      return firstString ? `${tool}: ${firstString[1] as string}` : tool
    }
  }
}

// ---------------------------------------------------------------------------
// Register
// ---------------------------------------------------------------------------

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    await $.command.register({
      name: COMMAND,
      description: 'Show the agent team pane (lead + spawned agents, live status)',
    })
    await setNode($, MAIN, () => ({}))
    await rebuildFromFeed($).catch(() => {})
    void $.ui.open({ id: PANE, title: 'Agent team' })
    $.clock.every(1500, () => {
      void syncFromList($).catch(() => {})
    })
    return next(e)
  })

  on('command.run', { command: COMMAND }, async $ => {
    await $.ui.open({ id: PANE, title: 'Agent team', focus: true })
    await syncFromList($)
    return { text: 'Agent team pane opened.' }
  })

  // A new agent: record it as soon as the spawn resolves.
  on('agent.spawn', async ($, e, next) => {
    const ran = await next(e)
    if (ran.agentId !== undefined) {
      const id = ran.agentId
      await setNode($, id, () => ({
        name: e.name,
        type: e.subagentType,
        description: e.description,
        parentId: e.parentAgentId,
        isTeammate: e.isTeammate === true,
        isListed: true,
        status: 'running',
        mode: 'thinking',
        activity: `started: ${preview(e.description || e.prompt, 50)}`,
      }))
    }
    return ran
  }).catch(($, e, next) => next(e))

  on('turn.start', async ($, e, next) => {
    const id = e.agentId ?? MAIN
    await setNode($, id, () => ({
      status: id === MAIN ? 'lead' : 'running',
      mode: 'thinking',
      activity: e.text ? `turn: ${preview(e.text, 50)}` : 'working',
    }))
    return next(e)
  })

  // The model's response streaming: thinking / writing / about to call a tool.
  on('turn.step', async function* ($, e, next) {
    const id = e.agentId ?? MAIN
    const stream = next(e)
    let lastMode: AgentTeamMode | null = null
    let textBuffer = ''
    let textWrites = 0

    while (true) {
      const step = await stream.next()
      if (step.done) return step.value
      const chunk = step.value
      yield chunk

      try {
        if (chunk.kind === 'thinking') {
          if (lastMode !== 'thinking') {
            lastMode = 'thinking'
            await setNode($, id, () => ({ mode: 'thinking', activity: 'thinking…' }))
          }
        } else if (chunk.kind === 'text') {
          textBuffer += chunk.text
          const shouldWrite =
            lastMode !== 'responding' ||
            (textWrites < 3 && textBuffer.length > textWrites * 40)
          if (shouldWrite) {
            lastMode = 'responding'
            textWrites += 1
            const snapshot = textBuffer
            await setNode($, id, () => ({
              mode: 'responding',
              activity: `writing: ${preview(snapshot, 50)}`,
            }))
          }
        } else if (chunk.kind === 'tool') {
          lastMode = 'tool'
          const name = chunk.name
          await setNode($, id, () => ({ mode: 'tool', activity: `calling ${name}` }))
        }
      } catch {
        // Never let bookkeeping break the model's stream.
      }
    }
  })

  on('tool.call', async ($, e, next) => {
    const id = e.agentId ?? MAIN
    const tool = String(e.tool)
    const label = preview(describeTool(tool, e as unknown as Record<string, unknown>), 70)
    await setNode($, id, () => ({ mode: 'tool', activity: label }))
    const ran = await next(e)
    const failed = ran.deny !== undefined || ran.isError === true
    await setNode($, id, () => ({
      mode: 'thinking',
      activity: failed ? `failed: ${label}` : `finished: ${label}`,
    }))
    return ran
  }).catch(($, e, next) => next(e))

  on('turn.complete', async ($, e, next) => {
    const id = e.agentId ?? MAIN
    if (id === MAIN) {
      await setNode($, id, () => ({
        mode: 'idle',
        activity: e.isAborted ? 'interrupted' : 'waiting for you',
      }))
    } else {
      const ok = e.reason === 'answer'
      await setNode($, id, () => ({
        mode: ok ? 'done' : 'failed',
        activity: ok ? `answered: ${preview(e.answer, 50)}` : `ended: ${e.reason}`,
      }))
    }
    return next(e)
  })

  // ------------------------------------------------------------------------
  // Drawing the pane
  // ------------------------------------------------------------------------

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e) => {
    const { Box, Text, Button } = $.ui.resolve(e)
    const { nodes } = await read($, board)
    const width = Math.max(20, e.props.bodyColumns)
    const viewed = e.props.view.agentId ?? MAIN

    // Depth-first order under the lead; orphans (parent unknown) at depth 1.
    const all = Object.values(nodes).filter(n => n.id !== MAIN)
    const byParent = new Map<string, AgentTeamNode[]>()
    for (const n of all) {
      const parent = n.parentId && nodes[n.parentId] ? n.parentId : MAIN
      const list = byParent.get(parent) ?? []
      list.push(n)
      byParent.set(parent, list)
    }
    const ordered: Array<{ node: AgentTeamNode; depth: number }> = []
    const walk = (parent: string, depth: number) => {
      for (const n of byParent.get(parent) ?? []) {
        ordered.push({ node: n, depth })
        walk(n.id, depth + 1)
      }
    }
    walk(MAIN, 1)

    // ● green = done / idle and ready for more work; ⚙ = working; ● red = failed.
    const glyph = (n: AgentTeamNode): string => {
      switch (n.mode) {
        case 'tool':
        case 'thinking':
        case 'responding':
          return '⚙'
        case 'failed':
          return '●'
        case 'done':
          return '● ✓'
        default:
          return '●'
      }
    }

    // Muted palette shared with the Agent Team Viewer: dim cyan frames, soft status colours.
    const CYAN = '#4FD9FF'
    const CYAN_DIM = '#2A7A99'
    const colorFor = (n: AgentTeamNode): string => {
      switch (n.mode) {
        case 'failed':
          return '#FF5C7A'
        case 'done':
        case 'idle':
        case 'waiting':
          return '#4CE0A6'
        case 'tool':
          return '#FFB347'
        default:
          return '#7FA6B8'
      }
    }

    const card = (n: AgentTeamNode, depth: number) => {
      const isViewed = n.id === viewed
      const indent = Math.min(depth - 1, 6) * 2
      const inner = Math.max(10, width - indent - 4)
      const title = n.id === MAIN ? 'LEAD · Claude' : (n.name ?? n.description ?? n.type)
      const sub =
        n.id === MAIN
          ? ''
          : [n.isTeammate ? 'teammate' : n.type, n.status, n.isListed || n.status === 'idle' ? '' : 'gone']
              .filter(Boolean)
              .join(' · ')
      const onPress = async () => {
        const shown = await (n.id === MAIN ? $.ui.view({}) : $.ui.view({ agentId: n.id }))
        if (shown.deny) $.ui.toast(`agent-team: ${shown.deny}`)
      }
      return (
        <Box
          key={`card:${n.id}`}
          flexDirection="column"
          marginLeft={indent}
          paddingX={1}
          borderStyle={n.id === MAIN ? 'double' : 'round'}
          borderColor={isViewed ? CYAN : CYAN_DIM}
          borderDimColor={!isViewed}
          width={width - indent}
        >
          <Button key={`view:${n.id}`} plain onPress={onPress}>
            <Text color={colorFor(n)}>{glyph(n)} </Text>
            <Text weight="bold" color={isViewed ? CYAN : '#D8F3FF'}>{preview(title, Math.max(6, inner - 4))}</Text>
            {sub ? <Text dimColor> {sub}</Text> : ''}
          </Button>
          <Text dimColor={n.mode === 'idle'} wrap="truncate-end">
            {preview(n.activity || '—', inner)}
          </Text>
          {isViewed && n.id !== MAIN ? (
            <Text dimColor italic>
              viewing · click LEAD to return
            </Text>
          ) : (
            ''
          )}
        </Box>
      )
    }

    const lead = nodes[MAIN]
    return (
      <Box
        flexDirection="column"
        gap={0}
        backgroundColor="#1E1F24"
        width={width}
        minHeight={Math.max(1, e.props.scroll.bodyRows)}
      >
        <Box justifyContent="space-between" width={width}>
          <Text weight="bold" color={CYAN_DIM}>Agent team</Text>
          <Text dimColor>
            {ordered.length} agent{ordered.length === 1 ? '' : 's'}
          </Text>
        </Box>
        {lead ? card(lead, 1) : <Text dimColor>starting…</Text>}
        {ordered.length === 0 && (
          <Text dimColor>No agents spawned yet. Spawned agents appear here.</Text>
        )}
        {ordered.map(({ node, depth }) => card(node, depth + 1))}
      </Box>
    )
  })
}
