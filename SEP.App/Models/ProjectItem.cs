using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SEP.App.Models;

/// <summary>A category as shown on a project (name + colour); null when the project has none.</summary>
public sealed record CategoryInfo(string Id, string Name, string Color);

/// <summary>
/// Someone in a project: Source "local" = open in E3D on this PC (from the local handle table), "e3d" = a login
/// recorded in the project's E3D session register (read by a manual usage check).
/// </summary>
public sealed record ProjectSession(string User, string Computer, string Source, bool IsThisDevice, DateTime? Since);

/// <summary>Outcome of the last manual "who is in this project" check.</summary>
public enum UsageState { Unknown, Free, InUse, Unreachable }

/// <summary>A discovered E3D project (one evarsXXX.bat) as shown in the UI.</summary>
public partial class ProjectItem : ObservableObject
{
    /// <summary>Stable id shared with the Python tool: "proj_" + sha1(lower(bat_path))[:12].</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The XXX of evarsXXX.bat (e.g. AvevaPlantSample, mdu).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>AVEVA project code from the "set XXX000=" line (e.g. SAM); null when it could not be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge))]
    private string? _code;

    public string BatPath { get; init; } = string.Empty;

    /// <summary>Folder holding the project (its sub-folder in a library, or the library folder itself).</summary>
    public string ProjectDir { get; init; } = string.Empty;

    public string LibraryId { get; init; } = string.Empty;
    public string LibraryName { get; init; } = string.Empty;
    public bool IsUnc { get; init; }
    public DateTime? DiscoveredAt { get; init; }

    // ── metadata (project_meta, shared with the Python UI) ───────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(HasDisplayName))]
    private string? _displayName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOwner))]
    private string? _owner;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTags))]
    private IReadOnlyList<string> _tags = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    private string? _description;

    [ObservableProperty]
    private string? _notes;

    /// <summary>Stored status token (进行中 / 已完成 / 暂停 / 归档) or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCategory))]
    private CategoryInfo? _category;

    [ObservableProperty]
    private DateTime? _metaUpdatedAt;

    // ── state ────────────────────────────────────────────────────────────────────

    /// <summary>Listed in "my projects" (the Python UI's 我的项目).</summary>
    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>Custom group id in "my projects".</summary>
    [ObservableProperty]
    private string? _myGroupId;

    /// <summary>Custom group display name in "my projects".</summary>
    [ObservableProperty]
    private string? _myGroupName;

    /// <summary>Launched last (settings.last_launched).</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>True while the project comes from the cache of a library that could not be reached this session.</summary>
    [ObservableProperty]
    private bool _isCached;

    [ObservableProperty]
    private bool _isLocked;

    [ObservableProperty]
    private int _lockCount;

    [ObservableProperty]
    private List<string> _lockFiles = new();

    /// <summary>True once the lock probe has run for this project.</summary>
    [ObservableProperty]
    private bool _lockStateKnown;

    /// <summary>People connected right now (SEP heartbeats and lock owners); empty until probed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnlineCount))]
    [NotifyPropertyChangedFor(nameof(OnlineBadgeText))]
    [NotifyPropertyChangedFor(nameof(IsOnline))]
    [NotifyPropertyChangedFor(nameof(OnlineSummary))]
    private IReadOnlyList<ProjectSession> _sessions = Array.Empty<ProjectSession>();

    [ObservableProperty]
    private bool _sessionsKnown;

    /// <summary>Result of the last manual usage check (never set automatically).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnlineCount))]
    [NotifyPropertyChangedFor(nameof(OnlineBadgeText))]
    [NotifyPropertyChangedFor(nameof(IsOnline))]
    [NotifyPropertyChangedFor(nameof(OnlineSummary))]
    [NotifyPropertyChangedFor(nameof(IsUsageFree))]
    private UsageState _usage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnlineSummary))]
    private DateTime? _usageCheckedAt;

    /// <summary>Logins E3D recorded for the project, newest first; only filled while the project is in use.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnlineCount))]
    [NotifyPropertyChangedFor(nameof(OnlineSummary))]
    private IReadOnlyList<ProjectSession> _usageLogins = Array.Empty<ProjectSession>();

    /// <summary>Selected in batch mode on the Projects page.</summary>
    [ObservableProperty]
    private bool _isSelected;

    // ── derived ──────────────────────────────────────────────────────────────────

    public string Title => string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Equals(Name, StringComparison.OrdinalIgnoreCase)
        ? Name
        : DisplayName;

    public bool HasDisplayName => !string.IsNullOrWhiteSpace(DisplayName) && !DisplayName.Equals(Name, StringComparison.OrdinalIgnoreCase);
    public bool HasOwner => !string.IsNullOrWhiteSpace(Owner);
    public bool HasTags => Tags.Count > 0;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasStatus => Status.Length > 0;
    public bool HasCategory => Category != null;
    /// <summary>
    /// Sessions known for certain: this PC's own. A manual check that finds the project in use proves at least one
    /// session but not how many — E3D's register also keeps sessions that already ended.
    /// </summary>
    public int OnlineCount => Usage == UsageState.InUse ? Math.Max(1, Sessions.Count) : Sessions.Count;

    /// <summary>Badge text: the number of this PC's sessions, or "In use" when a check found other sessions too.</summary>
    public string OnlineBadgeText => Usage == UsageState.InUse && Sessions.Count == 0 ? Resources.Strings.Usage_InUseBadge : OnlineCount.ToString();

    public bool IsOnline => Sessions.Count > 0 || Usage == UsageState.InUse;
    public bool IsUsageFree => Usage == UsageState.Free;

    /// <summary>
    /// Tooltip: this PC's sessions (certain), then — when a check found the project in use — the sessions E3D
    /// recorded as started recently, newest first (who may be in; start times in each computer's own clock).
    /// </summary>
    public string OnlineSummary
    {
        get
        {
            var lines = new List<string>();
            foreach (var s in Sessions)
                lines.Add(string.Format(Resources.Strings.Usage_ThisPc, s.User, s.Computer));
            if (Usage == UsageState.InUse)
            {
                var recent = UsageLogins.Where(l => !Sessions.Any(s => s.IsThisDevice && l.IsThisDevice)).ToList();
                if (recent.Count == 0 && Sessions.Count == 0) lines.Add(Resources.Strings.Usage_InUseUnknown);
                if (recent.Count > 0)
                {
                    lines.Add(Resources.Strings.Usage_RecentHeader);
                    foreach (var l in recent.Take(6))
                        lines.Add($"  {l.User} @ {l.Computer}  {l.Since:dd MMM HH:mm}");
                    if (recent.Count > 6) lines.Add("  " + string.Format(Resources.Strings.Usage_More, recent.Count - 6));
                }
            }
            if (UsageCheckedAt is { } at) lines.Add(string.Format(Resources.Strings.Usage_CheckedAt, at.ToString("HH:mm")));
            return string.Join("\n", lines);
        }
    }

    /// <summary>Badge text: the project code when known, otherwise the first letters of the name.</summary>
    public string Badge => !string.IsNullOrEmpty(Code) ? Code : (Name.Length <= 5 ? Name.ToUpperInvariant() : Name[..3].ToUpperInvariant());

    /// <summary>Everything a free-text search should match.</summary>
    public bool Matches(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return true;
        return Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || (Code?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || (DisplayName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || (Owner?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || (Description?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || BatPath.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || LibraryName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || (Category?.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
