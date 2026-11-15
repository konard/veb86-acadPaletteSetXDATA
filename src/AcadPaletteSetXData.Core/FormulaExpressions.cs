using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NCalc;
using NCalc.Handlers;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// Formula mode of <c>CalculatedString</c> (issue #43): a <c>format</c> that starts with <c>=</c> is an NCalc expression.
/// The expression is translated once per schema snapshot — <c>@@[Id]</c> / <c>@@[Id:key]</c> outside string literals
/// become typed NCalc parameters, the Excel operator <c>&amp;</c> becomes the concatenation <c>CONCAT(…)</c> — parsed by
/// NCalc into a tree, checked (unknown names and functions, nesting) and then evaluated for every object with the
/// values of its cache.
/// </summary>
public sealed class FormulaExpression
{
    /// <summary>The longest expression accepted; longer ones are a formula error.</summary>
    public const int MaxLength = 4096;
    /// <summary>The deepest expression tree (operators, functions, parentheses) accepted; deeper ones are a formula error.</summary>
    public const int MaxDepth = 100;
    /// <summary>The text of a formula that cannot be computed on an object (the error itself is in <see cref="CalculatedValue.Error"/>).</summary>
    public const string ErrorText = "#ОШИБКА";
    /// <summary>The function the Excel operator <c>&amp;</c> is translated to.</summary>
    public const string ConcatFunction = "CONCAT";

    private const ExpressionOptions Options =
        ExpressionOptions.IgnoreCaseAtBuiltInFunctions | ExpressionOptions.NoCache | ExpressionOptions.OverflowProtection | ExpressionOptions.RoundAwayFromZero;

