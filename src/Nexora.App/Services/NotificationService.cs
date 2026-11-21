using System;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Nexora.App.Services
{
    /// <summary>
    /// Shows Windows 10/11 toast notifications via Microsoft.Toolkit.Uwp.Notifications.
    /// Works for non-packaged (Win32) apps on Windows 10 1903+.
    /// </summary>
    public sealed class NotificationService : IDisposable
    {
        public NotificationService()
        {
            // Register COM activator so the SDK can route toast interactions.
            // For a show-only notification this is optional, but good practice.
            try { ToastNotificationManagerCompat.OnActivated += _ => { }; }
            catch { /* non-critical */ }
        }

        /// <summary>
        /// Shows a Windows toast notification.
        /// </summary>
        /// <param name="title">Bold title line.</param>
        /// <param name="body">Message body text.</param>
        public void Show(string title, string body)
        {
            try
            {
                new ToastContentBuilder()
                    .AddText(title)
                    .AddText(body)
                    .Show();
            }
            catch
            {
                // Notifications are best-effort — never crash the app.
            }
        }

        public void Dispose()
        {
            try { ToastNotificationManagerCompat.Uninstall(); }
            catch { /* non-critical */ }
        }
    }
}
