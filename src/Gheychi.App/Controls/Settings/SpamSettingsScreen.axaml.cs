using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Gheychi.App.Localization;
using Gheychi.App.Platforms.Android.Notifications;
using Gheychi.App.Services;
using Gheychi.App.Ui;
using Gheychi.App.ViewModels;
using Gheychi.Core.Services;
using Gheychi.Core.Spam;

namespace Gheychi.App.Controls.Settings;

public partial class SpamSettingsScreen : SettingsScreen
{
    private const int MinThresholdPercent = 50;
    private const int MaxThresholdPercent = 99;
    private static readonly int[] RetentionChoices = [7, 14, 30, 90];

    private readonly ISpamSettings? _settings;
    private readonly ISpamModelUpdater? _models;
    private readonly SpamModelUpdates? _modelUpdates;
    private readonly SpamViewModel? _spam;
    private readonly (Border Segment, int Percent)[] _presets;
    private bool _binding;
    private bool _updating;
    private SpamModelRelease? _available;

    public SpamSettingsScreen()
    {
        InitializeComponent();

        var services = IPlatformApplication.Current?.Services;
        _settings = services?.GetService<ISpamSettings>();
        _models = services?.GetService<ISpamModelUpdater>();
        _modelUpdates = services?.GetService<SpamModelUpdates>();
        if (_modelUpdates is not null)
            _modelUpdates.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(() => _ = ShowModelUpdateAsync());
        _spam = services?.GetService<SpamViewModel>();

        _presets = [(PresetRelaxed, 95), (PresetBalanced, 85), (PresetStrict, 70)];
        PresetRelaxedHint.Text = SpamItem.FormatPercent(95);
        PresetBalancedHint.Text = SpamItem.FormatPercent(85);
        PresetStrictHint.Text = SpamItem.FormatPercent(70);

        // The Settings tab lays screens out ahead of time without OnShown. A slider still at its default 0 is
        // coerced up to Minimum when its template applies, and that change saved a 50% threshold on every launch.
        _binding = true;
        SensitivitySlider.Value = ThresholdPercent;
        _binding = false;
    }

    private int ThresholdPercent =>
        Math.Clamp((int)MathF.Round((_settings?.Threshold ?? SpamDetector.DefaultThreshold) * 100f), MinThresholdPercent, MaxThresholdPercent);

    private int RetentionDays => _spam?.RetentionDays ?? _settings?.RetentionDays ?? SpamDetector.DefaultRetentionDays;

    public override void OnShown()
    {
        var loc = LocalizationManager.Instance;
        _binding = true;
        FilterSwitch.IsChecked = _settings?.Enabled ?? true;
        SensitivitySlider.Value = ThresholdPercent;
        DigestSwitch.IsChecked = SpamDigestPreferences.Enabled;
        _binding = false;

        ShowFilterState();
        ShowDigestState();
        ShowSensitivity(ThresholdPercent);
        AutoClearValue.Text = SettingsUi.Days(RetentionDays);

        var trusted = _spam?.TrustedSenders().Count ?? 0;
        TrustedHint.Text = trusted == 0 ? loc["Settings_TrustedNone"] : string.Format(loc["Settings_TrustedHint"], SettingsUi.Number(trusted));

        ModelVersion.Text = loc["Settings_ModelNone"];
        ModelVersionHint.Text = loc["Settings_ModelHint"];
        _ = RefreshModelSectionAsync();
    }

    private async Task RefreshModelSectionAsync()
    {
        ShowModelCustomRows();
        await ShowModelVersionAsync();
        await ShowModelUpdateAsync();
    }

    private void ShowModelCustomRows()
    {
        var custom = _models?.IsCustomActive == true;
        ModelUpdateRow.IsVisible = !custom;
        ModelImportDivider.IsVisible = !custom;
        ModelRevertDivider.IsVisible = custom;
        ModelRevertRow.IsVisible = custom;
    }

