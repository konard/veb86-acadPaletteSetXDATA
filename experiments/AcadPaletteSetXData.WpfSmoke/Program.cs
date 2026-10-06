using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AcadPaletteSetXData.Core;
using AcadPaletteSetXData.UI;

namespace AcadPaletteSetXData.WpfSmoke;

/// <summary>Runs the real WPF shell without AutoCAD and renders review images.</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var application = new Application();
            var model = new PaletteViewModel();
            using var view = new PaletteView(model);
            var window = new Window
            {
                Title = "Предпросмотр панели XDATA", Content = view, Width = 440, Height = 640
            };
            if (args.Contains("--interactive"))
            {
                application.Run(window);
                return 0;
            }

            var output = args.Length > 0 ? args[0] : "artifacts/screenshots";
            Directory.CreateDirectory(output);
            window.Show();
            var tabs = (TabControl)view.FindName("Tabs");
            Check(tabs.TabStripPlacement == Dock.Right, "Tabs must be on the right.");
            Check(tabs.Items.Count == 3, "Expected three tabs.");

            foreach (var theme in new[] { PaletteTheme.Dark, PaletteTheme.Light })
            {
                model.Theme = theme;
                Pump(view);
                var expectedBackground = theme == PaletteTheme.Dark ? "#FF3D4451" : "#FFF5F6F8";
                Check(((SolidColorBrush)view.Background).Color.ToString() == expectedBackground,
                    "The WPF background must follow the theme.");

                for (var index = 0; index < model.Tabs.Count; index++)
                {
                    // Set the control selection to exercise the two-way binding used by a click.
                    tabs.SelectedIndex = index;
                    Pump(view);
                    Check(model.SelectedTabIndex == index, "Selection must update the ViewModel.");
                    Check(Descendants<TextBlock>(view).Any(text => text.Text == model.Tabs[index].Title),
                        "The selected tab content must be displayed.");
                    var previousY = -1.0;
                    for (var tabIndex = 0; tabIndex < tabs.Items.Count; tabIndex++)
                    {
                        var item = (TabItem)tabs.ItemContainerGenerator.ContainerFromIndex(tabIndex);
                        var location = item.TranslatePoint(new Point(), view);
                        Check(location.X > view.ActualWidth / 2, "The tab strip must be at the right edge.");
                        Check(location.Y > previousY, "Tab headers must be stacked vertically.");
                        Check(item.ActualWidth < 50 && item.ActualHeight > 65,
                            "Tab labels must have vertical geometry.");
                        previousY = location.Y;
                    }
                    Save(view, Path.Combine(output, $"stage1-{theme.ToString().ToLowerInvariant()}-{index}.png"));
                }
            }

            model.SelectedTabIndex = 1;
            model.Theme = PaletteTheme.Dark;
            Pump(view);
            Check(tabs.SelectedIndex == 1, "Changing the theme must preserve selection.");
            // Exercise the minimum supported palette size as well as the normal render.
            window.Width = 280;
            window.Height = 320;
            Pump(view);
            Check(view.ActualWidth > 0 && tabs.ActualWidth <= view.ActualWidth,
                "The shell must fit the minimum palette width.");
            window.Close();
            view.Dispose();
            model.Theme = PaletteTheme.Light;
            Check(view.DataContext == null, "Disposal must detach the ViewModel.");
            application.Shutdown();
            Console.WriteLine("WPF smoke checks passed: right tabs, selection, both themes, resize, disposal; six PNG renders.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Pump(FrameworkElement view)
    {
        view.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        view.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Save(FrameworkElement view, string path)
    {
        var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight,
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
