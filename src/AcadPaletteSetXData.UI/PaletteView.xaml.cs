using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AcadPaletteSetXData.Core;

namespace AcadPaletteSetXData.UI;

public partial class PaletteView : UserControl, IDisposable
{
    private readonly PaletteViewModel viewModel;

    public PaletteView(PaletteViewModel viewModel)
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        ApplyTheme();
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PaletteViewModel.Theme))
        {
            if (Dispatcher.CheckAccess()) ApplyTheme();
            else Dispatcher.Invoke(ApplyTheme);
        }
    }

    private void ApplyTheme()
    {
        var name = viewModel.Theme == PaletteTheme.Light ? "Light" : "Dark";
        var assembly = typeof(PaletteView).Assembly.GetName().Name;
        Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"/{assembly};component/Themes/{name}.xaml", UriKind.Relative)
        };
    }

    public void Dispose()
    {
        viewModel.PropertyChanged -= OnViewModelChanged;
        DataContext = null;
    }
}
