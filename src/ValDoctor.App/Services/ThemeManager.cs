using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ValDoctor.App.Services;

/// <summary>Follows the Windows "app mode" (light/dark) setting, including live changes.</summary>
internal static partial class ThemeManager
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;
    private static ResourceDictionary? _current;

    public static bool IsDark { get; private set; }

    public static event EventHandler? ThemeChanged;

    public static void Initialize(Application app)
    {
        Apply(app, ReadSystemPrefersDark());
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            {
                app.Dispatcher.BeginInvoke(() =>
                {
                    var dark = ReadSystemPrefersDark();
                    if (dark != IsDark)
                    {
                        Apply(app, dark);
                    }
                });
            }
        };
    }

    public static void Apply(Application app, bool dark)
    {
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/ValDoctor;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };

        var merged = app.Resources.MergedDictionaries;
        if (_current is not null)
        {
            merged.Remove(_current);
        }
        else if (merged.Count > 0)
        {
            // Remove the design-time placeholder (Light.xaml) declared in App.xaml.
            merged.RemoveAt(0);
        }

        merged.Insert(0, dictionary);
        _current = dictionary;
        IsDark = dark;
        foreach (Window window in app.Windows)
        {
            ApplyTitleBar(window);
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var value = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeLegacy, ref value, sizeof(int));
        }
    }

    private static bool ReadSystemPrefersDark()
    {
        try
        {
            if (SystemParameters.HighContrast)
            {
                return false;
            }

            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [LibraryImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
