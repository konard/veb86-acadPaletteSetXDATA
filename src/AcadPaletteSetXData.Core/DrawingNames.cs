namespace AcadPaletteSetXData.Core;

/// <summary>Names stored in drawings (TZXDATA 13): the RegApp of object values and the NOD root of the schema.</summary>
public static class DrawingNames
{
    public const string RegApp = "BobrovXDATA";
    public const string NodRoot = "VELEC";
    /// <summary>The earlier RegApp of the plugin (TZXDATA 3, П11): only read by <c>VELEC_MIGRATE</c> (stage 9).</summary>
    public const string LegacyRegApp = "SMARTLINE";
    /// <summary>RegApp of the earlier ESMT format (TZXDATA 3): only read and migrated (stage 9).</summary>
    public const string EsmtRegApp = "ESMT_LEP_v1.0";
    /// <summary>
    /// A copy of v1 <c>key=value</c> strings kept by <c>VELEC_MIGRATE</c> when <see cref="RegApp"/> is converted to v2
    /// in place and the sources are not deleted (TZXDATA 13: sources are deleted only by the flag of the command).
    /// </summary>
    public const string BackupRegApp = "BobrovXDATA_V1";
}
