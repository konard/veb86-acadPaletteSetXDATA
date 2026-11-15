using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using AcadPaletteSetXData.Core;
using Xunit;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// The formula mode of <c>CalculatedString</c> (issue #43): a <c>format</c> that starts with <c>=</c> is an NCalc
/// expression over typed values of <c>@@[Id]</c>, with the Excel concatenation <c>&amp;</c>; other formats keep the
/// string substitution of TZXDATA 7.
/// </summary>
public sealed class FormulaExpressionTests
{
    private static readonly CultureInfo Comma = DecimalComma();

    /// <summary>The parameters of the examples of the issue; formulas are added by each test.</summary>
    private const string Parameters = @"
        { 'id': 'Number', 'type': 'String', 'caption': 'Номер щита' },
        { 'id': 'Group', 'type': 'String', 'caption': 'Группа' },
        { 'id': 'Name', 'type': 'String', 'caption': 'Наименование' },
        { 'id': 'Text', 'type': 'String', 'caption': 'Текст' },
        { 'id': 'Power', 'type': 'Double', 'caption': 'Мощность', 'default': 0, 'editor': { 'precision': 2 } },
        { 'id': 'Reserve', 'type': 'Double', 'caption': 'Резерв' },
        { 'id': 'Length', 'type': 'Double', 'caption': 'Длина' },
        { 'id': 'Count', 'type': 'Integer', 'caption': 'Количество' },
        { 'id': 'Voltage', 'type': 'Integer', 'caption': 'Напряжение' },
        { 'id': 'Enabled', 'type': 'Boolean', 'caption': 'Включено' },
        { 'id': 'Phase', 'type': 'Enum', 'caption': 'Фазы', 'enum': 'Phases', 'default': '_1' },
        { 'id': 'Rows', 'type': 'Table', 'caption': 'Строки', 'columns': [ 'Text' ] }";

    [Theory]
    [InlineData("=@@[Power] * 2", "25")]
    [InlineData("=(@@[Power] + @@[Reserve]) / @@[Count]", "6,5")]
    [InlineData("=ROUND(@@[Length] / 1000, 2)", "12,35")]
    [InlineData("=@@[Count] + 1", "3")]
    [InlineData("=@@[Count] * @@[Voltage]", "760")]
    [InlineData("=10 % 3", "1")]
    [InlineData("=1 / 3", "0,333333333333333")]
    [InlineData("=0.1 + 0.2", "0,3")]
    [InlineData("=-@@[Reserve]", "-0,5")]
    [InlineData("=1e3 / 4", "250")]
    public void ArithmeticOfTheIssue(string format, string expected) => Assert.Equal(expected, Text(format, Sample()));

    [Theory]
    [InlineData("=@@[Voltage] > 220", "Да")]
    [InlineData("=@@[Power] == 0", "Нет")]
    [InlineData("=@@[Voltage] > 220 AND @@[Power] > 10", "Да")]
    [InlineData("=@@[Power] > 100 OR @@[Reserve] > 5", "Нет")]
    [InlineData("=@@[Power] > 10 || @@[Reserve] > 5", "Да")]
    [InlineData("=@@[Enabled] && @@[Power] > 1", "Да")]
    [InlineData("=NOT @@[Enabled]", "Нет")]
    [InlineData("=!(@@[Count] >= 2)", "Нет")]
    [InlineData("=@@[Count] != 2", "Нет")]
    [InlineData("=@@[Name] == \"Освещение\"", "Да")]
    [InlineData("=@@[Phase] == \"Трёхфазное\"", "Да")]
    [InlineData("=@@[Phase:key] == \"_3\"", "Да")]
    [InlineData("=@@[Voltage] in (220, 380)", "Да")]
    public void LogicOfTheIssue(string format, string expected) => Assert.Equal(expected, Text(format, Sample()));

    [Theory]
    [InlineData("=IF(@@[Voltage] > 220, \"Высокое\", \"Норма\")", "Высокое")]
    [InlineData("=IF(@@[Power] > 0, @@[Power] * 2, 0)", "25")]
    [InlineData("=IF(@@[Enabled], \"вкл\", \"выкл\")", "вкл")]
    [InlineData("=IF(\n  @@[Power] > 100,\n  \"Большая\",\n  IF(\n    @@[Power] > 10,\n    \"Средняя\",\n    \"Малая\"\n  )\n)", "Средняя")]
    [InlineData("=ifs(@@[Count] == 1, \"один\", @@[Count] == 2, \"два\", \"много\")", "два")]
    // The branch that is not selected is not evaluated: its division by zero is not an error.
    [InlineData("=IF(@@[Count] > 100, 10 % (@@[Count] - 2), \"нет\")", "нет")]
    [InlineData("=IF(@@[Count] == 2, \"да\", @@[Text] * 2)", "да")]
    public void ConditionsOfTheIssue(string format, string expected) => Assert.Equal(expected, Text(format, Sample()));

