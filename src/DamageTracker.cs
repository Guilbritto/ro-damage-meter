using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RODamageMeter
{
    class SkillStat
    {
        public int Skill;
        public long Total, Max;
        public int Hits, Crits;

        public SkillStat Clone() { return (SkillStat)MemberwiseClone(); }
    }

    class Snapshot
    {
        public List<SkillStat> Skills = new List<SkillStat>();
        public long Total;
        public double Seconds;
        public string Map = "";
        public uint Me;
        public bool MeGuessed;
    }

    class DamageTracker
    {
        struct Hit
        {
            public uint Src;
            public int Skill;
            public long Dmg;
            public bool Crit;
            public DateTime Time;
        }

        const int GuessMinHits = 15, VotesNeeded = 2, RecentMax = 5000;
        static readonly TimeSpan OutboundWindow = TimeSpan.FromSeconds(2);

        readonly object sync = new object();
        readonly Dictionary<int, SkillStat> stats = new Dictionary<int, SkillStat>();
        readonly HashSet<uint> entities = new HashSet<uint>();
        readonly List<Hit> recent = new List<Hit>();
        readonly Dictionary<uint, int> votes = new Dictionary<uint, int>();
        readonly LinkedList<KeyValuePair<DateTime, byte[]>> outbound = new LinkedList<KeyValuePair<DateTime, byte[]>>();
        long total;
        DateTime first, last, resetAt;
        uint me;
        bool meGuessed;
        string map = "";

        public volatile bool AutoResetOnMapChange;

        public void Reset()
        {
            lock (sync) { ClearStats(); resetAt = DateTime.Now; }
        }

        public Snapshot Snapshot()
        {
            lock (sync)
            {
                var s = new Snapshot();
                s.Skills = stats.Values.Select(x => x.Clone()).OrderByDescending(x => x.Total).ToList();
                s.Total = total;
                s.Seconds = first == DateTime.MinValue ? 0 : Math.Max(1, (last - first).TotalSeconds);
                s.Map = map;
                s.Me = me;
                s.MeGuessed = meGuessed;
                return s;
            }
        }

        public void OnOutbound(byte[] d, int off, int len)
        {
            var copy = new byte[len];
            Buffer.BlockCopy(d, off, copy, 0, len);
            lock (sync)
            {
                var now = DateTime.Now;
                outbound.AddLast(new KeyValuePair<DateTime, byte[]>(now, copy));
                PruneOutbound(now);
            }
        }

        public void HandlePacket(int id, byte[] b, int o, int len)
        {
            lock (sync)
            {
                var now = DateTime.Now;
                switch (id)
                {
                    case Protocol.ZC_NOTIFY_SKILL2:
                        AddHit(Bin.U32(b, o + 4), Bin.U16(b, o + 2), Bin.I32(b, o + 24), false, now);
                        break;
                    case Protocol.ZC_NOTIFY_SKILL:
                        AddHit(Bin.U32(b, o + 4), Bin.U16(b, o + 2), Bin.I16(b, o + 24), false, now);
                        break;
                    case Protocol.ZC_NOTIFY_ACT3:
                        if (b[o + 26] == 0) // dano em SP não conta
                            AddHit(Bin.U32(b, o + 2), 0, (long)Bin.I32(b, o + 22) + Bin.I32(b, o + 30), IsCrit(b[o + 29]), now);
                        break;
                    case Protocol.ZC_NOTIFY_ACT2:
                        AddHit(Bin.U32(b, o + 2), 0, (long)Bin.I32(b, o + 22) + Bin.I32(b, o + 29), IsCrit(b[o + 28]), now);
                        break;
                    case Protocol.ZC_NOTIFY_ACT:
                        AddHit(Bin.U32(b, o + 2), 0, Bin.I16(b, o + 22) + Bin.I16(b, o + 27), IsCrit(b[o + 26]), now);
                        break;
                    case Protocol.ZC_USESKILL_ACK2:
                        Vote(Bin.U32(b, o + 2), Bin.U16(b, o + 14), Bin.U32(b, o + 6), now);
                        break;
                    case Protocol.ZC_USE_SKILL2:
                        Vote(Bin.U32(b, o + 12), Bin.U16(b, o + 2), Bin.U32(b, o + 8), now);
                        break;
                    case Protocol.ZC_AID:
                        SetMe(Bin.U32(b, o + 2), false);
                        break;
                    case Protocol.ZC_MOVEENTRY:
                    case Protocol.ZC_NEWENTRY:
                    case Protocol.ZC_STANDENTRY:
                        entities.Add(Bin.U32(b, o + 5));
                        break;
                    case Protocol.ZC_NPCACK_MAPMOVE:
                    case Protocol.ZC_NPCACK_SERVERMOVE:
                    case Protocol.ZC_NPCACK_SERVERMOVE2:
                        ChangeMap(MapName(b, o + 2));
                        break;
                }
            }
        }

        // 10 = crítico, 13 = crítico com multi-hit
        static bool IsCrit(byte type) { return type == 10 || type == 13; }

        static string MapName(byte[] b, int o)
        {
            var s = Encoding.ASCII.GetString(b, o, 16);
            int z = s.IndexOf('\0');
            if (z >= 0) s = s.Substring(0, z);
            if (s.EndsWith(".gat") || s.EndsWith(".rsw")) s = s.Substring(0, s.Length - 4);
            return s;
        }

        void ChangeMap(string name)
        {
            if (name.Length == 0 || name == map) return;
            if (AutoResetOnMapChange) { ClearStats(); resetAt = DateTime.Now; }
            map = name;
        }

        void AddHit(uint src, int skill, long dmg, bool crit, DateTime now)
        {
            if (dmg <= 0) return;
            var h = new Hit { Src = src, Skill = skill, Dmg = dmg, Crit = crit, Time = now };
            recent.Add(h);
            if (recent.Count > RecentMax) recent.RemoveRange(0, RecentMax / 5);

            if (me == 0) GuessMe();
            else if (src == me) Count(h);
        }

        void Count(Hit h)
        {
            SkillStat s;
            if (!stats.TryGetValue(h.Skill, out s)) { s = new SkillStat { Skill = h.Skill }; stats[h.Skill] = s; }
            s.Total += h.Dmg;
            s.Hits++;
            if (h.Crit) s.Crits++;
            if (h.Dmg > s.Max) s.Max = h.Dmg;
            total += h.Dmg;
            if (first == DateTime.MinValue) first = h.Time;
            last = h.Time;
        }

        void ClearStats()
        {
            stats.Clear();
            total = 0;
            first = last = DateTime.MinValue;
        }

        // Sem ZC_AID (programa aberto no meio da sessão) e sem votos ainda: o próprio personagem
        // nunca é anunciado por pacotes de entidade, então a fonte de dano "invisível" mais ativa é você.
        void GuessMe()
        {
            var counts = new Dictionary<uint, int>();
            foreach (var h in recent)
            {
                if (entities.Contains(h.Src)) continue;
                int c; counts.TryGetValue(h.Src, out c); counts[h.Src] = c + 1;
            }
            uint best = 0; int bestCount = 0;
            foreach (var kv in counts) if (kv.Value > bestCount) { best = kv.Key; bestCount = kv.Value; }
            if (bestCount >= GuessMinHits) SetMe(best, true);
        }

        // O servidor confirmou uma skill (src, skill, alvo) que o cliente acabou de pedir: src é você.
        void Vote(uint src, int skill, uint dst, DateTime now)
        {
            if (src == 0 || (src == me && !meGuessed)) return;
            if (!OutboundContains(skill, dst, now)) return;
            int v; votes.TryGetValue(src, out v);
            votes[src] = ++v;
            if (v >= VotesNeeded) SetMe(src, false);
        }

        void SetMe(uint id, bool guessed)
        {
            if (id == me) { if (!guessed) meGuessed = false; return; }
            me = id;
            meGuessed = guessed;
            votes.Clear();
            ClearStats();
            foreach (var h in recent) if (h.Src == me && h.Time >= resetAt) Count(h);
        }

        bool OutboundContains(int skill, uint dst, DateTime now)
        {
            PruneOutbound(now);
            byte s0 = (byte)skill, s1 = (byte)(skill >> 8);
            foreach (var kv in outbound)
            {
                var d = kv.Value;
                for (int i = 0; i + 1 < d.Length; i++)
                    if (d[i] == s0 && d[i + 1] == s1 && (dst == 0 || Contains(d, dst))) return true;
            }
            return false;
        }

        static bool Contains(byte[] d, uint v)
        {
            for (int i = 0; i + 3 < d.Length; i++)
                if (Bin.U32(d, i) == v) return true;
            return false;
        }

        void PruneOutbound(DateTime now)
        {
            while (outbound.Count > 0 && now - outbound.First.Value.Key > OutboundWindow) outbound.RemoveFirst();
        }
    }
}