    private async Task ShowModelVersionAsync()
    {
        try
        {
            var loc = LocalizationManager.Instance;
            ModelVersionHint.Text = _models?.CustomLoadFailed == true
                ? loc["Settings_ModelCustomFailed"]
                : loc["Settings_ModelHint"];

            if (_models is not null && await _models.GetActiveVersionAsync() is { } version)
            {
                var versionText = SettingsUi.Number(version);
                ModelVersion.Text = _models.IsCustomActive
                    ? string.Format(loc["Settings_ModelCustom"], versionText)
                    : string.Format(loc["Spam_InfoModelValue"], versionText);
            }
            else
                ModelVersion.Text = loc["Settings_ModelNone"];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Reading the spam model version failed: {ex}");
        }
    }

    private async Task ShowModelUpdateAsync()
    {
        try
        {
            // Off the UI thread: it reads the saved release (JSON) and the model files.
            _available = _modelUpdates is not { } updates ? null : await Task.Run(() => updates.GetAvailableAsync());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Reading the spam model update failed: {ex}");
            _available = null;
        }

        if (_updating)
            return;

        ShowModelCustomRows();

        var loc = LocalizationManager.Instance;
        ModelUpdateTitle.Text = loc[_available is null ? "Settings_ModelUpdate" : "Settings_ModelUpdateAvailable"];
        ModelUpdateHint.Text = _available is { } release
            ? string.Format(loc["Settings_ModelUpdateAvailableHint"], SettingsUi.Number(release.Version), SettingsUi.Megabytes(release.Size))
            : loc["Settings_ModelUpdateHint"];
    }

    private void ShowFilterState()
    {
        var enabled = FilterSwitch.IsChecked == true;
        FilterHint.Text = LocalizationManager.Instance[enabled ? "Settings_SpamFilterHint" : "Settings_SpamFilterOffHint"];
        FilterOptions.Opacity = enabled ? 1 : 0.45;
        FilterOptions.IsHitTestVisible = enabled;
    }

    private void OnFilterToggled(object? sender, RoutedEventArgs e)
    {
        if (_binding || _settings is null)
            return;

        _settings.Enabled = FilterSwitch.IsChecked == true;
        ShowFilterState();
        RescheduleDigest();
    }

    private void ShowDigestState()
    {
        var on = DigestSwitch.IsChecked == true;
        DigestTimeValue.Text = new DateFormattingService().FormatMessageTime(
            DateTime.Today.AddMinutes(SpamDigestPreferences.MinuteOfDay), CultureInfo.CurrentUICulture);
        DigestTimeRow.Opacity = on ? 1 : 0.45;
        DigestTimeRow.IsHitTestVisible = on;
    }

    private static void RescheduleDigest()
    {
        var context = Platform.AppContext;
        _ = Task.Run(() => SpamDigestNotifier.Schedule(context));
    }

    private void OnDigestToggled(object? sender, RoutedEventArgs e)
    {
        if (_binding)
            return;

        SpamDigestPreferences.Enabled = DigestSwitch.IsChecked == true;
        ShowDigestState();
        RescheduleDigest();
    }

    private async void OnDigestTimeTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            var minute = SpamDigestPreferences.MinuteOfDay;
            if (await DateTimeSheets.PickTimeAsync(minute / 60, minute % 60, DateTimeSheets.Is24Hour) is not { } picked)
                return;

