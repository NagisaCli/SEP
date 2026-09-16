using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using E3dAdmin.Models;

namespace SEP.App.Models;

/// <summary>One membership link shown as a removable chip on a user or team row.</summary>
public sealed record TeamMembership(string User, string Team)
{
    /// <summary>Team name without AVEVA's leading asterisk.</summary>
    public string TeamLabel => Team.TrimStart('*');
}

/// <summary>A project user as listed by ADMIN, shaped for the Users page.</summary>
public sealed partial class UserRow : ObservableObject
{
    public UserRow(UserInfo info)
    {
        Name = info.Name;
        Security = info.Security;
        Description = info.Description;
        Teams = info.Teams.ToList();
        Memberships = Teams.Select(t => new TeamMembership(Name, t)).ToList();
    }

    public string Name { get; }
    public string Security { get; }
    public string Description { get; }
    public List<string> Teams { get; }
    public List<TeamMembership> Memberships { get; }

    public bool IsFree => Security.Equals("Free", StringComparison.OrdinalIgnoreCase);
    public bool IsSystem => Name.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase);
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasTeams => Teams.Count > 0;
    public string Initials => Name.Length <= 2 ? Name.ToUpperInvariant() : Name[..2].ToUpperInvariant();

    /// <summary>True while an operation on this row is running (row shows a spinner and disables its buttons).</summary>
    [ObservableProperty]
    private bool _isBusy;

    public bool Matches(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Description.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Teams.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A project team as listed by ADMIN.</summary>
public sealed partial class TeamRow : ObservableObject
{
    public TeamRow(TeamInfo info)
    {
        Name = info.Name;
        Description = info.Description;
        Members = info.Users.ToList();
        Memberships = Members.Select(u => new TeamMembership(u, Name)).ToList();
    }

    /// <summary>Raw name as ADMIN reports it (e.g. *MASTER).</summary>
    public string Name { get; }
    public string Label => Name.TrimStart('*');
    public string Description { get; }
    public List<string> Members { get; }
    public List<TeamMembership> Memberships { get; }

    public bool IsMaster => Label.Equals("MASTER", StringComparison.OrdinalIgnoreCase);
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasMembers => Members.Count > 0;
    public int MemberCount => Members.Count;
    public string Initials => Label.Length <= 2 ? Label.ToUpperInvariant() : Label[..2].ToUpperInvariant();

    [ObservableProperty]
    private bool _isBusy;

    public bool Matches(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Description.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Members.Any(m => m.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }
}
