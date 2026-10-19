# Agent feed: the contract between the `agent-feed` mod and the viewer app

The `agent-feed` Claude Code mod publishes everything the engine sees about the
session's agents. The viewer app reads it. Neither side knows anything else about
the other. This file is the contract; change it here first.

## Where the feed lives

```
~/.claude/agent-feed/         (%USERPROFILE%\.claude\agent-feed\ on Windows)
  <sessionId>/
    meta.json          { "sessionId", "cwd", "startedAt", "pid"? }   written once
    000001.jsonl       events, one JSON object per line, in order
    000002.jsonl       ...
```

`~` is `%USERPROFILE%` on Windows and `$HOME` on macOS.

- A session directory is created at session start. The viewer should open the
  session directory with the **newest `meta.json` mtime** by default and offer a
  picker for older ones.
- Events go into numbered chunk files. The writer appends events to the **last**
  chunk by rewriting that file whole (the plugin API has no append). A chunk is
  closed when it reaches ~400 events or ~256 KiB, and the next number starts.
  Closed chunks never change again.
- **Tailing rule for the viewer:** list `*.jsonl`, read every chunk you have not
  seen fully, and re-read only the last chunk on change (compare its length; the
  new content is a strict prefix-extension of the old, so you can diff by line
  count). Poll every 100–200 ms or use a FileSystemWatcher plus a poll fallback.
- Lines are UTF-8, `\n` terminated. A partially written last line can occur
  mid-rewrite: ignore any final line that fails to parse and retry next poll.

## Identity

- Every event has `t` (Unix ms) and `k` (kind).
- Agents are addressed by `agent`, an opaque id string. The lead (the main
  conversation, i.e. Claude talking to the user) is always the literal `"main"`.
- Teammates may also have a `name` (what SendMessage addresses them by). Names
  are not unique over time; ids are.

## Event kinds

### `session` — once, first line of chunk 000001
```json
{"t":0,"k":"session","sessionId":"abc","cwd":"C:\\Users\\me\\proj","version":"2.1.301"}
```

### `agent` — an agent exists or its facts changed
Emitted when an agent is spawned, and again whenever the roster sync sees a
change (name, status, parent). Treat it as an upsert keyed by `id`.
```json
{"t":0,"k":"agent","id":"a1","name":"scout","type":"Explore","description":"scout the repo",
 "parentId":"main","isTeammate":false,"status":"running"}
```
`status` ∈ `pending | running | waiting | idle | completed | failed | killed | gone`.
`model` (optional) is the model id the agent runs on (`claude-opus-5-5`), known at
spawn; absent on roster-sync upserts, so keep the last value you saw.
`gone` means the engine dropped it from the roster. `parentId` is `"main"` when
the lead spawned it, else another agent id. The lead itself is never sent as an
`agent` event; the viewer creates it implicitly.

### `turn` — a model turn starts or ends
```json
{"t":0,"k":"turn","agent":"a1","phase":"start","turnId":"t1","text":"Find the bug"}
{"t":0,"k":"turn","agent":"a1","phase":"end","turnId":"t1","reason":"answer","answer":"Found it in tokenize()","durationMs":8120,"isAborted":false}
```
A third phase, `step`, is emitted once per turn when its first model request is sent and
carries `model`: `{"k":"turn","agent":"main","phase":"step","turnId":"t1","model":"claude-opus-5-5"}`.
It is how the lead's (`main`) model becomes known. Ignore it otherwise.

`reason` ∈ `answer | aborted | refusal | error`. `text` is the prompt the turn
began with (the user's message for `main`; the task or incoming message for an
agent). For `main`, `phase:"end"` means Claude is now waiting for the user.

### `chunk` — streamed model output
The model's response as it streams, coalesced: the writer buffers per agent and
per type and flushes at most every ~150 ms, so one `chunk` may hold many tokens.
```json
{"t":0,"k":"chunk","agent":"a1","type":"thinking","text":"The parser fails when..."}
{"t":0,"k":"chunk","agent":"a1","type":"text","text":"I found the issue. "}
```
Each chunk may carry `model`, the model that produced it.
Consecutive chunks of the same `agent` and `type` are one continuous stream:
concatenate them. A `turn start`, a `tool start` or a change of `type` begins a
new block visually.

