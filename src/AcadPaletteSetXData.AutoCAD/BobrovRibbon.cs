using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AcadPaletteSetXData.Core;
using Autodesk.Windows;

namespace AcadPaletteSetXData.AutoCAD;

/// <summary>
/// The «Bobrov» ribbon tab (issue #39): panel «Палитра XDATA» with the large buttons «Включить» and
/// «Выключить». It depends only on AdWindows, so the WPF check renders it without AutoCAD.
/// </summary>
internal static class BobrovRibbon
{
    public static RibbonTab? Find(RibbonControl ribbon) => ribbon.FindTab(BobrovUi.RibbonTabId);

    /// <summary>Adds the tab once; returns false when the ribbon already has it.</summary>
    public static bool AddTo(RibbonControl ribbon, Action<string> runMacro)
    {
        if (ribbon == null) throw new ArgumentNullException(nameof(ribbon));
        if (Find(ribbon) != null) return false;
        ribbon.Tabs.Add(Create(runMacro));
        return true;
    }

    public static RibbonTab Create(Action<string> runMacro)
    {
        var handler = new MacroCommand(runMacro ?? throw new ArgumentNullException(nameof(runMacro)));
        var source = new RibbonPanelSource { Id = BobrovUi.RibbonPalettePanelId, Title = BobrovUi.PalettePanelTitle };
        foreach (var command in BobrovUi.PaletteCommands)
        {
            source.Items.Add(new RibbonButton
            {
                Id = command.Id,
                Name = command.MenuLabel,
                Text = command.RibbonLabel,
                ShowText = true,
                ShowImage = true,
                Size = RibbonItemSize.Large,
                Orientation = Orientation.Vertical,
                Image = Icon(command.TurnsOn, 16),
                LargeImage = Icon(command.TurnsOn, 32),
                ToolTip = command.ToolTip,
                CommandParameter = command.Macro,
                CommandHandler = handler,
            });
        }

        var tab = new RibbonTab { Id = BobrovUi.RibbonTabId, Title = BobrovUi.RibbonTabTitle };
        tab.Panels.Add(new RibbonPanel { Source = source });
        return tab;
    }

    /// <summary>A palette window with a green «on» or a red «off» badge; vector, so it stays sharp at any DPI.</summary>
    internal static ImageSource Icon(bool turnsOn, double size)
    {
        var unit = size / 32;
        var drawing = new DrawingGroup();
        // The transparent square fixes the bounds, so the image keeps its size and position.
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(0x3C, 0x6E, 0xB4)), 2 * unit);
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xEE, 0xF3, 0xFA)), outline,
            new RectangleGeometry(new Rect(3 * unit, 3 * unit, 20 * unit, 25 * unit), 2 * unit, 2 * unit)));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x3C, 0x6E, 0xB4)), null,
            new RectangleGeometry(new Rect(3 * unit, 3 * unit, 20 * unit, 5 * unit))));
        var rows = new Pen(new SolidColorBrush(Color.FromRgb(0x8A, 0xA4, 0xC8)), 2 * unit);
        for (var row = 0; row < 3; row++)
            drawing.Children.Add(new GeometryDrawing(null, rows,
                new LineGeometry(new Point(7 * unit, (13 + 5 * row) * unit), new Point(19 * unit, (13 + 5 * row) * unit))));

        var badge = turnsOn ? Color.FromRgb(0x2E, 0x9E, 0x44) : Color.FromRgb(0xD2, 0x3B, 0x3B);
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(badge), new Pen(Brushes.White, 1.5 * unit),
            new EllipseGeometry(new Point(23 * unit, 23 * unit), 8 * unit, 8 * unit)));
        var mark = new Pen(Brushes.White, 2.5 * unit) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (turnsOn)
        {
            // A check mark: the palette is shown.
            var check = new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(19 * unit, 23 * unit) };
            figure.Segments.Add(new LineSegment(new Point(22 * unit, 26 * unit), true));
            figure.Segments.Add(new LineSegment(new Point(27 * unit, 20 * unit), true));
            check.Figures.Add(figure);
            drawing.Children.Add(new GeometryDrawing(null, mark, check));
        }
        else
        {
            // A cross: the palette is hidden.
            drawing.Children.Add(new GeometryDrawing(null, mark, new LineGeometry(new Point(20 * unit, 20 * unit), new Point(26 * unit, 26 * unit))));
            drawing.Children.Add(new GeometryDrawing(null, mark, new LineGeometry(new Point(26 * unit, 20 * unit), new Point(20 * unit, 26 * unit))));
        }

        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    /// <summary>The ribbon passes the pressed button; its <c>CommandParameter</c> is the command macro.</summary>
    private sealed class MacroCommand : ICommand
    {
        private readonly Action<string> runMacro;

        public MacroCommand(Action<string> runMacro) => this.runMacro = runMacro;

        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            if ((parameter as RibbonCommandItem)?.CommandParameter is string macro) runMacro(macro);
        }
    }
}
