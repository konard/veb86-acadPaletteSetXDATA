// Issue #43: what NCalc 7.4.0 gives for the built-in string concatenation (ExpressionOptions.StringConcat)
// and for a single & — the reason the formula mode translates & into its own CONCAT function.
using System;
using System.Globalization;
using NCalc;

var ru = (CultureInfo)CultureInfo.GetCultureInfo("ru-RU").Clone();
foreach (var options in new[] { ExpressionOptions.None, ExpressionOptions.StringConcat })
    foreach (var text in new[] { "\"ЩУ\" + 1 + \".\" + 3", "\"P=\" + p + \" кВт\"", "\"1\" + 2", "1 + \"2\"", "\"a\" & \"b\"", "6 & 3", "b && true" })
    {
        string result;
        try
        {
            var expression = new Expression(text, ExpressionConfiguration.FromOptions(options), new ExpressionContext(), ru, null!);
            expression.Parameters["p"] = 12.5;
            expression.Parameters["b"] = true;
            var value = expression.Evaluate();
            result = value + " (" + value?.GetType().Name + ")";
        }
        catch (Exception error) { result = error.GetType().Name + ": " + error.Message; }
        Console.WriteLine($"{options,-12} {text,-28} → {result}");
    }
