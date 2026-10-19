using System.IO;
using AgentTeamViewer.Feed;
using AgentTeamViewer.Model;

namespace AgentTeamViewer.ViewModels;

/// <summary>
/// One row of the session picker: a feed directory (openable), or a live Claude Code session
/// that has no feed directory yet (shown dim and disabled).
/// </summary>
public sealed class SessionPickerItem : ObservableObject
{
    public SessionPickerItem(SessionInfo? feed, SessionRecord? record)
    {
        Feed = feed;
        Record = record;
        SessionId = feed?.SessionId ?? record?.SessionId ?? "";
    }

    public SessionInfo? Feed { get; }
    public SessionRecord? Record { get; }
    public string SessionId { get; }

    private bool _isCurrent;
    /// <summary>True for the row whose directory is the feed currently open in the viewer.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (Set(ref _isCurrent, value)) Raise(nameof(CanDelete)); }
    }

    /// <summary>A feed that is not the one being shown can be deleted from the picker.</summary>
    public bool CanDelete => HasFeed && !IsCurrent;

    /// <summary>Set by the owner (MainViewModel); invoked when the row's delete button is clicked.</summary>
    public Action<SessionPickerItem>? DeleteRequested { get; set; }
    public void RequestDelete() => DeleteRequested?.Invoke(this);

    public bool HasFeed => Feed is not null;
    public string? Directory => Feed?.Directory;
    public bool IsLive => Record?.IsLive ?? false;
    public bool IsBusy => Record?.IsBusy ?? false;

    /// <summary>The session's real name when the registry knows it, else the cwd leaf, else the short id.</summary>
    public string Name => Record?.Name is { Length: > 0 } n ? n : (CwdLeaf is { Length: > 0 } c ? c : ShortId);

    public string ShortId => SessionId.Length > 8 ? SessionId[..8] : SessionId;

    public string? Cwd => Record?.Cwd ?? Feed?.Cwd;
    public string CwdLeaf => string.IsNullOrEmpty(Cwd) ? "" : Path.GetFileName(Cwd.TrimEnd('\\', '/'));

    /// <summary>"name · short-id · cwd leaf", or "name · no feed yet (run /reload-plugins there)".</summary>
    public string Label => HasFeed
        ? string.IsNullOrEmpty(CwdLeaf) || CwdLeaf == Name ? $"{Name} · {ShortId}" : $"{Name} · {ShortId} · {CwdLeaf}"
        : $"{Name} · no feed yet (run /reload-plugins there)";

    /// <summary>Feed mtime for sorting; live no-feed rows use their record's updatedAt.</summary>
    public long SortTime => Feed is not null
        ? new DateTimeOffset(Feed.MetaMtimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds()
        : Record?.UpdatedAt ?? 0;

    public bool SameDirectory(string? dir) =>
        dir is not null && Directory is not null && string.Equals(dir, Directory, StringComparison.OrdinalIgnoreCase);
}