### `tool` — a tool call starts or ends
```json
{"t":0,"k":"tool","agent":"a1","phase":"start","id":"toolu_1","name":"Bash",
 "summary":"Bash: run the tests","input":{"command":"npm test"}}
{"t":0,"k":"tool","agent":"a1","phase":"end","id":"toolu_1","name":"Bash",
 "isError":false,"durationMs":2300,"text":"> 42 passing\n"}
```
`summary` is a one-line human description (what the compact cards show).
`input` is the tool's arguments, JSON-trimmed to ~4000 chars. `text` on `end`
is the result as text, trimmed to ~4000 chars (`truncated: true` when cut).
Pair start/end by `id`.

### `row` — a conversation row that is not model output
User prompts, messages delivered from other agents, notices. These are what
make the lead's panel read as a conversation.
```json
{"t":0,"k":"row","agent":"main","door":"prompt","role":"user","text":"Set up the viewer"}
{"t":0,"k":"row","agent":"main","door":"delivery","role":"user","from":"scout","text":"Found it in tokenize()"}
{"t":0,"k":"row","agent":"a1","door":"notice","text":"compaction boundary"}
```
`door` ∈ `prompt | delivery | notice | note | hook-context | attachment | command`.
Model response rows are NOT sent here (they arrive as `chunk`s); tool results
are not sent here (they arrive as `tool end`).

### `message` — one agent addressed another
Emitted when a SendMessage tool call is made. This is the event that drives the
connector animation: a glow travels from `from` to `to`.
```json
{"t":0,"k":"message","from":"main","to":"a1","toName":"scout","text":"Also check the lexer"}
```
`to` is an agent id when the writer could resolve the name, else `null` with
`toName` set. `from` is `"main"` or an agent id. A `delivery` row on the
receiver follows shortly after and can be used to confirm arrival.

## Control channel

The app cannot stop agents itself. It writes requests to a control file in the
session's feed directory and the agent-feed mod carries them out.

`<feed dir>/control.jsonl`: the app appends one JSON line per request:
```json
{"t":0,"k":"dismiss","id":"<request uuid>","agent":"<agent id>"}
```
The mod polls the file every second and handles each request `id` exactly once
(it rebuilds the handled set from the feed's `control` events on session start,
so a reload never repeats one). The app must use a fresh `id` per click.

Progress comes back as ordinary feed events:
```json
{"t":0,"k":"control","id":"<request id>","agent":"a1","status":"accepted"}
{"t":0,"k":"control","id":"<request id>","agent":"a1","status":"failed","reason":"..."}
{"t":0,"k":"dismissed","agent":"a1","id":"<request id>","summary":"...","summarySource":"agent"}
```
Sequence for one request: `control accepted`, then either `control failed`
(with `reason`; the agent is left as it was) or `dismissed` followed by an
`agent` event for that agent with `status:"dismissed"`. `summarySource` is
`"agent"` (the agent answered the dismissal prompt within 90 s) or
`"last-answer"` (its last turn-end answer on record). On success the mod also
appends the summary to the lead's conversation without waking it and shows a
toast. A dismissed agent is not overwritten by later roster syncs.

Dismissing a parent does not stop its children.

## Viewer expectations (so both sides agree on semantics)

- The lead panel shows, in order: `row door=prompt` (user, highlighted),
  `chunk thinking` (dim), `chunk text` (normal), `tool start/end` (one-liners,
  expandable), `row door=delivery` (incoming from an agent, tagged with `from`).
- An agent card's "last 2–3 lines" come from its most recent `chunk`/`tool`/`turn`
  events. Its status glyph comes from the latest `agent.status` or, between
  roster syncs, from events: `tool start` → working, `chunk thinking` → thinking,
  `turn end` → done/idle.
- Connector topology: an edge from `parentId` to `id` for every agent. A
  `message` event pulses the edge between `from` and `to`; if no parent edge
  exists between them, draw a temporary dashed edge for the pulse.
- Unknown `k` values must be ignored, not treated as errors. Unknown fields too.
