using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using SEP.App.Resources;
using SEP.App.Services;
using Wpf.Ui.Controls;

namespace SEP.App.Views.Dialogs;

public sealed partial class CategoryRow : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _color = "#4f8cff";
    [ObservableProperty] private string _usageText = string.Empty;
}

/// <summary>Add / rename / recolour / delete the business categories projects are filed under.</summary>
public partial class CategoriesDialog : FluentWindow
{
    private static readonly string[] Palette = { "#4f8cff", "#2dd4a7", "#f5b85c", "#ff5d6c", "#b07bff", "#36b6e8", "#ff8f6b", "#8bd66b", "#e86b9a", "#9aa8ff" };
    private readonly IProjectCatalog _catalog;
    private readonly ObservableCollection<CategoryRow> _rows = new();

    public CategoriesDialog(IProjectCatalog catalog)
    {
        _catalog = catalog;
        InitializeComponent();
        List.ItemsSource = _rows;
        Reload();
        Loaded += (_, _) => NewName.Focus();
    }

    private void Reload()
    {
        _rows.Clear();
        foreach (var c in _catalog.Categories)
        {
            int used = _catalog.Projects.Count(p => p.Category?.Id == c.Id);
            _rows.Add(new CategoryRow { Id = c.Id, Name = c.Name, Color = c.Color, UsageText = string.Format(Strings.Category_Usage, used) });
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e) => Add();

    private void OnNewNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Add(); e.Handled = true; }
    }

    private void Add()
    {
        string name = NewName.Text.Trim();
        if (name.Length == 0) return;
        try
        {
            _catalog.AddCategory(name);
            NewName.Text = string.Empty;
            Status.Text = string.Format(Strings.Category_Added, name);
            Reload();
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
        }
    }

    private void OnNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not CategoryRow row) return;
        var current = _catalog.Categories.FirstOrDefault(c => c.Id == row.Id);
        if (current == null || current.Name == row.Name.Trim()) return;
        var (ok, msg) = _catalog.UpdateCategory(row.Id, row.Name, null);
        Status.Text = msg;
        if (!ok) row.Name = current.Name;
    }

    private void OnCycleColor(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not CategoryRow row) return;
        int idx = Array.FindIndex(Palette, c => c.Equals(row.Color, StringComparison.OrdinalIgnoreCase));
        string next = Palette[(idx + 1 + Palette.Length) % Palette.Length];
        var (ok, msg) = _catalog.UpdateCategory(row.Id, null, next);
        if (ok) row.Color = next;
        Status.Text = msg;
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not CategoryRow row) return;
        var dialogs = (IDialogService)App.Services.GetService(typeof(IDialogService))!;
        bool ok = await dialogs.ConfirmAsync(Strings.Category_Delete, string.Format(Strings.Category_DeleteBody, row.Name), Strings.Category_Delete, danger: true);
        if (!ok) return;
        _catalog.RemoveCategory(row.Id);
        Status.Text = string.Format(Strings.Category_Deleted, row.Name);
        Reload();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
