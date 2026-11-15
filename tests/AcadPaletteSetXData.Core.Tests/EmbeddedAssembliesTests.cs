using System;
using System.Reflection;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// The loader of the third-party assemblies (issue #43): the release DLL embeds NCalc, the development build deploys it
/// next to the plugin. The embedded case runs in the release smoke test of CI (experiments/AcadPaletteSetXData.WpfSmoke).
/// </summary>
public sealed class EmbeddedAssembliesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not An, Assembly, Name, =")]
    [InlineData("AcadPaletteSetXData.Unknown, Version=1.0.0.0")]
    [InlineData("NCalc.resources, Version=7.4.0.0, Culture=ru-RU")]
    [InlineData("acdbmgd, Version=24.0.0.0")]
    public void OnlyTheAssembliesOfThePluginAreResolved(string? name) => Assert.Null(EmbeddedAssemblies.Resolve(name));

    [Fact]
    public void ThePluginItselfIsNotResolved() =>
        Assert.Null(EmbeddedAssemblies.Resolve(typeof(EmbeddedAssemblies).Assembly.FullName));

    [Fact]
    public void TheDevelopmentBuildDeploysNCalcNextToThePlugin()
    {
        // Core embeds nothing: the resolver falls back to the folder of the plugin and returns the deployed assembly.
        Assert.Empty(EmbeddedAssemblies.Names);
        var engine = EmbeddedAssemblies.Resolve("NCalc, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
        Assert.NotNull(engine);
        Assert.Equal("NCalc", engine!.GetName().Name);
        Assert.Same(engine, EmbeddedAssemblies.Resolve("ncalc"));
    }

    [Fact]
    public void TheResolverIsRegisteredByTheModuleInitializer()
    {
        // Using any type of the plugin runs the module initializer first: nothing has to call Register.
        Assert.NotNull(FormulaExpression.Compile("=1 + 1"));
        var registered = typeof(EmbeddedAssemblies).GetField("registered", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True((bool)registered.GetValue(null)!);
        EmbeddedAssemblies.Register();
        Assert.True((bool)registered.GetValue(null)!);
    }
}
