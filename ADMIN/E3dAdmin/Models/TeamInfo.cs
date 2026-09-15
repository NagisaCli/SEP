namespace E3dAdmin.Models;

public class TeamInfo
{
    public string Name { get; set; } = string.Empty;
    public List<string> Users { get; set; } = new();

    public override string ToString() =>
        $"Team: {Name} (Members: {string.Join(", ", Users)})";
}
