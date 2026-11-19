using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;

namespace FocusMark
{
    internal sealed partial class App
    {
        Window settings;
        void OpenSettings()
        {
            if (settings != null) { settings.Activate(); return; }
            Window w;
            using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("Settings.xaml"))
                w = (Window)XamlReader.Load(src);
            settings = w;
            w.Resources[typeof(Button)] = panel.Resources[typeof(Button)];
            theme.Apply(w.Resources);
            w.Closed += delegate { settings = null; };
            w.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape && !((ComboBox)w.FindName("Palette")).IsDropDownOpen && !((ComboBox)w.FindName("Letter")).IsDropDownOpen && !((ComboBox)w.FindName("Position")).IsDropDownOpen) w.Close(); };
            var regular = (RadioButton)w.FindName("ShowPill");
            var edge = (RadioButton)w.FindName("ShowEdge");
            var hidden = (RadioButton)w.FindName("ShowNone");
            var position = (ComboBox)w.FindName("Position");
            var mask = (CheckBox)w.FindName("Mask");
            var letter = (ComboBox)w.FindName("Letter");
            var palette = (ComboBox)w.FindName("Palette");
            var error = (TextBlock)w.FindName("Error");
            Action<string> report = message => { error.Text = message; error.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible; };
            position.SelectedIndex = store.Current.Bottom ? 1 : 0;
            mask.IsChecked = store.Current.HidePreview;
            (store.Current.Rest == "hidden" ? hidden : store.Current.Rest == "edge" ? edge : regular).IsChecked = true;
            for (char c = 'A'; c <= 'Z'; c++) letter.Items.Add("Ctrl+Alt+" + c);
            letter.SelectedIndex = store.Current.Letter[0] - 'A';
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
            Action preview = delegate {
                var sample = (Border)w.FindName("Sample");
                bool tab = edge.IsChecked == true, off = hidden.IsChecked == true;
                sample.VerticalAlignment = position.SelectedIndex == 1 ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                sample.Margin = new Thickness(0,tab ? 0 : 5,0,tab ? 0 : 5);
                sample.Visibility = off ? Visibility.Collapsed : Visibility.Visible;
                sample.Height = tab ? 14 : 26;
                sample.Padding = tab ? new Thickness(0) : new Thickness(12,3,12,3);
                sample.Width = tab ? 52 : double.NaN;
                var label = (TextBlock)w.FindName("SampleLabel");
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.Text = tab ? "━" : mask.IsChecked == true ? "查看备注" : "继续读下一段";
                mask.IsEnabled = !off && !tab;
                ((TextBlock)w.FindName("Help")).Text = off ? "按 " + letter.SelectedItem + " 或从托盘打开，即可找回备注。" : tab ? "点击屏幕边缘的短条，即可展开全文。" : "点击胶囊查看全文，中键点击可暂时隐藏。";
            };
            regular.Checked += delegate { preview(); }; edge.Checked += delegate { preview(); }; hidden.Checked += delegate { preview(); };
            position.SelectionChanged += delegate { preview(); }; letter.SelectionChanged += delegate { preview(); };
            mask.Click += delegate { preview(); };
            palette.SelectionChanged += delegate {
                try { ThemeProfile.Read(idSelected()).Apply(w.Resources); report(null); }
                catch (Exception e) { if (!IsThemeError(e)) throw; report(e.Message); }
            };
            ((Button)w.FindName("Cancel")).Click += delegate { w.Close(); };
            ((Button)w.FindName("Folder")).Click += delegate { OpenThemeFolder(); };
            ((Button)w.FindName("Apply")).Click += delegate {
                string id = idSelected();
                ThemeProfile next;
                try { next = ThemeProfile.Read(id); }
                catch (Exception e) { if (!IsThemeError(e)) throw; report(e.Message); return; }
                string key = ((char)('A' + letter.SelectedIndex)).ToString();
                string rest = edge.IsChecked == true ? "edge" : hidden.IsChecked == true ? "hidden" : null;
                string problem = CommitWithKey(key, delegate { store.Configure(position.SelectedIndex == 1, mask.IsChecked == true, id, key, rest); });
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

        int regionWidth, regionHeight;
        void RoundWindow(RectPx rect)
        {
            if (view != View.Pill)
            {
                if (regionWidth != 0 && SetWindowRgn(handle, IntPtr.Zero, true) != 0) regionWidth = 0;
                return;
            }
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0 || (width == regionWidth && height == regionHeight)) return;
            IntPtr region = CreateRoundRectRgn(0,0,width+1,height+1,height,height);
            if (region == IntPtr.Zero) return;
            if (SetWindowRgn(handle, region, true) == 0) { DeleteObject(region); return; }
            regionWidth = width; regionHeight = height;
        }
        [DllImport("gdi32")] static extern IntPtr CreateRoundRectRgn(int left,int top,int right,int bottom,int ellipseWidth,int ellipseHeight);
        [DllImport("gdi32")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32")] static extern int SetWindowRgn(IntPtr window,IntPtr region,bool redraw);
    }
}
