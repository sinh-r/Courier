using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Courier.Core.Abstractions;

namespace Courier.App.Services;

/// <summary>
/// Supplies the native window handle the WAM broker parents its dialog to. ENT-01.
/// </summary>
/// <remarks>
/// TECH_SPEC 1.2 and 7.2 both flag this as the likeliest wall in the whole plan, and it is worth
/// saying plainly why: MSAL's broker shows a native dialog owned by an HWND. Given the wrong one,
/// or none, the dialog appears behind the app and the user sees a hang with no way out. Avalonia
/// exposes the handle through <see cref="TopLevel.TryGetPlatformHandle"/>, which is enough — the
/// window-parenting problem turns out to be a two-line lookup rather than a framework blocker.
/// </remarks>
public sealed class ActiveWindowHandleProvider : IBrokerWindowHandleProvider
{
    public IntPtr GetActiveWindowHandle()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return IntPtr.Zero;
        }

        // The active window, so a broker prompt raised from a settings dialog parents to that
        // dialog rather than to the main window behind it.
        var window = desktop.Windows.FirstOrDefault(w => w.IsActive)
            ?? desktop.MainWindow;

        return window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
    }
}
