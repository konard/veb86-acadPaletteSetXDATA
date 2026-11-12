using System;
using System.Collections.Generic;
using System.Linq;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

// Issue #39: the «Bobrov» menu bar menu and ribbon tab with the on/off buttons of the palette.
// The fakes expose the same late-bound members as AutoCAD's COM AcadMenuGroups/AcadPopupMenus/AcadPopupMenu.
public sealed class BobrovUiTests
{
    [Fact]
    public void MenuAndRibbonShowTheOnAndOffButtonsOfThePalette()
    {
        Assert.Equal("Bobrov", BobrovUi.MenuName);
        Assert.Equal("Bobrov", BobrovUi.RibbonTabTitle);
        Assert.Equal(new[] { "XDATAPALETTE_ON", "XDATAPALETTE_OFF" }, BobrovUi.PaletteCommands.Select(item => item.Command));
        Assert.Equal(new[] { "Включить", "Выключить" }, BobrovUi.PaletteCommands.Select(item => item.RibbonLabel));
        Assert.Equal(new[] { true, false }, BobrovUi.PaletteCommands.Select(item => item.TurnsOn));
        Assert.Equal("\u0003\u0003_XDATAPALETTE_ON ", BobrovUi.PaletteCommands[0].Macro);
        Assert.Equal(BobrovUi.PaletteCommands.Count, BobrovUi.PaletteCommands.Select(item => item.Id).Distinct().Count());
        Assert.All(BobrovUi.PaletteCommands, item => Assert.Contains(item.Command, item.ToolTip));
        Assert.Throws<ArgumentException>(() => BobrovUi.Macro(" "));
    }

    [Fact]
    public void AddsTheBobrovMenuToTheMenuBarOfTheAcadGroup()
    {
        var bar = new FakeMenuBar();
        bar.Items.Add(new FakePopupMenu("Файл", bar) { OnMenuBar = true });
        var groups = new FakeMenuGroups(bar, "ACAD", "EXPRESS");

        Assert.Null(BobrovMenuInstaller.Install(groups, bar));

        var menu = Assert.Single(groups.Groups["ACAD"].Menus.Items);
        Assert.Equal("Bobrov", menu.Name);
        Assert.Empty(groups.Groups["EXPRESS"].Menus.Items);
        Assert.Equal(new[] { "Включить палитру XDATA", "Выключить палитру XDATA" }, menu.Items.Select(item => item.Label));
        Assert.Equal(new[] { "\u0003\u0003_XDATAPALETTE_ON ", "\u0003\u0003_XDATAPALETTE_OFF " }, menu.Items.Select(item => item.Macro));
        Assert.True(menu.OnMenuBar);
        Assert.Equal(new[] { "Файл", "Bobrov" }, bar.Items.Select(item => item.Name));
    }

    [Fact]
    public void ReloadRefillsTheExistingMenuWithoutDoublingIt()
    {
        var bar = new FakeMenuBar();
        var groups = new FakeMenuGroups(bar, "ACAD");
        Assert.Null(BobrovMenuInstaller.Install(groups, bar));
        groups.Groups["ACAD"].Menus.Items[0].Items.Add(new FakeMenuItem("Старый пункт", "_OLD "));

        Assert.Null(BobrovMenuInstaller.Install(groups, bar));

        var menu = Assert.Single(groups.Groups["ACAD"].Menus.Items);
        Assert.Equal(2, menu.Items.Count);
        Assert.Single(bar.Items);
    }

    [Fact]
    public void FallsBackToTheFirstMenuGroupWithoutAcad()
    {
        var bar = new FakeMenuBar();
        var groups = new FakeMenuGroups(bar, "CUSTOM");
        Assert.Null(BobrovMenuInstaller.Install(groups, bar));
        Assert.Equal("Bobrov", Assert.Single(groups.Groups["CUSTOM"].Menus.Items).Name);
    }

