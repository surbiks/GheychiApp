using System.Text.Json;
using Gheychi.Core.Spam;
using Xunit;

namespace Gheychi.Core.Tests;

public sealed class TrainingExportTests
{
    [Fact]
    public void Prepare_NormalizesDropsEmptyAndDuplicates()
    {
        var a = "تخفیف ویژه 09121234567";
        var prepared = TrainingExport.Prepare([a, a, "   ", null, "سلام"]);
        Assert.Equal([TextNormalizer.Normalize(a), TextNormalizer.Normalize("سلام")], prepared);
    }

    [Fact]
    public void ToJson_IsDatasetFormatWithReadablePersian()
    {
        var json = TrainingExport.ToJson(["سلام"], isSpam: true);
        Assert.Contains("سلام", json);
        var rows = JsonSerializer.Deserialize<List<JsonElement>>(json)!;
        Assert.Equal("سلام", rows[0].GetProperty("Text").GetString());
        Assert.Equal(1, rows[0].GetProperty("Label").GetInt32());
        Assert.Equal(0, JsonSerializer.Deserialize<List<JsonElement>>(TrainingExport.ToJson(["x"], false))![0].GetProperty("Label").GetInt32());
    }

    [Fact]
    public void FileName_HasLabelAndMinute()
    {
        var t = new DateTime(2026, 10, 22, 14, 30, 59);
        Assert.Equal("gheychi-spam-20261022-1430.json", TrainingExport.FileName(true, t));
        Assert.Equal("gheychi-ham-20261022-1430.json", TrainingExport.FileName(false, t));
    }
}
