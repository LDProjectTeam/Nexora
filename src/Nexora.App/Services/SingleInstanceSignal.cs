using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nexora.App.Services
{
    /// <summary>
    /// Второй экземпляр лаунчера не может работать (один торрент-движок на профиль),
    /// поэтому вместо молчаливого выхода он просит уже запущенное окно показаться.
    /// Используется зарегистрированное оконное сообщение — без named pipes и служб.
    /// </summary>
    internal static class SingleInstanceSignal
    {
        private const string MessageName = "Nexora.Launcher.Activate";
        private static readonly IntPtr HwndBroadcast = (IntPtr)0xFFFF;

        /// <summary>0, если Windows не смогла зарегистрировать сообщение.</summary>
        public static readonly int ActivateMessage = (int)NativeMethods.RegisterWindowMessage(MessageName);

        /// <summary>
        /// Шлёт широковещательный запрос на показ окна. Повторяет несколько раз:
        /// первый экземпляр мог ещё не создать главное окно.
        /// </summary>
        public static void RequestActivation()
        {
            if (ActivateMessage == 0)
                return;

            for (var attempt = 0; attempt < 6; attempt++)
            {
                NativeMethods.PostMessage(HwndBroadcast, ActivateMessage, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(150);
            }
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern uint RegisterWindowMessage(string lpString);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        }
    }
}
