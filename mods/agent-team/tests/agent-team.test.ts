import { test, expect, mock } from 'claude-code/testing'

const PANE_PROPS = {
  title: 'Agent team',
  isFocused: false,
  bodyColumns: 60,
  placement: 'dock',
  scroll: { offset: 0, bodyRows: 30 },
  view: {},
} as const

test('the pane shows the lead, each spawned agent and its current step, and a press views its transcript', async ($, on) => {
  // The engine beneath the plugin: what each call settles to in a session.
  mock.clock(on, { now: 1_000_000 })
  on('agent.spawn', () => ({ model: 'm', agentId: 'a1', teammateId: 'scout@team' }))
  on('agent.list', () => ({ value: [] }))
  on('turn.start', (_$, e) => ({ turnId: e.turnId }))
  on('turn.complete', () => ({ text: '' }))
  on('tool.call', () => ({ result: 'ok', text: 'ok' }))
  let viewed: unknown = 'untouched'
  on('ui.view', (_$, e) => {
    viewed = e
    return { value: {} }
  })

  await $.agent.spawn({
    prompt: 'Find the bug in the parser',
    description: 'scout the repo',
    name: 'scout',
    subagentType: 'Explore',
  })
  await $.turn.start({ text: 'Find the bug in the parser', turnId: 't1', agentId: 'a1' })
  // A tool call with no agentId lands on the main loop: the lead's step.
  await $.tool.call({ tool: 'WebFetch', url: 'https://example.com', prompt: 'read it' })

  for (const surface of ['terminal', 'desktop'] as const) {
    const ui = await $.ui.mount({
      plugin: 'agent-team',
      surface,
      component: 'Pane',
      props: PANE_PROPS,
      requestId: 'agent-team',
    })

    expect(await ui.find({ type: 'Button', key: 'view:main' })).toBeDefined()
    expect(await ui.find({ type: 'Text', text: /LEAD/ })).toBeDefined()
    expect(await ui.find({ type: 'Text', text: /finished: opening https:\/\/example\.com/ })).toBeDefined()

    expect(await ui.find({ type: 'Button', key: 'view:a1' })).toBeDefined()
    expect(await ui.find({ type: 'Text', text: /scout/ })).toBeDefined()
    expect(await ui.find({ type: 'Text', text: /turn: Find the bug in the parser/ })).toBeDefined()

    await ui.press({ key: 'view:a1' })
    expect(viewed).toEqual({ agentId: 'a1' })
    await ui.press({ key: 'view:main' })
    expect(viewed).toEqual({})

    await ui.unmount()
  }

  await $.turn.complete({
    answer: 'The bug is in tokenize()',
    durationMs: 10,
    isAborted: false,
    turnId: 't1',
    agentId: 'a1',
    reason: 'answer',
  })

  const ui = await $.ui.mount({
    plugin: 'agent-team',
    surface: 'terminal',
    component: 'Pane',
    props: PANE_PROPS,
    requestId: 'agent-team',
  })
  expect(await ui.find({ type: 'Text', text: /answered: The bug is in tokenize\(\)/ })).toBeDefined()
  await ui.unmount()
})

test('after a restart the pane rebuilds the board from the feed on disk', async ($, on) => {
  mock.clock(on, { now: 1_000_000 })
  mock.env(on, { USERPROFILE: 'C:/Users/test' })
  const norm = (p: string) => p.split('\\').join('/')
  const dir = 'C:/Users/test/.claude/agent-feed/sess-9'
  const files = new Map<string, string>([
    [
      `${dir}/000001.jsonl`,
      [
        { t: 1, k: 'agent', id: 'a1', name: 'viewer-core', type: 'general-purpose', description: 'core', parentId: 'main', isTeammate: true, status: 'running' },
        { t: 2, k: 'agent', id: 'a2', name: 'scout', type: 'Explore', description: 'scout', parentId: 'main', isTeammate: true, status: 'running' },
        { t: 3, k: 'turn', agent: 'a1', phase: 'end', reason: 'answer', answer: 'core is done' },
        { t: 4, k: 'tool', agent: 'a2', phase: 'start', id: 'x', name: 'Bash', summary: 'Bash: npm test' },
      ]
        .map(l => JSON.stringify(l))
        .join('\n') + '\nbroken line\n',
    ],
  ])
  on('fs.exists', (_$, e) => {
    const p = norm(e.path)
    return { value: [...files.keys()].some(k => k.startsWith(p + '/')) }
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
  on('session.id', () => ({ value: 'sess-9' }))
  on('agent.list', () => ({ value: [] }))
  on('session.start', (_$, e) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: undefined }))
  on('ui.open', () => ({ value: undefined }))

  await $.session.start({ cwd: 'C:/proj', surface: 'terminal', isInteractive: true })

  const ui = await $.ui.mount({
    plugin: 'agent-team',
    surface: 'terminal',
    component: 'Pane',
    props: PANE_PROPS,
    requestId: 'agent-team',
  })
  expect(await ui.find({ type: 'Button', key: 'view:a1' })).toBeDefined()
  expect(await ui.find({ type: 'Button', key: 'view:a2' })).toBeDefined()
  expect(await ui.find({ type: 'Text', text: /answered: core is done/ })).toBeDefined()
  expect(await ui.find({ type: 'Text', text: /last: Bash: npm test/ })).toBeDefined()
  await ui.unmount()
})
