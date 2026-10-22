using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Gheychi.App.Controls;
using Gheychi.App.Localization;
using Gheychi.App.Platforms.Android.Notifications;
using Gheychi.App.ViewModels;

namespace Gheychi.App.Pages;

public partial class SpamPage : UserControl
{
    private bool _loaded;
    private ScrollViewer? _scroll;
    private bool _holdFired;
    private long _lastTappedItemId;
    private long _lastTappedItemTick;

    private SpamViewModel? Vm => DataContext as SpamViewModel;

    public SpamPage()
    {
        InitializeComponent();
        Focusable = true;

        if (IPlatformApplication.Current?.Services?.GetService(typeof(SpamViewModel)) is SpamViewModel viewModel)
        {
            DataContext = viewModel;
            viewModel.PropertyChanged += OnVmPropertyChanged;
            viewModel.Items.CollectionChanged += OnItemsChanged;
            _ = LoadAsync(viewModel);
        }

        SpamList.TemplateApplied += (_, e) => _scroll = e.NameScope.Find<ScrollViewer>("SpamScroll");
        Ui.RowPressEffect.Attach(SpamList, SpamHost);
        SpamList.AddHandler(PointerPressedEvent, (_, _) => _holdFired = false, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Asked to open Settings on its spam section (the header's gear); the tab host does the switch.</summary>
    public event Action? OpenSpamSettingsRequested;

    /// <summary>The tab was selected: the spam summary notification has nothing left to tell.</summary>
    public void OnShown()
    {
        var context = Platform.AppContext;
        _ = Task.Run(() => SpamDigestNotifier.MarkSeen(context));
    }

    public void OnHidden()
    {
        if (SearchBar.IsVisible)
            ClearFocus();

        // Back on the tab it starts from its top: menus and the search closed, the list at its first row.
        for (var layers = 0; layers < 4 && HandleBack(); layers++)
        {
        }

        if (_scroll is not null)
            _scroll.Offset = default;
    }

    /// <summary>True when it consumed the back press.</summary>
    public bool HandleBack()
    {
        if (Overlay.HandleBack())
            return true;

        if (Vm?.IsSelectionMode == true)
        {
            Vm.ExitSelectionMode();
            return true;
        }

        if (!SearchBar.IsVisible)
            return false;

        CloseSearch();
        return true;
    }

    private async Task LoadAsync(SpamViewModel viewModel)
    {
        try
        {
            await viewModel.EnsureLoadedAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Loading spam failed: {ex}");
        }

        _loaded = true;
        UpdateEmptyState();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState() => EmptyState.IsVisible = _loaded && Vm is { Items.Count: 0 };

    // No focus-clear API: the page takes focus, which also drops the keyboard.
    private void ClearFocus()
    {
        if (SearchEntry.IsFocused)
            Focus();
    }

    private void OnBackToInboxTapped(object? sender, TappedEventArgs e) => Ui.MainView.Current?.SelectMessagesTab();

    private void OnSettingsTapped(object? sender, TappedEventArgs e) => OpenSpamSettingsRequested?.Invoke();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SpamViewModel.IsSelectionMode))
            SyncHeaderVisibility();
    }

    private void SyncHeaderVisibility() =>
        AppHeader.IsVisible = !SearchBar.IsVisible && Vm is not { IsSelectionMode: true };

    private void OnSearchTapped(object? sender, TappedEventArgs e)
    {
        SearchBar.IsVisible = true;
        SyncHeaderVisibility();
        Dispatcher.UIThread.Post(() => SearchEntry.Focus(), DispatcherPriority.Loaded);
    }

    private void OnSearchCloseTapped(object? sender, TappedEventArgs e) => CloseSearch();

    private void CloseSearch()
    {
        ClearFocus();
        if (Vm is not null)
            Vm.SearchText = string.Empty;
        SearchBar.IsVisible = false;
        SyncHeaderVisibility();
    }

    private void OnListTapped(object? sender, TappedEventArgs e)
    {
        if (_holdFired || (e.Source as StyledElement)?.DataContext is not SpamItem item)
            return;

        var now = Environment.TickCount64;
        if (item.Id == _lastTappedItemId && now - _lastTappedItemTick < 300)
            return;
        _lastTappedItemId = item.Id;
        _lastTappedItemTick = now;

        if (Vm?.IsSelectionMode == true)
        {
            Vm.ToggleSelection(item);
            return;
        }

        ClearFocus();
        Overlay.ShowMenu(item);
    }

    private void OnListHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Started || (e.Source as StyledElement)?.DataContext is not SpamItem item || Vm is null)
            return;

        _holdFired = true;
        _lastTappedItemId = item.Id;
        _lastTappedItemTick = Environment.TickCount64;

        if (!Vm.IsSelectionMode)
            Vm.EnterSelectionMode(item);
        else
            Vm.ToggleSelection(item);
    }

    private void OnExitSelectionModeTapped(object? sender, TappedEventArgs e) => Vm?.ExitSelectionMode();

    private async void OnExportSelectedTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is null || Vm.SelectedCount == 0)
            return;

        var bodies = Vm.SelectedItems.Select(i => i.Message.Body).ToList();
        await TrainingExportFlow.RunAsync(() => Task.FromResult<IReadOnlyList<string>>(bodies));
        Vm.ExitSelectionMode();
    }

    private async void OnClearAllTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            var loc = LocalizationManager.Instance;
            if (Vm is not null && await Overlay.ConfirmAsync(loc["Spam_ClearAllTitle"], loc["Spam_ClearAllMessage"], loc["Spam_ClearAll"]))
                await Vm.ClearAllAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Clear all failed: {ex}");
        }
    }
}
