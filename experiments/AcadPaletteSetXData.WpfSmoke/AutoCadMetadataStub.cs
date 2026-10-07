using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace AcadPaletteSetXData.WpfSmoke;

// .NET Framework resolves assembly-level attribute types during WPF resource lookup.
// The standalone UI test has no AutoCAD host. Supply only its two attribute types
// in memory; never execute native commands or copy SDK assemblies into the package.
internal static class AutoCadMetadataStub
{
    private static readonly Dictionary<string, Assembly> assemblies = new(StringComparer.OrdinalIgnoreCase);

    public static void Install()
    {
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name);
        string attribute;
        if (string.Equals(name.Name, "Acdbmgd", StringComparison.OrdinalIgnoreCase))
            attribute = "ExtensionApplicationAttribute";
        else if (string.Equals(name.Name, "accoremgd", StringComparison.OrdinalIgnoreCase))
            attribute = "CommandClassAttribute";
        else
            return null;
        if (assemblies.TryGetValue(name.FullName, out var assembly)) return assembly;

        var builder = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
        var module = builder.DefineDynamicModule(name.Name!);
        var type = module.DefineType("Autodesk.AutoCAD.Runtime." + attribute,
            TypeAttributes.Public | TypeAttributes.Sealed, typeof(Attribute));
        var constructor = type.DefineConstructor(MethodAttributes.Public,
            CallingConventions.Standard, new[] { typeof(Type) });
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!);
        il.Emit(OpCodes.Ret);
        type.CreateType();
        assemblies.Add(name.FullName, builder);
        return builder;
    }
}