    [Theory]
    [InlineData("=\"ЩУ\" & @@[Number] & \".\" & @@[Group]", "ЩУ1.3")]
    // A reference alone keeps the text of the string mode: Editor.Precision and the decimal separator.
    [InlineData("=\"P=\" & @@[Power] & \" кВт\"", "P=12,50 кВт")]
    [InlineData("=\"Мощность: \" & @@[Power] & \" кВт\"", "Мощность: 12,50 кВт")]
    [InlineData("=@@[Name] & \" / \" & @@[Group]", "Освещение / 3")]
    [InlineData("=@@[Power]", "12,50")]
    [InlineData("=@@[Enabled] & \"; \" & @@[Phase] & \"; \" & @@[Phase:key]", "Да; Трёхфазное; _3")]
    // & binds weaker than arithmetic and stronger than comparisons, as in Excel.
    [InlineData("=\"Σ=\" & @@[Power] * 2 & \" кВт\"", "Σ=25 кВт")]
    [InlineData("=\"Σ=\" & (@@[Power] + @@[Reserve]) & \" кВт\"", "Σ=13 кВт")]
    [InlineData("=@@[Number] & @@[Group] == \"13\"", "Да")]
    [InlineData("=IF(@@[Enabled] && @@[Count] > 1, \"QF\" & @@[Count], \"-\")", "QF2")]
    [InlineData("=\"a&b\" & 'c\"&\"d' & \"e\\\"&\"", "a&bc\"&\"de\"&")]
    [InlineData("=\"Текст @@[Power]\"", "Текст @@[Power]")]
    [InlineData("=\"=\" & 1", "=1")]
    public void ConcatenationOfTheIssue(string format, string expected) => Assert.Equal(expected, Text(format, Sample()));

    [Theory]
    [InlineData("=SIN(30)", "0,5")]
    [InlineData("=COS(60)", "0,5")]
    [InlineData("=COS(90)", "0")]
    [InlineData("=SIN(180)", "0")]
    [InlineData("=TAN(45)", "1")]
    [InlineData("=ASIN(1)", "90")]
    [InlineData("=ACOS(0.5)", "60")]
    [InlineData("=ATAN(1)", "45")]
    [InlineData("=ATAN2(1, 1)", "45")]
    [InlineData("=DEGREES(PI())", "180")]
    [InlineData("=RADIANS(180)", "3,14159265358979")]
    [InlineData("=SQRT(16) + ABS(-1)", "5")]
    [InlineData("=POW(2, 10)", "1024")]
    [InlineData("=MAX(1, 2.5) + MIN(3, 4)", "5,5")]
    [InlineData("=FLOOR(2.7) + CEILING(2.1)", "5")]
    [InlineData("=ROUND(2.5, 0)", "3")]
    [InlineData("=TRUNCATE(-2.7)", "-2")]
    [InlineData("=LOG10(1000)", "3")]
    [InlineData("=TEXT(@@[Power] / 3, \"0.000\")", "4,167")]
    [InlineData("=\"L=\" & TEXT(@@[Length], \"N0\")", "L=12 345")]
    public void MathFunctionsWorkInDegrees(string format, string expected) => Assert.Equal(expected, Text(format, Sample()));

    [Fact]
    public void LegacyFormatsKeepTheStringSubstitution()
    {
        var values = Sample();
        Assert.Equal("ЩУ1.3", Text("ЩУ@@[Number].@@[Group]", values));
        Assert.Equal("P=12,50 кВт", Text("P=@@[Power] кВт", values));
        Assert.Equal("@@1 Да Трёхфазное _3 2", Text("@@@@@@[Number] @@[Enabled] @@[Phase] @@[Phase:key] @@[Count]", values));
        // An expression is only a format that starts with =; \= at the start is the text =.
        Assert.Equal("Σ = @@[Power] * 2", Text("Σ = @@[Power] * 2", values).Replace("12,50", "@@[Power]"));
        Assert.Equal("=12,50 * 2", Text("\\=@@[Power] * 2", values));
        Assert.Equal("\\=1", Text("\\\\=@@[Number]", values));
        Assert.Equal("a\\=1", Text("a\\=@@[Number]", values));
        Assert.False(Snapshot("\\=@@[Power]").Formulas.Find("F")!.IsExpression);
        Assert.True(Snapshot("=@@[Power]").Formulas.Find("F")!.IsExpression);
    }

