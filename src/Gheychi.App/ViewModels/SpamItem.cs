using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Gheychi.App.Localization;
using Gheychi.App.Theming;
using Gheychi.Core.Services;
using Gheychi.Core.Spam;

namespace Gheychi.App.ViewModels;

public sealed class SpamItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public static readonly IBrush VeryLikelyColor = Palette.Brush("#D64545");
    public static readonly IBrush LikelyColor = Palette.Brush("#C7841A");

    public required SpamMessage Message { get; init; }
    public long Id => Message.Id;
    public required string Sender { get; init; }
    public required string Confidence { get; init; }
    public required string Time { get; init; }
    /// <summary>Time of day alone, for the Spam tab where the day is in the header above the row.</summary>
    public required string Clock { get; init; }
    public string Preview => Message.Body;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotSelected));
            OnPropertyChanged(nameof(RowBackgroundColor));
        }
    }

    public bool IsNotSelected => !IsSelected;
    public IBrush RowBackgroundColor => IsSelected ? SelectedRowBrush : Palette.Transparent;
    private static readonly IBrush SelectedRowBrush = Palette.Accent(AccentRole.Solid, ThemeState.IsDark ? (byte)0x26 : (byte)0x14);

    public bool IsVeryLikely => SpamConfidence.Level(Message.Score) == SpamConfidenceLevel.VeryLikely;
    public IBrush ConfidenceColor => IsVeryLikely ? VeryLikelyColor : LikelyColor;
    public string ScoreText => FormatPercent(SpamConfidence.Percent(Message.Score));

    /// <summary>"85%" or, in Persian, "۸۵٪".</summary>
    public static string FormatPercent(int percent)
    {
        var digits = percent.ToString(CultureInfo.InvariantCulture);
        if (CultureInfo.CurrentUICulture.Name.StartsWith("fa", StringComparison.OrdinalIgnoreCase))
            digits = DateFormattingService.ToPersianDigits(digits);
        return string.Format(LocalizationManager.Instance["Spam_InfoPercent"], digits);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
