using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// Issue #57: in AutoCAD the formula <c>=@@[Power]</c> failed with «Не удалось загрузить тип
/// Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory из сборки … Version=1.1.1.0». Another component had
/// loaded the old logging abstractions, and <c>NCalc.Expression</c> initializes loggers in its static constructors.
/// The tests load the plugin core and NCalc into a separate load context where the logging abstractions are 1.1.1.
/// </summary>
public sealed class LegacyLoggingTests
{
    private const string Logging = "Microsoft.Extensions.Logging.Abstractions";

    [Fact]
    public void TheLegacyContextReproducesTheLoadErrorOfNCalcExpression()
    {
        // The trap of the issue is armed: NCalc's own entry point fails exactly as in AutoCAD.
        var context = new LegacyLoggingContext();
        try
        {
            var logging = context.LoadFromAssemblyName(new AssemblyName(Logging));
            Assert.Equal(new Version(1, 1, 1, 0), logging.GetName().Version);
            Assert.Null(logging.GetType(Logging + ".NullLoggerFactory"));

            var ncalc = context.LoadFromAssemblyName(new AssemblyName("NCalc.Core"));
            var expression = ncalc.GetType("NCalc.Expression", throwOnError: true)!;
            var error = Assert.ThrowsAny<Exception>(() =>
            {
                var constructor = expression.GetConstructor(new[] { typeof(string), typeof(System.Globalization.CultureInfo) })!;
                try { constructor.Invoke(new object[] { "1 + 1", System.Globalization.CultureInfo.InvariantCulture }); }
                catch (TargetInvocationException wrapped) { throw wrapped.InnerException!; }
            });
            Assert.Contains("NullLoggerFactory", Messages(error));
        }
        finally { context.Unload(); }
    }

    [Theory]
    [InlineData("=@@[Power]", "2,50")]
    [InlineData("=@@[Power] * 2", "5")]
    [InlineData("=ROUND(@@[Power] * 3, 1) & \" кВт\"", "7,5 кВт")]
    [InlineData("=SIN(90) + MAX(1, 2)", "3")]
    [InlineData("=if(@@[Power] > 2, \"много\", \"мало\")", "много")]
    public void FormulasWorkWithTheLegacyLoggingAbstractions(string format, string expected)
    {
        var context = new LegacyLoggingContext();
        try
        {
            var core = context.LoadFromAssemblyName(typeof(FormulaExpression).Assembly.GetName());
            Assert.NotSame(typeof(FormulaExpression).Assembly, core);
            var formulas = core.GetType(typeof(FormulaExpression).FullName!, throwOnError: true)!;
            var fragment = core.GetType(typeof(FormatFragment).FullName!, throwOnError: true)!;

            var compiled = formulas.GetMethod(nameof(FormulaExpression.Compile))!.Invoke(null, new object[] { format })!;
            var error = (string?)formulas.GetProperty(nameof(FormulaExpression.Error))!.GetValue(compiled);
            Assert.Null(error);

            var value = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(fragment, typeof(object)),
                typeof(LegacyLoggingTests).GetMethod(nameof(Value), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(fragment));
            var text = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(fragment, typeof(string)),
                typeof(LegacyLoggingTests).GetMethod(nameof(Text), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(fragment));
            var result = formulas.GetMethod(nameof(FormulaExpression.Evaluate))!
                .Invoke(compiled, new object[] { value, text, new System.Globalization.CultureInfo("ru-RU") });
            Assert.Equal(expected, result);

            // The formula does not need the logging abstractions at all.
            Assert.False(context.LoggingRequested);
        }
        finally { context.Unload(); }
    }

    private static object? Value<T>(T fragment) => 2.5;
    private static string Text<T>(T fragment) => "2,50";

    private static string Messages(Exception error)
    {
        var messages = "";
        for (Exception? item = error; item != null; item = item.InnerException) messages += item.GetType().Name + ": " + item.Message + "\n";
        return messages;
    }

    /// <summary>
    /// The plugin core and NCalc come from the test output, the logging abstractions are the old 1.1.1 like in AutoCAD;
    /// everything else (the framework) is shared with the default context.
    /// </summary>
    private sealed class LegacyLoggingContext : AssemblyLoadContext
    {
        private static readonly string[] Own = { "AcadPaletteSetXData.Core", "NCalc.Core", "NCalc.Parser", "NCalc.Domain", "ExtendedNumerics.BigDecimal" };
        private readonly string directory = Path.GetDirectoryName(typeof(LegacyLoggingTests).Assembly.Location)!;

        public LegacyLoggingContext() : base("issue57-legacy-logging", isCollectible: true) { }

        public bool LoggingRequested { get; private set; }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == Logging)
            {
                LoggingRequested = true;
                var legacy = Path.Combine(directory, "legacy", Logging + ".dll");
                Assert.True(File.Exists(legacy), "Microsoft.Extensions.Logging.Abstractions 1.1.1 is not copied to " + legacy);
                return LoadFromAssemblyPath(legacy);
            }
            if (!Own.Contains(name.Name)) return null;
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
