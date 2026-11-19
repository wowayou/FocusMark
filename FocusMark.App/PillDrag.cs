using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace FocusMark
{
    internal sealed partial class App
    {
        bool dragging;
        DispatcherTimer dragTimer;
        System.Drawing.Point pressPoint;
        RectPx pressRect;

        void EnablePillDrag(Button pill)
        {
            // An inactive HWND cannot capture pointer moves outside its bounds.
            // Sample only for the duration of a left press to preserve foreground focus.
            dragTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
            dragTimer.Tick += delegate { TrackPress(pill); };
            pill.PreviewMouseLeftButtonDown += (s,e) => {
                if (!GetWindowRect(handle, out pressRect)) return;
                e.Handled = true;
                pressPoint = Forms.Cursor.Position;
                dragTimer.Start();
            };
            pill.PreviewMouseLeftButtonUp += (s,e) => {
                if (!dragTimer.IsEnabled) return;
                e.Handled = true;
                TrackPress(pill);
            };
        }

        void TrackPress(Button pill)
        {
            if (view != View.Pill || !panel.IsVisible) { StopPress(); return; }
            var cursor = Forms.Cursor.Position;
            var scale = PresentationSource.FromVisual(panel).CompositionTarget.TransformToDevice;
            int dx = cursor.X - pressPoint.X, dy = cursor.Y - pressPoint.Y;
            if (!dragging && (Math.Abs(dx) >= SystemParameters.MinimumHorizontalDragDistance * scale.M11 ||
                              Math.Abs(dy) >= SystemParameters.MinimumVerticalDragDistance * scale.M22))
            {
                dragging = true;
                pill.Cursor = Cursors.SizeAll;
            }
            if ((Forms.Control.MouseButtons & Forms.MouseButtons.Left) != 0)
            {
                if (dragging) SetWindowPos(handle, IntPtr.Zero, pressRect.Left + dx, pressRect.Top + dy, 0, 0, 0x0015);
                return;
            }
            bool moved = dragging;
            StopPress();
            if (!moved)
            {
                if (pill.IsMouseOver) pill.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return;
            }
            var destination = Forms.Screen.FromPoint(cursor);
            var work = destination.WorkingArea;
            int[] distances = { Math.Abs(cursor.Y - work.Top), Math.Abs(work.Bottom - cursor.Y), Math.Abs(cursor.X - work.Left), Math.Abs(work.Right - cursor.X) };
            int dock = store.Current.Dock;
            for (int i = 0; i < distances.Length; i++) if (distances[i] < distances[dock]) dock = i;
            bool horizontal = dock < 2;
            double dip = store.Current.Rest == "edge" ? 64 : horizontal ? (store.Current.HidePreview ? 100 : theme.Width) : 32;
            double length = dip * store.Current.Scale / 100.0 * (horizontal ? scale.M11 : scale.M22);
            double space = (horizontal ? work.Width : work.Height) - length;
            double along = (horizontal ? cursor.X - work.Left : cursor.Y - work.Top) - length / 2;
            int offset = space <= 0 ? 0 : (int)Math.Round(100 * Math.Max(0, Math.Min(1, along / space))) - 50;
            if (TryCommit(delegate { store.Move(dock, offset); }))
            {
                screen = destination;
                if (settings != null)
                {
                    ((ComboBox)settings.FindName("Position")).SelectedIndex = dock;
                    ((Slider)settings.FindName("Along")).Value = offset + 50;
                }
            }
            UpdatePreview();
            panel.UpdateLayout();
            Place();
        }

        void StopPress()
        {
            if (dragTimer == null || !dragTimer.IsEnabled) return;
            dragTimer.Stop();
            dragging = false;
            Find<Button>("Pill").Cursor = Cursors.Hand;
        }
    }
}
