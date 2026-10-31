using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using SEP.App.ViewModels;

namespace SEP.App.Views.Pages;

public partial class OverviewPage : Page
{
    private readonly OverviewViewModel _viewModel;

    public OverviewPage()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<OverviewViewModel>();
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.Refresh();
        // six tiles in one row need about 1000 px; below that two even rows read better than one orphan tile
        SizeChanged += (_, e) => KpiGrid.Rows = e.NewSize.Width >= 1000 ? 1 : 2;
    }

    // KPI tiles are plain borders (no button chrome); a click (press and release on the same tile) opens the
    // matching page. Release alone is not a click: a press on a card or the scroll bar released over a tile
    // used to navigate away.
    private object? _pressedTile;

    private void OnTilePressed(object sender, MouseButtonEventArgs e) => _pressedTile = sender;

    private bool IsClick(object sender)
    {
        bool click = ReferenceEquals(sender, _pressedTile);
        _pressedTile = null;
        return click;
    }

    private void OnProjectsTile(object sender, MouseButtonEventArgs e) { if (IsClick(sender)) _viewModel.GoProjectsCommand.Execute(null); }
    private void OnMineTile(object sender, MouseButtonEventArgs e) { if (IsClick(sender)) _viewModel.GoMyProjectsCommand.Execute(null); }
    private void OnSettingsTile(object sender, MouseButtonEventArgs e) { if (IsClick(sender)) _viewModel.GoSettingsCommand.Execute(null); }
    private void OnToolsTile(object sender, MouseButtonEventArgs e) { if (IsClick(sender)) _viewModel.GoToolsCommand.Execute(null); }
}
