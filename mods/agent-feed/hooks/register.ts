import type { Register, EngineInterface, AgentInfo } from 'claude-code'

// agent-feed: publishes the session's agent activity as JSONL chunk files.
// The format is the contract in FEED.md of the Agent Team Viewer:
//   <home>/.claude/agent-feed/<sessionId>/meta.json
//   <home>/.claude/agent-feed/<sessionId>/000001.jsonl, 000002.jsonl, ...
// The plugin API writes whole files, so the current chunk is rewritten on each
// flush and closed at a size limit; closed chunks never change.

const MAIN = 'main'
const FLUSH_MS = 150
const SYNC_MS = 2000
const CHUNK_EVENTS = 400
const CHUNK_BYTES = 256 * 1024
const TEXT_CAP = 4000
const INPUT_CAP = 4000

type FeedEvent = Record<string, unknown> & { t: number; k: string }

// Appended to the system prompt in every session this mod is loaded in.
const DELEGATE_ASYNC = [
  '# Delegating asynchronous work',
  '',
  'When a task splits into pieces that do not depend on each other, or into work that can run',
  'while you continue, spawn subagents (the Agent tool) for those pieces and run them in parallel,',
  'in the same message, instead of doing them one after another yourself. Give each a narrow,',
  'self-contained brief and the files it needs. Keep for yourself the parts that depend on each',
  'other, the final integration, and anything that needs your conversation context. Do not',
  'delegate a lookup that is one or two tool calls.',
  '',
  'When the person hands you a task, do not start working on it yourself. Analyse it briefly,',
  'decide the split, hand it to one or more subagents or teammates with a clear brief, and come',
  'back to the conversation so the person can keep talking to you while the work runs. You do',
  'triage, coordination, review and integration; the agents do the work. Tell the person in one',
  'or two lines who is doing what, and report when the agents report.',
].join(String.fromCharCode(10))

type Writer = {
  dir: string
  index: number
  lines: string[]
  bytes: number
}

// ---------------------------------------------------------------------------
// Module state (lost on hot reload; the chunk on disk is re-read at start)
// ---------------------------------------------------------------------------

let writer: Writer | null = null
let pending: FeedEvent[] = []
const chunkBuffers = new Map<string, { agent: string; type: 'thinking' | 'text'; text: string; model?: string }>()
const roster = new Map<string, string>() // last emitted agent JSON by id
const names = new Map<string, string>() // name -> id
const toolStarts = new Map<string, number>() // tool_use_id -> started at
let flushing: Promise<void> | null = null
let dirty = false

function pad(n: number): string {
  return String(n).padStart(6, '0')
}

function chunkPath(w: Writer, index = w.index): string {
  return `${w.dir}/${pad(index)}.jsonl`
}

function cap(text: string, max: number): { text: string; truncated?: true } {
  return text.length > max ? { text: text.slice(0, max), truncated: true } : { text }
}

function trimInput(args: Record<string, unknown>): unknown {
  const copy: Record<string, unknown> = {}
  for (const [k, v] of Object.entries(args)) {
    if (k === 'tool' || k === 'tool_use_id' || k === 'agentId' || k === 'requestMeta') continue
    copy[k] = v
  }
  let json = JSON.stringify(copy)
  if (json.length <= INPUT_CAP) return copy
  // Too large: cap each string field instead of dropping the whole input.
  for (const [k, v] of Object.entries(copy)) {
    if (typeof v === 'string' && v.length > 400) copy[k] = v.slice(0, 400) + '…'
  }
  json = JSON.stringify(copy)
  return json.length <= INPUT_CAP ? copy : { _truncated: true, keys: Object.keys(copy) }
}

function textOf(blocks: readonly { type: string; [k: string]: unknown }[]): string {
  return blocks
    .filter(b => b.type === 'text' && typeof b.text === 'string')
    .map(b => b.text as string)
    .join('\n')
}

