using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Win32;

namespace CinemaMode
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _timer;
        private bool _isModeActive = false;
        private bool _isDisconnected = false;
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;
        private bool _isInitializing = true;
        private DateTime _lastActionTime = DateTime.MinValue;
        private List<Window> _dimmingWindows = new List<Window>();

        private static readonly HashSet<string> _builtinExceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SnagitCaptureUX", "Snagit", "SnagIt32", "SnagItEditor", "SnagitCapture",
            "Greenshot", "ShareX", "Lightshot", "PicPick", "Flameshot", "explorer",
            "ScreenClippingHost", "SnippingTool", "SnippingToolWPF"
        };
        private HashSet<string> _exceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;

        // P/Invoke for Display Configuration
        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [In, Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray, ref uint numModeInfoArrayElements, [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr topologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME deviceName);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;

        [DllImport("user32.dll")]
        private static extern int SetDisplayConfig(uint numPathArrayElements, [In, Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray, uint numModeInfoArrayElements, [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
            private uint _padding; // x64 requires 8-byte alignment; pads 68 to 72 bytes.
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint status; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint status;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public uint targetAvailable; // Win32 BOOL is 4 bytes, C# bool is 1. Using uint fixes heap corruption.
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO { public uint infoType; public uint id; public LUID adapterId; public DISPLAYCONFIG_MODE_INFO_UNION union; }

        [StructLayout(LayoutKind.Explicit)]
        private struct DISPLAYCONFIG_MODE_INFO_UNION { [FieldOffset(0)] public DISPLAYCONFIG_TARGET_MODE targetMode; [FieldOffset(0)] public DISPLAYCONFIG_SOURCE_MODE sourceMode; [FieldOffset(0)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_2DREGION
        {
            public uint cx;
            public uint cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
        {
            public ulong pixelRate;
            public DISPLAYCONFIG_RATIONAL hSyncFreq;
            public DISPLAYCONFIG_RATIONAL vSyncFreq;
            public DISPLAYCONFIG_2DREGION activeSize;
            public DISPLAYCONFIG_2DREGION totalSize;
            public uint videoStandard;
            public uint scanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_TARGET_MODE { public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_SOURCE_MODE { public uint width; public uint height; public uint pixelFormat; public POINTL position; }
        [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO { public POINTL PathSourceSize; public RECTL DesktopImageRegion; public RECTL DesktopImageClip; }
        [StructLayout(LayoutKind.Sequential)] private struct POINTL { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECTL { public int left; public int top; public int right; public int bottom; }

        private const uint QDC_ALL_PATHS = 0x00000001;
        private const uint SDC_APPLY = 0x00000080;
        private const uint SDC_ALLOW_CHANGES = 0x00000400;
        private const uint SDC_USE_SUPPLIED_CONFIG = 0x00000020;
        private const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
        private const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
        private const uint DISPLAYCONFIG_MODE_INFO_TYPE_TARGET = 2;
        private const uint DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE = 3;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private DISPLAYCONFIG_PATH_INFO[] _savedPaths;
        private DISPLAYCONFIG_MODE_INFO[] _savedModes;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        public MainWindow()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += CheckFullscreen;
            _timer.Start();
            this.MouseLeftButtonDown += (s, e) => DragMove();

            // Setup tray icon
            SetupTrayIcon();

            // Set window icon in the UI
            using (var icon = GetTrayIcon())
            {
                AppIconImg.Source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }

            LoadSettings();
        }

        private void LoadSettings()
        {
            _isInitializing = true;
            try
            {
                // Load "Start with Windows" from Registry
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    StartWithWindowsCheck.IsChecked = key?.GetValue("CinemaMode") != null;
                }

                // Load "Start Minimized" and Mode state from Registry
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\CinemaMode", false))
                {
                    StartMinimizedCheck.IsChecked = (int)(key?.GetValue("StartMinimized", 0) ?? 0) == 1;
                    ModeToggle.IsChecked = (int)(key?.GetValue("IsEnabled", 0) ?? 0) == 1;
                    DimModeCheck.IsChecked = (int)(key?.GetValue("DimMode", 0) ?? 0) == 1;
                    BrightnessSlider.Value = (int)(key?.GetValue("Brightness", 0) ?? 0);

                    string exc = key?.GetValue("Exceptions", "") as string;
                    if (!string.IsNullOrEmpty(exc))
                        foreach (var s in exc.Split('|')) if (!string.IsNullOrWhiteSpace(s)) _exceptions.Add(s.Trim());
                }
            }
            finally { _isInitializing = false; }
        }

        private void SettingChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            // Save "Start with Windows"
            using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (StartWithWindowsCheck.IsChecked == true)
                    key?.SetValue("CinemaMode", $"\"{Environment.ProcessPath}\"");
                else
                    key?.DeleteValue("CinemaMode", false);
            }

            // Save "Start Minimized" and Mode state
            using (var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\CinemaMode"))
            {
                key.SetValue("StartMinimized", StartMinimizedCheck.IsChecked == true ? 1 : 0);
                key.SetValue("IsEnabled", ModeToggle.IsChecked == true ? 1 : 0);
                key.SetValue("DimMode", DimModeCheck.IsChecked == true ? 1 : 0);
                key.SetValue("Brightness", (int)BrightnessSlider.Value);
                key.SetValue("Exceptions", string.Join("|", _exceptions));
            }

            // Update active dimming windows in real-time if they exist
            if (_isDisconnected && _dimmingWindows.Count > 0)
            {
                foreach (var w in _dimmingWindows)
                    w.Opacity = (100 - BrightnessSlider.Value) / 100.0;
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (StartMinimizedCheck.IsChecked == true)
            {
                this.WindowState = WindowState.Minimized;
            }
        }

        private void SetupTrayIcon()
        {
            // Create context menu for tray icon
            _trayMenu = new ContextMenuStrip();

            var showItem = new ToolStripMenuItem("Show", null, (s, e) => RestoreWindow());
            var exitItem = new ToolStripMenuItem("Exit", null, (s, e) => ExitApplication());

            _trayMenu.Items.AddRange(new ToolStripItem[] { showItem, exitItem });

            // Create tray icon
            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = GetTrayIcon();
            _trayIcon.Visible = true;
            _trayIcon.ContextMenuStrip = _trayMenu;
            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    if (this.Visibility == Visibility.Visible && this.WindowState == WindowState.Normal)
                        this.WindowState = WindowState.Minimized;
                    else
                        RestoreWindow();
                }
            };
        }

        private System.Drawing.Icon GetTrayIcon()
        {
            try
            {
                // 1. Try to extract the icon from the executable itself (Portable support)
                // This uses the icon embedded during build via -p:ApplicationIcon
                string exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
                {
                    using (var exeIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath))
                    {
                        if (exeIcon != null) return (System.Drawing.Icon)exeIcon.Clone();
                    }
                }

                // 2. Fallback: Try to load the icon file from the same directory
                string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CinemaMode.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    return new System.Drawing.Icon(iconPath);
                }
            }
            catch { }

            // Fallback: Create a simple icon if file not found
            Bitmap bitmap = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(System.Drawing.Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (Brush brush = new SolidBrush(_isModeActive ? System.Drawing.Color.Green : System.Drawing.Color.Red))
                {
                    g.FillEllipse(brush, 2, 2, 12, 12);
                }
            }
            IntPtr iconHandle = bitmap.GetHicon();
            return System.Drawing.Icon.FromHandle(iconHandle);
        }

        private void RestoreWindow()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            if (!this.IsVisible) this.Show();
            if (this.WindowState == WindowState.Minimized) ShowWindow(hwnd, SW_RESTORE);

            this.Visibility = Visibility.Visible;
            this.ShowInTaskbar = true;
            this.WindowState = WindowState.Normal;

            this.Topmost = true; // Bring to front forcefully
            this.Activate();
            SetForegroundWindow(hwnd);
            this.Topmost = false;
            this.Focus();
        }

        private void ExitApplication()
        {
            RestoreScreens();
            _trayIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                this.Visibility = Visibility.Hidden;
                this.ShowInTaskbar = false;
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void Exceptions_Click(object sender, RoutedEventArgs e)
        {
            Window win = new Window { Title = "Exceptions", Width = 260, Height = 410, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(26, 26, 26)), Foreground = System.Windows.Media.Brushes.White, Owner = this, WindowStartupLocation = WindowStartupLocation.Manual, Left = this.Left + this.Width + 5, Top = this.Top, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None, AllowsTransparency = true };
            win.MouseLeftButtonDown += (s, ev) => { if (ev.LeftButton == System.Windows.Input.MouseButtonState.Pressed) win.DragMove(); };

            var grid = new System.Windows.Controls.Grid();
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(45) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Custom Title Bar
            var titleBar = new System.Windows.Controls.Grid { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 37, 37)) };
            var titleText = new System.Windows.Controls.TextBlock { Text = "Exceptions", FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var brush = new System.Windows.Media.LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 0) };
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(147, 112, 219), 0.0));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(106, 90, 205), 0.5));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(65, 105, 225), 1.0));
            titleText.Foreground = brush;
            titleBar.Children.Add(titleText);

            var closeBtn = new System.Windows.Controls.Button { Content = "✕", Width = 30, Height = 30, Background = System.Windows.Media.Brushes.Transparent, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(136, 136, 136)), BorderThickness = new Thickness(0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0), Cursor = System.Windows.Input.Cursors.Hand };
            closeBtn.Click += (s, ev) => win.Close();
            titleBar.Children.Add(closeBtn);
            System.Windows.Controls.Grid.SetRow(titleBar, 0); grid.Children.Add(titleBar);

            var contentGrid = new System.Windows.Controls.Grid { Margin = new Thickness(15) };
            System.Windows.Controls.Grid.SetRow(contentGrid, 1);
            contentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            contentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            contentGrid.Children.Add(new System.Windows.Controls.TextBlock { Text = "Ignored Processes:", Foreground = System.Windows.Media.Brushes.White, FontSize = 12, Margin = new Thickness(0, 0, 0, 5) });
            var list = new System.Windows.Controls.ListBox { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 35)), Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 10), FontSize = 13 };
            foreach (var ex in _exceptions) list.Items.Add(ex);
            System.Windows.Controls.Grid.SetRow(list, 1); contentGrid.Children.Add(list);

            // Manual Entry Grid
            var manualGrid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 15) };
            manualGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            manualGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            var manualInput = new System.Windows.Controls.TextBox { Height = 25, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 45)), Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(1), BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60)), VerticalContentAlignment = System.Windows.VerticalAlignment.Center, Padding = new Thickness(5, 0, 5, 0) };
            var manualAddBtn = new System.Windows.Controls.Button { Content = "Add", Width = 40, Height = 25, Margin = new Thickness(5, 0, 0, 0), Foreground = System.Windows.Media.Brushes.White, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 45)), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, FontSize = 11 };
            manualAddBtn.Click += (s, ev) => { if (!string.IsNullOrWhiteSpace(manualInput.Text)) { string n = manualInput.Text.Trim(); if (_exceptions.Add(n)) { list.Items.Add(n); SettingChanged(null, null); } manualInput.Clear(); } };
            System.Windows.Controls.Grid.SetColumn(manualInput, 0); manualGrid.Children.Add(manualInput);
            System.Windows.Controls.Grid.SetColumn(manualAddBtn, 1); manualGrid.Children.Add(manualAddBtn);
            System.Windows.Controls.Grid.SetRow(manualGrid, 2); contentGrid.Children.Add(manualGrid);

            var btnStack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            var selectBtn = new System.Windows.Controls.Button { Content = "Select Running...", Width = 110, Height = 30, Margin = new Thickness(0, 0, 10, 0), Foreground = System.Windows.Media.Brushes.White, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 45)), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            selectBtn.Click += (s, ev) =>
            {
                Window pWin = new Window { Title = "Select Process", Width = 260, Height = 400, Background = win.Background, Foreground = win.Foreground, Owner = win, WindowStartupLocation = WindowStartupLocation.Manual, Left = win.Left + win.Width + 5, Top = win.Top, WindowStyle = WindowStyle.None, AllowsTransparency = true };
                pWin.MouseLeftButtonDown += (s2, ev2) => { if (ev2.LeftButton == System.Windows.Input.MouseButtonState.Pressed) pWin.DragMove(); };
                var pGrid = new System.Windows.Controls.Grid();
                pGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(45) });
                pGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                var pTitleBar = new System.Windows.Controls.Grid { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 37, 37)) };
                var pTitleText = new System.Windows.Controls.TextBlock { Text = "Select Process", FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                var pBrush = new System.Windows.Media.LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 0) };
                pBrush.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(147, 112, 219), 0.0));
                pBrush.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(106, 90, 205), 0.5));
                pBrush.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(65, 105, 225), 1.0));
                pTitleText.Foreground = pBrush;
                pTitleBar.Children.Add(pTitleText);
                var pCloseBtn = new System.Windows.Controls.Button { Content = "✕", Width = 30, Height = 30, Background = System.Windows.Media.Brushes.Transparent, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(136, 136, 136)), BorderThickness = new Thickness(0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0), Cursor = System.Windows.Input.Cursors.Hand };
                pCloseBtn.Click += (s2, ev2) => pWin.Close();
                pTitleBar.Children.Add(pCloseBtn);
                System.Windows.Controls.Grid.SetRow(pTitleBar, 0); pGrid.Children.Add(pTitleBar);
                var pList = new System.Windows.Controls.ListBox { Background = list.Background, Foreground = list.Foreground, BorderThickness = new Thickness(0), Margin = new Thickness(15), FontSize = 13 };
                var names = new List<string>();
                foreach (var p in System.Diagnostics.Process.GetProcesses()) try { if (!string.IsNullOrEmpty(p.MainWindowTitle) && !names.Contains(p.ProcessName)) names.Add(p.ProcessName); } catch { }
                names.Sort(); foreach (var n in names) pList.Items.Add(n);
                pList.MouseDoubleClick += (s2, ev2) => { if (pList.SelectedItem != null) { string n = pList.SelectedItem.ToString(); if (_exceptions.Add(n)) { list.Items.Add(n); SettingChanged(null, null); } pWin.Close(); } };
                System.Windows.Controls.Grid.SetRow(pList, 1); pGrid.Children.Add(pList);
                pWin.Content = pGrid; pWin.ShowDialog();
            };

            var remBtn = new System.Windows.Controls.Button { Content = "Remove", Width = 90, Height = 30, Foreground = System.Windows.Media.Brushes.White, Background = selectBtn.Background, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            remBtn.Click += (s, ev) => { if (list.SelectedItem != null) { _exceptions.Remove(list.SelectedItem.ToString()); list.Items.Remove(list.SelectedItem); SettingChanged(null, null); } };
            btnStack.Children.Add(selectBtn); btnStack.Children.Add(remBtn);
            System.Windows.Controls.Grid.SetRow(btnStack, 3); contentGrid.Children.Add(btnStack);

            grid.Children.Add(contentGrid);
            win.Content = grid;
            win.ShowDialog();
        }

        private void ModeToggle_Checked(object sender, RoutedEventArgs e)
        {
            _isModeActive = true;
            UpdateTrayIcon();
            SettingChanged(sender, e);
        }

        private void ModeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            _isModeActive = false;
            RestoreScreens();
            UpdateTrayIcon();
            SettingChanged(sender, e);
        }

        private void UpdateTrayIcon()
        {
            if (_trayIcon != null)
            {
                _trayIcon.Icon = GetTrayIcon();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => ExitApplication();

        private void CheckFullscreen(object sender, EventArgs e)
        {
            try
            {
                if (!_isModeActive) return;

                // Ignore checks for 2 seconds after a screen change to let the OS settle
                if (DateTime.Now - _lastActionTime < TimeSpan.FromSeconds(2)) return;

                IntPtr foregroundWnd = GetForegroundWindow();
                if (foregroundWnd == IntPtr.Zero) return;

                // Ignore Windows Desktop and Taskbar to prevent accidental triggers
                StringBuilder className = new StringBuilder(256);
                GetClassName(foregroundWnd, className, className.Capacity);
                string cn = className.ToString();
                if (cn == "Progman" || cn == "WorkerW" || cn == "Shell_TrayWnd")
                {
                    RestoreScreens();
                    return;
                }

                if (!GetWindowRect(foregroundWnd, out RECT rect)) return;

                // Check exceptions list by process name
                uint pid;
                GetWindowThreadProcessId(foregroundWnd, out pid);
                try
                {
                    using (var proc = System.Diagnostics.Process.GetProcessById((int)pid))
                        if (_exceptions.Contains(proc.ProcessName) || _builtinExceptions.Contains(proc.ProcessName))
                        { RestoreScreens(); return; }
                }
                catch { }

                var screen = Screen.FromHandle(foregroundWnd);
                if (screen == null) return;

                // Check if foreground window matches screen dimensions (Fullscreen). 
                // We use a small 10px tolerance for window borders or DPI rounding.
                bool isFullscreen = (rect.Bottom - rect.Top) >= screen.Bounds.Height - 10 &&
                                    (rect.Right - rect.Left) >= screen.Bounds.Width - 10;

                if (isFullscreen) DisconnectOthers(foregroundWnd);
                else RestoreScreens();
            }
            catch { /* Ignore transient errors when switching windows */ }
        }

        private void DisconnectOthers(IntPtr activeWindow)
        {
            if (_isDisconnected) return;
            _timer.Stop(); // Prevent timer re-entry during display configuration changes

            try
            {
                IntPtr hMonitor = MonitorFromWindow(activeWindow, 2); // MONITOR_DEFAULTTONEAREST
                MONITORINFOEX mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (!GetMonitorInfo(hMonitor, ref mi)) return;

                if (DimModeCheck.IsChecked == true)
                {
                    DimOtherScreens(mi.szDevice);
                    _isDisconnected = true;
                    _lastActionTime = DateTime.Now;
                    return;
                }

                uint numPath, numMode;
                if (GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out numPath, out numMode) != 0) return;

                var pathArray = new DISPLAYCONFIG_PATH_INFO[numPath];
                var modeArray = new DISPLAYCONFIG_MODE_INFO[numMode];
                if (QueryDisplayConfig(QDC_ALL_PATHS, ref numPath, pathArray, ref numMode, modeArray, IntPtr.Zero) != 0) return;

                _savedPaths = (DISPLAYCONFIG_PATH_INFO[])pathArray.Clone();
                _savedModes = (DISPLAYCONFIG_MODE_INFO[])modeArray.Clone();

                List<DISPLAYCONFIG_PATH_INFO> targetPaths = new List<DISPLAYCONFIG_PATH_INFO>();
                int currentActiveCount = 0;
                foreach (var p in pathArray) if ((p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0) currentActiveCount++;

                for (int i = 0; i < numPath; i++)
                {
                    if ((pathArray[i].flags & DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;

                    DISPLAYCONFIG_SOURCE_DEVICE_NAME sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    sourceName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    sourceName.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
                    sourceName.header.adapterId = pathArray[i].sourceInfo.adapterId;
                    sourceName.header.id = pathArray[i].sourceInfo.id;

                    if (DisplayConfigGetDeviceInfo(ref sourceName) == 0)
                    {
                        if (string.Equals(sourceName.viewGdiDeviceName, mi.szDevice, StringComparison.OrdinalIgnoreCase))
                        {
                            targetPaths.Add(pathArray[i]);
                        }
                    }
                }

                if (targetPaths.Count > 0 && targetPaths.Count < currentActiveCount)
                {
                    var activePath = targetPaths[0];

                    for (int m = 0; m < modeArray.Length; m++)
                    {
                        // Only modify modes belonging to our active monitor's adapter
                        if (modeArray[m].adapterId.LowPart != activePath.sourceInfo.adapterId.LowPart ||
                            modeArray[m].adapterId.HighPart != activePath.sourceInfo.adapterId.HighPart)
                            continue;

                        // If this is the Source/Desktop info for our screen, force it to the primary position (0,0)
                        if (modeArray[m].id == activePath.sourceInfo.id)
                        {
                            if (modeArray[m].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                            {
                                modeArray[m].union.sourceMode.position.x = 0;
                                modeArray[m].union.sourceMode.position.y = 0;
                            }
                            else if (modeArray[m].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE)
                            {
                                int w = modeArray[m].union.desktopImageInfo.DesktopImageRegion.right - modeArray[m].union.desktopImageInfo.DesktopImageRegion.left;
                                int h = modeArray[m].union.desktopImageInfo.DesktopImageRegion.bottom - modeArray[m].union.desktopImageInfo.DesktopImageRegion.top;
                                modeArray[m].union.desktopImageInfo.DesktopImageRegion = new RECTL { left = 0, top = 0, right = w, bottom = h };
                                modeArray[m].union.desktopImageInfo.PathSourceSize = new POINTL { x = w, y = h };
                            }
                        }
                    }

                    // Pass the full modeArray so Windows has all the Target/Timing info it needs.
                    int result = SetDisplayConfig((uint)targetPaths.Count, targetPaths.ToArray(), (uint)modeArray.Length, modeArray,
                        SDC_APPLY | SDC_ALLOW_CHANGES | SDC_USE_SUPPLIED_CONFIG);

                    System.Diagnostics.Debug.WriteLine($"Cinema Mode: SetDisplayConfig result: {result}");

                    if (result == 0)
                    {
                        _isDisconnected = true;
                        _lastActionTime = DateTime.Now;

                        // After screen config changes, the coordinate system shifts. 
                        // We wait for the GPU driver to settle, then force the window to the new origin.
                        System.Threading.Thread.Sleep(1000);

                        MONITORINFOEX freshMi = new MONITORINFOEX();
                        freshMi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));

                        // Get the monitor handle for our single remaining screen
                        IntPtr hMon = MonitorFromWindow(activeWindow, 1); // MONITOR_DEFAULTTOPRIMARY
                        if (GetMonitorInfo(hMon, ref freshMi))
                        {
                            int screenW = freshMi.rcMonitor.Right - freshMi.rcMonitor.Left;
                            int screenH = freshMi.rcMonitor.Bottom - freshMi.rcMonitor.Top;

                            // SWP_FRAMECHANGED forces the window to recalculate its internal scaling (DPI)
                            // and rendering viewport, which fixes the 1/4 or 1/6 cropping issue.
                            SetWindowPos(activeWindow, IntPtr.Zero, 0, 0, screenW, screenH, SWP_NOZORDER | SWP_FRAMECHANGED);
                        }
                    }
                }
            }
            finally
            {
                _timer.Start();
            }
        }

        private void DimOtherScreens(string activeDeviceName)
        {
            foreach (var screen in Screen.AllScreens)
            {
                if (string.Equals(screen.DeviceName, activeDeviceName, StringComparison.OrdinalIgnoreCase)) continue;

                Window dimWindow = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Black,
                    Opacity = (100 - BrightnessSlider.Value) / 100.0,
                    Topmost = true,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };

                dimWindow.Show();
                _dimmingWindows.Add(dimWindow);

                var hwnd = new System.Windows.Interop.WindowInteropHelper(dimWindow).Handle;
                int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);

                SetWindowPos(hwnd, new IntPtr(-1), screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height, 0x0040);
            }
        }

        private void RestoreScreens()
        {
            if (!_isDisconnected) return;
            if (DateTime.Now - _lastActionTime < TimeSpan.FromSeconds(1)) return;

            foreach (var w in _dimmingWindows) w.Close();
            _dimmingWindows.Clear();

            if (_savedPaths != null)
            {
                SetDisplayConfig((uint)_savedPaths.Length, _savedPaths, (uint)_savedModes.Length, _savedModes, SDC_APPLY | SDC_USE_SUPPLIED_CONFIG | SDC_ALLOW_CHANGES);
                _savedPaths = null;
                _savedModes = null;
            }

            _isDisconnected = false;
            _lastActionTime = DateTime.Now;
            _savedPaths = null;
            _savedModes = null;
        }
    }
}
