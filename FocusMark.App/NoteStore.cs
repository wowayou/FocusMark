using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace FocusMark
{
    [DataContract]
    internal sealed class Note
    {
        [DataMember(Name="text", IsRequired=true)] public string Text;

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
            return !String.IsNullOrWhiteSpace(value) && Length(value) <= 200;
        }
    }

    internal sealed class NoteStore
    {
        readonly string directory, file;
        readonly DataContractJsonSerializer json = new DataContractJsonSerializer(typeof(Note));
        internal NoteStore(string directory)
        {
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
                    if (note == null || !Note.Valid(note.Text)) throw new SerializationException();
                    return note.Text;
                }
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (SerializationException)
            {
                string damaged = file + ".damaged-" + Guid.NewGuid().ToString("N");
                File.Move(file, damaged);
                warning = "无法读取备注，原文件已保留在：\n" + damaged;
                return null;
            }
        }

        internal void Save(string text)
        {
            if (!Note.Valid(text)) throw new ArgumentException("备注应为 1–200 字。");
            Directory.CreateDirectory(directory);
            string temp = file + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    json.WriteObject(stream, new Note { Text = text });
                    stream.Flush(true);
                }
                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
            }
            finally { File.Delete(temp); }
        }

        internal void Clear()
        {
            File.Delete(file + ".tmp");
            File.Delete(file);
        }
    }
}
