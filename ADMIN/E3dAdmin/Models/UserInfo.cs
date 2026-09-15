namespace E3dAdmin.Models;

public class UserInfo
{
    public string Name { get; set; } = string.Empty;
    public string Security { get; set; } = "General"; // Free or General
    public string Description { get; set; } = string.Empty;
    public List<string> Teams { get; set; } = new();

    public override string ToString() =>
        $"User: {Name} [{Security}] - {Description} (Teams: {string.Join(", ", Teams)})";
}
