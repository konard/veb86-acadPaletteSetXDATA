using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AcadPaletteSetXData.AutoCAD;
using AcadPaletteSetXData.Core;
using Autodesk.Windows;

// Issue #39: the «Bobrov» ribbon tab built by the shipped code on a real AdWindows RibbonControl,
// as AutoCAD 2021 does on ComponentManager.Ribbon. The classic menu needs AutoCAD COM and is covered by unit tests.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var output = args.Length > 0 ? args[0] : "artifacts/screenshots/ribbon";
            Directory.CreateDirectory(output);
            var application = new Application();
            var ribbon = new RibbonControl();
            var macros = new List<string>();

            Check(BobrovRibbon.AddTo(ribbon, macros.Add), "The first load must add the «Bobrov» tab.");
            Check(!BobrovRibbon.AddTo(ribbon, macros.Add), "A second load must not add the tab twice.");
            var tab = ribbon.Tabs.Single(item => item.Id == BobrovUi.RibbonTabId);
            Check(ReferenceEquals(BobrovRibbon.Find(ribbon), tab), "FindTab must find the «Bobrov» tab by its id.");
            Check(tab.Title == "Bobrov", "The ribbon tab must be called «Bobrov».");
            var panel = tab.Panels.Single();
            Check(panel.Source.Title == BobrovUi.PalettePanelTitle, "The panel must be «Палитра XDATA».");
            var buttons = panel.Source.Items.Cast<RibbonButton>().ToList();
            Check(buttons.Select(button => button.Text).SequenceEqual(new[] { "Включить", "Выключить" }),
                "The panel must have the «Включить» and «Выключить» buttons.");
            foreach (var button in buttons)
            {
                Check(button.ShowText && button.ShowImage && button.Size == RibbonItemSize.Large, button.Text + " must be a large button with text and icon.");
                Check(button.LargeImage != null && button.Image != null, button.Text + " must have icons.");
                Check(button.CommandHandler != null && button.CommandHandler.CanExecute(button), button.Text + " must be enabled.");
                // AutoCAD passes the pressed RibbonButton to its command handler.
                button.CommandHandler!.Execute(button);
            }
            Check(macros.SequenceEqual(new[] { "\u0003\u0003_XDATAPALETTE_ON ", "\u0003\u0003_XDATAPALETTE_OFF " }),
                "The buttons must start XDATAPALETTE_ON and XDATAPALETTE_OFF: " + string.Join(" | ", macros));

            // A ribbon of an AutoCAD workspace has its own tabs; «Bobrov» is added after them.
            var home = new RibbonTab { Id = "SMOKE_HOME", Title = "Главная" };
            home.Panels.Add(new RibbonPanel { Source = new RibbonPanelSource { Title = "Рисование" } });
            ribbon.Tabs.Insert(0, home);
            tab.IsActive = true;
            var window = new Window
            {
                Title = "Вкладка ленты Bobrov", Content = ribbon, Width = 640, Height = 190,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            SaveIcons(Path.Combine(output, "issue39-bobrov-icons.png"));
            window.Show();
            Save(ribbon, Path.Combine(output, "issue39-bobrov-ribbon.png"));
            window.Close();
            application.Shutdown();
            Console.WriteLine("Ribbon checks passed: «Bobrov» tab, «Палитра XDATA» panel, «Включить»/«Выключить» start XDATAPALETTE_ON/XDATAPALETTE_OFF, no duplicate tab.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void SaveIcons(string path)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.White };
        foreach (var size in new[] { 16.0, 32.0, 64.0 })
        foreach (var turnsOn in new[] { true, false })
            panel.Children.Add(new Image { Source = BobrovRibbon.Icon(turnsOn, size), Width = size, Height = size, Margin = new Thickness(8), Stretch = Stretch.None });
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        panel.Arrange(new Rect(panel.DesiredSize));
        Write(panel, (int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), path);
    }

    private static void Save(FrameworkElement view, string path)
    {
        view.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        view.UpdateLayout();
        Check(view.ActualWidth > 0 && view.ActualHeight > 0, "The ribbon must be laid out.");
        Write(view, (int)view.ActualWidth, (int)view.ActualHeight, path);
    }

    private static void Write(Visual visual, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine("Rendered " + path + ".");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
