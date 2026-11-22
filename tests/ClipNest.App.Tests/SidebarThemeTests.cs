using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace ClipNest.App.Tests;

public sealed class SidebarThemeTests
{
    private static ControlTheme FindTheme(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var value));
        return Assert.IsType<ControlTheme>(value);
    }

    [AvaloniaFact]
    public void SidebarRowButton_LeavesRowFillToStyles()
    {
        var fill = new Border { Classes = { "rowFill" } };
        var button = new Button
        {
            Theme = FindTheme("SidebarRowButton"),
            Classes = { "nav" },
            Content = fill,
        };
        var hover = new Style(x => x
            .OfType<Button>()
            .Class("nav")
            .Class(":pointerover")
            .Descendant()
            .OfType<Border>()
            .Class("rowFill"));
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Red));
        var window = new Window { Width = 200, Height = 100, Content = button };
        window.Styles.Add(hover);
        window.Show();

        window.MouseMove(new Point(20, 20), RawInputModifiers.None);

        var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First();
        Assert.Contains(":pointerover", button.Classes);
        Assert.True(Brushes.Red.Equals(fill.Background), $"row fill: {fill.Background}");
        Assert.False(Brushes.Red.Equals(presenter.Background), $"theme painted the template: {presenter.Background}");
        window.Close();
    }

    [AvaloniaFact]
    public void FolderItemTheme_LeavesStateBackgroundToStyles()
    {
        var list = new ListBox
        {
            Width = 150,
            Height = 60,
            ItemsSource = new[] { "a" },
            ItemContainerTheme = FindTheme("FolderItemTheme"),
        };
        var selected = new Style(x => x.OfType<ListBoxItem>().Class(":selected"));
        selected.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, Brushes.Red));
        var window = new Window { Width = 200, Height = 100, Content = list };
        window.Styles.Add(selected);
        window.Show();

        list.SelectedIndex = 0;
        window.UpdateLayout();

        var item = list.GetVisualDescendants().OfType<ListBoxItem>().First();
        var presenter = item.GetVisualDescendants().OfType<ContentPresenter>().First();
        Assert.Equal(Brushes.Red, presenter.Background);
        window.Close();
    }
}