    [Fact]
    public void ReferencesAreTypedParametersAndJoinTheGraph()
    {
        var snapshot = Snapshot("=@@[Power] * 2 + @@[Count] & \" @@[Reserve]\"",
            ("Twice", "=@@[F] & \"!\""), ("Plain", "[@@[Twice]]"), ("Sum", "=@@[Total] + 1"), ("Total", "=@@[Power] + @@[Reserve]"));
        var formulas = snapshot.Formulas;
        // A reference inside a string literal is text: it is neither a value nor an edge of the graph.
        Assert.Equal(new[] { "Power", "Count" }, formulas.Find("F")!.References);
        Assert.Equal(new[] { "F", "Twice", "Plain" }, formulas.Dependents("Count").Select(formula => formula.Id));
        Assert.DoesNotContain(formulas.Dependents("Reserve"), formula => formula.Id == "F");
        Assert.Equal(new[] { "Total", "Sum" }, formulas.Dependents("Reserve").Select(formula => formula.Id));
        Assert.Empty(formulas.Diagnostics);

        var calculation = Calculate(Sample(), snapshot);
        Assert.Equal("27 @@[Reserve]", calculation.Text("F"));
        Assert.Equal("27 @@[Reserve]!", calculation.Text("Twice"));
        Assert.Equal("[27 @@[Reserve]!]", calculation.Text("Plain"));
        // A CalculatedString is a value of its computed text: 13 → "13" → 13 + 1.
        Assert.Equal("13", calculation.Text("Total"));
        Assert.Equal("14", calculation.Text("Sum"));
        var count = calculation.EvaluationCount;

        // A change recalculates only its dependents, in the order of the graph.
        Assert.Equal(new[] { "F", "Twice", "Plain" }, calculation.SetValue("Count", 4).Select(result => result.Id));
        Assert.Equal(count + 3, calculation.EvaluationCount);
        Assert.Equal("[29 @@[Reserve]!]", calculation.Text("Plain"));
        Assert.Equal(new[] { "Total", "Sum" }, calculation.SetValue("Reserve", 1.25).Select(result => result.Id));
        Assert.Equal("13,75", calculation.Text("Total"));
        Assert.Equal("14,75", calculation.Text("Sum"));
        Assert.Throws<ArgumentException>(() => calculation.SetValue("F", "вручную"));

        // Missing values: the parameter default (Power 0), otherwise null — arithmetic with null is empty.
        var empty = Calculate(V2("B"), snapshot);
        Assert.Equal("", empty.Text("Total"));
        Assert.Equal("0", Text("=@@[Power] * 2", V2("C")));
        // A missing value alone is empty, as in the string mode; in a comparison it is not equal to a value.
        Assert.Equal("", Text("=@@[Enabled]", V2("C")));
        Assert.Equal("Нет", Text("=@@[Enabled] == true", V2("C")));
        Assert.Equal("Однофазное", Text("=@@[Phase]", V2("C")));
    }

