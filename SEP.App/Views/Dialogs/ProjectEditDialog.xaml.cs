using System.Collections.Generic;
using System.Windows;
using SEP.App.Models;
using SEP.App.Services;
using SEP.App.ViewModels;
using Wpf.Ui.Controls;

namespace SEP.App.Views.Dialogs;

public partial class ProjectEditDialog : FluentWindow
{
    private readonly ProjectEditViewModel _vm;

    public ProjectEditDialog(IProjectCatalog catalog, IReadOnlyList<ProjectItem> projects)
    {
        _vm = new ProjectEditViewModel(catalog, projects);
        DataContext = _vm;
        InitializeComponent();
        Loaded += (_, _) => { if (!_vm.IsBatch) NameField.Focus(); };
    }

    public bool Saved { get; private set; }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Saved = _vm.Save();
        DialogResult = Saved;
        Close();
    }

    private void OnManageCategories(object sender, RoutedEventArgs e)
    {
        var dlg = new CategoriesDialog((IProjectCatalog)App.Services.GetService(typeof(IProjectCatalog))!) { Owner = this };
        dlg.ShowDialog();
        _vm.RefreshCategories();
    }
}