function describeTool(tool: string, args: Record<string, unknown>): string {
  const s = (k: string) => (typeof args[k] === 'string' ? (args[k] as string) : undefined)
  const one = (t: string) => t.replace(/\s+/g, ' ').trim().slice(0, 120)
  switch (tool) {
    case 'Bash':
    case 'PowerShell':
      return one(`${tool}: ${s('description') ?? s('command') ?? ''}`)
    case 'Read':
      return one(`reading ${s('file_path') ?? ''}`)
    case 'Edit':
      return one(`editing ${s('file_path') ?? ''}`)
    case 'Write':
      return one(`writing ${s('file_path') ?? ''}`)
    case 'Glob':
      return one(`globbing ${s('pattern') ?? ''}`)
    case 'Grep':
      return one(`searching ${s('pattern') ?? ''}`)
    case 'WebFetch':
      return one(`opening ${s('url') ?? ''}`)
    case 'WebSearch':
      return one(`searching web: ${s('query') ?? ''}`)
    case 'Agent':
      return one(`spawning agent: ${s('name') ?? s('description') ?? ''}`)
    case 'SendMessage':
      return one(`messaging ${s('to') ?? ''}`)
    case 'Skill':
      return one(`skill ${s('skill') ?? ''}`)
    default: {
      const first = Object.entries(args).find(
        ([k, v]) => typeof v === 'string' && !['tool', 'tool_use_id', 'agentId'].includes(k),
      )
      return one(first ? `${tool}: ${first[1] as string}` : tool)
    }
  }
}

function resolveTarget(to: string | undefined): { to: string | null; toName?: string } {
  if (!to) return { to: null }
  const lower = to.toLowerCase()
  if (lower === MAIN || lower === 'lead' || lower === 'team-lead' || lower === 'leader') return { to: MAIN, toName: to }
  const bare = to.split('@')[0] ?? to
  const id = names.get(to) ?? names.get(bare)
  return id ? { to: id, toName: to } : { to: null, toName: to }
}

// A name the engine no longer lists but the feed remembers: the agent id, so
// the engine resumes that teammate from its transcript. Otherwise undefined.
async function resumableId($: EngineInterface, to: string): Promise<string | undefined> {
  const lower = to.toLowerCase()
  if (lower === MAIN || lower === 'lead' || lower === 'team-lead' || lower === 'leader') return undefined
  const bare = to.split('@')[0] ?? to
  const id = names.get(to) ?? names.get(bare)
  if (!id || id === to) return undefined
  const list = await $.agent.list()
  const live = list.some(
    a => a.id === to || a.name === to || a.name === bare || a.teammateId === to || a.teammateId === bare,
  )
  return live ? undefined : id
}

function senderOf(text: string): string | undefined {
  const m =
    /teammate_id="([^"]+)"/.exec(text) ??
    /\bfrom\s+(?:teammate|agent)\s+["']?([\w@.-]+)/i.exec(text) ??
    /^\s*\[?([\w-]+)\]?\s*(?:says|sent|wrote)\b/i.exec(text)
  return m?.[1]
}

// ---------------------------------------------------------------------------
// Writing
// ---------------------------------------------------------------------------

function emit(event: Omit<FeedEvent, 't'> & { t?: number }, now: number): void {
  pending.push({ ...event, t: event.t ?? now } as FeedEvent)
  dirty = true
}

function drainChunkBuffers(now: number): void {
  for (const buf of chunkBuffers.values()) {
    if (buf.text.length > 0) emit({ k: 'chunk', agent: buf.agent, type: buf.type, text: buf.text, model: buf.model }, now)
  }
  chunkBuffers.clear()
}

async function flush($: EngineInterface): Promise<void> {
  if (flushing) return flushing
  flushing = (async () => {
    try {
      const w = writer
      if (!w) return
      const now = await $.clock.now()
      drainChunkBuffers(now)
      if (!dirty && pending.length === 0) return
      dirty = false
      const batch = pending
      pending = []
      for (const ev of batch) {
        const line = JSON.stringify(ev)
        if (w.lines.length >= CHUNK_EVENTS || w.bytes + line.length + 1 > CHUNK_BYTES) {
          // Close the current chunk: it is already on disk whole from the last write.
          await $.fs.write(chunkPath(w), w.lines.join('\n') + (w.lines.length ? '\n' : ''))
          w.index += 1
          w.lines = []
          w.bytes = 0
        }
        w.lines.push(line)
        w.bytes += line.length + 1
      }
      await $.fs.write(chunkPath(w), w.lines.join('\n') + '\n')
    } finally {
      flushing = null
    }
  })()
  return flushing
}