    [Fact]
    public void TextErrorsAreFormulaErrors()
    {
        AssertFormulaError("=@@[Missing] + 1", "неизвестная ссылка @@[Missing]");
        AssertFormulaError("=@@[Rows] & \"!\"", "таблица Rows не подставляется в @@[Rows]");
        AssertFormulaError("=@@[Power:key]", "@@[Power:key]: модификатор :key применим только к Enum");
        AssertFormulaError("=Power * 2", "неизвестное имя Power");
        AssertFormulaError("=[Power] * 2", "неизвестное имя Power");
        AssertFormulaError("=FOO(1)", "неизвестная функция FOO");
        AssertFormulaError("=EscapeLike(\"a\")", "неизвестная функция EscapeLike");
        AssertFormulaError("=1 +", "ошибка синтаксиса выражения");
        AssertFormulaError("=", "пустая формула");
        AssertFormulaError("=\"ЩУ", "незакрытая строка в позиции 1");
        AssertFormulaError("=(1 + 2", "незакрытая скобка");
        AssertFormulaError("=1 + 2)", "лишняя закрывающая скобка в позиции 6");
        AssertFormulaError("=@@[Power", "незакрытая ссылка @@[ в позиции 1");
        AssertFormulaError("=@@[Phase:caption]", "недопустимая ссылка @@[Phase:caption]");
        AssertFormulaError("=@Power", "недопустимый символ @ в позиции 1");
        AssertFormulaError("=& \"a\"", "нет значения слева от &");
        AssertFormulaError("=\"a\" & ", "нет значения справа от &");
        AssertFormulaError("=" + new string('(', 101) + "1" + new string(')', 101), "формула слишком сложная");
        AssertFormulaError("=1" + string.Concat(Enumerable.Repeat(" + 1", 150)), "формула слишком сложная");
        AssertFormulaError("=\"" + new string('x', FormulaExpression.MaxLength) + "\"", "формула длиннее 4096 символов");
        // A formula over an erroneous one is an error as well.
        var snapshot = Snapshot("=@@[Missing]", ("Next", "=@@[F] & \"!\""));
        Assert.Contains("зависит от ошибочного параметра F", snapshot.Formulas.Find("Next")!.Errors);
        Assert.Equal(FormulaExpression.ErrorText, Calculate(Sample(), snapshot).Text("Next"));
    }

    [Fact]
    public void CyclesKeepTheirDiagnostics()
    {
        var snapshot = Snapshot("=@@[B] + 1", ("B", "=@@[F] * 2"), ("C", "=\"@@[C]\" & 1"), ("D", "=@@[D]"));
        Assert.True(snapshot.Formulas.Find("F")!.InCycle);
        Assert.Contains("циклическая ссылка: F → B → F", snapshot.Formulas.Find("F")!.Errors);
        Assert.Contains("циклическая ссылка: D → D", snapshot.Formulas.Find("D")!.Errors);
        // A self reference inside a string literal is not a cycle.
        Assert.False(snapshot.Formulas.Find("C")!.IsError);
        var calculation = Calculate(Sample(), snapshot);
        Assert.Equal(FormulaExpression.ErrorText, calculation.Text("F"));
        Assert.Equal(FormulaExpression.ErrorText, calculation.Text("B"));
        Assert.Equal("@@[C]1", calculation.Text("C"));
        var issues = new SchemaValidator().Validate(snapshot.Schema!);
        Assert.Contains(issues, issue => issue.Code == SchemaIssueCode.CyclicFormat && issue.Message.Contains("F → B → F"));
        Assert.DoesNotContain(issues, issue => issue.Message.Contains("C → C"));
    }

    [Fact]
    public void ValueErrorsAreErrorsOfTheObject()
    {
        var snapshot = Snapshot("=10 % @@[Count]", ("Next", "=@@[F] & \" шт\""), ("Legacy", "<@@[F]>"), ("Ok", "=@@[Count] * 2"));
        Assert.Empty(snapshot.Formulas.Diagnostics);
        var calculation = Calculate(V2("A", Pair("Count", 0)), snapshot);
        var result = calculation.Find("F")!;
        Assert.True(result.IsError);
        Assert.False(result.Formula.IsError);
        Assert.Equal("деление на ноль", result.Error);
        Assert.Equal(FormulaExpression.ErrorText, result.Text);
        Assert.Equal("зависит от ошибочного параметра F", calculation.Find("Next")!.Error);
        Assert.Equal("зависит от ошибочного параметра F", calculation.Find("Legacy")!.Error);
        Assert.Equal("0", calculation.Text("Ok"));
        Assert.Contains("F = «#ОШИБКА» !", calculation.Describe());

        // The value changes, the error goes away.
        calculation.SetValue("Count", 3);
        Assert.False(calculation.Find("F")!.IsError);
        Assert.Equal("1", calculation.Text("F"));
        Assert.Equal("1 шт", calculation.Text("Next"));
        Assert.Equal("<1>", calculation.Text("Legacy"));

        Assert.Contains("деление на ноль", Error("=1 / 0", Sample()));
        Assert.Contains("деление на ноль", Error("=@@[Power] / (@@[Count] - 2)", Sample()));
        Assert.Contains("значение не является числом", Error("=@@[Text] * 2", Sample()));
        Assert.Contains("переполнение", Error("=2147483647 + @@[Count]", Sample()));
        Assert.Contains("TAN(90°) не определён", Error("=TAN(90)", Sample()));
        Assert.Contains("значение не является числом", Error("=SQRT(\"x\")", Sample()));
        Assert.Contains("ожидается аргументов", Error("=SIN(1, 2)", Sample()));
    }

