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
const string schemaCommandsType = "AcadPaletteSetXData.AutoCAD.SchemaCommands";
const string templateCommandsType = "AcadPaletteSetXData.AutoCAD.TemplateCommands";
const string migrationCommandsType = "AcadPaletteSetXData.AutoCAD.MigrationCommands";
foreach (var (name, targets) in new[]
{
    ("ExtensionApplication", new[] { applicationType }),
    ("CommandClass", new[] { applicationType, schemaCommandsType, templateCommandsType, migrationCommandsType }),
})
{
    var attributes = assembly.GetCustomAttributes().Select(metadata.GetCustomAttribute)
        .Where(item => AttributeName(item) == "Autodesk.AutoCAD.Runtime." + name + "Attribute").ToList();
    Check(attributes.Count == targets.Length, "Release DLL must have " + targets.Length + " AutoCAD " + name + " registration(s).");
    var registered = attributes.Select(attribute =>
    {
        var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        var attributeType = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        var apiAssembly = metadata.GetAssemblyReference((AssemblyReferenceHandle)attributeType.ResolutionScope);
        Console.WriteLine("Verified " + name + " registration via " + metadata.GetString(apiAssembly.Name) + ".");
        var hostMetadata = Assembly.Load(new AssemblyName(metadata.GetString(apiAssembly.Name)) { Version = apiAssembly.Version });
        Check(hostMetadata.GetType(AttributeName(attribute))?.IsSubclassOf(typeof(Attribute)) == true,
            "Standalone WPF host must resolve the release DLL's " + name + " attribute type.");
        var blob = metadata.GetBlobReader(attribute.Value);
        Check(blob.ReadUInt16() == 1, name + " attribute blob is malformed.");
        return blob.ReadSerializedString()!;
    }).ToList();
    foreach (var target in targets)
        Check(registered.Any(type => type == target || type.StartsWith(target + ",", StringComparison.Ordinal)),
            name + " must point to " + target + " in the release DLL.");
}

void CheckCommand(string typeName, string methodName, string commandName)
{
    var type = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition).Single(item =>
        metadata.GetString(item.Namespace) + "." + metadata.GetString(item.Name) == typeName);
    var method = type.GetMethods().Select(metadata.GetMethodDefinition)
        .Single(item => metadata.GetString(item.Name) == methodName);
    Check((method.Attributes & (MethodAttributes.Public | MethodAttributes.Static)) ==
        (MethodAttributes.Public | MethodAttributes.Static), "Command handler must be public and static.");
    var command = method.GetCustomAttributes().Select(metadata.GetCustomAttribute).Single(item =>
        AttributeName(item) == "Autodesk.AutoCAD.Runtime.CommandMethodAttribute");
    var commandBlob = metadata.GetBlobReader(command.Value);
    Check(commandBlob.ReadUInt16() == 1 && commandBlob.ReadSerializedString() == commandName,
        commandName + " must be registered on the shipped command handler.");
    Console.WriteLine("Verified command " + commandName + ".");
}

CheckCommand(applicationType, "TogglePalette", "XDATAPALETTE");
CheckCommand(schemaCommandsType, "Import", "VELEC_SCHEMA_IMPORT");
CheckCommand(schemaCommandsType, "Export", "VELEC_SCHEMA_EXPORT");
CheckCommand(schemaCommandsType, "Edit", "VELEC_SCHEMA_EDIT");
CheckCommand(templateCommandsType, "Apply", "VELEC_TEMPLATE_APPLY");
CheckCommand(migrationCommandsType, "Migrate", "VELEC_MIGRATE");
CheckCommand(migrationCommandsType, "Check", "VELEC_CHECK");

foreach (var reference in metadata.AssemblyReferences.Select(metadata.GetAssemblyReference))
    Check(!metadata.GetString(reference.Name).StartsWith("AcadPaletteSetXData.", StringComparison.Ordinal),
        "Single-DLL release must not depend on a separate plugin assembly.");
// TZXDATA stage 10: the release adds no dependencies; it references only .NET Framework 4.8 and the AutoCAD API.
var references = metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
Console.WriteLine("Assembly references: " + string.Join(", ", references) + ".");
string[] framework = { "mscorlib", "System", "WindowsBase", "PresentationCore", "PresentationFramework", "Microsoft.CSharp" };
string[] autocad = { "AcMgd", "AcDbMgd", "AcCoreMgd", "AdWindows" };
foreach (var name in references)
    Check(framework.Contains(name) || name.StartsWith("System.", StringComparison.Ordinal) || autocad.Contains(name, StringComparer.OrdinalIgnoreCase),
        "Single-DLL release must not depend on " + name + ": only .NET Framework 4.8 and the AutoCAD API are allowed.");
foreach (var typeName in new[] { "PaletteView", "PropertyGridView", "XDataWriter", "AutoCadPaletteHost", "SchemaNodWriter", "AutoCadSchemaNodTransaction", "TemplateApply", "SchemaEditorViewModel", "SchemaEditorWindow", "SchemaMigration", "DrawingCheck" })
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
