using System.Linq;
using Xunit;
using static AcadPaletteSetXData.Core.Tests.GroupEditingV2Tests;
using static AcadPaletteSetXData.Core.Tests.RepeatItemsTests;

namespace AcadPaletteSetXData.Core.Tests;

/// <summary>
/// Issue #61: <c>VELEC_CHECK</c> reports records of a <c>Repeat</c> with gaps in the numbers (spec_1 and spec_3 without
/// spec_2) and records without some fields; full records numbered 1..n are clean.
/// </summary>
public sealed class RepeatDrawingCheckTests
{
    [Fact]
    public void FullRecordsNumberedFromOneAreClean()
    {
        var cache = RepeatCache();
        var report = new DrawingCheck(cache.Schema).Run(new[]
        {
            Device("A", Spec(1, "1", "Шкаф", 2).Concat(Spec(2, "2", "Кабель", 10)).ToArray()),
            Device("B"),
        });
        Assert.DoesNotContain(report.Issues, issue => issue.Kind == DrawingIssueKind.RepeatRecords);
        // The objects are built by hand without the stored result of the Persist formula Spec_First.
        Assert.All(report.Issues, issue => Assert.Equal(DrawingIssueKind.StalePersist, issue.Kind));
    }

    [Fact]
    public void GapsAndMissingFieldsAreReported()
    {
        var cache = RepeatCache();
        // spec_2 is missing; spec_3 has no col (a record edited by hand or by another program).
        var records = Spec(1, "1", "Шкаф", 2).Concat(Spec(3, "3", "Лоток", 4).Take(2)).ToArray();
        var report = new DrawingCheck(cache.Schema).Run(new[] { Device("A", records) });
        var issues = report.Issues.Where(issue => issue.Kind == DrawingIssueKind.RepeatRecords).ToArray();
        Assert.Equal(2, issues.Length);
        Assert.Equal(("spec", "нет записей №2 (есть №1, 3); «+ Добавить» продолжит нумерацию с №4."), (issues[0].Id, issues[0].Message));
        Assert.Equal(("spec_3", "Спецификация №3: нет поля col."), (issues[1].Id, issues[1].Message));
        Assert.Equal("A", issues[0].Handle);
        Assert.Contains("Пропуски и неполные записи наборов", string.Join("\n", report.Lines()));
    }
}
