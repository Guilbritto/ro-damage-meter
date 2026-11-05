using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RODamageMeter
{
    static class Bin
    {
        public static int U16(byte[] b, int o) { return b[o] | (b[o + 1] << 8); }
        public static short I16(byte[] b, int o) { return (short)U16(b, o); }
        public static uint U32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }
        public static int I32(byte[] b, int o) { return (int)U32(b, o); }

        public static string Hex(byte[] b, int o, int n)
        {
            var sb = new StringBuilder();
            for (int i = o; i < Math.Min(b.Length, o + n); i++) sb.Append(b[i].ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }
    }

    // Pacotes servidor -> cliente do RO LATAM (layouts confirmados por captura em 2026-11-05).
    static class Protocol
    {
        public const int ZC_NOTIFY_ACT = 0x008A, ZC_NOTIFY_ACT2 = 0x02E1, ZC_NOTIFY_ACT3 = 0x08C8;
        public const int ZC_NOTIFY_SKILL = 0x0114, ZC_NOTIFY_SKILL2 = 0x01DE;
        public const int ZC_USESKILL_ACK2 = 0x07FB, ZC_USE_SKILL2 = 0x09CB;
        public const int ZC_AID = 0x0283;
        public const int ZC_NPCACK_MAPMOVE = 0x0091, ZC_NPCACK_SERVERMOVE = 0x0092, ZC_NPCACK_SERVERMOVE2 = 0x0AC7;
        public const int ZC_MOVEENTRY = 0x09FD, ZC_NEWENTRY = 0x09FE, ZC_STANDENTRY = 0x09FF;

        // id -> tamanho; -1 = variável (tamanho em u16 no offset 2)
        public static readonly Dictionary<int, int> Len = new Dictionary<int, int>();

        static Protocol()
        {
            Set(-1, 0x008D, 0x008E, 0x009A, 0x00B4, 0x00B7, 0x00FB, 0x0109, 0x010F, 0x017F, 0x01C3, 0x07FD, 0x09CA,
                0x09DB, 0x09DC, 0x09DD, 0x09FD, 0x09FE, 0x09FF, 0x0A3B, 0x0A44, 0x0B08, 0x0B09, 0x0B0A);
            Set(6, 0x007F, 0x00A1, 0x00AF, 0x00B5, 0x00B6, 0x0120, 0x013D, 0x0283);
            Set(7, 0x0080, 0x00C0, 0x0A36);
            Set(4, 0x013A);
            Set(8, 0x00B0, 0x00B1, 0x00C3, 0x01D0, 0x043D);
            Set(9, 0x0196, 0x009C);
            Set(10, 0x0088, 0x019B, 0x01F3, 0x01FF, 0x08D2);
            Set(11, 0x01D7);
            Set(12, 0x0087, 0x0ACB);
            Set(13, 0x0119, 0x02EB);
            Set(14, 0x0110, 0x0141, 0x0284, 0x0977, 0x0A18, 0x07F6);
            Set(15, 0x011A, 0x0229);
            Set(16, 0x0086);
            Set(17, 0x09CB);
            Set(18, 0x0117, 0x0ACC);
            Set(22, 0x0091);
            Set(24, 0x013E);
            Set(25, 0x07FB, 0x043F);
            Set(28, 0x0092, 0x0984);
            Set(29, 0x008A, 0x0983, 0x0B1A);
            Set(30, 0x0095);
            Set(31, 0x0114);
            Set(33, 0x01DE, 0x02E1);
            Set(34, 0x08C8);
            Set(35, 0x0115);
            Set(44, 0x00BD);
            Set(58, 0x0ADF);
            Set(102, 0x0195);
            Set(106, 0x0A30);
            Set(156, 0x0AC7);
        }

        static void Set(int len, params int[] ids) { foreach (int id in ids) Len[id] = len; }

        // pacotes.txt: uma linha "0xID TAMANHO" (TAMANHO -1 = variável) para ensinar pacotes novos sem recompilar
        public static void LoadExtra(string path)
        {
            if (!File.Exists(path)) return;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Split('#')[0].Trim();
                var parts = line.Split(new[] { ' ', '\t', '=' }, StringSplitOptions.RemoveEmptyEntries);
                int id, len;
                if (parts.Length < 2) continue;
                string hex = parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[0].Substring(2) : parts[0];
                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id) && int.TryParse(parts[1], out len))
                    Len[id] = len;
            }
        }
    }

    // Quebra um fluxo TCP em pacotes do RO, ressincronizando quando perde o alinhamento
    // (captura iniciada no meio da sessão, pacote desconhecido, segmento perdido).
    class PacketStream
    {
        public delegate void PacketHandler(int id, byte[] data, int offset, int length);
        public delegate void UnknownHandler(int id, byte[] data, int offset, int available);

        public PacketHandler OnPacket;
        public UnknownHandler OnUnknown;

        byte[] buf = new byte[65536];
        int start, end;
        bool synced;

        public void Reset() { start = end = 0; synced = false; }

        public void Feed(byte[] d, int off, int n)
        {
            Ensure(n);
            Buffer.BlockCopy(d, off, buf, end, n);
            end += n;
            Process();
        }

        void Ensure(int n)
        {
            if (end + n <= buf.Length) return;
            int used = end - start;
            if (used + n > buf.Length)
            {
                var nb = new byte[Math.Max(buf.Length * 2, used + n)];
                Buffer.BlockCopy(buf, start, nb, 0, used);
                buf = nb;
            }
            else Buffer.BlockCopy(buf, start, buf, 0, used);
            start = 0;
            end = used;
        }

        void Process()
        {
            while (true)
            {
                if (!synced)
                {
                    int k = start;
                    bool found = false;
                    for (; k + 2 <= end; k++)
                    {
                        int r = Chain(k, 3);
                        if (r == 1) { found = true; break; }
                        if (r == 0) break; // pode ser válido, espera mais dados
                    }
                    start = k;
                    if (!found)
                    {
                        if (end - start > (1 << 20)) start = end;
                        return;
                    }
                    synced = true;
                }

                if (end - start < 2) return;
                int id = Bin.U16(buf, start), len;
                if (!Protocol.Len.TryGetValue(id, out len))
                {
                    if (OnUnknown != null) OnUnknown(id, buf, start, end - start);
                    synced = false;
                    start++;
                    continue;
                }
                if (len < 0)
                {
                    if (end - start < 4) return;
                    len = Bin.U16(buf, start + 2);
                    if (len < 4 || len > 32768) { synced = false; start++; continue; }
                }
                if (end - start < len) return;
                if (OnPacket != null) OnPacket(id, buf, start, len);
                start += len;
            }
        }

        // 1 = n pacotes conhecidos encadeados, -1 = inválido, 0 = precisa de mais dados
        int Chain(int k, int n)
        {
            for (int i = 0; i < n; i++)
            {
                if (k + 2 > end) return 0;
                int id = Bin.U16(buf, k), len;
                if (!Protocol.Len.TryGetValue(id, out len)) return -1;
                if (len < 0)
                {
                    if (k + 4 > end) return 0;
                    len = Bin.U16(buf, k + 2);
                    if (len < 4 || len > 32768) return -1;
                }
                k += len;
                if (k > end) return 0;
            }
            return 1;
        }
    }

    // Registra cada ID desconhecido uma vez, para depois adicionarmos à tabela.
    class UnknownLog
    {
        readonly HashSet<int> seen = new HashSet<int>();
        readonly string path;

        public UnknownLog(string path) { this.path = path; }

        public void Add(int id, byte[] b, int o, int available)
        {
            lock (seen)
            {
                if (!seen.Add(id)) return;
                try
                {
                    File.AppendAllText(path, string.Format("{0:yyyy-MM-dd HH:mm:ss}  0x{1:X4}  {2}\r\n",
                        DateTime.Now, id, Bin.Hex(b, o, Math.Min(available, 64))));
                }
                catch (IOException) { }
            }
        }
    }
}
