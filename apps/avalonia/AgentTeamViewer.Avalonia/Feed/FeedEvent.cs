namespace AgentTeamViewer.Feed;

/// <summary>Base type for every line in the feed. <see cref="Kind"/> is the raw <c>k</c> value.</summary>
public abstract record FeedEvent(long T, string Kind);

public sealed record SessionEvent(long T, string? SessionId, string? Cwd, string? Version, string? Model)
    : FeedEvent(T, "session");

/// <summary><paramref name="Model"/> is optional: the LLM model id when the writer knows it.</summary>
public sealed record AgentEvent(
    long T, string Id, string? Name, string? Type, string? Description,
    string? ParentId, bool IsTeammate, string? Status, string? Model)
    : FeedEvent(T, "agent");

public sealed record TurnEvent(
    long T, string Agent, string Phase, string? TurnId, string? Text,
    string? Reason, string? Answer, long? DurationMs, bool IsAborted, string? Model)
    : FeedEvent(T, "turn");

public sealed record ChunkEvent(long T, string Agent, string Type, string Text, string? Model)
    : FeedEvent(T, "chunk");

public sealed record ToolEvent(
    long T, string Agent, string Phase, string Id, string? Name, string? Summary,
    string? InputJson, bool IsError, long? DurationMs, string? Text, bool Truncated)
    : FeedEvent(T, "tool");

public sealed record RowEvent(long T, string Agent, string Door, string? Role, string? From, string? Text)
    : FeedEvent(T, "row");

public sealed record MessageEvent(long T, string From, string? To, string? ToName, string? Text)
    : FeedEvent(T, "message");

/// <summary>The mod's answer to a control request: <paramref name="Status"/> is accepted | failed.</summary>
public sealed record ControlEvent(long T, string? Id, string Agent, string Status, string? Reason)
    : FeedEvent(T, "control");

/// <summary>An agent was dismissed; <paramref name="Summary"/> is what it reported before it was stopped.</summary>
public sealed record DismissedEvent(long T, string Agent, string? Id, string? Summary, string? SummarySource)
    : FeedEvent(T, "dismissed");

/// <summary>Any <c>k</c> the viewer does not know. Kept so counts stay honest; never an error.</summary>
public sealed record UnknownEvent(long T, string Kind, string RawJson) : FeedEvent(T, Kind);
