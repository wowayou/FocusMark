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
    internal sealed partial class App : Application
    {
        int hotkeyId;
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
        bool composing, exiting;
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
            string themeError = LoadTheme(store.Current.Theme);
            handle = new WindowInteropHelper(panel).EnsureHandle();
            HwndSource.FromHwnd(handle).AddHook(Messages);
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
            Find<Button>("Pill").Click += delegate {
                ShowView(View.Details);
                if (!store.Current.Still && SystemParameters.ClientAreaAnimation)
                {
                    var details = Find<FrameworkElement>("Details");
                    var duration = TimeSpan.FromMilliseconds(130);
                    var fade = new System.Windows.Media.Animation.DoubleAnimation(0.45, 1, duration);
                    var slide = new System.Windows.Media.Animation.DoubleAnimation(store.Current.Bottom ? 5 : -5, 0, duration);
                    slide.EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                    fade.FillBehavior = slide.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
                    details.BeginAnimation(UIElement.OpacityProperty, fade);
                    ((System.Windows.Media.TranslateTransform)details.RenderTransform).BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
                }
            };
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
            menu.Items.Add("选项", null, delegate { OpenSettings(); });
            menu.Items.Add("退出", null, delegate { exiting = true; Shutdown(); });
            // csc /win32icon stores the application's icon group at resource 32512.
            var appIcon = LoadImage(Marshal.GetHINSTANCE(typeof(App).Module), new IntPtr(32512), 1, 0, 0, 0x8040); // LR_SHARED | LR_DEFAULTSIZE
            if (appIcon == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            tray = new Forms.NotifyIcon { Text = "FocusMark", Icon = System.Drawing.Icon.FromHandle(appIcon), ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { Edit(IntPtr.Zero); };
            string keyError = CommitWithKey(store.Current.Letter, store.Current.Modifiers, delegate { });
            if (note != null) ShowView(View.Pill);
            if (themeError != null) Alert(themeError + "\n已使用默认深色。请在托盘“选项 → 外观”中重新选择。");
            if (warning != null) Alert(warning);
            if (keyError != null)
            {
                tray.Text = "FocusMark · 快捷键未启用";
                Alert(keyError + "\n可从托盘打开，或进入“选项 → 唤出快捷键”改键。");
            }
        }

        string CommitWithKey(string letter, int mods, Action commit)
        {
            bool register = hotkeyId == 0 || letter != store.Current.Letter || mods != store.Current.Modifiers;
            int nextId = hotkeyId == 1 ? 2 : 1;
            if (register && !RegisterHotKey(handle, nextId, 0x4000 | mods, Note.VirtualKey(letter)))
                return Note.Chord(mods, letter) + " 注册失败，可能已被占用。";
            try
            {
                commit();
            }
            catch (Exception e)
            {
                if (register) UnregisterHotKey(handle, nextId);
                if (!(e is IOException || e is UnauthorizedAccessException)) throw;
                return "设置保存失败。\n" + e.Message;
            }
            if (register)
            {
                if (hotkeyId != 0) UnregisterHotKey(handle, hotkeyId);
                hotkeyId = nextId;
            }
            tray.Text = "FocusMark · " + Note.Chord(mods, letter);
            return null;
        }

        string LoadTheme(string id)
        {
            try
            {
                var next = ThemeProfile.Read(id);
                activeTheme = id;
                theme = next;
                theme.Apply(panel.Resources);
                if (view == View.Pill) UpdatePreview();
                return null;
            }
            catch (Exception e)
            {
                if (!(e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)) throw;
                theme.Apply(panel.Resources);
                return "无法应用主题。\n" + e.Message;
            }
        }

        void OpenThemeFolder()
        {
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
        }

        IntPtr Messages(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x0312 && hotkeyId != 0 && w.ToInt32() == hotkeyId) { if (!recordingKey) Edit(WindowTarget.GetForegroundWindow()); handled = true; }
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
            bool edge = store.Current.Rest == "edge";
            var pill = Find<Button>("Pill");
            bool upright = edge && store.Current.Dock >= 2;
            pill.Height = upright ? 64 : edge ? 18 : 32;
            var grip = Find<Border>("Grip");
            grip.Width = upright ? 4 : 30;
            grip.Height = upright ? 30 : 4;
            Find<FrameworkElement>("Grip").Visibility = edge ? Visibility.Visible : Visibility.Collapsed;
            Find<FrameworkElement>("Summary").Visibility = edge ? Visibility.Collapsed : Visibility.Visible;
            Find<TextBlock>("Preview").Text = edge ? null : store.Current.HidePreview ? "查看备注" : note;
            if (view == View.Pill) panel.Width = upright ? 18 : edge ? 64 : store.Current.HidePreview ? 100 : theme.Width;
        }

        void ShowView(View next)
        {
            if (next == View.Pill && store.Current.Rest == "hidden") { HidePanel(); return; }
            if (next != View.Editor) { EndEdit(); DiscardCandidate(); }
            // Hiding releases editor focus when collapsing. A visible passive capsule
            // can become an editor in place without hiding and showing the HWND again.
            if (next == View.Pill && panel.IsActive) panel.Hide();
            view = next;
            var shell = Find<Border>("Shell");
            shell.CornerRadius = new CornerRadius(next == View.Pill ? 16 : 10);
            shell.SetResourceReference(Border.BackgroundProperty, next == View.Pill ? "PillSurface" : "Panel");
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
            int gap = view == View.Pill && store.Current.Rest == "edge" ? 0 : 8;
            var point = DockPoint(store.Current.Dock, store.Current.Offset, work.Width - (rect.Right-rect.Left), work.Height - (rect.Bottom-rect.Top), gap);
            int x = work.Left + (int)Math.Round(point.X);
            int y = work.Top + (int)Math.Round(point.Y);
            if (rect.Left == x && rect.Top == y) return;
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0015); // NOSIZE | NOZORDER | NOACTIVATE
        }

        protected override void OnExit(ExitEventArgs e)
        {
            exiting = true;
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            if (hotkeyId != 0) UnregisterHotKey(handle, hotkeyId);
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

    }
}
