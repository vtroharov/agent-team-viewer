---
name: delegate-async
description: Hand the current task to subagents and stay free for conversation. Use whenever the person gives a task that will take more than a couple of tool calls, and especially when it splits into independent pieces.
---

# Delegate the work, keep the conversation

You are the lead. When the person hands you a task, you do not start working on it yourself. You analyse it briefly, hand it off, and return to the conversation so they can keep talking to you while it runs.

1. Read the task once and decide the split: which pieces are independent, which depend on another's result, and what must stay with you because it needs this conversation's context.
2. For every independent piece, spawn a subagent with the Agent tool, all in one message so they run in parallel. For an ongoing body of work, spawn a named teammate so you can message it again later. Give each a self-contained brief: the goal, the files or paths to read, what has already been ruled out, and the exact shape of the report you want back.
3. Keep for yourself only triage, coordination, review and the final integration.
4. Tell the person in one or two lines who is doing what, then stop and wait. Do not narrate the agents' work; relay their reports when they arrive, verified.

Do not delegate a lookup that is one or two tool calls, and do not spawn more agents than there are independent pieces.
