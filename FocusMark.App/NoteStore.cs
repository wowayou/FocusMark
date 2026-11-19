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

        internal Note(string text = null, bool bottom = false, bool hidePreview = false)
        {
            Text = text;
            Bottom = bottom;
            HidePreview = hidePreview;
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
                    if (note == null || (note.Text == null ? !note.Bottom && !note.HidePreview : !Note.Valid(note.Text)))
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
            Commit(new Note(text, Current.Bottom, Current.HidePreview));
        }

        internal void Clear() { Commit(new Note(null, Current.Bottom, Current.HidePreview)); }

        internal void SetOptions(bool bottom, bool hidePreview)
        {
            Commit(new Note(Current.Text, bottom, hidePreview));
        }

        void Commit(Note next)
        {
            if (next.Text == null && !next.Bottom && !next.HidePreview)
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