// Rebuild name -> id from the agent events already on disk, so a restarted
// process can still address teammates spawned before it.
async function loadNames($: EngineInterface, dir: string, chunks: number[]): Promise<void> {
  for (const n of chunks) {
    let text: string
    try {
      text = await $.fs.read(`${dir}/${pad(n)}.jsonl`)
    } catch {
      continue
    }
    for (const line of text.split('\n')) {
      if (!line.includes('"agent"') && !line.includes('"control"') && !line.includes('"dismissed"') && !line.includes('"turn"')) continue
      try {
        const ev = JSON.parse(line) as Record<string, unknown>
        if ((ev.k === 'control' || ev.k === 'dismissed') && typeof ev.id === 'string') {
          handled.add(ev.id)
          if (ev.k === 'dismissed' && typeof ev.agent === 'string') dismissed.add(ev.agent)
          continue
        }
        if (ev.k === 'turn' && ev.phase === 'end' && typeof ev.agent === 'string' && typeof ev.answer === 'string') {
          lastAnswers.set(ev.agent, ev.answer)
          continue
        }
        if (ev.k !== 'agent' || typeof ev.id !== 'string' || ev.id === MAIN) continue
        known.set(ev.id, ev)
        if (ev.status === 'dismissed') dismissed.add(ev.id)
        if (typeof ev.name === 'string' && ev.name) names.set(ev.name, ev.id)
      } catch {
        // skip malformed lines
      }
    }
  }
}

async function openWriter($: EngineInterface): Promise<Writer> {
  const home =
    (await $.env.get('USERPROFILE')) ?? (await $.env.get('HOME')) ?? $.plugin.root.replace(/[\\/]\.claude[\\/].*$/, '')
  const sessionId = await $.session.id()
  const dir = `${home.replace(/[\\/]+$/, '')}/.claude/agent-feed/${sessionId}`
  const w: Writer = { dir, index: 1, lines: [], bytes: 0 }
  controlDir = dir

  const now = await $.clock.now()
  if (await $.fs.exists(dir)) {
    // Resume (hot reload or restarted process): continue the last chunk.
    const entries = await $.fs.list(dir)
    const chunks = entries
      .map(e => /^(\d{6})\.jsonl$/.exec(e.name)?.[1])
      .filter((n): n is string => n !== undefined)
      .map(n => Number(n))
      .sort((a, b) => a - b)
    await loadNames($, dir, chunks)
    const last = chunks[chunks.length - 1]
    if (last !== undefined) {
      w.index = last
      try {
        const text = await $.fs.read(chunkPath(w, last))
        const lines = text.split('\n').filter(l => l.length > 0)
        w.lines = lines
        w.bytes = lines.reduce((n, l) => n + l.length + 1, 0)
      } catch {
        w.lines = []
        w.bytes = 0
      }
    }
  } else {
    const version = await $.session.version().catch(() => undefined)
    const cwd = await $.session.cwd().catch(() => '')
    await $.fs.write(
      `${dir}/meta.json`,
      JSON.stringify({ sessionId, cwd, startedAt: now, version: version?.version }, null, 2) + '\n',
    )
    pending.unshift({ t: now, k: 'session', sessionId, cwd, version: version?.version })
    dirty = true
  }
  return w
}

// ---------------------------------------------------------------------------
// Roster
// ---------------------------------------------------------------------------

function agentEvent(info: AgentInfo): Record<string, unknown> {
  return {
    k: 'agent',
    id: info.id,
    name: info.name,
    type: info.type,
    description: info.description,
    parentId: info.parentId ?? MAIN,
    isTeammate: info.teammateId !== undefined,
    status: info.status,
  }
}

async function syncRoster($: EngineInterface): Promise<void> {
  const list = await $.agent.list()
  const now = await $.clock.now()
  const seen = new Set<string>()
  for (const info of list) {
    seen.add(info.id)
    if (dismissed.has(info.id)) continue
    if (info.name) names.set(info.name, info.id)
    if (info.teammateId) names.set(info.teammateId, info.id)
    const ev = agentEvent(info)
    const json = JSON.stringify(ev)
    if (roster.get(info.id) !== json) {
      roster.set(info.id, json)
      emit(ev, now)
    }
  }
  for (const [id, json] of roster) {
    if (seen.has(id) || dismissed.has(id)) continue
    const prev = JSON.parse(json) as Record<string, unknown>
    if (prev.status === 'gone') continue
    const ev = { ...prev, status: 'gone' }
    roster.set(id, JSON.stringify(ev))
    emit(ev, now)
  }
}

