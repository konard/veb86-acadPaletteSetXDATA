using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using AcadPaletteSetXData.WpfSmoke;

// Read the shipped assembly without loading Autodesk or WPF into the test process.
if (args.Length != 1) throw new ArgumentException("Pass the path to acadPaletteSetXDATA.dll.");
using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var metadata = pe.GetMetadataReader();
var assembly = metadata.GetAssemblyDefinition();
Check(metadata.GetString(assembly.Name) == "acadPaletteSetXDATA", "Incorrect release assembly identity.");
AutoCadMetadataStub.Install();

string AttributeName(CustomAttribute attribute)
{
    if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
    {
        var constructorDefinition = metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
        var definition = metadata.GetTypeDefinition(constructorDefinition.GetDeclaringType());
        return metadata.GetString(definition.Namespace) + "." + metadata.GetString(definition.Name);
    }
    var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
    var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
    return metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
}

const string applicationType = "AcadPaletteSetXData.AutoCAD.PaletteApplication";
foreach (var name in new[] { "ExtensionApplication", "CommandClass" })
{
    var handle = assembly.GetCustomAttributes().SingleOrDefault(item =>
        AttributeName(metadata.GetCustomAttribute(item)) == "Autodesk.AutoCAD.Runtime." + name + "Attribute");
    Check(!handle.IsNil, "Release DLL is missing AutoCAD " + name + " registration.");
    var attribute = metadata.GetCustomAttribute(handle);
    var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
    var attributeType = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
    var apiAssembly = metadata.GetAssemblyReference((AssemblyReferenceHandle)attributeType.ResolutionScope);
    Console.WriteLine("Verified " + name + " registration via " + metadata.GetString(apiAssembly.Name) + ".");
    var hostMetadata = Assembly.Load(new AssemblyName(metadata.GetString(apiAssembly.Name)) { Version = apiAssembly.Version });
    Check(hostMetadata.GetType(AttributeName(attribute))?.IsSubclassOf(typeof(Attribute)) == true,
        "Standalone WPF host must resolve the release DLL's " + name + " attribute type.");
    var blob = metadata.GetBlobReader(attribute.Value);
    Check(blob.ReadUInt16() == 1 && blob.ReadSerializedString()!.StartsWith(applicationType, StringComparison.Ordinal),
        name + " must point to PaletteApplication in the release DLL.");
}

var application = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition).Single(type =>
    metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name) == applicationType);
var toggle = application.GetMethods().Select(metadata.GetMethodDefinition)
    .Single(method => metadata.GetString(method.Name) == "TogglePalette");
Check((toggle.Attributes & (MethodAttributes.Public | MethodAttributes.Static)) ==
    (MethodAttributes.Public | MethodAttributes.Static), "Command handler must be public and static.");
var command = toggle.GetCustomAttributes().Select(metadata.GetCustomAttribute).Single(item =>
    AttributeName(item) == "Autodesk.AutoCAD.Runtime.CommandMethodAttribute");
var commandBlob = metadata.GetBlobReader(command.Value);
Check(commandBlob.ReadUInt16() == 1 && commandBlob.ReadSerializedString() == "XDATAPALETTE",
    "XDATAPALETTE must be registered on the shipped command handler.");

foreach (var reference in metadata.AssemblyReferences.Select(metadata.GetAssemblyReference))
    Check(!metadata.GetString(reference.Name).StartsWith("AcadPaletteSetXData.", StringComparison.Ordinal),
        "Single-DLL release must not depend on a separate plugin assembly.");
foreach (var typeName in new[] { "PaletteView", "PropertyEditorView", "XDataWriter", "AutoCadPaletteHost" })
    Check(metadata.TypeDefinitions.Select(metadata.GetTypeDefinition).Any(type => metadata.GetString(type.Name) == typeName),
        "Release must contain " + typeName + ".");
Check(metadata.ManifestResources.Select(metadata.GetManifestResource)
    .Any(resource => metadata.GetString(resource.Name) == "acadPaletteSetXDATA.g.resources"),
    "Release must contain compiled WPF resources.");
Console.WriteLine("Passed release command registration, shared implementation and WPF resource checks.");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