    [Fact]
    public void ReportsWhyTheMenuIsUnavailableInsteadOfThrowing()
    {
        var bar = new FakeMenuBar();
        Assert.NotNull(BobrovMenuInstaller.Install(null, bar));
        Assert.NotNull(BobrovMenuInstaller.Install(new FakeMenuGroups(bar), null));
        Assert.NotNull(BobrovMenuInstaller.Install(new FakeMenuGroups(bar), bar));
        Assert.NotNull(BobrovMenuInstaller.Install(new object(), bar));

        var refusing = new FakeMenuGroups(bar, "ACAD");
        refusing.Groups["ACAD"].Menus.RefuseAdd = true;
        Assert.Contains("Bobrov", BobrovMenuInstaller.Install(refusing, bar));

        var hidden = new FakeMenuGroups(bar, "ACAD");
        bar.RefuseInsert = true;
        Assert.Contains("строку меню", BobrovMenuInstaller.Install(hidden, bar));
    }

    public sealed class FakeMenuBar
    {
        public List<FakePopupMenu> Items { get; } = new List<FakePopupMenu>();
        public bool RefuseInsert { get; set; }
        public int Count => Items.Count;
    }

    public sealed class FakeMenuGroups
    {
        public Dictionary<string, FakeMenuGroup> Groups { get; } = new Dictionary<string, FakeMenuGroup>();
        private readonly List<string> order = new List<string>();

        public FakeMenuGroups(FakeMenuBar bar, params string[] names)
        {
            foreach (var name in names)
            {
                Groups[name] = new FakeMenuGroup(bar);
                order.Add(name);
            }
        }

        public int Count => order.Count;

        // COM: AcadMenuGroups.Item(Index) takes a name or a number and fails for a missing group.
        public FakeMenuGroup Item(object index) => index is string name
            ? Groups.TryGetValue(name, out var group) ? group : throw new ArgumentException(name)
            : Groups[order[(int)index]];
    }

    public sealed class FakeMenuGroup
    {
        public FakeMenuGroup(FakeMenuBar bar) => Menus = new FakePopupMenus(bar);
        public FakePopupMenus Menus { get; }
    }

    public sealed class FakePopupMenus
    {
        private readonly FakeMenuBar bar;
        public FakePopupMenus(FakeMenuBar bar) => this.bar = bar;
        public List<FakePopupMenu> Items { get; } = new List<FakePopupMenu>();
        public bool RefuseAdd { get; set; }
        public int Count => Items.Count;

        public FakePopupMenu Item(object index) =>
            Items.FirstOrDefault(menu => Equals(menu.Name, index)) ?? throw new ArgumentException(index.ToString());

        public FakePopupMenu Add(string name)
        {
            if (RefuseAdd) throw new InvalidOperationException("menu group is read-only");
            var menu = new FakePopupMenu(name, bar);
            Items.Add(menu);
            return menu;
        }
    }

    public sealed class FakePopupMenu
    {
        private readonly FakeMenuBar bar;

        public FakePopupMenu(string name, FakeMenuBar bar)
        {
            Name = name;
            this.bar = bar;
        }

        public string Name { get; }
        public bool OnMenuBar { get; set; }
        public List<FakeMenuItem> Items { get; } = new List<FakeMenuItem>();
        public int Count => Items.Count;

        public FakeMenuItem Item(object index)
        {
            var item = Items[(int)index];
            item.Owner = Items;
            return item;
        }

        public FakeMenuItem AddMenuItem(object index, string label, string macro)
        {
            var item = new FakeMenuItem(label, macro);
            Items.Insert((int)index, item);
            return item;
        }

        public void InsertInMenuBar(object index)
        {
            if (bar.RefuseInsert) throw new InvalidOperationException("menu bar is locked");
            bar.Items.Insert(Math.Min((int)index, bar.Items.Count), this);
            OnMenuBar = true;
        }
    }

    public sealed class FakeMenuItem
    {
        public FakeMenuItem(string label, string macro)
        {
            Label = label;
            Macro = macro;
        }

        public string Label { get; }
        public string Macro { get; }
        public List<FakeMenuItem>? Owner { get; set; }
        public void Delete() => Owner!.Remove(this);
    }
}
