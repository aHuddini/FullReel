using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Playnite.SDK;
using Playnite.SDK.Events;

namespace FullVid.Common
{
    // Helper for creating controller-friendly modal dialogs.
    public static class DialogHelper
    {
        // Bridges SDK controller events (routed via ControllerEventRouter) into a modal window.
        // Only responds to button presses; releases are ignored.
        private class ModalControllerReceiver : Services.Controller.IControllerInputReceiver
        {
            private readonly Action<ControllerInput> _onPressed;

            public ModalControllerReceiver(Action<ControllerInput> onPressed)
            {
                _onPressed = onPressed;
            }

            public void OnControllerButtonPressed(ControllerInput button) => _onPressed?.Invoke(button);
            public void OnControllerButtonReleased(ControllerInput button) { }
        }

        private static readonly ILogger Logger = LogManager.GetLogger();

        // Dialog background (#212121)
        public static readonly Color DefaultDarkBackground = Color.FromRgb(33, 33, 33);

        // Success border - Material Design green (#4CAF50)
        public static readonly Color ToastSuccessBorderColor = Color.FromRgb(76, 175, 80);

        // Error border - Material Design red (#F44336)
        public static readonly Color ToastErrorBorderColor = Color.FromRgb(244, 67, 54);

        private static readonly Color HintTextColor = Color.FromRgb(150, 150, 150);           // #969696
        private static readonly Color ButtonUnselectedBg = Color.FromRgb(60, 60, 60);         // #3C3C3C
        private static readonly Color ButtonUnselectedBorder = Color.FromRgb(100, 100, 100);  // #646464

        // Application.Current.Properties key under which FullVid registers itself so
        // dialogs can reach the shared ControllerEventRouter.
        public const string PluginPropertyKey = "FullVidPlugin";

        // Dialog window configuration options.
        public class DialogOptions
        {
            public string Title { get; set; } = "Dialog";
            public double Width { get; set; } = 600;
            public double Height { get; set; } = 500;
            public bool CanResize { get; set; } = true;
            public bool ShowMaximizeButton { get; set; } = true;
            public bool ShowMinimizeButton { get; set; } = false;
            public bool ShowCloseButton { get; set; } = true;
            public bool ShowInTaskbar { get; set; } = true;
            public bool Topmost { get; set; } = false;
            public bool ApplyDarkBackground { get; set; } = false;
            public bool SetOwner { get; set; } = false;
            public WindowStartupLocation StartupLocation { get; set; } = WindowStartupLocation.CenterOwner;
        }

        // Creates a fullscreen-optimized dialog (topmost, dark background when in fullscreen mode).
        public static Window CreateFullscreenDialog(
            IPlayniteAPI playniteApi,
            object content,
            string title,
            double width = 600,
            double height = 500,
            bool isFullscreenMode = false)
        {
            return CreateDialog(playniteApi, content, new DialogOptions
            {
                Title = title,
                Width = width,
                Height = height,
                CanResize = true,
                ShowMaximizeButton = true,
                Topmost = isFullscreenMode,
                ApplyDarkBackground = isFullscreenMode,
                SetOwner = true,
                ShowInTaskbar = !isFullscreenMode // Hide from taskbar in fullscreen mode (topmost handles visibility)
            });
        }

        // Creates a BORDERLESS window (no title bar at all) via a plain WPF Window — NOT
        // Playnite's CreateWindow, which always draws its own chrome. Used by the player, which
        // paints its own in-page top/bottom bars, so any window chrome would be a duplicate.
        // Opaque (no AllowsTransparency) so WebView2 hardware compositing keeps working.
        public static Window CreateBorderlessDialog(
            IPlayniteAPI playniteApi,
            object content,
            double width,
            double height,
            bool isFullscreenMode)
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Width = width,
                Height = height,
                Content = content,
                Background = new SolidColorBrush(DefaultDarkBackground),
                // Center on the monitor, not the owner window — a video player should sit
                // dead-center regardless of where Playnite's window is.
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = isFullscreenMode,
                ShowInTaskbar = !isFullscreenMode
            };

            try
            {
                var owner = playniteApi?.Dialogs?.GetCurrentAppWindow();
                if (owner != null)
                    window.Owner = owner;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Error setting borderless window owner");
            }

