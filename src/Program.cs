using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace RODamageMeter
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            string replay = null;
            int replayDelay = 120;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--replay" && i + 1 < args.Length) replay = args[++i];
                else if (args[i] == "--delay" && i + 1 < args.Length) int.TryParse(args[++i], out replayDelay);
            }

            // raw socket exige administrador
            if (replay == null && !IsAdmin())
            {
                try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { Verb = "runas", UseShellExecute = true }); }
                catch (Win32Exception) { MessageBox.Show("O RO Damage Meter precisa rodar como administrador para capturar os pacotes.", "RO Damage Meter"); }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Protocol.LoadExtra(Path.Combine(baseDir, "pacotes.txt"));
            SkillDb.Load(baseDir);
            var settings = Settings.Load(Path.Combine(baseDir, "settings.ini"));
            var tracker = new DamageTracker();
            tracker.AutoResetOnMapChange = settings.AutoReset;
            var form = new OverlayForm(tracker, settings);

            if (replay != null) Replay(replay, replayDelay, tracker, form);
            else
            {
                var capture = new CaptureEngine(tracker, form.SetStatus, baseDir);
                capture.Start();
                form.FormClosed += (s, e) => capture.Stop();
            }
            Application.Run(form);
        }

        static bool IsAdmin()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // Reproduz um fluxo servidor->cliente salvo em pedaços aleatórios, começando no meio de um pacote,
        // para testar a interface e o resync sem o jogo.
        static void Replay(string path, int delayMs, DamageTracker tracker, OverlayForm form)
        {
            var t = new Thread(() =>
            {
                var data = File.ReadAllBytes(path);
                var ps = new PacketStream();
                ps.OnPacket = tracker.HandlePacket;
                var rnd = new Random(1);
                form.SetStatus("Replay: " + Path.GetFileName(path), 0);
                int o = Math.Min(37, data.Length);
                while (o < data.Length)
                {
                    int n = Math.Min(rnd.Next(1, 1400), data.Length - o);
                    ps.Feed(data, o, n);
                    o += n;
                    Thread.Sleep(delayMs);
                }
                form.SetStatus("Replay concluído", 0);
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