    /// <summary>Functions of NCalc that formulas may call (the names are case-insensitive); others are a formula error.</summary>
    private static readonly HashSet<string> BuiltInFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Abs", "Ceiling", "Exp", "Floor", "IEEERemainder", "Ln", "Log", "Log10", "Max", "Min", "Pow", "Round", "Sign", "Sqrt",
        "Truncate", "if", "ifs", "in", "isNull", "isNullOrEmpty"
    };

    /// <summary>Functions implemented here; the trigonometry works in degrees.</summary>
    private static readonly HashSet<string> OwnFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ConcatFunction, "PI", "TEXT", "Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Atan2", "Radians", "Degrees"
    };

    private static readonly string[] Operators = { "==", "!=", "<>", "<=", ">=", "<<", ">>", "||", "=", "<", ">", "|", "^", "?", ":", "," };
    private static readonly string[] OperatorWords = { "and", "or", "in", "like" };

    private readonly Dictionary<string, FormatFragment> parameters;
    private readonly LogicalExpression? tree;

    private FormulaExpression(string source, string translated, List<FormatFragment> references, Dictionary<string, FormatFragment> parameters, LogicalExpression? tree, string? error)
    {
        Source = source; Translated = translated; References = references; this.parameters = parameters; this.tree = tree; Error = error;
    }

    /// <summary>The expression after the leading <c>=</c>.</summary>
    public string Source { get; }
    /// <summary>The NCalc text: references as <c>[Id]</c>, <c>&amp;</c> as <c>CONCAT(…)</c>.</summary>
    public string Translated { get; }
    /// <summary>The references <c>@@[Id]</c> outside string literals, in the order of the text.</summary>
    public IReadOnlyList<FormatFragment> References { get; }
    /// <summary>The first syntax error; null when the expression can be evaluated.</summary>
    public string? Error { get; }

    /// <summary>
    /// Translates and parses a formula: <paramref name="format"/> starts with <c>=</c> (<see cref="CalculatedFormat.IsFormula"/>).
    /// Never throws: a problem of the text is returned in <see cref="Error"/>.
    /// </summary>
    public static FormulaExpression Compile(string format)
    {
        if (format is null) throw new ArgumentNullException(nameof(format));
        var source = CalculatedFormat.IsFormula(format) ? format.Substring(1) : format;
        var references = new List<FormatFragment>();
        var parameters = new Dictionary<string, FormatFragment>(StringComparer.Ordinal);
        var translated = "";
        try
        {
            if (source.Trim().Length == 0) throw new FormulaException("пустая формула после =");
            if (format.Length > MaxLength) throw new FormulaException("формула длиннее " + MaxLength.ToString(CultureInfo.InvariantCulture) + " символов");
            translated = Translate(format, CalculatedFormat.IsFormula(format) ? 1 : 0, references, parameters);
            var tree = new Expression(translated, ExpressionConfiguration.FromOptions(Options), new ExpressionContext(), CultureInfo.InvariantCulture, null!).GetLogicalExpression();
            if (tree is null) throw new FormulaException("ошибка синтаксиса выражения");
            Check(tree, parameters);
            return new FormulaExpression(source, translated, references, parameters, tree, null);
        }
        catch (FormulaException error)
        {
            return new FormulaExpression(source, translated, references, parameters, null, error.Message);
        }
        catch (Exception error) when (!(error is OutOfMemoryException))
        {
            // NCalc reports a syntax error with the position in the inner exception.
            var detail = error.InnerException?.Message ?? error.Message;
            return new FormulaExpression(source, translated, references, parameters, null, "ошибка синтаксиса выражения: " + detail);
        }
    }

    /// <summary>
    /// Renames the references <c>@@[old]</c> / <c>@@[old:key]</c> outside string literals; text inside strings stays as is.
    /// </summary>
    public static string RenameReference(string format, string old, string value)
    {
        var result = new StringBuilder(format.Length);
        var index = 0;
        while (index < format.Length)
        {
            var c = format[index];
            if (c == '"' || c == '\'')
            {
                var end = StringEnd(format, index);
                var stop = end < 0 ? format.Length : end + 1;
                result.Append(format, index, stop - index);
                index = stop;
                continue;
            }
            if (string.CompareOrdinal(format, index, "@@[", 0, 3) == 0 && format.IndexOf(']', index + 3) is int close && close > 0)
            {
                var body = format.Substring(index + 3, close - index - 3);
                var colon = body.IndexOf(':');
                var id = colon < 0 ? body : body.Substring(0, colon);
                result.Append("@@[").Append(ParameterSchema.SameId(id, old) ? value + (colon < 0 ? "" : body.Substring(colon)) : body).Append(']');
                index = close + 1;
                continue;
            }
            result.Append(c);
            index++;
        }
        return result.ToString();
    }

    /// <summary>
    /// Evaluates the expression for one object. <paramref name="value"/> gives the typed value of a reference,
    /// <paramref name="text"/> its text as in the string mode (<c>Editor.Precision</c>, «Да»/«Нет», captions);
    /// a problem of the values (division by zero, a string in arithmetic) throws <see cref="FormulaException"/>.
    /// </summary>
    public string Evaluate(Func<FormatFragment, object?> value, Func<FormatFragment, string> text, CultureInfo culture)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (culture is null) throw new ArgumentNullException(nameof(culture));
        if (tree is null) throw new FormulaException(Error ?? "формула не разобрана");
        // A single reference keeps the text of the string mode: =@@[Power] gives 2,50 with Editor.Precision 2.
        if (tree is Identifier single && parameters.TryGetValue(single.Name, out var only)) return text(only);
        var expression = new Expression(tree, ExpressionConfiguration.FromOptions(Options), new ExpressionContext(), culture, null!);
        foreach (var pair in parameters) expression.Parameters[pair.Key] = value(pair.Value);
        expression.EvaluateFunction += (name, args) => Function(name, args, text, culture);
        object? result;
        try
        {
            result = expression.Evaluate();
        }
        catch (FormulaException) { throw; }
        catch (Exception error) when (!(error is OutOfMemoryException))
        {
            throw new FormulaException(Describe(error));
        }
        return Format(result, culture);
    }

    /// <summary>
    /// The text of a computed value: numbers with up to 15 significant digits and the decimal separator of
    /// <paramref name="culture"/> (<c>0,1 + 0,2</c> gives <c>0,3</c>), integers without separators, <c>Boolean</c> as
    /// «Да»/«Нет», null as an empty string. Infinity and NaN (division by zero) are an error.
    /// </summary>
    public static string Format(object? value, CultureInfo culture)
    {
        switch (value)
        {
            case null: return "";
            case string text: return text;
            case bool flag: return flag ? "Да" : "Нет";
            case double number:
                if (double.IsNaN(number) || double.IsInfinity(number)) throw new FormulaException("результат не является числом (деление на ноль?)");
                return (number == 0 ? 0 : number).ToString("G15", culture);
            case float number: return Format((double)number, culture);
            case decimal number: return number.ToString(culture);
            case sbyte or byte or short or ushort or int or uint or long or ulong: return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
            case IFormattable formattable: return formattable.ToString(null, culture);
            default: return value.ToString() ?? "";
        }
    }

    // ---- Translation -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The lexical translation of the expression before NCalc: string literals are copied as is (quotes and <c>\</c>
    /// escapes respected), references become bracketed parameters, and every operand list joined by a single <c>&amp;</c>
    /// becomes <c>CONCAT(a, b, …)</c>. <c>&amp;</c> binds weaker than arithmetic and stronger than comparisons, as in
    /// Excel: the operands end at comparisons, logical operators, <c>?</c> <c>:</c>, commas and parentheses.
    /// </summary>
    private static string Translate(string format, int start, List<FormatFragment> references, Dictionary<string, FormatFragment> parameters)
    {
        var frames = new Stack<Frame>();
        var frame = new Frame();
        var index = start;
        while (index < format.Length)
        {
            var c = format[index];
            if (c == '"' || c == '\'')
            {
                var end = StringEnd(format, index);
                if (end < 0) throw new FormulaException("незакрытая строка в позиции " + Position(index));
                frame.Operand.Append(format, index, end + 1 - index);
                index = end + 1;
                continue;
            }
            if (c == '@')
            {
                if (string.CompareOrdinal(format, index, "@@[", 0, 3) != 0)
                    throw new FormulaException("недопустимый символ @ в позиции " + Position(index) + ": ссылка записывается как @@[Id], текст — в кавычках");
                var close = format.IndexOf(']', index + 3);
                if (close < 0) throw new FormulaException("незакрытая ссылка @@[ в позиции " + Position(index));
                var body = format.Substring(index + 3, close - index - 3);
                var colon = body.IndexOf(':');
                var id = colon < 0 ? body : body.Substring(0, colon);
                if (!SchemaValidator.IsValidId(id) || colon >= 0 && body.Substring(colon + 1) != "key")
                    throw new FormulaException("недопустимая ссылка @@[" + body + "]");
                var fragment = FormatFragment.Parameter(id, colon >= 0);
                references.Add(fragment);
                if (!parameters.ContainsKey(body)) parameters.Add(body, fragment);
                frame.Operand.Append('[').Append(body).Append(']');
                index = close + 1;
                continue;
            }
            if (c == '[' || c == '{')
            {
                // A parameter written in the NCalc way: kept, and refused by the check of names after parsing.
                var close = format.IndexOf(c == '[' ? ']' : '}', index + 1);
                var stop = close < 0 ? format.Length : close + 1;
                frame.Operand.Append(format, index, stop - index);
                index = stop;
                continue;
            }
            if (c == '&')
            {
                if (index + 1 < format.Length && format[index + 1] == '&') { frame.Boundary("&&", index); index += 2; continue; }
                frame.Concatenate(index);
                index++;
                continue;
            }
            if (c == '(')
            {
                if (frames.Count >= MaxDepth) throw new FormulaException("формула слишком сложная: вложенность скобок больше " + MaxDepth.ToString(CultureInfo.InvariantCulture));
                frames.Push(frame);
                frame = new Frame();
                index++;
                continue;
            }
            if (c == ')')
            {
                if (frames.Count == 0) throw new FormulaException("лишняя закрывающая скобка в позиции " + Position(index));
                var inner = frame.Finish(index);
                frame = frames.Pop();
                frame.Operand.Append('(').Append(inner).Append(')');
                index++;
                continue;
            }
            if (Operators.FirstOrDefault(item => string.CompareOrdinal(format, index, item, 0, item.Length) == 0) is string op)
            {
                frame.Boundary(op, index);
                index += op.Length;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var end = index;
                while (end < format.Length && (char.IsLetterOrDigit(format[end]) || format[end] == '_')) end++;
                var word = format.Substring(index, end - index);
                var previous = index == 0 ? ' ' : format[index - 1];
                // A word right after a digit is a part of a number (1e5) or an error that NCalc reports.
                if (!char.IsDigit(previous) && OperatorWords.Contains(word, StringComparer.OrdinalIgnoreCase)) { frame.Boundary(word, index); index = end; continue; }
                if (!char.IsDigit(previous) && string.Equals(word, "not", StringComparison.OrdinalIgnoreCase) && NextWord(format, end, out var next, out var after) &&
                    (string.Equals(next, "in", StringComparison.OrdinalIgnoreCase) || string.Equals(next, "like", StringComparison.OrdinalIgnoreCase)))
                {
                    frame.Boundary(format.Substring(index, after - index), index);
                    index = after;
                    continue;
                }
                frame.Operand.Append(word);
                index = end;
                continue;
            }
            frame.Operand.Append(c);
            index++;
        }
        if (frames.Count != 0) throw new FormulaException("незакрытая скобка: не хватает " + frames.Count.ToString(CultureInfo.InvariantCulture) + " «)»");
        return frame.Finish(format.Length);
    }

    private static bool NextWord(string text, int index, out string word, out int end)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        end = index;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
        word = text.Substring(index, end - index);
        return word.Length != 0;
    }

    /// <summary>The closing quote of the string literal that opens at <paramref name="start"/>, or -1; <c>\</c> escapes the next character.</summary>
    private static int StringEnd(string text, int start)
    {
        var quote = text[start];
        for (var index = start + 1; index < text.Length; index++)
        {
            if (text[index] == '\\') { index++; continue; }
            if (text[index] == quote) return index;
        }
        return -1;
    }

    /// <summary>Positions in messages count from the first character of <c>format</c>, the leading <c>=</c> included, like the string mode.</summary>
    private static string Position(int index) => index.ToString(CultureInfo.InvariantCulture);

    /// <summary>One level of parentheses: the text done so far and the operands of the current <c>&amp;</c> list.</summary>
    private sealed class Frame
    {
        private readonly StringBuilder output = new StringBuilder();
        private readonly List<string> operands = new List<string>();
        public StringBuilder Operand { get; } = new StringBuilder();

        public void Concatenate(int index)
        {
            var operand = Operand.ToString().Trim();
            if (operand.Length == 0) throw new FormulaException("нет значения слева от & в позиции " + Position(index));
            operands.Add(operand);
            Operand.Clear();
        }

        public void Boundary(string op, int index)
        {
            EndOperands(index);
            output.Append(op);
        }

        public string Finish(int index)
        {
            EndOperands(index);
            return output.ToString();
        }

        private void EndOperands(int index)
        {
            if (operands.Count == 0) { output.Append(Operand); Operand.Clear(); return; }
            var last = Operand.ToString().Trim();
            if (last.Length == 0) throw new FormulaException("нет значения справа от & перед позицией " + Position(index));
            operands.Add(last);
            output.Append(' ').Append(ConcatFunction).Append('(').Append(string.Join(", ", operands)).Append(") ");
            operands.Clear();
            Operand.Clear();
        }
    }

    // ---- Checks ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The tree is walked without recursion: the depth is limited (deep trees would exhaust the stack of AutoCAD when
    /// NCalc evaluates them recursively), names must be references and functions must be known.
    /// </summary>
    private static void Check(LogicalExpression tree, Dictionary<string, FormatFragment> parameters)
    {
        var stack = new Stack<KeyValuePair<LogicalExpression, int>>();
        stack.Push(new KeyValuePair<LogicalExpression, int>(tree, 1));
        while (stack.Count != 0)
        {
            var item = stack.Pop();
            var depth = item.Value;
            if (depth > MaxDepth) throw new FormulaException("формула слишком сложная: вложенность больше " + MaxDepth.ToString(CultureInfo.InvariantCulture));
            void Push(LogicalExpression? child) { if (child != null) stack.Push(new KeyValuePair<LogicalExpression, int>(child, depth + 1)); }
            switch (item.Key)
            {
                case Identifier identifier:
                    if (!parameters.ContainsKey(identifier.Name))
                        throw new FormulaException("неизвестное имя " + identifier.Name + ": ссылка на параметр записывается как @@[Id], текст — в кавычках");
                    break;
                case Function function:
                    var name = function.Identifier.Name;
                    if (!BuiltInFunctions.Contains(name) && !OwnFunctions.Contains(name)) throw new FormulaException("неизвестная функция " + name);
                    foreach (var argument in function.Parameters) Push(argument);
                    break;
                case BinaryExpression binary: Push(binary.LeftExpression); Push(binary.RightExpression); break;
                case UnaryExpression unary: Push(unary.Expression); break;
                case TernaryExpression ternary: Push(ternary.LeftExpression); Push(ternary.MiddleExpression); Push(ternary.RightExpression); break;
                case LogicalExpressionList list: foreach (var element in list) Push(element); break;
            }
        }
    }

    // ---- Evaluation --------------------------------------------------------------------------------------------------

    private void Function(string name, FunctionEventArgs args, Func<FormatFragment, string> text, CultureInfo culture)
    {
        var data = args.Parameters;
        double Number(int index)
        {
            var value = data.Evaluate(index);
            if (value is null) throw new FormulaException(name + ": аргумент " + (index + 1).ToString(CultureInfo.InvariantCulture) + " не задан");
            if (value is bool) throw new FormulaException(name + ": аргумент " + (index + 1).ToString(CultureInfo.InvariantCulture) + " должен быть числом");
            try { return Convert.ToDouble(value, culture); }
            catch (FormatException) { throw new FormulaException(name + ": «" + value + "» не является числом"); }
        }
        void Arguments(int count)
        {
            if (data.Count != count) throw new FormulaException(name + ": ожидается аргументов — " + count.ToString(CultureInfo.InvariantCulture));
        }

        switch (name.ToUpperInvariant())
        {
            case ConcatFunction:
                var result = new StringBuilder();
                for (var index = 0; index < data.Count; index++)
                    result.Append(data[index] is Identifier identifier && parameters.TryGetValue(identifier.Name, out var fragment)
                        ? text(fragment)
                        : Format(data.Evaluate(index), culture));
                args.Result = result.ToString();
                break;
            case "PI": Arguments(0); args.Result = Math.PI; break;
            case "TEXT":
                Arguments(2);
                var format = data.Evaluate(1) as string ?? throw new FormulaException("TEXT: формат должен быть строкой, например \"0.00\"");
                var value = data.Evaluate(0);
                args.Result = value is IFormattable formattable && !(value is bool) ? formattable.ToString(format, culture) : Format(value, culture);
                break;
            case "SIN": Arguments(1); args.Result = Sin(Number(0)); break;
            case "COS": Arguments(1); args.Result = Sin(Number(0) + 90); break;
            case "TAN":
                Arguments(1);
                var angle = Number(0);
                var cosine = Sin(angle + 90);
                if (cosine == 0) throw new FormulaException("TAN(" + angle.ToString(culture) + "°) не определён");
                args.Result = Sin(angle) / cosine;
                break;
            case "ASIN": Arguments(1); args.Result = Degrees(Math.Asin(Number(0))); break;
            case "ACOS": Arguments(1); args.Result = Degrees(Math.Acos(Number(0))); break;
            case "ATAN": Arguments(1); args.Result = Degrees(Math.Atan(Number(0))); break;
            case "ATAN2": Arguments(2); args.Result = Degrees(Math.Atan2(Number(0), Number(1))); break;
            case "RADIANS": Arguments(1); args.Result = Number(0) * Math.PI / 180; break;
            case "DEGREES": Arguments(1); args.Result = Degrees(Number(0)); break;
        }
    }

    /// <summary>The sine of an angle in degrees; multiples of 90° are exact (<c>SIN(180)</c> is 0, not 1.2E-16).</summary>
    private static double Sin(double degrees)
    {
        if (double.IsNaN(degrees) || double.IsInfinity(degrees)) return double.NaN;
        var turn = degrees % 360;
        if (turn < 0) turn += 360;
        if (turn % 90 == 0) return new[] { 0.0, 1.0, 0.0, -1.0 }[(int)(turn / 90) % 4];
        return Math.Sin(turn * Math.PI / 180);
    }

    private static double Degrees(double radians) => radians * 180 / Math.PI;

    private static string Describe(Exception error) => error switch
    {
        DivideByZeroException => "деление на ноль",
        // With OverflowProtection a division by zero gives the overflow of the infinite result.
        OverflowException => "переполнение или деление на ноль",
        FormatException => "значение не является числом: " + error.Message,
        InvalidCastException or InvalidOperationException => "несовместимые типы значений: " + error.Message,
        ArgumentException => "неверные аргументы функции: " + error.Message,
        _ => "ошибка вычисления: " + error.Message
    };
}

/// <summary>A problem of a formula of the formula mode: its text, or its evaluation on one object.</summary>
public sealed class FormulaException : Exception
{
    public FormulaException(string message) : base(message) { }
}
