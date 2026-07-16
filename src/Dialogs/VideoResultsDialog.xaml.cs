using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FullVid.Common;
using FullVid.Models;
using FullVid.Services.Controller;
using Playnite.SDK;
using Playnite.SDK.Events;

namespace FullVid.Dialogs
{
    // What a controller button press resolves to on the results list.
    public enum DialogAction
    {
        None,
        Watch,
        Download,
        Close,
        NavigateUp,
        NavigateDown,
        PrevCategory,  // LB / Q — switch to the previous search category
        NextCategory   // RB / E — switch to the next search category
    }

    // Controller-navigable list of YouTube search results. A = watch, Y = download,
    // B = close, D-pad = navigate. Watch/Download are raised as callbacks; the caller
    // owns what they do (player/download live in later tasks).
    public partial class VideoResultsDialog : UserControl, IControllerInputReceiver
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // D-pad debounce: XInput + WPF can both fire for one physical press. 150ms
        // gate (ported from UniPlaySong's SimpleControllerDialog) drops the duplicate.
        private DateTime _lastDpadNavigationTime = DateTime.MinValue;
        private const int DpadDebounceMs = 150;

        private readonly IPlayniteAPI _api;
        private readonly bool _swapAB;
        private readonly Action<VideoResult> _onWatch;
        private readonly Action<VideoResult> _onDownload;
        private readonly Action _onClose;
        private readonly Action<int> _onCategoryChanged;
        private readonly System.Collections.Generic.IReadOnlyList<Models.VideoCategory> _categories;
        private int _categoryIndex;
        private readonly ObservableCollection<ResultRow> _rows = new ObservableCollection<ResultRow>();

        // Pure button-decision seam. swapAB models Playnite's SwapConfirmCancelButtons:
        // when true, A and B trade their confirm(Watch)/cancel(Close) roles. Y and the
        // D-pad are unaffected by the swap. A/Y do nothing on an empty list.
        public static DialogAction Decide(ControllerInput button, int selectedIndex, int itemCount, bool swapAB)
        {
            var confirm = swapAB ? ControllerInput.B : ControllerInput.A;
            var cancel = swapAB ? ControllerInput.A : ControllerInput.B;

            if (button == confirm)
                return itemCount > 0 ? DialogAction.Watch : DialogAction.None;
            if (button == cancel)
                return DialogAction.Close;
            if (button == ControllerInput.Y)
                return itemCount > 0 ? DialogAction.Download : DialogAction.None;
            if (button == ControllerInput.DPadUp)
                return DialogAction.NavigateUp;
            if (button == ControllerInput.DPadDown)
                return DialogAction.NavigateDown;
            if (button == ControllerInput.LeftShoulder)
                return DialogAction.PrevCategory;
            if (button == ControllerInput.RightShoulder)
                return DialogAction.NextCategory;

            return DialogAction.None;
        }

        public VideoResultsDialog(
            IPlayniteAPI api,
            System.Collections.Generic.IReadOnlyList<Models.VideoCategory> categories,
            Action<VideoResult> onWatch,
            Action<VideoResult> onDownload,
            Action onClose,
            Action<int> onCategoryChanged)
        {
            InitializeComponent();

            _api = api;
            _categories = categories;
            _onWatch = onWatch;
            _onDownload = onDownload;
            _onClose = onClose;
            _onCategoryChanged = onCategoryChanged;

            // Read the confirm/cancel swap once, up front. Read-only SDK property; default
            // to un-swapped on any failure so A always confirms in the worst case.
            try { _swapAB = api?.ApplicationSettings?.Fullscreen?.SwapConfirmCancelButtons ?? false; }
            catch { _swapAB = false; }

            BuildCategoryTabs();

            // Opens empty in a "Searching…" state; the caller pushes results via SetResults
            // once its background search completes — the window never blocks on the search.
            ResultsListBox.ItemsSource = _rows;

            Loaded += OnDialogLoaded;
            Unloaded += OnDialogUnloaded;
        }