    [Fact]
    public void ErroneousPersistResultsAreNotWritten()
    {
        var snapshot = new SchemaSnapshot(Load(Schema("=\"QF\" & 10 % @@[Count]", persist: true)));
        var patch = new XDataPatch(snapshot, newGuid: () => Guid.Empty, culture: Comma);
        var written = patch.Apply(V2("A"), SelectionEdit.SetParameter("Count", 3));
        Assert.Equal(new[] { "Count", "3", "F", "QF1" }, Payload(written));
        // A value the formula cannot be computed from leaves the stored result as it was.
        var failed = patch.Apply(written, SelectionEdit.SetParameter("Count", 0));
        Assert.Equal(new[] { "Count", "0", "F", "QF1" }, Payload(failed));
        var calculation = new ObjectCalculation(XDataV2.Read(failed, snapshot), Comma);
        Assert.False(calculation.Find("F")!.IsStale);
        Assert.Equal(Payload(failed), Payload(patch.Apply(failed, SelectionEdit.RecalculatePersisted())));
    }

    [Fact]
    public void TheValidatorChecksFormulas()
    {
        Assert.Contains(Validate("=1 +"), issue => issue.Code == SchemaIssueCode.InvalidFormat && issue.Message.Contains("ошибка синтаксиса"));
        Assert.Contains(Validate("=@@[Missing] * 2"), issue => issue.Code == SchemaIssueCode.UnknownReference && issue.Message.Contains("@@[Missing]"));
        Assert.Contains(Validate("=FOO(1)"), issue => issue.Code == SchemaIssueCode.InvalidFormat && issue.Message.Contains("неизвестная функция FOO"));
        Assert.DoesNotContain(Validate("=\"@@[Missing]\" & @@[Power] * 2"), issue => issue.Severity == SchemaIssueSeverity.Error);
        Assert.DoesNotContain(Validate("=IF(@@[Voltage] > 220, \"Высокое\", \"Норма\")"), issue => issue.Severity == SchemaIssueSeverity.Error);
    }

    [Fact]
    public void RenamingKeepsStringLiterals()
    {
        Assert.Equal("=@@[Pwr] * 2 & \"@@[Power]\" & '@@[Power]' & @@[Pwr:key]",
            FormulaExpression.RenameReference("=@@[Power] * 2 & \"@@[Power]\" & '@@[Power]' & @@[power:key]", "Power", "Pwr"));
        Assert.Equal("=\"a\\\"@@[Power]\" & @@[Pwr]", FormulaExpression.RenameReference("=\"a\\\"@@[Power]\" & @@[Power]", "Power", "Pwr"));
    }

    [Fact]
    public void TranslationReplacesOnlyTheConcatenation()
    {
        var expression = FormulaExpression.Compile("=\"a && b\" & (@@[Power] && 'c & d') && @@[Count] & 'x'");
        Assert.Null(expression.Error);
        Assert.Equal(" CONCAT(\"a && b\", ([Power] && 'c & d')) && CONCAT([Count], 'x') ", expression.Translated);
        Assert.Equal(new[] { "Power", "Count" }, expression.References.Select(fragment => fragment.Reference));
        Assert.Equal("[Power] > 1 AND [Count] < 3", FormulaExpression.Compile("=@@[Power] > 1 AND @@[Count] < 3").Translated);
    }

    [Fact]
    public void PathologicalFormulasDoNotExhaustTheStack()
    {
        // AutoCAD runs the palette on its main thread; a formula of the maximal length must not overflow a small stack.
        var formats = new[]
        {
            "=" + new string('-', FormulaExpression.MaxLength - 2) + "1",
            "=" + new string('!', FormulaExpression.MaxLength - 2) + "1",
            "=" + new string('(', FormulaExpression.MaxLength - 1),
            "=" + string.Concat(Enumerable.Repeat("1+", FormulaExpression.MaxLength / 2 - 1)) + "1",
            "=" + string.Concat(Enumerable.Repeat("IF(1,", FormulaExpression.MaxLength / 5 - 1)),
            "=" + string.Concat(Enumerable.Repeat("\"a\"&", FormulaExpression.MaxLength / 4 - 1)) + "1",
        };
        var errors = new string?[formats.Length];
        var thread = new Thread(() =>
        {
            for (var i = 0; i < formats.Length; i++) errors[i] = FormulaExpression.Compile(formats[i]).Error;
        }, 256 * 1024);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        for (var i = 0; i < formats.Length - 1; i++) Assert.NotNull(errors[i]);
        Assert.Null(errors[formats.Length - 1]);
    }

