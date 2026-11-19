using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace FocusMark
{
    [DataContract]
    internal sealed class ThemeProfile
    {
        [DataMember(Name="version", IsRequired=true)] public int Version;
        [DataMember(Name="base", IsRequired=true)] public string Base;
        [DataMember(Name="accent", IsRequired=true)] public string Accent;
        [DataMember(Name="capsuleWidth", IsRequired=true)] public int Width;

        internal static readonly string Folder = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "FocusMark", "themes");
        internal static readonly ThemeProfile Dark = new ThemeProfile { Version=1, Base="dark", Accent="#9ED4BC", Width=216 };
        internal static readonly ThemeProfile Light = new ThemeProfile { Version=1, Base="light", Accent="#285D4C", Width=216 };

        static bool SafeId(string id) { return id != null && Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{1,40}\z"); }
        internal static IEnumerable<string> Installed()
        {
            if (!Directory.Exists(Folder)) yield break;
            foreach (var path in Directory.EnumerateFiles(Folder, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (SafeId(id) && !String.Equals(id,"light",StringComparison.OrdinalIgnoreCase) && !String.Equals(id,"dark",StringComparison.OrdinalIgnoreCase)) yield return id;
            }
        }

        internal static ThemeProfile Read(string id)
        {
            if (id == null || id == "dark") return Dark;
            if (id == "light") return Light;
            if (!SafeId(id)) throw new InvalidDataException("主题文件名无效。");
            using (var stream = File.OpenRead(Path.Combine(Folder, id + ".json")))
            {
                if (stream.Length > 4096) throw new InvalidDataException("主题文件超过 4 KiB。");
                ThemeProfile p;
                try { p = (ThemeProfile)new DataContractJsonSerializer(typeof(ThemeProfile)).ReadObject(stream); }
                catch (SerializationException e) { throw new InvalidDataException("主题 JSON 格式有误。", e); }
                if (p == null || p.Version != 1 || (p.Base != "dark" && p.Base != "light") || p.Width < 144 || p.Width > 320 || !Regex.IsMatch(p.Accent ?? "", @"\A#[0-9A-Fa-f]{6}\z"))
                    throw new InvalidDataException("请检查主题版本、底色、强调色和胶囊宽度。");
                return p;
            }
        }

        internal void Apply(ResourceDictionary resources)
        {
            bool light = Base == "light";
            Brush(resources,"Panel",light ? "#F8F7F2" : "#22252B");
            Brush(resources,"Text",light ? "#212B27" : "#F1F2F4");
            Brush(resources,"Secondary",light ? "#4A5852" : "#BEC5CF");
            Brush(resources,"Control",light ? "#E2E8E1" : "#343A43");
            Brush(resources,"PillSurface",light ? "#EEF1EA" : "#292E36");
            Brush(resources,"Field",light ? "#FFFFFF" : "#191C21");
            Brush(resources,"Stroke",light ? "#687B70" : "#738C83");
            Brush(resources,"Primary",light ? "#C5DFD1" : "#405D51");
            Brush(resources,"Pressed",light ? "#B2C9BC" : "#486256");
            Brush(resources,"Warning",light ? "#7D461D" : "#E6C59E");
            Brush(resources,"Mint",Accent);
        }
        static void Brush(ResourceDictionary resources, string key, string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            resources[key] = brush;
        }
    }
}
