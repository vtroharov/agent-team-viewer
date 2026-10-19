import { test, expect, mock } from 'claude-code/testing'

type Line = Record<string, unknown>

test('the feed records spawn, turn, tool, message and prompt events as JSONL chunks', async ($, on) => {
  const clock = mock.clock(on, { now: 1_000_000 })
  mock.env(on, { USERPROFILE: 'C:/Users/test' })

  // A fake disk beneath the plugin.
  // The engine hands paths with the platform separator; keep one spelling.
  const norm = (p: string) => p.split('\\').join('/')
  const files = new Map<string, string>()
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
        .map(k => ({ name: k.slice(p.length + 1), kind: 'file' as const, size: files.get(k)!.length, mtimeMs: 0, isLink: false })),
    }
  })
  on('fs.read', (_$, e) => ({ value: files.get(norm(e.path)) ?? '' }))
  on('session.id', () => ({ value: 'sess-1' }))
  on('session.cwd', () => ({ value: 'C:/proj' }))
  on('agent.list', () => ({ value: [] }))
  on('session.start', (_$, e) => ({ cwd: e.cwd }))
  on('agent.spawn', () => ({ model: 'm', agentId: 'a1', teammateId: 'scout@team' }))
  on('turn.start', (_$, e) => ({ turnId: e.turnId }))
  on('turn.complete', () => ({ text: '' }))
  on('tool.call', () => ({ result: 'ok', text: '> 42 passing' }))

  await $.session.start({ cwd: 'C:/proj', surface: 'terminal', isInteractive: true })

  const metaPath = 'C:/Users/test/.claude/agent-feed/sess-1/meta.json'
  expect([...files.keys()]).toContain(metaPath)
  expect(JSON.parse(files.get(metaPath)!).sessionId).toBe('sess-1')

  await $.agent.spawn({
    tool_use_id: 'toolu_spawn',
    prompt: 'Find the bug',
    description: 'scout the repo',
    name: 'scout',
    subagentType: 'Explore',
    provider: { plugin: 'engine', tier: 'core' },
    parentModel: 'm',
    background: true,
    fork: false,
  })
  await $.turn.start({ text: 'Find the bug', turnId: 't1', agentId: 'a1' })
  await $.tool.call({ tool: 'Bash', command: 'npm test', description: 'run the tests' })
  await $.tool.call({ tool: 'SendMessage', to: 'scout', message: 'Also check the lexer' })
  await $.turn.complete({ answer: 'Found it', durationMs: 5, isAborted: false, turnId: 't1', agentId: 'a1', reason: 'answer' })

  // The flush timer runs on the mocked clock.
  await clock.advance(200)

  const chunkPath = 'C:/Users/test/.claude/agent-feed/sess-1/000001.jsonl'
  expect(files.has(chunkPath)).toBe(true)
  const lines: Line[] = files
    .get(chunkPath)!
    .split('\n')
    .filter(l => l.length > 0)
    .map(l => JSON.parse(l) as Line)

  const kinds = lines.map(l => `${l.k}${l.phase ? ':' + l.phase : ''}`)
  expect(kinds[0]).toBe('session')
  expect(kinds).toContain('agent')
  expect(kinds).toContain('turn:start')
  expect(kinds).toContain('tool:start')
  expect(kinds).toContain('tool:end')
  expect(kinds).toContain('message')
  expect(kinds).toContain('turn:end')

  const agent = lines.find(l => l.k === 'agent')!
  expect(agent.id).toBe('a1')
  expect(agent.name).toBe('scout')
  expect(agent.isTeammate).toBe(true)
  expect(agent.parentId).toBe('main')
  expect(agent.model).toBe('m')

  const toolStart = lines.find(l => l.k === 'tool' && l.phase === 'start' && l.name === 'Bash')!
  expect(toolStart.agent).toBe('main')
  expect(toolStart.summary).toBe('Bash: run the tests')
  expect((toolStart.input as Line).command).toBe('npm test')

  const toolEnd = lines.find(l => l.k === 'tool' && l.phase === 'end' && l.name === 'Bash')!
  expect(toolEnd.text).toBe('> 42 passing')
  expect(toolEnd.isError).toBe(false)

  const message = lines.find(l => l.k === 'message')!
  expect(message.from).toBe('main')
  expect(message.to).toBe('a1')
  expect(message.toName).toBe('scout')
  expect(message.text).toBe('Also check the lexer')

  const turnEnd = lines.find(l => l.k === 'turn' && l.phase === 'end')!
  expect(turnEnd.agent).toBe('a1')
  expect(turnEnd.answer).toBe('Found it')
  expect(turnEnd.reason).toBe('answer')

  // Every line is well-formed JSON with t and k.
  for (const l of lines) {
    expect(typeof l.t).toBe('number')
    expect(typeof l.k).toBe('string')
  }
})

test('after a restart a SendMessage to a remembered name reaches the engine with the agent id', async ($, on) => {
  mock.clock(on, { now: 2_000_000 })
  mock.env(on, { USERPROFILE: 'C:/Users/test' })

  const norm = (p: string) => p.split('\\').join('/')
  const dir = 'C:/Users/test/.claude/agent-feed/sess-2'
  const files = new Map<string, string>([
    [`${dir}/meta.json`, '{}'],
    [
      `${dir}/000001.jsonl`,
      [
        JSON.stringify({ t: 1, k: 'session', sessionId: 'sess-2' }),
        JSON.stringify({ t: 2, k: 'agent', id: 'a1', name: 'viewer-core', type: 'general-purpose', parentId: 'main', isTeammate: true, status: 'running' }),
        'not json',
      ].join('\n') + '\n',
    ],
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
  on('session.id', () => ({ value: 'sess-2' }))
  on('session.cwd', () => ({ value: 'C:/proj' }))
  on('agent.list', () => ({ value: [] }))
  on('session.start', (_$, e) => ({ cwd: e.cwd }))
  const seen: unknown[] = []
  on('tool.call', (_$, e) => {
    seen.push((e as unknown as { to?: string }).to)
    return { result: 'ok', text: 'sent' }
  })

  await $.session.start({ cwd: 'C:/proj', surface: 'terminal', isInteractive: true })

  await $.tool.call({ tool: 'SendMessage', to: 'viewer-core', message: 'hello again' })
  await $.tool.call({ tool: 'SendMessage', to: 'unknown-guy', message: 'x' })
  await $.tool.call({ tool: 'SendMessage', to: 'team-lead', message: 'x' })
  await $.tool.call({ tool: 'SendMessage', to: 'a1', message: 'x' })
  expect(seen).toEqual(['a1', 'unknown-guy', 'team-lead', 'a1'])
})
