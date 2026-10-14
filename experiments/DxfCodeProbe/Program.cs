// Prints DxfCode members in the 280-299 range from an AcDbMgd.dll without loading it (metadata only).
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var md = pe.GetMetadataReader();
foreach (var th in md.TypeDefinitions)
{
    var type = md.GetTypeDefinition(th);
    if (md.GetString(type.Name) != "DxfCode") continue;
    foreach (var fh in type.GetFields())
    {
        var field = md.GetFieldDefinition(fh);
        var c = field.GetDefaultValue();
        if (c.IsNil) continue;
        var value = md.GetBlobReader(md.GetConstant(c).Value).ReadInt16();
        if (value >= 280 && value < 300) Console.WriteLine($"{md.GetString(field.Name)} = {value}");
    }
}