            SpamDigestPreferences.MinuteOfDay = picked.Hour * 60 + picked.Minute;
            ShowDigestState();
            RescheduleDigest();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Picking the spam summary time failed: {ex}");
        }
    }

    private void ShowSensitivity(int percent)
    {
        var text = SpamItem.FormatPercent(percent);
        SensitivityValue.Text = text;
        SensitivityExplain.Text = string.Format(LocalizationManager.Instance["Settings_SensitivityExplain"], text);
        SettingsUi.Select(_presets.Select(p => p.Segment), _presets.FirstOrDefault(p => p.Percent == percent).Segment);
    }

    private void SaveThreshold(int percent)
    {
        if (_settings is not null && percent != ThresholdPercent)
            _settings.Threshold = percent / 100f;
    }

    private void OnSensitivityChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_binding)
            return;

        var percent = (int)Math.Round(e.NewValue);
        ShowSensitivity(percent);
        SaveThreshold(percent);
    }

    private void OnPresetTapped(object? sender, TappedEventArgs e)
    {
        var preset = _presets.FirstOrDefault(p => p.Segment == sender);
        if (preset.Segment is null)
            return;

        SaveThreshold(preset.Percent);
        _binding = true;
        SensitivitySlider.Value = preset.Percent;
        _binding = false;
        ShowSensitivity(preset.Percent);
    }

    private async void OnAutoClearTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            var loc = LocalizationManager.Instance;
            var labels = RetentionChoices.Select(SettingsUi.Days).ToArray();
            var choice = await Dialogs.ActionSheetAsync(loc["Settings_AutoClearTitle"], loc["Chat_Cancel"], null, labels);
            var index = Array.IndexOf(labels, choice);
            if (index < 0 || RetentionChoices[index] == RetentionDays)
                return;

            if (_spam is not null)
                await _spam.SetRetentionDaysAsync(RetentionChoices[index]);
            else if (_settings is not null)
                _settings.RetentionDays = RetentionChoices[index];
            AutoClearValue.Text = SettingsUi.Days(RetentionDays);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Changing spam auto-clear failed: {ex}");
        }
    }

    private void OnTrustedTapped(object? sender, TappedEventArgs e) => RequestOpen(SettingsScreenKind.Trusted);

    private void OnAnalyticsTapped(object? sender, TappedEventArgs e) => RequestOpen(SettingsScreenKind.Analytics);

    private async void OnModelUpdateTapped(object? sender, TappedEventArgs e)
    {
        if (_modelUpdates is not { } updates || _updating)
            return;

        var loc = LocalizationManager.Instance;
        var release = _available;
        _updating = true;
        try
        {
            if (release is null)
            {
                ModelUpdateHint.Text = loc["Settings_ModelChecking"];
                if (await Task.Run(() => updates.CheckAsync(force: true)) is null)
                    Toast.Show(loc["Settings_ModelUpToDate"]);
            }
            else
            {
                ModelUpdateHint.Text = string.Format(loc["Settings_ModelDownloading"], SettingsUi.Megabytes(release.Size));
                var installed = await Task.Run(() => updates.InstallAsync(release));
                Toast.Show(installed
                    ? string.Format(loc["Settings_ModelInstalled"], SettingsUi.Number(release.Version))
                    : loc["Settings_ModelInstallFailed"]);
                await ShowModelVersionAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Spam model update failed: {ex}");
            Toast.Show(loc[release is null ? "Settings_ModelCheckFailed" : "Settings_ModelInstallFailed"]);
        }
        finally
        {
            _updating = false;
            await ShowModelUpdateAsync();
        }
    }

    private async void OnModelImportTapped(object? sender, TappedEventArgs e)
    {
        if (_models is null)
            return;

        var loc = LocalizationManager.Instance;
        try
        {
            if (TopLevel.GetTopLevel(MainView.Current)?.StorageProvider is not { } storage)
                return;

            var options = new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("ZIP") { Patterns = ["*.zip"] },
                ],
            };

            var files = await storage.OpenFilePickerAsync(options);
            if (files is not { Count: > 0 })
                return;

            await using var stream = await files[0].OpenReadAsync();
            var imported = await _models.ImportCustomAsync(stream);
            if (imported && await _models.GetActiveVersionAsync() is { } version)
                Toast.Show(string.Format(loc["Settings_ModelImported"], SettingsUi.Number(version)));
            else
                Toast.Show(loc["Settings_ModelImportFailed"]);

            await RefreshModelSectionAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Spam model import failed: {ex}");
            Toast.Show(loc["Settings_ModelImportFailed"]);
        }
    }

    private async void OnModelRevertTapped(object? sender, TappedEventArgs e)
    {
        if (_models is null)
            return;

        var loc = LocalizationManager.Instance;
        try
        {
            if (!await Dialogs.AlertAsync(
                    loc["Settings_ModelRevert"],
                    loc["Settings_ModelRevertConfirm"],
                    loc["Settings_ModelRevert"],
                    loc["Chat_Cancel"],
                    danger: true))
                return;

            await _models.RemoveCustomAsync();
            await RefreshModelSectionAsync();

            if (_modelUpdates is { } updates)
            {
                try
                {
                    await Task.Run(() => updates.CheckAsync(force: true));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Reading the spam model update failed: {ex}");
                }

                await ShowModelUpdateAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Spam model revert failed: {ex}");
        }
    }
}
