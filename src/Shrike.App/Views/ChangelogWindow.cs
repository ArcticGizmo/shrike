using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Shrike.Core.Changelog;

namespace Shrike.App.Views;

/// <summary>
/// The post-update "what's new" card: a headline, a scrollable list of the changelog sections released
/// since the version that last ran here, and two buttons — Close, and "Don't show changelogs again" which
/// suppresses future pop-ups via <paramref name="onSuppress"/>. Shown once per update from the startup
/// check; the entries are picked by <see cref="ChangelogParser"/>. Built in code, styled like the About
/// and toast windows so the app reads as one.
/// </summary>
public sealed class ChangelogWindow : Window
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#140F0A"));
    private static readonly IBrush Fg = new SolidColorBrush(Color.Parse("#EDE6DA"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9A8F7D"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F5A524"));

    private readonly Action _onSuppress;

    public ChangelogWindow(string headline, string subhead, IReadOnlyList<ChangelogSection> sections, Action onSuppress)
    {
        _onSuppress = onSuppress;

        Title = "Shrike — What's New";
        Width = 480;
        Height = 560;
        CanResize = false;
        Background = Bg;
        Topmost = true;   // it's a post-update announcement — surface it above whatever's focused
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;

        Content = BuildCard(headline, subhead, sections);
    }

    private Control BuildCard(string headline, string subhead, IReadOnlyList<ChangelogSection> sections)
    {
        var title = new TextBlock { Text = headline, Foreground = Amber, FontWeight = FontWeight.SemiBold, FontSize = 18 };
        var sub = new TextBlock
        {
            Text = subhead, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        var header = new StackPanel { Children = { title, sub } };

        var body = new StackPanel();
        if (sections.Count == 0)
            body.Children.Add(new TextBlock { Text = "No changelog entries in that range.", Foreground = Muted, FontSize = 12 });
        for (int i = 0; i < sections.Count; i++)
            ChangelogView.Render(body, sections[i].Block);

        var scroller = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 12, 0, 12),
        };

        var suppress = FlatButton("Don't show changelogs again");
        suppress.Click += (_, _) => { try { _onSuppress(); } catch { /* best effort */ } Close(); };

        var close = FlatButton("Close");
        close.Background = Amber;
        close.Foreground = Bg;
        close.MinWidth = 84;
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { suppress, close },
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(20) };
        Grid.SetRow(header, 0);
        Grid.SetRow(scroller, 1);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(header);
        grid.Children.Add(scroller);
        grid.Children.Add(buttons);
        return grid;
    }

    private static Button FlatButton(string text) => new()
    {
        Content = text,
        Background = new SolidColorBrush(Color.Parse("#2A2318")),
        Foreground = Fg,
        Padding = new Thickness(11, 7),
        CornerRadius = new CornerRadius(7),
        FontSize = 12,
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
