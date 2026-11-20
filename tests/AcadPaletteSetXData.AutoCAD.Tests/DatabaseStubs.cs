using System;
using System.Collections.Generic;
using System.Linq;

namespace Autodesk.AutoCAD.Runtime
{
    public sealed class Exception : System.Exception { }
}

namespace Autodesk.AutoCAD.DatabaseServices
{
    public enum OpenMode { ForRead }

    public sealed record ObjectId(int Handle, string DxfName = "", bool IsErased = false)
    {
        public static ObjectId Null { get; } = new ObjectId(0);
        public bool IsNull => Handle == 0;
        public bool IsValid => !IsNull;
        public ObjectId ObjectClass => this;
    }

    public sealed class Transaction
    {
        public IDictionary<ObjectId, object> Objects { get; } = new Dictionary<ObjectId, object>();
        public object GetObject(ObjectId id, OpenMode mode) => Objects.TryGetValue(id, out var value)
            ? value : throw new Runtime.Exception();
    }

    public readonly record struct TypedValue(int TypeCode, object Value);
    public sealed class ResultBuffer : List<TypedValue>, IDisposable
    {
        public void Dispose() { }
    }

    public sealed record DBDictionaryEntry(string Key, ObjectId Value);
    public sealed class DBDictionary : List<DBDictionaryEntry> { }
    public sealed class Xrecord
    {
        public ResultBuffer? Data { get; init; }
    }

    public class Entity
    {
        public int Handle { get; init; }
        public ResultBuffer? XData { get; init; }
        public ObjectId ExtensionDictionary { get; init; } = ObjectId.Null;
    }

    public class Curve : Entity
    {
        public double? MeasuredLength { get; init; }
        public double? MeasuredArea { get; init; }
        public double EndParam => 1;
        public double GetDistanceAtParameter(double value) => MeasuredLength ?? throw new Runtime.Exception();
        public double Area => MeasuredArea ?? throw new Runtime.Exception();
    }

    public sealed class Line : Curve
    {
        public double Length { get; init; }
    }

    public sealed class Ray : Curve { }
    public sealed class Xline : Curve { }

    public sealed class Hatch : Entity
    {
        public double Area { get; init; }
    }

    public sealed class Region : Entity
    {
        public double Area { get; init; }
        public double Perimeter { get; init; }
    }

    public sealed class BlockReference : Entity
    {
        public List<ObjectId> AttributeCollection { get; } = new List<ObjectId>();
        public ObjectId BlockTableRecord { get; init; } = ObjectId.Null;
    }

    public sealed class BlockTableRecord : List<ObjectId>
    {
        public bool HasAttributeDefinitions => this.Any(id => id.DxfName == "ATTDEF");
    }

    public sealed class MText : IDisposable
    {
        public string Text { get; init; } = "";
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    public sealed class AttributeReference
    {
        public string Tag { get; init; } = "";
        public string TextString { get; init; } = "";
        public MText? MTextAttribute { get; init; }
        public bool IsMTextAttribute => MTextAttribute != null;
    }

    public sealed class AttributeDefinition
    {
        public string Tag { get; init; } = "";
        public string TextString { get; init; } = "";
        public bool Constant { get; init; }
        public MText? MTextAttributeDefinition { get; init; }
        public bool IsMTextAttributeDefinition => MTextAttributeDefinition != null;
    }
}
