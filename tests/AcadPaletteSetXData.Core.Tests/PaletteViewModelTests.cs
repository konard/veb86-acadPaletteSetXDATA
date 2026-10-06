using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

public sealed class PaletteViewModelTests
{
    [Fact]
    public void ExposesTheThreeSpecifiedTabsAndSelectsPropertiesInitially()
    {
        var model = new PaletteViewModel();
        Assert.Equal(new[] { "Свойства", "XDATA", "Команды" }, model.Tabs.Select(tab => tab.Header));
        Assert.Equal(0, model.SelectedTabIndex);
    }

    [Fact]
    public void ChangingThemeDoesNotResetTheSelectedTabAndNotifiesOnce()
    {
        var model = new PaletteViewModel { SelectedTabIndex = 2 };
        var notifications = new List<string?>();
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        model.Theme = PaletteTheme.Light;
        model.Theme = PaletteTheme.Light;
        Assert.Equal(new[] { nameof(PaletteViewModel.Theme) }, notifications);
        Assert.Equal(2, model.SelectedTabIndex);
    }
}
