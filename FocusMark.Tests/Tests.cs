using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using FocusMark;

internal static class Tests
{
    static void Check(bool pass, string name)
    {
        if (!pass) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
    static Process StartFixture()
    {
        var process = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--window") { UseShellExecute = false });
        for (int i = 0; i < 100; i++)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero) return process;
            Thread.Sleep(25);
        }
        process.Kill();
        throw new Exception("Fixture window timeout");
    }

    static void WindowIdentity()
    {
        WindowTarget retained;
        using (var first = StartFixture())
        {
            try
            {
                retained = WindowTarget.Capture(first.MainWindowHandle);
                Check(retained != null && retained.Matches(), "live target matches");
                using (var invalidated = WindowTarget.Capture(first.MainWindowHandle))
                {
                    invalidated.Dispose(); // Same HWND, PID, thread and start time, but missing window-instance marker.
                    Check(!invalidated.Matches() && invalidated.TryReturn().Result != null, "missing instance marker rejects identical numeric identity");
                }
            }
            finally { first.Kill(); first.WaitForExit(); }
        }
        using (retained)
        using (var second = StartFixture())
        {
            try { Check(!retained.Matches() && retained.TryReturn().Result != null, "same program restarted, old target rejected"); }
            finally { second.Kill(); second.WaitForExit(); }
        }
    }

    [STAThread] static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--window") { Application.Run(new Form { Text = "FocusMark identity fixture" }); return; }
        string root = Path.Combine(Path.GetTempPath(), "FocusMarkTests-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(root, "note.json"), warning;
        var store = new NoteStore(root);
        try
        {
            Check(store.Load(out warning) == null && warning == null, "first start");
            Check(!Note.Valid(" \t") && Note.Clean(" A\r\nB ") == "A B", "blank and multiline");
            string limit = String.Concat(Enumerable.Repeat("🙂", 200));
            Check(Note.Valid(limit) && !Note.Valid(limit + "字"), "200 Unicode scalar boundary");
            store.Save("先看第 2 行"); store.Save(limit);
            Check(store.Load(out warning) == limit && Directory.GetFiles(root).Length == 1, "atomic overwrite roundtrip");
            File.WriteAllText(file + ".tmp", "{");
            Check(store.Load(out warning) == limit, "interrupted temporary write ignored");
            using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { store.Save("new"); throw new Exception("expected sharing violation"); } catch (IOException) { }
            }
            Check(store.Load(out warning) == limit, "replace failure preserves previous record");
            store.Save("restored");
            Check(File.ReadAllText(file) == "{\"text\":\"restored\"}", "only text persisted; retry succeeded");
            File.WriteAllText(file, "{bad");
            Check(store.Load(out warning) == null && warning != null && File.ReadAllText(Directory.GetFiles(root, "*.damaged-*").Single()) == "{bad", "corrupt bytes quarantined");
            store.Save("final"); store.Clear();
            Check(store.Load(out warning) == null, "clear");
            Check(WindowTarget.Capture(IntPtr.Zero) == null, "missing foreground rejected");
            WindowIdentity();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
