namespace ClipNest.App.ViewModels;

public sealed record TagTreeItemViewModel(long Id, string Name, string Path, int Depth)
{
    public string DisplayName => $"{new string(' ', Depth * 2)}{Name}";

    public Avalonia.Thickness Indent => new(Depth * 12, 0, 0, 0);
}
