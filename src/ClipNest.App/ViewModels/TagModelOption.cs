using ClipNest.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClipNest.App.ViewModels;

/// <summary>设置页里的一个标注模型条目，也用于文件夹的模型下拉框。</summary>
public sealed partial class TagModelOption : ObservableObject
{
    public TagModelOption(string id, string displayName, string description = "")
    {
        Id = id;
        _displayName = displayName;
        _description = description;
    }

    public string Id { get; }

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string _description;

    public bool HasDescription => Description.Length > 0;

    partial void OnDescriptionChanged(string value) => OnPropertyChanged(nameof(HasDescription));

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _progress;
}

/// <summary>一个文件夹与其模型选择。空模型表示跟随默认。</summary>
public sealed partial class FolderTagModelOption : ObservableObject
{
    public FolderTagModelOption(Folder folder, IReadOnlyList<TagModelOption> choices)
    {
        Folder = folder;
        Choices = choices;
        _selected = choices.FirstOrDefault(choice =>
                        string.Equals(choice.Id, folder.TagModelId, StringComparison.Ordinal))
                    ?? choices[0];
    }

    public Folder Folder { get; }

    public IReadOnlyList<TagModelOption> Choices { get; }

    [ObservableProperty] private TagModelOption _selected;
}
