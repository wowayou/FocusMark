using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace FocusMark
{
    [DataContract]
    internal sealed class Note
    {
        internal const int Limit = 200;
        [DataMember(Name="text", IsRequired=true)] public string Text { get; private set; }
        [DataMember(Name="bottom", EmitDefaultValue=false)] public bool Bottom { get; private set; }
        [DataMember(Name="hidePreview", EmitDefaultValue=false)] public bool HidePreview { get; private set; }

        [DataMember(Name="theme", EmitDefaultValue=false)] public string Theme { get; private set; }
        [DataMember(Name="key", EmitDefaultValue=false)] public string Key { get; private set; }
        [DataMember(Name="rest", EmitDefaultValue=false)] public string Rest { get; private set; }
        [DataMember(Name="mods", EmitDefaultValue=false)] public int Mods { get; private set; }
        [DataMember(Name="side", EmitDefaultValue=false)] public string Side { get; private set; }
        [DataMember(Name="offset", EmitDefaultValue=false)] public int Offset { get; private set; }
        [DataMember(Name="still", EmitDefaultValue=false)] public bool Still { get; private set; }
        [DataMember(Name="scale", EmitDefaultValue=false)] internal int? ScaleSetting;
        [DataMember(Name="radius", EmitDefaultValue=false)] internal int? RadiusSetting;
        internal int Scale { get { return Math.Max(80, Math.Min(140, ScaleSetting ?? 100)); } }
        internal int Radius { get { return Math.Max(0, Math.Min(16, RadiusSetting ?? 4)); } }
        internal int Modifiers { get { return Mods >= 1 && Mods <= 15 ? Mods : 3; } }
        internal string Letter { get { return VirtualKey(Key) == 0 ? "J" : Key; } }
        internal int Dock { get { return Side == "left" ? 2 : Side == "right" ? 3 : Bottom ? 1 : 0; } }
        internal bool HasOptions { get { return Bottom || HidePreview || Theme != null || Key != null || Rest != null || Mods != 0 || Side != null || Offset != 0 || Still || ScaleSetting.HasValue || RadiusSetting.HasValue; } }

        internal static int VirtualKey(string key)
        {
            if (String.IsNullOrEmpty(key)) return 0;
            char c = key[0];
            if (key.Length == 1 && ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return c;
            int f;
            if (c == 'F' && int.TryParse(key.Substring(1), out f) && f >= 1 && f <= 11 && key == "F" + f) return 111 + f;
            return 0;
        }
        internal static string Chord(int mods, string key)
        {
            return ((mods & 2) != 0 ? "Ctrl+" : "") + ((mods & 1) != 0 ? "Alt+" : "") +
                ((mods & 4) != 0 ? "Shift+" : "") + ((mods & 8) != 0 ? "Win+" : "") + key;
        }
        internal Note WithText(string text)
        {
            var next = (Note)MemberwiseClone();
            next.Text = text;
            return next;
        }
        internal Note WithOptions(int dock, int offset, bool mask, string theme, string key, int mods, string rest, bool still, int scale, int radius)
        {
            if (scale < 80 || scale > 140 || radius < 0 || radius > 16 || dock < 0 || dock > 3 || offset < -50 || offset > 50 || VirtualKey(key) == 0 || mods < 1 || mods > 15 ||
                (rest != null && rest != "edge" && rest != "hidden")) throw new ArgumentException("设置值超出范围。");
            return new Note { Text = Text, Bottom = dock == 1, Side = dock == 2 ? "left" : dock == 3 ? "right" : null,
                Offset = offset, HidePreview = mask, Theme = theme, Key = key == "J" ? null : key,
                ScaleSetting = scale == 100 ? (int?)null : scale, RadiusSetting = radius == 4 ? (int?)null : radius,
                Mods = mods == 3 ? 0 : mods, Rest = rest, Still = still };
        }

        internal static string Clean(string value)
        {
            return (value ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        internal static int Length(string value)
        {
            int count = 0;
            for (int i = 0; i < value.Length; i++, count++)
                if (char.IsSurrogatePair(value, i)) i++;
            return count;
        }

        internal static bool Valid(string value)
        {
            return !String.IsNullOrWhiteSpace(value) && Length(value) <= Limit;
        }
    }

    internal sealed class NoteStore
    {
        readonly string directory, file;
        readonly DataContractJsonSerializer json = new DataContractJsonSerializer(typeof(Note));
        internal Note Current { get; private set; }

        internal NoteStore(string directory)
        {
            Current = new Note();
            this.directory = directory;
            file = Path.Combine(directory, "note.json");
        }

        internal string Load(out string warning)
        {
            warning = null;
            try
            {
                using (var stream = File.OpenRead(file))
                {
                    if (stream.Length > 64 * 1024) throw new SerializationException();
                    var note = (Note)json.ReadObject(stream);
                    if (note == null || (note.Text == null ? !note.HasOptions : !Note.Valid(note.Text)))
                        throw new SerializationException();
                    Current = note;
                    return Current.Text;
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (SerializationException)
            {
                string damaged = file + ".damaged-" + Guid.NewGuid().ToString("N");
                File.Move(file, damaged);
                warning = "无法读取备注，原文件已保留在：\n" + damaged;
            }
            Current = new Note();
            return null;
        }

        internal void Save(string text)
        {
            if (!Note.Valid(text)) throw new ArgumentException("备注应为 1–200 字。");
            Commit(Current.WithText(text));
        }

        internal void Clear() { Commit(Current.WithText(null)); }

        internal void Configure(int dock, int offset, bool mask, string theme, string key, int mods, string rest, bool still, int scale = 100, int radius = 4)
        {
            Commit(Current.WithOptions(dock, offset, mask, theme, key, mods, rest, still, scale, radius));
        }

        internal void Move(int dock, int offset)
        {
            var n = Current;
            if (dock != n.Dock || offset != n.Offset)
                Configure(dock, offset, n.HidePreview, n.Theme, n.Letter, n.Modifiers, n.Rest, n.Still, n.Scale, n.Radius);
        }

        void Commit(Note next)
        {
            if (next.Text == null && !next.HasOptions)
            {
                try
                {
                    File.Delete(file + ".tmp");
                    File.Delete(file);
                }
                catch (DirectoryNotFoundException) { }
                Current = next;
                return;
            }
            Directory.CreateDirectory(directory);
            string temp = file + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    json.WriteObject(stream, next);
                    stream.Flush(true);
                }
                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
            }
            finally { File.Delete(temp); }
            Current = next;
        }
    }
}
