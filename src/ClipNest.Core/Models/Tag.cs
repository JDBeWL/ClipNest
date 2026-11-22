namespace ClipNest.Core.Models;

public sealed class Tag
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public long? ParentId { get; set; }
    public string? Color { get; set; }
    public List<Tag> Children { get; set; } = new();
}
