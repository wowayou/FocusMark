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
        string activeTheme;
        ThemeProfile theme = ThemeProfile.Dark;
        Window panel;
        IntPtr handle;
        TextBox input;
        Forms.NotifyIcon tray;
        Forms.Screen screen;
        WindowTarget target, candidate;
        CheckBox rebind;
        string note { get { return store.Current.Text; } }
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
            store.Load(out warning);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Panel.xaml"))
                panel = (Window)XamlReader.Load(stream);
            string themeError = LoadTheme(store.Current.Theme, false);
            handle = new WindowInteropHelper(panel).EnsureHandle();
            HwndSource.FromHwnd(handle).AddHook(Messages);
            int corner = 3; // DWMWCP_ROUNDSMALL
            DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
            screen = Forms.Screen.FromHandle(WindowTarget.GetForegroundWindow());
            rebind = Find<CheckBox>("Rebind");
            input = Find<TextBox>("Input");
            input.TextChanged += delegate { if (view == View.Editor) UpdateCount(); };
            DataObject.AddPastingHandler(input, CheckPaste);
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
            Find<Button>("Fold").Click += delegate { Collapse(); };
            Find<Button>("Edit").Click += delegate { Edit(IntPtr.Zero); };
            Find<Button>("Done").Click += delegate {
                if (!TryCommit(store.Clear)) return;
                DropTarget(); HidePanel();
            };
            panel.Closing += (s, a) => { if (!exiting) { a.Cancel = true; HidePanel(); } };
            panel.SizeChanged += delegate { if (panel.IsVisible) Place(); };
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开", null, delegate { Edit(IntPtr.Zero); });
            menu.Items.Add("隐藏", null, delegate { HidePanel(); });
            var bottom = new Forms.ToolStripMenuItem("贴近屏幕底部") { Checked = store.Current.Bottom };
            var mask = new Forms.ToolStripMenuItem("隐藏胶囊文字") { Checked = store.Current.HidePreview };
            bottom.Click += delegate {
                if (TryCommit(delegate { store.SetOptions(!store.Current.Bottom, store.Current.HidePreview); }))
                { bottom.Checked = store.Current.Bottom; Place(); }
            };
            mask.Click += delegate {
                if (TryCommit(delegate { store.SetOptions(store.Current.Bottom, !store.Current.HidePreview); }))
                { mask.Checked = store.Current.HidePreview; UpdatePreview(); }
            };
            var appearance = new Forms.ToolStripMenuItem("外观");
            appearance.DropDownItems.Add("主题");
            appearance.DropDownOpening += delegate { PopulateThemes(appearance); };
            menu.Items.Add(appearance);
            menu.Items.Add(bottom);
            menu.Items.Add(mask);
            menu.Items.Add("退出", null, delegate { exiting = true; Shutdown(); });
            // csc /win32icon stores the application's icon group at resource 32512.
            var appIcon = LoadImage(Marshal.GetHINSTANCE(typeof(App).Module), new IntPtr(32512), 1, 0, 0, 0x8040); // LR_SHARED | LR_DEFAULTSIZE
            if (appIcon == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            tray = new Forms.NotifyIcon { Text = "FocusMark · Ctrl+Alt+J", Icon = System.Drawing.Icon.FromHandle(appIcon), ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { Edit(IntPtr.Zero); };
            registered = RegisterHotKey(handle, Hotkey, 0x4003, 0x4A); // NOREPEAT | ALT | CONTROL
            if (note != null) ShowView(View.Pill);
            if (themeError != null) Alert(themeError + "\n已使用默认深色。请在托盘的“外观”菜单重新选择。");
            if (warning != null) Alert(warning);
            if (!registered) Alert("Ctrl+Alt+J 注册失败，可能已被占用。请从托盘选择“打开”。");
        }

        string LoadTheme(string id, bool persist)
        {
            try
            {
                var next = ThemeProfile.Read(id);
                if (persist && store.Current.Theme != id) store.SetTheme(id);
                activeTheme = id;
                theme = next;
                theme.Apply(panel.Resources);
                if (view == View.Pill) UpdatePreview();
                return null;
            }
            catch (Exception e)
            {
                if (!(e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)) throw;
                if (!persist) theme.Apply(panel.Resources);
                return "无法应用主题。\n" + e.Message;
            }
        }

        void PopulateThemes(Forms.ToolStripMenuItem menu)
        {
            while (menu.DropDownItems.Count > 0) menu.DropDownItems[0].Dispose();
            Action<string,string> add = (label, id) => {
                var item = new Forms.ToolStripMenuItem(label) { Checked = id == activeTheme };
                item.Click += delegate { string error = LoadTheme(id, true); if (error != null) Alert(error); };
                menu.DropDownItems.Add(item);
            };
            add("深色 · 薄荷", null);
            add("浅色 · 纸白", "light");
            try { foreach (var id in ThemeProfile.Installed()) add(id, id); }
            catch (IOException e) { menu.DropDownItems.Add(e.Message).Enabled = false; }
            catch (UnauthorizedAccessException e) { menu.DropDownItems.Add(e.Message).Enabled = false; }
            menu.DropDownItems.Add(new Forms.ToolStripSeparator());
            menu.DropDownItems.Add("打开主题目录与示例", null, delegate {
                try
                {
                    Directory.CreateDirectory(ThemeProfile.Folder);
                    string example = Path.Combine(ThemeProfile.Folder, "amber.json");
                    if (!File.Exists(example))
                        using (var dest = new FileStream(example, FileMode.CreateNew, FileAccess.Write))
                        using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("amber.json")) src.CopyTo(dest);
                    System.Diagnostics.Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + ThemeProfile.Folder + "\"");
                }
                catch (IOException e) { Alert(e.Message); }
                catch (UnauthorizedAccessException e) { Alert(e.Message); }
                catch (System.ComponentModel.Win32Exception e) { Alert(e.Message); }
            });
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
            bool newNote = note == null;
            if (!Note.Valid(text) || !TryCommit(delegate { store.Save(text); })) return;
            if (newNote || replace)
            {
                DropTarget();
                target = candidate;
                candidate = null;
            }
            ShowView(View.Pill);
        }

        bool TryCommit(Action write)
        {
            try { write(); return true; }
            catch (IOException e) { Alert("更改未保存。\n" + e.Message); }
            catch (UnauthorizedAccessException e) { Alert("没有写入权限，更改未保存。\n" + e.Message); }
            return false;
        }

        void CheckPaste(object sender, DataObjectPastingEventArgs e)
        {
            string pasted = e.DataObject.GetData(DataFormats.UnicodeText) as string;
            if (pasted == null) return;
            // At most two UTF-16 code units per Unicode scalar. Do not insert an
            // oversized clipboard payload into WPF layout or the undo stack.
            if (pasted.Length <= Note.Limit * 2)
            {
                string draft = input.Text.Remove(input.SelectionStart, input.SelectionLength)
                    .Insert(input.SelectionStart, pasted);
                if (Note.Length(Note.Clean(draft)) <= Note.Limit) return;
            }
            e.CancelCommand();
            Find<TextBlock>("Count").Text = "粘贴超出 200 字，请缩短后再试";
        }

        void UpdateCount()
        {
            int length = Note.Length(Note.Clean(input.Text));
            Find<TextBlock>("Count").Text = length > Note.Limit ? "请再精简 " + (length - Note.Limit) + " 字" : length + " / 200   ·   Esc 取消";
            Find<Button>("Save").IsEnabled = length > 0 && length <= Note.Limit;
        }

        void EndEdit()
        {
            if (view != View.Editor) return;
            input.IsUndoEnabled = false;
            input.Clear();
            input.IsUndoEnabled = true;
            composing = false;
        }

        void Collapse() { if (note == null) HidePanel(); else ShowView(View.Pill); }
        void DropTarget() { if (target != null) { target.Dispose(); target = null; } }
        void DiscardCandidate() { if (candidate != null) { candidate.Dispose(); candidate = null; } }
        void HidePanel() { EndEdit(); DiscardCandidate(); view = View.Hidden; panel.Hide(); }
        void SetStatus(string message)
        {
            var label = Find<TextBlock>("Status");
            label.Text = message;
            label.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        }

        void UpdatePreview()
        {
            Find<TextBlock>("Preview").Text = store.Current.HidePreview ? "查看备注" : note;
            if (view == View.Pill) panel.Width = store.Current.HidePreview ? 100 : theme.Width;
        }

        void ShowView(View next)
        {
            if (next != View.Editor) { EndEdit(); DiscardCandidate(); }
            // Hiding releases editor focus when collapsing. A visible passive capsule
            // can become an editor in place without hiding and showing the HWND again.
            if (next == View.Pill && panel.IsActive) panel.Hide();
            view = next;
            foreach (var item in new[] { View.Pill, View.Editor, View.Details })
                Find<FrameworkElement>(item.ToString()).Visibility = item == next ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("FullNote").Text = next == View.Details ? note : null;
            UpdatePreview();
            SetStatus(target == null ? "本次会话没有关联窗口。" : null);
            Find<Button>("Return").IsEnabled = target != null;
            if (next != View.Pill) panel.Width = 360;
            int style = GetWindowLong(handle, -20);
            SetWindowLong(handle, -20, next == View.Pill ? style | 0x08000000 : style & ~0x08000000); // WS_EX_NOACTIVATE
            panel.UpdateLayout();
            if (!panel.IsVisible) panel.Show();
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
            int y = store.Current.Bottom ? Math.Max(work.Top, work.Bottom - (rect.Bottom - rect.Top) - 8) : work.Top + 8;
            if (rect.Left == x && rect.Top == y) return;
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0015); // NOSIZE | NOZORDER | NOACTIVATE
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
