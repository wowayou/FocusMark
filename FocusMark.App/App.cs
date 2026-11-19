using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8.1")]
namespace FocusMark
{
    internal sealed class App : Application
    {
        const int Hotkey = 1;
        readonly NoteStore store = new NoteStore(Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "FocusMark"));
        Window panel;
        IntPtr handle;
        TextBox input;
        Forms.NotifyIcon tray;
        Forms.Screen screen;
        WindowTarget target, candidate;
        CheckBox rebind;
        string note;
        bool composing, registered, exiting;
        enum View { Hidden, Editor, Pill, Details }
        View view;

        [STAThread] static void Main()
        {
            bool first;
            using (var mutex = new Mutex(true, @"Local\FocusMark.Single", out first))
            {
                if (!first) { Alert("FocusMark 已运行，请使用快捷键或托盘打开。"); return; }
                try { new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }.Run(); }
                catch (Exception e) { Alert("FocusMark 已停止。\n" + e.Message); }
            }
        }

        static void Alert(string text) { MessageBox.Show(text, "FocusMark", MessageBoxButton.OK, MessageBoxImage.Information); }
        T Find<T>(string name) where T : FrameworkElement { return (T)panel.FindName(name); }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            System.Windows.Media.RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            string warning;
            note = store.Load(out warning); // Unexpected I/O errors abort startup before enabling writes.
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Panel.xaml"))
                panel = (Window)XamlReader.Load(stream);
            handle = new WindowInteropHelper(panel).EnsureHandle();
            HwndSource.FromHwnd(handle).AddHook(Messages);
            int corner = 3; // DWMWCP_ROUNDSMALL
            DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
            screen = Forms.Screen.FromHandle(WindowTarget.GetForegroundWindow());
            rebind = Find<CheckBox>("Rebind");
            input = Find<TextBox>("Input");
            input.TextChanged += delegate { UpdateCount(); };
            TextCompositionManager.AddPreviewTextInputStartHandler(input, delegate { composing = true; });
            TextCompositionManager.AddPreviewTextInputUpdateHandler(input, delegate { composing = true; });
            input.AddHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler(delegate { composing = false; }), true);
            panel.PreviewKeyDown += Keys;
            Find<Button>("Save").Click += delegate { Save(); };
            Find<Button>("Pill").Click += delegate { ShowView(View.Details); };
            Find<Button>("Pill").PreviewMouseDown += (s, a) => { if (a.ChangedButton == MouseButton.Middle) { HidePanel(); a.Handled = true; } };
            Find<Button>("Return").Click += async delegate {
                var original = target;
                if (original == null) return;
                Find<Button>("Return").IsEnabled = false;
                string message = await original.TryReturn();
                if (original != target || view != View.Details) return;
                Find<Button>("Return").IsEnabled = true;
                if (message == null) ShowView(View.Pill); else SetStatus(message);
            };
            Find<Button>("Edit").Click += delegate { Edit(IntPtr.Zero); };
            Find<Button>("Done").Click += delegate {
                if (!WriteNote(store.Clear)) return;
                DropTarget(); note = null; HidePanel();
            };
            panel.Closing += (s, a) => { if (!exiting) { a.Cancel = true; HidePanel(); } };
            panel.SizeChanged += delegate { if (panel.IsVisible) Place(); };
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开", null, delegate { Edit(IntPtr.Zero); });
            menu.Items.Add("隐藏", null, delegate { HidePanel(); });
            menu.Items.Add("退出", null, delegate { exiting = true; Shutdown(); });
            // csc /win32icon stores the application's icon group at resource 32512.
            var appIcon = LoadImage(Marshal.GetHINSTANCE(typeof(App).Module), new IntPtr(32512), 1, 0, 0, 0x8040); // LR_SHARED | LR_DEFAULTSIZE
            if (appIcon == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            tray = new Forms.NotifyIcon { Text = "FocusMark · Ctrl+Alt+J", Icon = System.Drawing.Icon.FromHandle(appIcon), ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { Edit(IntPtr.Zero); };
            registered = RegisterHotKey(handle, Hotkey, 0x4003, 0x4A); // NOREPEAT | ALT | CONTROL
            if (note != null) ShowView(View.Pill);
            if (warning != null) Alert(warning);
            if (!registered) Alert("Ctrl+Alt+J 注册失败，可能已被占用。请从托盘选择“打开”。");
        }

        IntPtr Messages(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x0312 && w.ToInt32() == Hotkey) { Edit(WindowTarget.GetForegroundWindow()); handled = true; }
            if (msg == 0x02E0 || msg == 0x001A) Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
            return IntPtr.Zero;
        }

        void Edit(IntPtr foreground)
        {
            if (view != View.Editor)
            {
                if (foreground != IntPtr.Zero && foreground != handle) screen = Forms.Screen.FromHandle(foreground);
                if (foreground != IntPtr.Zero && foreground != handle &&
                    (target == null || target.Handle != foreground || !target.Matches()))
                    candidate = WindowTarget.Capture(foreground);
                rebind.IsChecked = false;
                rebind.Visibility = note != null && candidate != null ? Visibility.Visible : Visibility.Collapsed;
                input.Text = note ?? "";
                composing = false;
                ShowView(View.Editor);
                UpdateCount();
                input.SelectAll();
            }
            panel.Activate();
            Keyboard.Focus(input);
        }

        void Keys(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.ImeProcessed || composing) return;
            if (e.Key == Key.Escape) { e.Handled = true; Collapse(); }
            else if (e.Key == Key.Enter && view == View.Editor && input.IsKeyboardFocused)
            { e.Handled = true; if (!e.IsRepeat) Save(); }
        }

        void Save()
        {
            string text = Note.Clean(input.Text);
            bool replace = rebind.IsChecked == true;
            if (replace && (candidate == null || !candidate.Matches()))
            {
                rebind.IsChecked = false;
                rebind.Visibility = Visibility.Collapsed;
                Alert("刚才的窗口已失效。可以保留原关联并保存文字。");
                return;
            }
            if (!Note.Valid(text) || !WriteNote(delegate { store.Save(text); })) return;
            if (note == null || replace)
            {
                DropTarget();
                target = candidate;
                candidate = null;
            }
            note = text;
            ShowView(View.Pill);
        }

        bool WriteNote(Action write)
        {
            try { write(); return true; }
            catch (IOException e) { Alert("备注未更改。\n" + e.Message); }
            catch (UnauthorizedAccessException e) { Alert("没有写入权限，备注未更改。\n" + e.Message); }
            return false;
        }

        void UpdateCount()
        {
            string text = Note.Clean(input.Text);
            Find<TextBlock>("Count").Text = Note.Length(text) + " / 200   ·   Esc 取消";
            Find<Button>("Save").IsEnabled = Note.Valid(text);
        }

        void Collapse() { if (note == null) HidePanel(); else ShowView(View.Pill); }
        void DropTarget() { if (target != null) { target.Dispose(); target = null; } }
        void DiscardCandidate() { if (candidate != null) { candidate.Dispose(); candidate = null; } }
        void HidePanel() { DiscardCandidate(); view = View.Hidden; panel.Hide(); }
        void SetStatus(string message)
        {
            var label = Find<TextBlock>("Status");
            label.Text = message;
            label.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        }

        void ShowView(View next)
        {
            if (next != View.Editor) DiscardCandidate();
            if (panel.IsVisible) panel.Hide();
            view = next;
            foreach (var item in new[] { View.Pill, View.Editor, View.Details })
                Find<FrameworkElement>(item.ToString()).Visibility = item == next ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("Preview").Text = Find<TextBlock>("FullNote").Text = note;
            SetStatus(target == null ? "本次会话没有关联窗口。" : null);
            Find<Button>("Return").IsEnabled = target != null;
            panel.Width = next == View.Pill ? 216 : 360;
            int style = GetWindowLong(handle, -20);
            SetWindowLong(handle, -20, next == View.Pill ? style | 0x08000000 : style & ~0x08000000); // WS_EX_NOACTIVATE
            panel.UpdateLayout();
            panel.Show();
            Place();
        }

        void DisplayChanged(object s, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(delegate { screen = Forms.Screen.FromHandle(handle); Place(); }));
        }

        void Place()
        {
            RectPx rect;
            if (screen == null || !panel.IsVisible || !GetWindowRect(handle, out rect)) return;
            var work = screen.WorkingArea;
            int x = work.Left + Math.Max(0, (work.Width - (rect.Right - rect.Left)) / 2);
            SetWindowPos(handle, IntPtr.Zero, x, work.Top + 8, 0, 0, 0x0015); // NOSIZE | NOZORDER | NOACTIVATE
        }

        protected override void OnExit(ExitEventArgs e)
        {
            exiting = true;
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            if (registered) UnregisterHotKey(handle, Hotkey);
            if (tray != null) { tray.Visible = false; tray.Icon.Dispose(); tray.ContextMenuStrip.Dispose(); tray.Dispose(); }
            DiscardCandidate();
            DropTarget();
            base.OnExit(e);
        }
        [StructLayout(LayoutKind.Sequential)] struct RectPx { public int Left, Top, Right, Bottom; }
        [DllImport("user32", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadImage(IntPtr module, IntPtr name, uint type, int width, int height, uint flags);
        [DllImport("user32")] static extern bool GetWindowRect(IntPtr h, out RectPx rect);
        [DllImport("user32")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
        [DllImport("user32")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32")] static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("user32")] static extern bool RegisterHotKey(IntPtr h, int id, int modifiers, int key);
        [DllImport("user32")] static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    }
}
