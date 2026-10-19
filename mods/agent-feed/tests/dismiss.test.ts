import { test, expect, mock } from 'claude-code/testing'

type Line = Record<string, unknown>

const DIR = 'C:/Users/test/.claude/agent-feed/sess-3'
const norm = (p: string) => p.split('\\').join('/')

function setup(on: Parameters<Parameters<typeof test>[1]>[1], control: string, failStop = false) {
  const files = new Map<string, string>([
    [`${DIR}/meta.json`, '{}'],
    [
      `${DIR}/000001.jsonl`,
      [
        { t: 1, k: 'session', sessionId: 'sess-3' },
        { t: 2, k: 'agent', id: 'a1', name: 'viewer-core', type: 'general-purpose', parentId: 'main', isTeammate: true, status: 'running' },
        { t: 3, k: 'turn', agent: 'a1', phase: 'end', reason: 'answer', answer: 'old answer' },
        { t: 4, k: 'control', id: 'req-old', agent: 'a9', status: 'accepted' },
      ]
        .map(l => JSON.stringify(l))
        .join('\n') + '\n',
    ],
    [`${DIR}/control.jsonl`, control],
  ])
  on('fs.write', (_$, e) => {
    files.set(norm(e.path), e.text)
    return { value: undefined }
  })
  on('fs.exists', (_$, e) => {
    const p = norm(e.path)
    return { value: files.has(p) || [...files.keys()].some(k => k.startsWith(p + '/')) }
  })
  on('fs.list', (_$, e) => {
    const p = norm(e.path)
    return {
      value: [...files.keys()]
        .filter(k => k.startsWith(p + '/'))
        .map(k => ({ name: k.slice(p.length + 1), kind: 'file' as const, size: 1, mtimeMs: 0, isLink: false })),
    }
  })
  on('fs.read', (_$, e) => ({ value: files.get(norm(e.path)) ?? '' }))
  on('session.id', () => ({ value: 'sess-3' }))
  on('session.cwd', () => ({ value: 'C:/proj' }))
  on('agent.list', () => ({
    value: failStop ? ([{ id: 'a1', name: 'viewer-core', description: '', type: 'general-purpose', status: 'running' }] as never) : [],
  }))
  on('session.start', (_$, e) => ({ cwd: e.cwd }))
  on('turn.complete', () => ({ text: '' }))
  const calls: Array<{ tool: string; to?: string; task_id?: string; message?: string }> = []
  on('tool.call', (_$, e) => {
    calls.push(e as unknown as { tool: string })
    if (failStop && (e as unknown as { tool: string }).tool === 'TaskStop') return { result: 'nope', text: 'task busy', isError: true } as never
    return { result: 'ok', text: 'ok' }
  })
  const appended: unknown[] = []
  on('session.append', (_$, e, next) => {
    appended.push(e)
    return next(e)
  })
  const toasts: string[] = []
  on('ui.toast', (_$, e) => {
    toasts.push((e as unknown as { text: string }).text)
    return { value: undefined } as never
  })
  const events = (): Line[] =>
    (files.get(`${DIR}/000001.jsonl`) ?? '')
      .split('\n')
      .filter(l => l.length > 0)
      .map(l => JSON.parse(l) as Line)
  return { calls, appended, toasts, events }
}

const REQ = JSON.stringify({ t: 5, k: 'dismiss', id: 'req-1', agent: 'a1' }) + '\n'
const OLD = JSON.stringify({ t: 5, k: 'dismiss', id: 'req-old', agent: 'a9' }) + '\n'

test('a dismiss request asks for a summary, stops the agent and tells the lead', async ($, on) => {
  const clock = mock.clock(on, { now: 3_000_000 })
  mock.env(on, { USERPROFILE: 'C:/Users/test' })
  const s = setup(on, OLD + REQ + REQ)

  await $.session.start({ cwd: 'C:/proj', surface: 'terminal', isInteractive: true })
  await clock.advance(1100)
  expect(s.calls.map(c => `${c.tool}:${c.to ?? c.task_id}`)).toEqual(['SendMessage:a1'])
  expect(s.calls[0]!.message).toContain('You are being dismissed by the user')

  await $.turn.complete({ answer: 'did X, Y unfinished', durationMs: 5, isAborted: false, turnId: 't9', agentId: 'a1', reason: 'answer' })
  await clock.advance(1100)

  // Stopped once, the old handled request and the duplicate were ignored.
  expect(s.calls.map(c => `${c.tool}:${c.to ?? c.task_id}`)).toEqual(['SendMessage:a1', 'TaskStop:a1'])
  expect(s.appended.length).toBe(1)
  const row = s.appended[0] as { message: { type: string; content: Array<{ text: string }> }; agentId?: string }
  expect(row.agentId).toBeUndefined()
  expect(row.message.content[0]!.text).toContain('dismissed agent viewer-core (a1)')
  expect(row.message.content[0]!.text).toContain('did X, Y unfinished')
  expect(s.toasts).toEqual(['Dismissed viewer-core'])

  const ev = s.events()
  const control = ev.filter(l => l.k === 'control' && l.id === 'req-1').map(l => l.status)
  expect(control).toEqual(['accepted'])
  const dismissed = ev.find(l => l.k === 'dismissed')!
  expect(dismissed.agent).toBe('a1')
  expect(dismissed.id).toBe('req-1')
  expect(dismissed.summarySource).toBe('agent')
  expect(dismissed.summary).toBe('did X, Y unfinished')
  const upsert = ev.filter(l => l.k === 'agent' && l.id === 'a1').pop()!
  expect(upsert.status).toBe('dismissed')
  expect(upsert.name).toBe('viewer-core')
  expect(ev.filter(l => l.k === 'control' && l.id === 'req-old').length).toBe(1)
})

test('without a reply in 90 s the last answer on record is the summary', async ($, on) => {
  const clock = mock.clock(on, { now: 3_000_000 })
  mock.env(on, { USERPROFILE: 'C:/Users/test' })
  const s = setup(on, REQ)

  await $.session.start({ cwd: 'C:/proj', surface: 'terminal', isInteractive: true })
  await clock.advance(1100)
  await clock.advance(91_000)

  const dismissed = s.events().find(l => l.k === 'dismissed')!
  expect(dismissed.summarySource).toBe('last-answer')
  expect(dismissed.summary).toBe('old answer')
  expect(s.calls.map(c => c.tool)).toEqual(['SendMessage', 'TaskStop'])
})

test('a failed stop of a live agent still delivers the summary but does not mark it dismissed', async ($, on) => {
  const clock = mock.clock(on, { now: 3_000_000 })
  mock.env(on, { USERPROFILE: 'C:/Users/test' })
  const s = setup(on, REQ, true)

  await $.session.start({ cwd: 'C:/proj', surface: 'terminal', isInteractive: true })
  await clock.advance(1100)
  await clock.advance(91_000)

  const row = s.appended[0] as { message: { content: Array<{ text: string }> } }
  expect(row.message.content[0]!.text).toContain('(a1) from the Agent Team Viewer; stopping it failed: task busy')
  const ev = s.events()
  expect(ev.some(l => l.k === 'dismissed')).toBe(false)
  const failed = ev.find(l => l.k === 'control' && l.status === 'failed')!
  expect(failed.reason).toContain('task busy')
})