    private static void AssertFormulaError(string format, string error)
    {
        var snapshot = Snapshot(format);
        var formula = snapshot.Formulas.Find("F")!;
        Assert.True(formula.IsError, format);
        Assert.Contains(error, formula.Error);
        var result = Calculate(Sample(), snapshot).Find("F")!;
        Assert.True(result.IsError);
        Assert.Equal(FormulaExpression.ErrorText, result.Text);
    }

    private static EntityDataSnapshot Sample() => V2("A", Pair("Number", "1"), Pair("Group", "3"), Pair("Name", "Освещение"), Pair("Text", "abc"),
        Pair("Power", 12.5), Pair("Reserve", 0.5), Pair("Length", 12345.0), Pair("Count", 2), Pair("Voltage", 380), Pair("Enabled", true), Pair("Phase", "_3"));

    private static string Text(string format, EntityDataSnapshot values)
    {
        var result = Calculate(values, Snapshot(format)).Find("F")!;
        Assert.False(result.IsError, format + ": " + result.Error);
        return result.Text;
    }

    private static string Error(string format, EntityDataSnapshot values)
    {
        var result = Calculate(values, Snapshot(format)).Find("F")!;
        Assert.True(result.IsError, format + " = " + result.Text);
        Assert.Equal(FormulaExpression.ErrorText, result.Text);
        return result.Error!;
    }

    private static IReadOnlyList<SchemaIssue> Validate(string format) => new SchemaValidator().Validate(Snapshot(format).Schema!);

    private static ObjectCalculation Calculate(EntityDataSnapshot values, SchemaSnapshot schema) => new ObjectCalculation(XDataV2.Read(values, schema), Comma);

    private static SchemaSnapshot Snapshot(string format, params (string Id, string Format)[] others) => new SchemaSnapshot(Load(Schema(format, others: others)));

    /// <summary>The parameters of the examples, the formula <c>F</c> and other formulas; formats are JSON-escaped.</summary>
    private static string Schema(string format, bool persist = false, params (string Id, string Format)[] others)
    {
        var formulas = new[] { ("F", format) }.Concat(others)
            .Select(item => "{ \"id\": \"" + item.Item1 + "\", \"type\": \"CalculatedString\", \"caption\": \"" + item.Item1 + "\", \"format\": " + Quote(item.Item2) +
                (persist && item.Item1 == "F" ? ", \"persist\": true" : "") + " }");
        return "{ \"format\": \"BobrovXDATA.schema\", \"version\": 1, \"parameters\": [" + ParameterSchemaJsonTests.Json(Parameters) + ", " + string.Join(", ", formulas) + "], " +
            "\"enums\": [ { \"id\": \"Phases\", \"caption\": \"Фазы\", \"items\": [ { \"key\": \"_1\", \"caption\": \"Однофазное\" }, { \"key\": \"_3\", \"caption\": \"Трёхфазное\" } ] } ] }";
    }

    private static string Quote(string text)
    {
        var result = new StringBuilder("\"");
        foreach (var c in text)
            result.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", _ => c.ToString() });
        return result.Append('"').ToString();
    }

    private static SchemaNodReadResult Load(string json)
    {
        var result = ParameterSchemaJson.Load(json);
        Assert.NotNull(result.Schema);
        return new SchemaNodReadResult(result.Schema, null, result.Issues);
    }

    private static CultureInfo DecimalComma()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = " ";
        return culture;
    }

    private static DataValue[] Pair(string id, object value) => new[]
    {
        new DataValue(1000, id),
        value switch
        {
            double number => new DataValue(1040, number),
            int number => new DataValue(1071, number),
            bool flag => new DataValue(1070, (short)(flag ? 1 : 0)),
            _ => new DataValue(1000, value)
        }
    };

    private static EntityDataSnapshot V2(string handle, params DataValue[][] pairs) => new EntityDataSnapshot(handle, "Line",
        new[] { new DataValue(1001, "BobrovXDATA"), new DataValue(1070, (short)2) }.Concat(pairs.SelectMany(pair => pair)).ToArray(), Array.Empty<DataRecord>());

    private static string[] Payload(EntityDataSnapshot snapshot) => snapshot.XData.Skip(2).Select(value => XDataV2.FormatValue(value.Value)).ToArray();
}
