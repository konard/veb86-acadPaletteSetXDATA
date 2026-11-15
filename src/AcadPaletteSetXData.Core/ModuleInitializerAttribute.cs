#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    /// <summary>The C# compiler emits a module initializer for methods marked with this attribute (.NET Framework, netstandard2.0).</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
#endif