        // Builds the clickable category tab row (mouse) and highlights the active one. LB/RB
        // and clicks both route to SwitchCategory. Tabs are TextBlocks in the CategoryTabs panel.
        private void BuildCategoryTabs()
        {
            CategoryTabs.Children.Clear();
            if (_categories == null) return;
            for (int i = 0; i < _categories.Count; i++)
            {
                var idx = i;
                var tab = new TextBlock
                {
                    Text = _categories[i].Label,
                    Margin = new Thickness(0, 0, 18, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    FontSize = 14
                };
                tab.MouseLeftButtonUp += (s, e) => SwitchCategory(idx);
                CategoryTabs.Children.Add(tab);
            }
            HighlightActiveTab();
        }

        // Switch to a category (bounded), re-run the search via the callback, restyle tabs.
        private void SwitchCategory(int index)
        {
            if (_categories == null || _categories.Count == 0) return;
            index = Math.Max(0, Math.Min(_categories.Count - 1, index));
            if (index == _categoryIndex && _rows.Count > 0) return; // no-op re-click of active tab
            _categoryIndex = index;
            HighlightActiveTab();
            _onCategoryChanged?.Invoke(index);
        }

        private void HighlightActiveTab()
        {
            for (int i = 0; i < CategoryTabs.Children.Count; i++)
            {
                if (CategoryTabs.Children[i] is TextBlock tb)
                {
                    bool active = i == _categoryIndex;
                    tb.Foreground = active
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB3, 0x9D, 0xDB))
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x99, 0x99, 0x99));
                    tb.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
                }
            }
        }

        // Called on the UI thread when the background search returns. Populates the list,
        // hides the status line, and selects the first row. Marshals itself if off-thread.
        public void SetResults(IEnumerable<VideoResult> results)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetResults(results)); return; }

            _rows.Clear();
            foreach (var r in results ?? Enumerable.Empty<VideoResult>())
                _rows.Add(new ResultRow(r));

            if (_rows.Count > 0)
            {
                StatusText.Visibility = Visibility.Collapsed;
                ResultsListBox.SelectedIndex = 0;
                ResultsListBox.ScrollIntoView(ResultsListBox.SelectedItem);
                ResultsListBox.Focus();
            }
            else
            {
                SetStatus("No videos found.");
            }
        }

        // Shows a status message (search error / empty) in place of the list.
        public void SetStatus(string message)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetStatus(message)); return; }
            StatusText.Text = message ?? string.Empty;
            StatusText.Visibility = Visibility.Visible;
        }

        private void OnDialogLoaded(object sender, RoutedEventArgs e)
        {
            // Keyboard parity with the controller — the UserControl is focusable and gets
            // key events before the ListBox, so arrows/Enter/Esc route through our own path.
            PreviewKeyDown += OnPreviewKeyDown;
            Focusable = true;
            Focus();

            if (_rows.Count > 0)
            {
                ResultsListBox.SelectedIndex = 0;
                ResultsListBox.Focus();
            }

            GetRouter()?.Register(this);
        }

        private void OnDialogUnloaded(object sender, RoutedEventArgs e)
        {
            PreviewKeyDown -= OnPreviewKeyDown;
            GetRouter()?.Unregister(this);
        }

        // Keyboard parity with the controller: Enter=Watch, Esc/Back=Close, Up/Down=Navigate,
        // D=Download. Routes through the same DialogAction dispatch. No debounce — key repeat
        // on a list is expected; the XInput+WPF double-fire that needs gating is D-pad only.
        private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            DialogAction action;
            switch (e.Key)
            {
                case System.Windows.Input.Key.Enter: action = _rows.Count > 0 ? DialogAction.Watch : DialogAction.None; break;
                case System.Windows.Input.Key.Escape:
                case System.Windows.Input.Key.Back: action = DialogAction.Close; break;
                case System.Windows.Input.Key.Up: action = DialogAction.NavigateUp; break;
                case System.Windows.Input.Key.Down: action = DialogAction.NavigateDown; break;
                case System.Windows.Input.Key.D: action = _rows.Count > 0 ? DialogAction.Download : DialogAction.None; break;
                case System.Windows.Input.Key.Q: action = DialogAction.PrevCategory; break;
                case System.Windows.Input.Key.E: action = DialogAction.NextCategory; break;
                default: return;
            }

            e.Handled = true;
            DispatchAction(action, keyboard: true);
        }

        private ControllerEventRouter GetRouter()
        {
            if (Application.Current?.Properties?.Contains(DialogHelper.PluginPropertyKey) == true)
            {
                var plugin = Application.Current.Properties[DialogHelper.PluginPropertyKey] as FullVid;
                return plugin?.GetControllerEventRouter();
            }
            return null;
        }

        public void OnControllerButtonPressed(ControllerInput button)
        {
            try
            {
                var action = Decide(button, ResultsListBox.SelectedIndex, _rows.Count, _swapAB);
                DispatchAction(action, keyboard: false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error handling controller input in VideoResultsDialog");
            }
        }

        // Shared action dispatch for both controller and keyboard. keyboard=true skips the
        // D-pad debounce (that gate only exists for the XInput+WPF double-fire, not keys).
        private void DispatchAction(DialogAction action, bool keyboard)
        {
            switch (action)
            {
                case DialogAction.Watch:
                    InvokeWithSelected(_onWatch);
                    break;
                case DialogAction.Download:
                    InvokeWithSelected(_onDownload);
                    break;
                case DialogAction.Close:
                    _onClose?.Invoke();
                    break;
                case DialogAction.NavigateUp:
                    if (keyboard || TryDpadNavigation()) Navigate(-1);
                    break;
                case DialogAction.NavigateDown:
                    if (keyboard || TryDpadNavigation()) Navigate(1);
                    break;
                case DialogAction.PrevCategory:
                    SwitchCategory(_categoryIndex - 1);
                    break;
                case DialogAction.NextCategory:
                    SwitchCategory(_categoryIndex + 1);
                    break;
            }
        }

        public void OnControllerButtonReleased(ControllerInput button) { }

        private void InvokeWithSelected(Action<VideoResult> callback)
        {
            var row = ResultsListBox.SelectedItem as ResultRow;
            if (row?.Source != null)
                callback?.Invoke(row.Source);
        }

        // 150ms gate so a D-pad press doesn't navigate twice (XInput + WPF).
        private bool TryDpadNavigation()
        {
            var now = DateTime.Now;
            if ((now - _lastDpadNavigationTime).TotalMilliseconds < DpadDebounceMs)
                return false;
            _lastDpadNavigationTime = now;
            return true;
        }

        private void Navigate(int offset)
        {
            if (_rows.Count == 0) return;
            var next = Math.Max(0, Math.Min(_rows.Count - 1, ResultsListBox.SelectedIndex + offset));
            ResultsListBox.SelectedIndex = next;
            ResultsListBox.ScrollIntoView(ResultsListBox.SelectedItem);
            ResultsListBox.Focus();
        }

        // View-model wrapper so the XAML DataTemplate binds display-ready values.
        private class ResultRow
        {
            public VideoResult Source { get; }
            public string Title => Source.Title;
            public string Duration => FormatDuration(Source.Duration);
            public string SubLine => BuildSubLine(Source);
            public BitmapImage Thumbnail { get; }

            public ResultRow(VideoResult source)
            {
                Source = source;
                Thumbnail = LoadThumbnail(source.ThumbnailUrl);
            }

            // Async, non-blocking BitmapImage load. OnLoad caches the decoded image and
            // releases the stream; a bad/empty URL just yields no image (fails silently).
            private static BitmapImage LoadThumbnail(string url)
            {
                if (string.IsNullOrWhiteSpace(url)) return null;
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bmp.UriSource = new Uri(url, UriKind.Absolute);
                    bmp.EndInit();
                    return bmp;
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Failed to load thumbnail: " + url);
                    return null;
                }
            }

            private static string FormatDuration(TimeSpan d)
            {
                if (d <= TimeSpan.Zero) return "";
                return d.TotalHours >= 1
                    ? string.Format("{0}:{1:D2}:{2:D2}", (int)d.TotalHours, d.Minutes, d.Seconds)
                    : string.Format("{0}:{1:D2}", (int)d.TotalMinutes, d.Seconds);
            }

            private static string BuildSubLine(VideoResult r)
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(r.Channel)) parts.Add(r.Channel);
                if (r.ViewCount > 0) parts.Add(FormatViews(r.ViewCount) + " views");
                return string.Join("  •  ", parts);
            }

            private static string FormatViews(long views)
            {
                if (views >= 1_000_000) return (views / 1_000_000.0).ToString("0.#") + "M";
                if (views >= 1_000) return (views / 1_000.0).ToString("0.#") + "K";
                return views.ToString();
            }
        }
    }
}
