using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FocusMark
{
    // The property belongs to this window instance. Windows discards it on destruction,
    // including when the same process later receives the same numeric HWND.
    internal sealed class WindowTarget : IDisposable
    {
        internal readonly IntPtr Handle;
        readonly uint pid, thread;
        readonly long started;
        readonly string marker = "FocusMark." + Guid.NewGuid().ToString("N");
        static readonly uint Self = (uint)Process.GetCurrentProcess().Id;

        static readonly IntPtr Present = new IntPtr(1);
        WindowTarget(IntPtr h, uint p, uint t, long s) { Handle = h; pid = p; thread = t; started = s; }

        static long ProcessStart(uint id)
        {
            try { using (var p = Process.GetProcessById((int)id)) return p.StartTime.ToUniversalTime().Ticks; }
            catch (ArgumentException) { return 0; }
            catch (InvalidOperationException) { return 0; }
            catch (System.ComponentModel.Win32Exception) { return 0; }
        }

        internal static WindowTarget Capture(IntPtr h)
        {
            uint p;
            uint t = GetWindowThreadProcessId(h, out p);
            if (t == 0 || p == Self || h == GetShellWindow() || !IsWindowVisible(h)) return null;
            long s = ProcessStart(p);
            if (s == 0) return null;
            var target = new WindowTarget(h, p, t, s);
            if (!SetProp(h, target.marker, Present)) return null;
            if (target.Matches()) return target;
            target.Dispose();
            return null;
        }

        internal bool Matches()
        {
            uint p;
            return IsWindow(Handle) && GetWindowThreadProcessId(Handle, out p) == thread && p == pid
                && ProcessStart(p) == started && GetProp(Handle, marker) == Present;
        }

        internal async System.Threading.Tasks.Task<string> TryReturn()
        {
            if (!Matches()) return "原窗口已关闭或身份已改变。";
            if (IsIconic(Handle))
            {
                ShowWindowAsync(Handle, 9); // SW_RESTORE on the target thread
                await System.Threading.Tasks.Task.Delay(120);
            }
            if (!Matches()) return "原窗口已关闭或身份已改变。";
            SetForegroundWindow(Handle);
            await System.Threading.Tasks.Task.Delay(100);
            return Matches() && GetForegroundWindow() == Handle && !IsIconic(Handle)
                ? null : "Windows 未确认切换，请手动返回。";
        }

        public void Dispose() { if (GetProp(Handle, marker) == Present) RemoveProp(Handle, marker); }
        [DllImport("user32")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32")] static extern IntPtr GetShellWindow();
        [DllImport("user32")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32", CharSet=CharSet.Unicode)] static extern bool SetProp(IntPtr h, string key, IntPtr value);
        [DllImport("user32", CharSet=CharSet.Unicode)] static extern IntPtr GetProp(IntPtr h, string key);
        [DllImport("user32", CharSet=CharSet.Unicode)] static extern IntPtr RemoveProp(IntPtr h, string key);
        [DllImport("user32")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32")] static extern bool ShowWindowAsync(IntPtr h, int cmd);
        [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr h);
    }
}
