using System;
using System.Linq;
using System.Reflection;
var t = typeof(NCalc.Expression).Assembly.GetType("NCalc.Helpers.BuiltInFunctionHelper")!;
foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)) { Console.WriteLine(f.Name + " " + f.FieldType); var v = f.GetValue(null); if (v is System.Collections.IEnumerable en and not string) foreach (var x in en) Console.WriteLine("  " + x); }
foreach (var p in t.GetProperties(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)) { Console.WriteLine("P " + p.Name); }
