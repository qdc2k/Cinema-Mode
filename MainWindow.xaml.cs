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
            this.Visibility = Visibility.Visible;
            this.WindowState = WindowState.Normal;
            this.ShowInTaskbar = true;
            this.Topmost = true;
            this.Activate();
            this.Focus();

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetForegroundWindow(hwnd);
            this.Topmost = false;
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

                var screen = Screen.FromHandle(foregroundWnd);
                if (screen == null) return;

                // Check if foreground window matches screen dimensions (Fullscreen)
                bool isFullscreen = (rect.Bottom - rect.Top) >= screen.Bounds.Height &&
                                    (rect.Right - rect.Left) >= screen.Bounds.Width;

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

                uint numPath, numMode;
                if (GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out numPath, out numMode) != 0) return;

                var pathArray = new DISPLAYCONFIG_PATH_INFO[numPath];
                var modeArray = new DISPLAYCONFIG_MODE_INFO[numMode];
                if (QueryDisplayConfig(QDC_ALL_PATHS, ref numPath, pathArray, ref numMode, modeArray, IntPtr.Zero) != 0) return;

                _savedPaths = (DISPLAYCONFIG_PATH_INFO[])pathArray.Clone();
                _savedModes = (DISPLAYCONFIG_MODE_INFO[])modeArray.Clone();

                List<DISPLAYCONFIG_PATH_INFO> activePaths = new List<DISPLAYCONFIG_PATH_INFO>();
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
                            activePaths.Add(pathArray[i]);
                        }
                    }
                }

                if (activePaths.Count > 0 && activePaths.Count < currentActiveCount)
                {
                    // IMPORTANT: For a single-monitor setup, the active monitor MUST be at coordinates (0,0).
                    // We find the source or desktop mode for our target path and force its position to the origin
                    // to prevent "clipping" or "cutting" on non-primary monitors.
                    for (int m = 0; m < modeArray.Length; m++)
                    {
                        if ((modeArray[m].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE ||
                             modeArray[m].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE) &&
                            modeArray[m].adapterId.LowPart == activePaths[0].sourceInfo.adapterId.LowPart &&
                            modeArray[m].adapterId.HighPart == activePaths[0].sourceInfo.adapterId.HighPart &&
                            modeArray[m].id == activePaths[0].sourceInfo.id)
                        {
                            if (modeArray[m].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                            {
                                modeArray[m].union.sourceMode.position.x = 0;
                                modeArray[m].union.sourceMode.position.y = 0;
                            }
                            else // DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE
                            {
                                int width = modeArray[m].union.desktopImageInfo.DesktopImageRegion.right - modeArray[m].union.desktopImageInfo.DesktopImageRegion.left;
                                int height = modeArray[m].union.desktopImageInfo.DesktopImageRegion.bottom - modeArray[m].union.desktopImageInfo.DesktopImageRegion.top;

                                modeArray[m].union.desktopImageInfo.DesktopImageRegion.left = 0;
                                modeArray[m].union.desktopImageInfo.DesktopImageRegion.top = 0;
                                modeArray[m].union.desktopImageInfo.DesktopImageRegion.right = width;
                                modeArray[m].union.desktopImageInfo.DesktopImageRegion.bottom = height;

                                modeArray[m].union.desktopImageInfo.PathSourceSize.x = width;
                                modeArray[m].union.desktopImageInfo.PathSourceSize.y = height;
                            }
                        }
                    }

                    int result = SetDisplayConfig((uint)activePaths.Count, activePaths.ToArray(), (uint)modeArray.Length, modeArray,
                        SDC_APPLY | SDC_ALLOW_CHANGES | SDC_USE_SUPPLIED_CONFIG);

                    if (result == 0) _isDisconnected = true;
                }
            }
            finally
            {
                _timer.Start();
            }
        }

        private void RestoreScreens()
        {
            if (!_isDisconnected || _savedPaths == null) return;

            SetDisplayConfig((uint)_savedPaths.Length, _savedPaths, (uint)_savedModes.Length, _savedModes, SDC_APPLY | SDC_USE_SUPPLIED_CONFIG | SDC_ALLOW_CHANGES);

            _isDisconnected = false;
            _savedPaths = null;
            _savedModes = null;
        }
    }
}
