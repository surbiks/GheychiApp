using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Gheychi.App.Localization;
using Gheychi.App.Ui;
using Gheychi.Core.Spam;

namespace Gheychi.App.Controls;

/// <summary>Builds a training JSON file from collected message bodies and saves it where the user chooses.</summary>
internal static class TrainingExportFlow
{
    /// <summary>Asks spam/ham, collects and normalizes texts, then saves UTF-8 JSON. Safe from async void handlers.</summary>
    public static async Task RunAsync(Func<Task<IReadOnlyList<string>>> collectTexts)
    {
        var loc = LocalizationManager.Instance;
        try
        {
            bool? isSpam;
            try
            {
                isSpam = await AskLabelAsync(loc);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Training export label sheet failed: {ex}");
                Ui.Toast.Show(loc["Export_Failed"]);
                return;
            }

            if (isSpam is null)
                return;

            IReadOnlyList<string> prepared;
            try
            {
                prepared = await Task.Run(async () => TrainingExport.Prepare(await collectTexts()));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Training export collection failed: {ex}");
                Ui.Toast.Show(loc["Export_Failed"]);
                return;
            }

            if (prepared.Count == 0)
            {
                Ui.Toast.Show(loc["Export_Empty"]);
                return;
            }

            if (TopLevel.GetTopLevel(MainView.Current)?.StorageProvider is not { } storage)
            {
                Ui.Toast.Show(loc["Export_Failed"]);
                return;
            }

            var localTime = DateTime.Now;
            var options = new FilePickerSaveOptions
            {
                SuggestedFileName = TrainingExport.FileName(isSpam.Value, localTime),
                DefaultExtension = "json",
                FileTypeChoices =
                [
                    new FilePickerFileType("JSON") { Patterns = ["*.json"] },
                    FilePickerFileTypes.All,
                ],
            };

            IStorageFile? target;
            try
            {
                target = await storage.SaveFilePickerAsync(options);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Training export save picker failed: {ex}");
                Ui.Toast.Show(loc["Export_Failed"]);
                return;
            }

            if (target is null)
                return;

            try
            {
                var json = TrainingExport.ToJson(prepared, isSpam.Value);
                await using var stream = await target.OpenWriteAsync();
                await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                await writer.WriteAsync(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Training export write failed: {ex}");
                Ui.Toast.Show(loc["Export_Failed"]);
                return;
            }

            Ui.Toast.Show(string.Format(loc["Export_Done"], prepared.Count));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Training export failed: {ex}");
            Ui.Toast.Show(loc["Export_Failed"]);
        }
    }

    private static async Task<bool?> AskLabelAsync(LocalizationManager loc)
    {
        var spam = loc["Export_LabelSpam"];
        var ham = loc["Export_LabelHam"];
        var cancel = loc["Chat_Cancel"];
        var picked = await Sheet.ListAsync(loc["Export_LabelTitle"], cancel, destruction: null, [spam, ham]);
        if (picked is null || picked == cancel)
            return null;
        if (picked == spam)
            return true;
        if (picked == ham)
            return false;
        return null;
    }
}
