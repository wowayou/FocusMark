using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;

namespace FocusMark
{
    internal sealed partial class App
    {
        Window settings;
        bool recordingKey;
        void OpenSettings()
        {
            if (settings != null) { settings.Activate(); return; }
            Window w;
            using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("Settings.xaml"))
                w = (Window)XamlReader.Load(src);
            settings = w;
            w.Resources[typeof(Button)] = panel.Resources[typeof(Button)];
            w.Resources["Bookmark"] = panel.Resources["Bookmark"];
            var previewTheme = theme;
            previewTheme.Apply(w.Resources);
            w.Closed += delegate { recordingKey = false; settings = null; };
            var regular = (RadioButton)w.FindName("ShowPill");
            var edge = (RadioButton)w.FindName("ShowEdge");
            var hidden = (RadioButton)w.FindName("ShowNone");
            var position = (ComboBox)w.FindName("Position");
            var mask = (CheckBox)w.FindName("Mask");
            var letter = (Button)w.FindName("Letter");
            var along = (Slider)w.FindName("Along");
            var animate = (CheckBox)w.FindName("Animate");
            string key = store.Current.Letter;
            int mods = store.Current.Modifiers;
            var palette = (ComboBox)w.FindName("Palette");
            var error = (TextBlock)w.FindName("Error");
            Action<string> report = message => { error.Text = message; error.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible; };
            position.SelectedIndex = store.Current.Dock;
            along.Value = 50 + store.Current.Offset;
            animate.IsChecked = !store.Current.Still;
            mask.IsChecked = store.Current.HidePreview;
            (store.Current.Rest == "hidden" ? hidden : store.Current.Rest == "edge" ? edge : regular).IsChecked = true;
            letter.Content = Note.Chord(mods, key);
            Action<string,string> add = (name,id) => {
                var item = new ComboBoxItem { Content = name, Tag = id };
                palette.Items.Add(item);
                if (activeTheme == id) palette.SelectedItem = item;
            };
            add("深色 · 薄荷", null);
            add("浅色 · 纸白", "light");
            try { foreach (var id in ThemeProfile.Installed()) add(id,id); }
            catch (IOException e) { report(e.Message); }
            catch (UnauthorizedAccessException e) { report(e.Message); }
            if (palette.SelectedIndex < 0) palette.SelectedIndex = 0;
            Func<string> idSelected = () => (string)((ComboBoxItem)palette.SelectedItem).Tag;
            w.MaxHeight = SystemParameters.WorkArea.Height;
            w.MaxWidth = SystemParameters.WorkArea.Width;
            bool expanded = false;
            var trySample = (Button)w.FindName("TrySample");
            Action preview = delegate {
                var sample = (Border)w.FindName("Sample");
                bool tab = edge.IsChecked == true, off = hidden.IsChecked == true;
                int dock = position.SelectedIndex;
                bool upright = tab && dock >= 2 && !expanded;
                trySample.Visibility = off ? Visibility.Collapsed : Visibility.Visible;
                ((StackPanel)w.FindName("HiddenHint")).Visibility = off ? Visibility.Visible : Visibility.Collapsed;
                ((TextBlock)w.FindName("RecallHint")).Text = "按 " + Note.Chord(mods, key) + " 找回";
                sample.Height = expanded ? 64 : upright ? 64 : tab ? 18 : 32;
                sample.Width = expanded ? 252 : upright ? 18 : tab ? 64 : mask.IsChecked == true ? 100 : previewTheme.Width;
                sample.Padding = tab && !expanded ? new Thickness(0) : new Thickness(12,0,12,0);
                sample.CornerRadius = new CornerRadius(expanded ? 10 : 16);
                var grip = (Border)w.FindName("SampleGrip");
                grip.Width = upright ? 4 : 30;
                grip.Height = upright ? 30 : 4;
                var stage = (Canvas)w.FindName("SampleArea");
                var point = DockPoint(dock, (int)along.Value - 50, stage.ActualWidth - sample.Width * 0.8 - 2,
                    stage.ActualHeight - sample.Height * 0.8 - 2, tab && !expanded ? 0 : 5);
                Canvas.SetLeft(trySample, point.X);
                Canvas.SetTop(trySample, point.Y);
                ((TextBlock)w.FindName("Percent")).Text = ((int)along.Value) + "%";
                ((StackPanel)w.FindName("SampleNote")).Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                ((Border)w.FindName("SampleGrip")).Visibility = tab && !expanded ? Visibility.Visible : Visibility.Collapsed;
                ((DockPanel)w.FindName("SampleSummary")).Visibility = tab || expanded ? Visibility.Collapsed : Visibility.Visible;
                ((TextBlock)w.FindName("SampleLabel")).Text = mask.IsChecked == true ? "查看备注" : "继续读下一段";
                mask.IsEnabled = !off && !tab;
                ((TextBlock)w.FindName("Help")).Text = off ? "保存后从桌面收起，也可从托盘打开。" : expanded ? "再次点击示例即可收起。" : tab ? "点击预览中的短条，试试看如何找回备注。" : "点击预览中的胶囊，试试看展开后的样子。";
            };
            trySample.Click += delegate { expanded = !expanded; preview(); };
            RoutedEventHandler changeMode = delegate { expanded = false; preview(); };
            regular.Checked += changeMode; edge.Checked += changeMode; hidden.Checked += changeMode;
            position.SelectionChanged += delegate { preview(); };
            along.ValueChanged += delegate { preview(); };
            ((Canvas)w.FindName("SampleArea")).SizeChanged += delegate { preview(); };
            Action stopRecording = delegate { recordingKey = false; letter.Content = Note.Chord(mods, key); };
            letter.Click += delegate { recordingKey = true; letter.Content = "请按组合键…"; letter.Focus(); };
            letter.LostKeyboardFocus += delegate { stopRecording(); };
            w.PreviewKeyDown += (sender,e) => {
                var pressed = e.Key == Key.System ? e.SystemKey : e.Key;
                if (!recordingKey)
                {
                    if (pressed == Key.Escape && !palette.IsDropDownOpen && !position.IsDropDownOpen) w.Close();
                    return;
                }
                if (pressed == Key.Tab) { stopRecording(); return; }
                e.Handled = true;
                if (pressed == Key.Escape) { stopRecording(); report(null); return; }
                int vk = KeyInterop.VirtualKeyFromKey(pressed);
                if ((vk >= 160 && vk <= 165) || vk == 91 || vk == 92) return;
                string name = vk >= 112 && vk <= 122 ? "F" + (vk-111) : ((char)vk).ToString();
                int modifiers = (int)Keyboard.Modifiers;
                if (modifiers == 0 || Note.VirtualKey(name) == 0)
                { report("请按修饰键加字母、数字或 F1–F11。Esc 取消录入。"); return; }
                mods = modifiers; key = name; stopRecording(); report(null); preview();
            };
            mask.Click += delegate { preview(); };
            palette.SelectionChanged += delegate {
                try
                {
                    previewTheme = ThemeProfile.Read(idSelected());
                    previewTheme.Apply(w.Resources);
                    preview();
                    report(null);
                }
                catch (Exception e) { if (!IsThemeError(e)) throw; report(e.Message); }
            };
            ((Button)w.FindName("Cancel")).Click += delegate { w.Close(); };
            ((Button)w.FindName("Folder")).Click += delegate { OpenThemeFolder(); };
            ((Button)w.FindName("Apply")).Click += delegate {
                string id = idSelected();
                ThemeProfile next;
                try { next = ThemeProfile.Read(id); }
                catch (Exception e) { if (!IsThemeError(e)) throw; report(e.Message); return; }
                string rest = edge.IsChecked == true ? "edge" : hidden.IsChecked == true ? "hidden" : null;
                string problem = CommitWithKey(key, mods, delegate { store.Configure(position.SelectedIndex, (int)along.Value - 50, mask.IsChecked == true, id, key, mods, rest, animate.IsChecked != true); });
                if (problem != null) { report(problem); return; }
                activeTheme = id; theme = next; theme.Apply(panel.Resources);
                if (view == View.Pill || (view == View.Hidden && note != null)) ShowView(View.Pill);
                else Place();
                w.Close();
            };
            preview();
            w.Show();
            w.Activate();
        }

        static bool IsThemeError(Exception e) { return e is IOException || e is InvalidDataException || e is UnauthorizedAccessException; }

        static Point DockPoint(int dock, int offset, double freeX, double freeY, double gap)
        {
            freeX = Math.Max(0, freeX);
            freeY = Math.Max(0, freeY);
            double fraction = Math.Max(0, Math.Min(1, (offset + 50.0) / 100.0));
            if (dock == 2 || dock == 3)
                return new Point(dock == 2 ? Math.Min(gap,freeX) : Math.Max(0,freeX-gap), freeY*fraction);
            return new Point(freeX*fraction, dock == 1 ? Math.Max(0,freeY-gap) : Math.Min(gap,freeY));
        }
    }
}