// ---------------------------------------------------------------------------
// Control channel: dismiss requests written by the viewer app (see FEED.md)
// ---------------------------------------------------------------------------

const CONTROL_POLL_MS = 1000
const SUMMARY_WAIT_MS = 90_000
const SUMMARY_ASK =
  'You are being dismissed by the user. Reply with at most 6 lines: what you did, what is unfinished, and anything the lead must know. Do no other work.'

const handled = new Set<string>() // control request ids already processed
const dismissed = new Set<string>() // agent ids dismissed (roster must not overwrite)
const known = new Map<string, Record<string, unknown>>() // last agent event by id, from disk
const lastAnswers = new Map<string, string>() // agent id -> last turn-end answer
const summaryWaiters = new Map<string, (answer: string) => void>() // agent id -> resolver
let controlDir: string | null = null
let polling = false

function agentRecord(id: string): Record<string, unknown> {
  const live = roster.get(id)
  if (live) return JSON.parse(live) as Record<string, unknown>
  return known.get(id) ?? { k: 'agent', id, parentId: MAIN, isTeammate: false }
}

function waitForSummary($: EngineInterface, agent: string): { promise: Promise<string | null>; cancel: () => void } {
  let timer: { cancel: () => void } | undefined
  const promise = new Promise<string | null>(resolve => {
    summaryWaiters.set(agent, answer => resolve(answer))
    timer = $.clock.after(SUMMARY_WAIT_MS, () => resolve(null))
  })
  return {
    promise,
    cancel: () => {
      summaryWaiters.delete(agent)
      timer?.cancel()
    },
  }
}

async function dismissAgent($: EngineInterface, reqId: string, agent: string): Promise<void> {
  const fail = async (reason: string) => {
    emit({ k: 'control', id: reqId, agent, status: 'failed', reason }, await $.clock.now())
  }
  emit({ k: 'control', id: reqId, agent, status: 'accepted' }, await $.clock.now())

  const rec = agentRecord(agent)
  const name = typeof rec.name === 'string' && rec.name ? rec.name : agent

  // 1. The agent's own summary, else its last answer on record.
  let summary: string | null = null
  let summarySource: 'agent' | 'last-answer' = 'agent'
  const wait = waitForSummary($, agent)
  try {
    const sent = await $.tool.call({ tool: 'SendMessage', to: agent, message: SUMMARY_ASK })
    if (sent.deny === undefined && sent.isError !== true) summary = await wait.promise
  } catch {
    // unreachable agent: fall back to its last answer
  }
  wait.cancel()
  if (!summary || summary.trim().length === 0) {
    summary = lastAnswers.get(agent) ?? '(no summary available)'
    summarySource = 'last-answer'
  }

  // 2. Stop it. A refusal is fine when the agent is no longer live anyway.
  let stopError: string | undefined
  try {
    const ran = await $.tool.call({ tool: 'TaskStop', task_id: agent })
    if (ran.deny !== undefined) stopError = ran.deny
    else if (ran.isError === true) stopError = typeof ran.text === 'string' && ran.text ? ran.text : 'TaskStop failed'
  } catch (err) {
    stopError = err instanceof Error ? err.message : String(err)
  }
  if (stopError !== undefined) {
    const list = await $.agent.list().catch(() => [])
    const live = list.find(a => a.id === agent)
    if (live && live.status !== 'completed' && live.status !== 'failed' && live.status !== 'killed') {
      // The lead still gets the summary; the agent stays as it was.
      await $.session
        .append({
          message: {
            type: 'user',
            content: [
              {
                type: 'text',
                text: `[The user asked to dismiss ${name} (${agent}) from the Agent Team Viewer; stopping it failed: ${stopError}. Its summary:]\n${summary}`,
              },
            ],
          },
        })
        .catch(() => undefined)
      await fail(`could not stop agent: ${stopError}`)
      return
    }
  }

  // 3. Tell the lead without waking it.
  try {
    await $.session.append({
      message: {
        type: 'user',
        content: [
          {
            type: 'text',
            text: `[The user dismissed agent ${name} (${agent}) from the Agent Team Viewer. Its summary:]\n${summary}`,
          },
        ],
      },
    })
  } catch (err) {
    await fail(`stopped, but delivering the summary failed: ${err instanceof Error ? err.message : String(err)}`)
    return
  }
  $.ui.toast(`Dismissed ${name}`)

  const now = await $.clock.now()
  dismissed.add(agent)
  emit({ k: 'dismissed', agent, id: reqId, summary, summarySource }, now)
  const upsert = { ...agentRecord(agent), k: 'agent', status: 'dismissed' }
  roster.set(agent, JSON.stringify(upsert))
  emit(upsert, now)
}

