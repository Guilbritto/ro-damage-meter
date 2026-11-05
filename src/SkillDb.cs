using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace RODamageMeter
{
    // Nomes vêm do skill_db do rAthena (data/skills.tsv); ícones são baixados do Divine Pride
    // na primeira vez e ficam em cache na pasta icons/.
    static class SkillDb
    {
        const string IconUrl = "https://static.divine-pride.net/images/skill/{0}.png";

        static readonly Dictionary<int, string> names = new Dictionary<int, string>();
        static readonly Dictionary<int, Image> icons = new Dictionary<int, Image>();
        static readonly HashSet<int> requested = new HashSet<int>();
        static string iconDir;

        public static void Load(string baseDir)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            iconDir = Path.Combine(baseDir, "icons");
            Directory.CreateDirectory(iconDir);

            var path = Path.Combine(baseDir, "skills.tsv");
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var parts = line.Split('\t');
                int id;
                if (parts.Length >= 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                    names[id] = parts[1];
            }
        }

        public static string Name(int id)
        {
            string n;
            return names.TryGetValue(id, out n) ? n : "Skill #" + id;
        }

        // null enquanto o ícone não existe/está baixando
        public static Image Icon(int id)
        {
            if (id == 0) return null;
            lock (icons)
            {
                Image img;
                if (icons.TryGetValue(id, out img)) return img;
                if (!requested.Add(id)) return null;
            }
            ThreadPool.QueueUserWorkItem(_ => FetchIcon(id));
            return null;
        }

        static void FetchIcon(int id)
        {
            var file = Path.Combine(iconDir, id + ".png");
            try
            {
                if (!File.Exists(file))
                    using (var wc = new WebClient()) wc.DownloadFile(string.Format(IconUrl, id), file);
                var img = new Bitmap(new MemoryStream(File.ReadAllBytes(file)));
                lock (icons) icons[id] = img;
            }
            catch (Exception)
            {
                try { if (File.Exists(file) && new FileInfo(file).Length == 0) File.Delete(file); } catch (IOException) { }
                lock (icons) icons[id] = null; // sem ícone: usa o placeholder
            }
        }
    }

    class Settings
    {
        public int X = 200, Y = 200, W = 300, H = 240;
        public double Opacity = 0.92;
        public bool AutoReset = true, Locked;

        string path;

        public static Settings Load(string path)
        {
            var s = new Settings { path = path };
            if (!File.Exists(path)) return s;
            foreach (var line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                int i; double d;
                bool isInt = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out i);
                switch (k)
                {
                    case "X": if (isInt) s.X = i; break;
                    case "Y": if (isInt) s.Y = i; break;
                    case "W": if (isInt) s.W = i; break;
                    case "H": if (isInt) s.H = i; break;
                    case "Opacity": if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) s.Opacity = d; break;
                    case "AutoReset": s.AutoReset = v == "1"; break;
                    case "Locked": s.Locked = v == "1"; break;
                }
            }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(path, string.Format(CultureInfo.InvariantCulture,
                    "X={0}\r\nY={1}\r\nW={2}\r\nH={3}\r\nOpacity={4}\r\nAutoReset={5}\r\nLocked={6}\r\n",
                    X, Y, W, H, Opacity, AutoReset ? 1 : 0, Locked ? 1 : 0));
            }
            catch (IOException) { }
        }
    }
}