            // Windows 11's DWM composites a ~1px border + rounded corners onto every top-level
            // window AFTER app rendering — on the borderless player it showed as a subtle gray
            // hairline riding the bottom edge that no in-page or WPF paint could remove. Turn
            // both off at the DWM level. The attributes don't exist before Win11 (22000); the
            // call fails harmlessly there.
            window.SourceInitialized += (s, e) =>
            {
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    if (hwnd == IntPtr.Zero) return;
                    int cornerPreference = 1; // DWMWCP_DONOTROUND
                    DwmSetWindowAttribute(hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref cornerPreference, sizeof(int));
                    int borderColor = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
                    DwmSetWindowAttribute(hwnd, 34 /*DWMWA_BORDER_COLOR*/, ref borderColor, sizeof(int));
                }
                catch { }
            };

            return window;
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Creates a dialog with full customization options.
        public static Window CreateDialog(
            IPlayniteAPI playniteApi,
            object content,
            DialogOptions options)
        {
            if (playniteApi == null)
                throw new ArgumentNullException(nameof(playniteApi));

            if (options == null)
                options = new DialogOptions();

            var window = playniteApi.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = options.ShowMinimizeButton,
                ShowMaximizeButton = options.ShowMaximizeButton,
                ShowCloseButton = options.ShowCloseButton
            });

            var width = options.Width;
            var height = options.Height;

            // Ensure dialog fits within screen bounds (with margin for window chrome/taskbar)
            try
            {
                var screenWidth = SystemParameters.PrimaryScreenWidth;
                var screenHeight = SystemParameters.PrimaryScreenHeight;
                var maxWidth = screenWidth * 0.95;  // 95% of screen width
                var maxHeight = screenHeight * 0.90; // 90% of screen height (account for taskbar)

                if (width > maxWidth || height > maxHeight)
                {
                    // Scale down proportionally to fit
                    var widthRatio = maxWidth / width;
                    var heightRatio = maxHeight / height;
                    var scaleDown = Math.Min(widthRatio, heightRatio);

                    width = width * scaleDown;
                    height = height * scaleDown;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Error getting screen dimensions for size clamping");
            }

            window.Title = options.Title;
            window.Width = width;
            window.Height = height;
            window.WindowStartupLocation = options.StartupLocation;
            window.ShowInTaskbar = options.ShowInTaskbar;
            window.ResizeMode = options.CanResize ? ResizeMode.CanResize : ResizeMode.NoResize;
            window.Content = content;

            if (options.Topmost)
            {
                window.Topmost = true;
            }

            if (options.ApplyDarkBackground)
            {
                window.Background = new SolidColorBrush(DefaultDarkBackground);
            }

            if (options.SetOwner)
            {
                try
                {
                    var ownerWindow = playniteApi.Dialogs.GetCurrentAppWindow();
                    if (ownerWindow != null)
                    {
                        window.Owner = ownerWindow;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Error setting window owner");
                }
            }

            return window;
        }

        // Attaches a closing handler that returns focus to Playnite's main window.
        public static void AddFocusReturnHandler(Window window, IPlayniteAPI playniteApi, string context = null)
        {
            if (window == null || playniteApi == null)
                return;

            window.Closing += (s, e) => ReturnFocusToMainWindow(playniteApi, context);
        }

        // Returns focus to Playnite's main window (important for fullscreen mode).
        public static void ReturnFocusToMainWindow(IPlayniteAPI playniteApi, string context = null)
        {
            try
            {
                var mainWindow = playniteApi?.Dialogs?.GetCurrentAppWindow();
                if (mainWindow != null)
                {
                    mainWindow.Activate();
                    mainWindow.Focus();
                    // Toggle topmost to ensure focus is grabbed
                    mainWindow.Topmost = true;
                    mainWindow.Topmost = false;
                }
            }
            catch (Exception ex)
            {
                var logContext = string.IsNullOrEmpty(context) ? "dialog close" : context;
                Logger.Debug(ex, $"Error returning focus during {logContext}");
            }
        }

        // Shows a dialog and returns true if confirmed (DialogResult == true).
        public static bool ShowDialogAndGetResult(Window window)
        {
            return window?.ShowDialog() == true;
        }

        // Resolves the shared ControllerEventRouter from the registered FullVid plugin instance.
        private static Services.Controller.ControllerEventRouter GetRouter()
        {
            if (Application.Current?.Properties?.Contains(PluginPropertyKey) == true)
            {
                var plugin = Application.Current.Properties[PluginPropertyKey] as FullVid;
                return plugin?.GetControllerEventRouter();
            }
            return null;
        }

        // Shows a controller-friendly Yes/No dialog with XInput support. Returns true if Yes selected.
        public static bool ShowControllerConfirmation(IPlayniteAPI playniteApi, string message, string title)
        {
            if (playniteApi == null) return false;

            try
            {
                bool result = false;
                Window window = null;
                System.Windows.Controls.Button yesButton = null;
                System.Windows.Controls.Button noButton = null;
                int selectedIndex = 0; // 0 = Yes, 1 = No

                // Create buttons with references
                Action updateButtonStyles = () =>
                {
                    if (yesButton == null || noButton == null) return;

                    // Yes button: green when selected, gray when not
                    yesButton.Background = new SolidColorBrush(selectedIndex == 0 ? ToastSuccessBorderColor : ButtonUnselectedBg);
                    yesButton.BorderBrush = new SolidColorBrush(selectedIndex == 0 ? Colors.White : ButtonUnselectedBorder);
                    yesButton.BorderThickness = selectedIndex == 0 ? new Thickness(3) : new Thickness(1);

                    // No button: red when selected, gray when not
                    noButton.Background = new SolidColorBrush(selectedIndex == 1 ? ToastErrorBorderColor : ButtonUnselectedBg);
                    noButton.BorderBrush = new SolidColorBrush(selectedIndex == 1 ? Colors.White : ButtonUnselectedBorder);
                    noButton.BorderThickness = selectedIndex == 1 ? new Thickness(3) : new Thickness(1);
                };

                // Create the confirmation content with controller support
                var grid = new System.Windows.Controls.Grid();
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

                // Message text
                var textBlock = new System.Windows.Controls.TextBlock
                {
                    Text = message,
                    FontSize = 18,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Colors.White),
                    Margin = new Thickness(20, 20, 20, 10),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = System.Windows.TextAlignment.Center
                };
                System.Windows.Controls.Grid.SetRow(textBlock, 0);
                grid.Children.Add(textBlock);

                // Controller hint
                var hintText = new System.Windows.Controls.TextBlock
                {
                    Text = "D-Pad/Arrows: Select  •  A/Enter: Confirm  •  B/Esc: Cancel",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(HintTextColor),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 5, 0, 10)
                };
                System.Windows.Controls.Grid.SetRow(hintText, 1);
                grid.Children.Add(hintText);

                // Button panel
                var buttonPanel = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 20)
                };

                // Rounded button style — eliminates WPF's default chrome so Background/Border match visually
                var roundedButtonStyle = new Style(typeof(System.Windows.Controls.Button));
                var template = new ControlTemplate(typeof(System.Windows.Controls.Button));
                var borderFactory = new FrameworkElementFactory(typeof(Border));
                borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
                borderFactory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                borderFactory.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                borderFactory.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                var contentFactory = new FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
                contentFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                contentFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                borderFactory.AppendChild(contentFactory);
                template.VisualTree = borderFactory;
                roundedButtonStyle.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, template));

                yesButton = new System.Windows.Controls.Button
                {
                    Content = "Yes",
                    Width = 140,
                    Height = 50,
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(10, 0, 10, 0),
                    Foreground = new SolidColorBrush(Colors.White),
                    Focusable = true,
                    IsTabStop = true,
                    Style = roundedButtonStyle
                };
                yesButton.Click += (s, e) => { result = true; window?.Close(); };
                yesButton.GotFocus += (s, e) => { selectedIndex = 0; updateButtonStyles(); };

                noButton = new System.Windows.Controls.Button
                {
                    Content = "No",
                    Width = 140,
                    Height = 50,
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(10, 0, 10, 0),
                    Foreground = new SolidColorBrush(Colors.White),
                    Focusable = true,
                    IsTabStop = true,
                    Style = roundedButtonStyle
                };
                noButton.Click += (s, e) => { result = false; window?.Close(); };
                noButton.GotFocus += (s, e) => { selectedIndex = 1; updateButtonStyles(); };

                buttonPanel.Children.Add(yesButton);
                buttonPanel.Children.Add(noButton);

                System.Windows.Controls.Grid.SetRow(buttonPanel, 2);
                grid.Children.Add(buttonPanel);

                // Initial button styles
                updateButtonStyles();

                window = CreateDialog(playniteApi, grid, new DialogOptions
                {
                    Title = title,
                    Width = 650,
                    Height = 400,
                    CanResize = false,
                    ShowMaximizeButton = false,
                    ShowMinimizeButton = false,
                    ApplyDarkBackground = true,
                    StartupLocation = WindowStartupLocation.CenterOwner,
                    SetOwner = true
                });

                // Focus Yes button on load so D-pad navigation works immediately
                window.ContentRendered += (s, e) =>
                {
                    yesButton.Focus();
                };

                // Handle keyboard input (Enter to confirm focused button, Escape to cancel)
                window.KeyDown += (s, e) =>
                {
                    if (e.Key == System.Windows.Input.Key.Enter)
                    {
                        result = selectedIndex == 0;
                        window?.Close();
                        e.Handled = true;
                    }
                    else if (e.Key == System.Windows.Input.Key.Escape)
                    {
                        result = false;
                        window?.Close();
                        e.Handled = true;
                    }
                    // D-pad Left/Right is translated to arrow keys by Playnite — WPF focus handles navigation
                    // GotFocus handlers on buttons update selectedIndex and styles automatically
                };

                // Register with controller event router for A/B button handling
                // D-pad navigation is handled by Playnite's D-pad→keyboard translation + WPF focus
                ModalControllerReceiver receiver = null;
                Services.Controller.ControllerEventRouter router = GetRouter();

                if (router != null)
                {
                    receiver = new ModalControllerReceiver(btn =>
                    {
                        if (btn == ControllerInput.A)
                        {
                            result = selectedIndex == 0;
                            window?.Close();
                        }
                        else if (btn == ControllerInput.B)
                        {
                            result = false;
                            window?.Close();
                        }
                    });
                    router.Register(receiver);
                }

                window.Closing += (s, e) =>
                {
                    if (receiver != null && router != null)
                        router.Unregister(receiver);
                };

                window.ShowDialog();

                return result;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Error showing controller confirmation, falling back to standard dialog");
                // Fallback to standard Playnite dialog
                return playniteApi.Dialogs.ShowMessage(message, title, MessageBoxButton.YesNo) == MessageBoxResult.Yes;
            }
        }
    }
}
