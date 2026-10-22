using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Gheychi.Core.Spam;

/// <summary>Builds training-dataset JSON from message text so exports match what the Trainer expects.</summary>
public static class TrainingExport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Normalizes texts, drops empties, and keeps first-seen order without duplicates.</summary>
    public static IReadOnlyList<string> Prepare(IEnumerable<string?> texts)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var text in texts)
        {
            var normalized = TextNormalizer.Normalize(text ?? "");
            if (normalized.Length == 0 || !seen.Add(normalized))
                continue;
            result.Add(normalized);
        }

        return result;
    }

    /// <summary>Serializes prepared rows as a <c>data.json</c>-compatible array with a single spam/ham label.</summary>
    public static string ToJson(IReadOnlyList<string> prepared, bool isSpam)
    {
        var label = isSpam ? 1 : 0;
        var rows = prepared.Select(text => new TrainingRow(text, label)).ToList();
        return JsonSerializer.Serialize(rows, JsonOptions);
    }

    /// <summary>Timestamped export name so multiple exports in one folder sort chronologically.</summary>
    public static string FileName(bool isSpam, DateTime localTime)
    {
        var kind = isSpam ? "spam" : "ham";
        var stamp = localTime.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return $"gheychi-{kind}-{stamp}.json";
    }

    private sealed record TrainingRow(string Text, int Label);
}
