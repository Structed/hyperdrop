using System.IO;
using System.Media;
using System.Windows;
using HyperDrop.App.Interop;
using HyperDrop.Core.Transfer;
using Forms = System.Windows.Forms;

namespace HyperDrop.App.Notifications;

/// <summary>
/// Tells the user a batch has finished, without requiring the window to be visible.
/// </summary>
/// <remarks>
/// A <see cref="Forms.NotifyIcon"/> balloon is used rather than a real toast because toasts need an
/// AppUserModelID registered through a Start menu shortcut, which is a lot of machinery for a small
/// tool. On Windows 10 and later the balloon is surfaced by the shell as an ordinary toast anyway.
/// The tray icon only appears while a notification is on screen.
/// </remarks>
public sealed class Notifier : IDisposable
{
    private static readonly Uri IconUri = new("pack://application:,,,/Assets/HyperDrop.ico", UriKind.Absolute);

    private readonly Forms.NotifyIcon _trayIcon;
    private readonly System.Drawing.Icon? _appIcon;
    private Window? _window;
    private bool _disposed;

    public Notifier()
    {
        _appIcon = LoadAppIcon();

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _appIcon ?? System.Drawing.SystemIcons.Application,
            Text = "HyperDrop",
            Visible = false,
        };

        _trayIcon.BalloonTipClosed += (_, _) => Hide();
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            Hide();
            RestoreWindow();
        };
    }

    /// <summary>
    /// Loads the application icon at the size the notification area asks for, so the tray picks the
    /// frame matching the current DPI instead of rescaling whichever one happens to come first.
    /// </summary>
    /// <remarks>
    /// A missing or unreadable icon must not cost the user their completion notification, so this
    /// falls back to the stock application icon rather than throwing.
    /// </remarks>
    private static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(IconUri);

            if (resource is null)
            {
                return null;
            }

            using var stream = resource.Stream;
            return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Associates the notifier with the main window so it can flash and restore it.</summary>
    public void Attach(Window window) => _window = window;

    public void NotifyBatchCompleted(TransferBatchSummary summary, bool showBalloon, bool playSound)
    {
        if (_window is not null)
        {
            WindowFlash.Flash(_window);
        }

        if (playSound)
        {
            if (summary.Failed > 0)
            {
                SystemSounds.Exclamation.Play();
            }
            else
            {
                SystemSounds.Asterisk.Play();
            }
        }

        if (!showBalloon || _disposed)
        {
            return;
        }

        var (title, message, icon) = Describe(summary);

        _trayIcon.Visible = true;
        _trayIcon.ShowBalloonTip(5000, title, message, icon);
    }

    private static (string Title, string Message, Forms.ToolTipIcon Icon) Describe(TransferBatchSummary summary)
    {
        var files = summary.Succeeded == 1 ? "file" : "files";

        if (summary.Failed == 0 && summary.Cancelled == 0)
        {
            return (
                "Transfer complete",
                $"Copied {summary.Succeeded} {files} ({Humanize.Bytes(summary.BytesTransferred)}) to {summary.VmName}.",
                Forms.ToolTipIcon.Info);
        }

        var parts = new List<string>();

        if (summary.Succeeded > 0)
        {
            parts.Add($"{summary.Succeeded} copied");
        }

        if (summary.Failed > 0)
        {
            parts.Add($"{summary.Failed} failed");
        }

        if (summary.Cancelled > 0)
        {
            parts.Add($"{summary.Cancelled} cancelled");
        }

        return (
            summary.Failed > 0 ? "Transfer finished with errors" : "Transfer finished",
            $"{string.Join(", ", parts)} for {summary.VmName}.",
            summary.Failed > 0 ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    private void RestoreWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void Hide()
    {
        if (!_disposed)
        {
            _trayIcon.Visible = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _appIcon?.Dispose();
    }
}