async function pollControl($: EngineInterface): Promise<void> {
  if (polling || !controlDir) return
  polling = true
  try {
    const path = `${controlDir}/control.jsonl`
    if (!(await $.fs.exists(path))) return
    const text = await $.fs.read(path)
    for (const line of text.split('\n')) {
      if (!line.includes('"dismiss"')) continue
      let req: Record<string, unknown>
      try {
        req = JSON.parse(line) as Record<string, unknown>
      } catch {
        continue
      }
      if (req.k !== 'dismiss' || typeof req.id !== 'string' || typeof req.agent !== 'string') continue
      if (handled.has(req.id)) continue
      handled.add(req.id)
      const reqId = req.id
      const agent = req.agent
      // Each request runs on its own so one slow summary does not hold the others.
      void dismissAgent($, reqId, agent).catch(async err => {
        emit(
          { k: 'control', id: reqId, agent, status: 'failed', reason: err instanceof Error ? err.message : String(err) },
          await $.clock.now(),
        )
      })
    }
  } catch {
    // a missing or unreadable control file is no error
  } finally {
    polling = false
  }
}

// ---------------------------------------------------------------------------
// Register
// ---------------------------------------------------------------------------

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    writer = await openWriter($)
    await flush($)
    $.clock.every(FLUSH_MS, () => {
      void flush($).catch(() => {})
    })
    $.clock.every(SYNC_MS, () => {
      void syncRoster($).catch(() => {})
    })
    $.clock.every(CONTROL_POLL_MS, () => {
      void pollControl($).catch(() => {})
    })
    return next(e)
  })

  on('agent.spawn', async ($, e, next) => {
    const ran = await next(e)
    if (ran.agentId !== undefined) {
      const now = await $.clock.now()
      if (e.name) names.set(e.name, ran.agentId)
      if (ran.teammateId) names.set(ran.teammateId, ran.agentId)
      const ev = {
        k: 'agent',
        id: ran.agentId,
        name: e.name,
        type: e.subagentType,
        description: e.description,
        parentId: e.parentAgentId ?? MAIN,
        isTeammate: e.isTeammate === true || ran.teammateId !== undefined,
        status: 'running',
        model: ran.model,
      }
      roster.set(ran.agentId, JSON.stringify(ev))
      emit(ev, now)
    }
    return ran
  }).catch(($, e, next) => next(e))

  on('turn.start', async ($, e, next) => {
    const now = await $.clock.now()
    const agent = e.agentId ?? MAIN
    // A turn boundary closes any streamed text still buffered for this agent.
    drainChunkBuffers(now)
    emit({ k: 'turn', agent, phase: 'start', turnId: e.turnId, text: e.text }, now)
    return next(e)
  })

  on('turn.step', async function* ($, e, next) {
    const agent = e.agentId ?? MAIN
    if (e.index === 0) emit({ k: 'turn', agent, phase: 'step', turnId: e.turnId, model: e.model }, await $.clock.now())
    const stream = next(e)
    while (true) {
      const step = await stream.next()
      if (step.done) return step.value
      const chunk = step.value
      yield chunk
      if (chunk.kind === 'thinking' || chunk.kind === 'text') {
        const key = `${agent}:${chunk.kind}`
        const other = `${agent}:${chunk.kind === 'text' ? 'thinking' : 'text'}`
        // Switching type: flush the other buffer first so order is kept.
        const prev = chunkBuffers.get(other)
        if (prev && prev.text.length > 0) {
          pending.push({ t: await $.clock.now(), k: 'chunk', agent, type: prev.type, text: prev.text, model: prev.model })
          dirty = true
          chunkBuffers.delete(other)
        }
        const buf = chunkBuffers.get(key) ?? { agent, type: chunk.kind, text: '', model: e.model }
        buf.text += chunk.text
        chunkBuffers.set(key, buf)
      }
    }
  })

  on('tool.call', async ($, e, next) => {
    const agent = e.agentId ?? MAIN
    const tool = String(e.tool)
    let call = e
    const args = e as unknown as Record<string, unknown>
    const id = e.tool_use_id
    let resolvedTo: string | undefined
    if (tool === 'SendMessage' && typeof args.to === 'string') {
      resolvedTo = await resumableId($, args.to).catch(() => undefined)
      if (resolvedTo) call = { ...e, to: resolvedTo } as typeof e
    }
    const startedAt = await $.clock.now()
    drainChunkBuffers(startedAt)
    toolStarts.set(id, startedAt)
    emit(
      { k: 'tool', agent, phase: 'start', id, name: tool, summary: describeTool(tool, args), input: trimInput(args) },
      startedAt,
    )
    if (tool === 'SendMessage') {
      const to = typeof args.to === 'string' ? args.to : undefined
      const text = typeof args.message === 'string' ? args.message : typeof args.text === 'string' ? args.text : ''
      const target = resolvedTo ? { to: resolvedTo, toName: to } : resolveTarget(to)
      emit({ k: 'message', from: agent, ...target, ...cap(text, 1000) }, startedAt)
    }

    const ran = await next(call)

    const endedAt = await $.clock.now()
    toolStarts.delete(id)
    const isError = ran.deny !== undefined || ran.isError === true
    const text = ran.deny ?? (typeof ran.text === 'string' ? ran.text : '')
    emit(
      { k: 'tool', agent, phase: 'end', id, name: tool, isError, durationMs: endedAt - startedAt, ...cap(text, TEXT_CAP) },
      endedAt,
    )
    return ran
  }).catch(($, e, next) => next(e))

  on('turn.complete', async ($, e, next) => {
    const now = await $.clock.now()
    const agent = e.agentId ?? MAIN
    drainChunkBuffers(now)
    if (e.agentId !== undefined) {
      lastAnswers.set(e.agentId, e.answer)
      summaryWaiters.get(e.agentId)?.(e.answer)
      summaryWaiters.delete(e.agentId)
    }
    const answer = cap(e.answer, TEXT_CAP)
    emit(
      {
        k: 'turn',
        agent,
        phase: 'end',
        turnId: e.turnId,
        reason: e.reason,
        isAborted: e.isAborted,
        durationMs: e.durationMs,
        answer: answer.text,
        truncated: answer.truncated,
      },
      now,
    )
    return next(e)
  })

  on('session.append', async ($, e, next) => {
    const door = e.door
    if (door === 'prompt' || door === 'delivery' || door === 'notice' || door === 'note' || door === 'hook-context' || door === 'attachment' || door === 'command') {
      const text = textOf(e.message.content)
      if (text.length > 0) {
        const now = await $.clock.now()
        const agent = e.agentId ?? MAIN
        const from = door === 'delivery' ? senderOf(text) : undefined
        const fromId = from ? (resolveTarget(from).to ?? undefined) : undefined
        emit(
          {
            k: 'row',
            agent,
            door,
            role: e.message.role,
            name: e.message.name,
            origin: e.origin.kind,
            from: fromId ?? from,
            ...cap(text, TEXT_CAP),
          },
          now,
        )
      }
    }
    return next(e)
  }).catch(($, e, next) => next(e))

  on('prompt.compose', async ($, e, next) => {
    const composed = await next(e)
    return {
      sections: [
        ...composed.sections,
        { id: 'agent-feed:delegate-async', text: DELEGATE_ASYNC, scope: 'session' as const },
      ],
    }
  })

  on('session.end', async ($, e, next) => {
    await flush($).catch(() => {})
    return next(e)
  })
}
