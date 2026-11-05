using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace RODamageMeter
{
    static class TcpTable
    {
        [DllImport("iphlpapi.dll")]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        const int AF_INET = 2, TCP_TABLE_OWNER_PID_ALL = 5;

        public struct Row
        {
            public uint LocalAddr, RemoteAddr;
            public int LocalPort, RemotePort, Pid;
        }

        public static List<Row> Get()
        {
            var list = new List<Row>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            size += 8192;
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(p, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return list;
                int count = Marshal.ReadInt32(p);
                for (int i = 0; i < count; i++)
                {
                    IntPtr r = p + 4 + i * 24;
                    var row = new Row();
                    row.LocalAddr = (uint)Marshal.ReadInt32(r, 4);
                    row.LocalPort = Port(Marshal.ReadInt32(r, 8));
                    row.RemoteAddr = (uint)Marshal.ReadInt32(r, 12);
                    row.RemotePort = Port(Marshal.ReadInt32(r, 16));
                    row.Pid = Marshal.ReadInt32(r, 20);
                    list.Add(row);
                }
            }
            finally { Marshal.FreeHGlobal(p); }
            return list;
        }

        static int Port(int v) { return ((v & 0xFF) << 8) | ((v >> 8) & 0xFF); }
    }

    // Captura passiva via raw socket (SIO_RCVALL). Só olha as conexões TCP do processo do jogo,
    // descobertas pela tabela de conexões do Windows — nada é lido ou escrito no processo.
    class Sniffer
    {
        public delegate void SegmentHandler(ulong conn, bool inbound, uint seq, byte flags, byte[] data, int off, int len);

        public SegmentHandler OnSegment;
        public Action<string, int> OnStatus;

        readonly string processName;
        volatile HashSet<ulong> conns = new HashSet<ulong>();
        volatile HashSet<uint> servers = new HashSet<uint>();
        volatile Socket sock;
        IPAddress boundIp;
        volatile bool running;

        public Sniffer(string processName) { this.processName = processName; }

        public static ulong Key(uint remoteIp, int remotePort, int localPort)
        {
            return ((ulong)remoteIp << 32) | ((ulong)(uint)remotePort << 16) | (uint)localPort;
        }

        public void Start()
        {
            running = true;
            var m = new Thread(MonitorLoop); m.IsBackground = true; m.Start();
            var r = new Thread(RecvLoop); r.IsBackground = true; r.Start();
        }

        public void Stop()
        {
            running = false;
            var s = sock; sock = null;
            if (s != null) s.Close();
        }

        void Status(string text, int level) { if (OnStatus != null) OnStatus(text, level); }

        void MonitorLoop()
        {
            while (running)
            {
                try
                {
                    var pids = new HashSet<int>();
                    foreach (var p in Process.GetProcessesByName(processName)) { pids.Add(p.Id); p.Dispose(); }

                    var set = new HashSet<ulong>();
                    var srv = new HashSet<uint>(servers);
                    uint local = 0;
                    foreach (var row in TcpTable.Get())
                    {
                        if (!pids.Contains(row.Pid) || row.RemoteAddr == 0 || IsWeb(row.RemotePort)) continue;
                        set.Add(Key(row.RemoteAddr, row.RemotePort, row.LocalPort));
                        srv.Add(row.RemoteAddr);
                        local = row.LocalAddr;
                    }
                    conns = set;
                    servers = srv;

                    if (pids.Count == 0) Status("Aguardando o " + processName + " abrir...", 1);
                    else if (set.Count == 0) Status("Jogo aberto, aguardando conexão com o servidor...", 1);
                    else if (local != 0 && (boundIp == null || !boundIp.Equals(new IPAddress(local)))) Bind(new IPAddress(local));
                }
                catch (SocketException ex) { Status("Erro ao abrir captura: " + ex.Message, 2); boundIp = null; }
                catch (Exception ex) { Status("Erro: " + ex.Message, 2); }
                Thread.Sleep(1000);
            }
        }

        static bool IsWeb(int port) { return port == 80 || port == 443; }

        void Bind(IPAddress ip)
        {
            var old = sock; sock = null;
            if (old != null) old.Close();

            var s = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
            s.Bind(new IPEndPoint(ip, 0));
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);
            s.ReceiveBufferSize = 8 << 20;
            s.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(1), new byte[4]);
            sock = s;
            boundIp = ip;
            Status("Capturando (" + ip + ")", 0);
        }

        void RecvLoop()
        {
            var b = new byte[65536];
            while (running)
            {
                var s = sock;
                if (s == null) { Thread.Sleep(200); continue; }
                int n;
                try { n = s.Receive(b); }
                catch (SocketException) { Thread.Sleep(100); continue; }
                catch (ObjectDisposedException) { continue; }
                Parse(b, n);
            }
        }

        void Parse(byte[] b, int n)
        {
            if (n < 40 || (b[0] >> 4) != 4 || b[9] != 6) return;
            int ihl = (b[0] & 15) * 4;
            int tot = (b[2] << 8) | b[3];
            if (tot == 0 || tot > n) tot = n;
            uint src = BitConverter.ToUInt32(b, 12), dst = BitConverter.ToUInt32(b, 16);
            int t = ihl;
            if (t + 20 > tot) return;
            int sp = (b[t] << 8) | b[t + 1], dp = (b[t + 2] << 8) | b[t + 3];
            uint seq = (uint)((b[t + 4] << 24) | (b[t + 5] << 16) | (b[t + 6] << 8) | b[t + 7]);
            int off = t + (b[t + 12] >> 4) * 4;
            byte flags = b[t + 13];
            int len = tot - off;
            if (len < 0) return;
            if (len == 0 && (flags & 0x07) == 0) return; // ACK puro

            ulong kin = Key(src, sp, dp), kout = Key(dst, dp, sp);
            var c = conns;
            // conexões novas (troca de map server) ainda não aparecem na tabela: aceita pelo IP do servidor
            if (c.Contains(kin) || (servers.Contains(src) && !IsWeb(sp))) { if (OnSegment != null) OnSegment(kin, true, seq, flags, b, off, len); }
            else if (c.Contains(kout)) { if (OnSegment != null) OnSegment(kout, false, seq, flags, b, off, len); }
        }
    }

    // Remonta o fluxo de uma conexão a partir dos segmentos (fora de ordem, retransmissões, lacunas).
    class TcpReassembler
    {
        public readonly PacketStream Stream;
        readonly SortedDictionary<uint, byte[]> pending = new SortedDictionary<uint, byte[]>();
        uint next;
        bool init;

        public TcpReassembler(PacketStream stream) { Stream = stream; }

        public void Add(uint seq, byte flags, byte[] d, int off, int len)
        {
            if ((flags & 0x02) != 0) // SYN: conexão nova, fluxo começa alinhado
            {
                init = true; next = seq + 1; pending.Clear(); Stream.Reset();
                return;
            }
            if (len == 0) return;
            if (!init) { init = true; next = seq; }

            if ((int)(seq - next) > 0)
            {
                if (!pending.ContainsKey(seq))
                {
                    var copy = new byte[len];
                    Buffer.BlockCopy(d, off, copy, 0, len);
                    pending[seq] = copy;
                }
                if (pending.Count > 64) SkipGap();
                return;
            }
            Deliver(seq, d, off, len);
            Drain();
        }

        void Deliver(uint seq, byte[] d, int off, int len)
        {
            int skip = (int)(next - seq);
            if (skip >= len) return;
            Stream.Feed(d, off + skip, len - skip);
            next = seq + (uint)len;
        }

        void Drain()
        {
            while (pending.Count > 0)
            {
                uint key = 0; byte[] data = null;
                foreach (var kv in pending) { key = kv.Key; data = kv.Value; break; }
                if ((int)(key - next) > 0) break;
                pending.Remove(key);
                Deliver(key, data, 0, data.Length);
            }
        }

        // segmento perdido de vez: pula a lacuna e deixa o PacketStream ressincronizar
        void SkipGap()
        {
            foreach (var kv in pending) { next = kv.Key; break; }
            Stream.Reset();
            Drain();
        }
    }

    class CaptureEngine
    {
        readonly Sniffer sniffer;
        readonly DamageTracker tracker;
        readonly UnknownLog unknown;
        readonly Dictionary<ulong, TcpReassembler> streams = new Dictionary<ulong, TcpReassembler>();

        public CaptureEngine(DamageTracker tracker, Action<string, int> status, string baseDir)
        {
            this.tracker = tracker;
            unknown = new UnknownLog(Path.Combine(baseDir, "pacotes_desconhecidos.log"));
            sniffer = new Sniffer("Ragexe");
            sniffer.OnStatus = status;
            sniffer.OnSegment = OnSegment;
        }

        public void Start() { sniffer.Start(); }
        public void Stop() { sniffer.Stop(); }

        void OnSegment(ulong conn, bool inbound, uint seq, byte flags, byte[] d, int off, int len)
        {
            if (!inbound)
            {
                if (len > 0) tracker.OnOutbound(d, off, len);
                return;
            }
            TcpReassembler r;
            if (!streams.TryGetValue(conn, out r))
            {
                var ps = new PacketStream();
                ps.OnPacket = tracker.HandlePacket;
                ps.OnUnknown = unknown.Add;
                r = new TcpReassembler(ps);
                streams[conn] = r;
            }
            r.Add(seq, flags, d, off, len);
            if ((flags & 0x05) != 0) streams.Remove(conn); // FIN/RST
        }
    }
}
