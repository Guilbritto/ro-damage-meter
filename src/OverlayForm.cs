using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RODamageMeter
{
    class OverlayForm : Form
    {
        const int HeaderH = 26, RowH = 22, FooterH = 22, Pad = 4, IconSz = 20, Grip = 8;

        static readonly CultureInfo PtBr = new CultureInfo("pt-BR");
        static readonly Color Bg = Color.FromArgb(18, 20, 26), HeaderBg = Color.FromArgb(30, 33, 42), RowBg = Color.FromArgb(34, 37, 47);
        static readonly Color TextCol = Color.FromArgb(236, 238, 243), MutedCol = Color.FromArgb(140, 146, 160), BasicCol = Color.FromArgb(112, 118, 134);
        static readonly Color[] Palette =
        {
            Color.FromArgb(190, 72, 58), Color.FromArgb(206, 136, 46), Color.FromArgb(70, 140, 196), Color.FromArgb(88, 160, 88),
            Color.FromArgb(146, 96, 196), Color.FromArgb(48, 156, 156), Color.FromArgb(192, 86, 148), Color.FromArgb(158, 148, 60),
        };
        static readonly Color[] StatusCol = { Color.FromArgb(80, 200, 120), Color.FromArgb(230, 180, 60), Color.FromArgb(230, 80, 70) };

        readonly DamageTracker tracker;
        readonly Settings settings;
        readonly Timer timer = new Timer();
        readonly ToolTip tip = new ToolTip();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly Font font = new Font("Segoe UI", 9f), fontBold = new Font("Segoe UI Semibold", 9f), fontSmall = new Font("Segoe UI", 8f), fontSym = new Font("Segoe UI Symbol", 10f);

        Snapshot snap = new Snapshot();
        volatile string status = "Iniciando...";
        volatile int statusLevel = 1;
        int hover = -1, scroll;
        string hoverButton, tipText;
        DateTime tipShownAt;
        Rectangle btnReset, btnMenu, btnClose;
        ToolStripMenuItem miAuto, miLock;

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

        public OverlayForm(DamageTracker tracker, Settings settings)
        {
            this.tracker = tracker;
            this.settings = settings;

            Text = "RO Damage Meter";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Bg;
            MinimumSize = new Size(200, 110);
            Bounds = new Rectangle(settings.X, settings.Y, settings.W, settings.H);
            Opacity = settings.Opacity;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            EnsureOnScreen();
            BuildMenu();

            tip.InitialDelay = 0;
            tip.ReshowDelay = 0;
            tip.UseAnimation = false;
            tip.UseFading = false;

            timer.Interval = 250;
            timer.Tick += (s, e) => { snap = tracker.Snapshot(); RefreshTip(); Invalidate(); };
            timer.Start();
        }

        public void SetStatus(string text, int level)
        {
            status = text;
            statusLevel = level;
        }

        void BuildMenu()
        {
            menu.Items.Add("Resetar dano agora", null, (s, e) => tracker.Reset());

            miAuto = new ToolStripMenuItem("Resetar ao trocar de mapa") { CheckOnClick = true, Checked = settings.AutoReset };
            miAuto.CheckedChanged += (s, e) =>
            {
                settings.AutoReset = miAuto.Checked;
                tracker.AutoResetOnMapChange = miAuto.Checked;
                settings.Save();
            };
            menu.Items.Add(miAuto);

            var opacity = new ToolStripMenuItem("Opacidade");
            foreach (int pct in new[] { 100, 90, 80, 70, 60, 50 })
            {
                var item = new ToolStripMenuItem(pct + "%") { Tag = pct };
                item.Click += (s, e) =>
                {
                    Opacity = (int)((ToolStripMenuItem)s).Tag / 100.0;
                    settings.Opacity = Opacity;
                    settings.Save();
                };
                opacity.DropDownItems.Add(item);
            }
            opacity.DropDownOpening += (s, e) =>
            {
                foreach (ToolStripMenuItem it in opacity.DropDownItems) it.Checked = Math.Abs((int)it.Tag / 100.0 - Opacity) < 0.01;
            };
            menu.Items.Add(opacity);

            miLock = new ToolStripMenuItem("Travar posição e tamanho") { CheckOnClick = true, Checked = settings.Locked };
            miLock.CheckedChanged += (s, e) => { settings.Locked = miLock.Checked; settings.Save(); };
            menu.Items.Add(miLock);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Abrir pasta do programa", null, (s, e) => Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory));
            menu.Items.Add("Sair", null, (s, e) => Close());
        }

        void EnsureOnScreen()
        {
            foreach (var scr in Screen.AllScreens)
                if (scr.WorkingArea.IntersectsWith(Bounds)) return;
            Location = new Point(200, 200);
        }

        // ---------- desenho ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int w = ClientSize.Width, h = ClientSize.Height;
            var s = snap;

            // cabeçalho
            using (var b = new SolidBrush(HeaderBg)) g.FillRectangle(b, 0, 0, w, HeaderH);
            using (var b = new SolidBrush(StatusCol[Math.Max(0, Math.Min(2, statusLevel))])) g.FillEllipse(b, 9, 9, 8, 8);
            btnClose = new Rectangle(w - 24, 3, 20, 20);
            btnMenu = new Rectangle(w - 46, 3, 20, 20);
            btnReset = new Rectangle(w - 68, 3, 20, 20);
            var titleRect = new Rectangle(22, 0, btnReset.Left - 24, HeaderH);
            string title = "Dano causado";
            var tsz = TextRenderer.MeasureText(g, title, fontBold, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, title, fontBold, titleRect, TextCol, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            if (s.Map.Length > 0)
            {
                var mapRect = new Rectangle(titleRect.X + tsz.Width + 6, 0, Math.Max(0, titleRect.Width - tsz.Width - 6), HeaderH);
                TextRenderer.DrawText(g, s.Map, fontSmall, mapRect, MutedCol, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
            DrawButton(g, btnReset, "↺", "reset");
            DrawButton(g, btnMenu, "⚙", "menu");
            DrawButton(g, btnClose, "✕", "close");

            // linhas
            var area = new Rectangle(0, HeaderH, w, h - HeaderH - FooterH);
            g.SetClip(area);
            if (s.Skills.Count == 0)
            {
                string msg = statusLevel == 0 ? (s.Me == 0 && s.Total == 0 ? "Aguardando dano..." : "Sem dano desde o reset") : status;
                TextRenderer.DrawText(g, msg, font, area, MutedCol, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
            else
            {
                int visible = Math.Max(1, (area.Height - 2) / RowH);
                scroll = Math.Max(0, Math.Min(scroll, s.Skills.Count - visible));
                long max = s.Skills[0].Total;
                for (int i = scroll; i < s.Skills.Count; i++)
                {
                    int y = HeaderH + 2 + (i - scroll) * RowH;
                    if (y > area.Bottom) break;
                    DrawRow(g, s.Skills[i], new Rectangle(Pad, y, w - 2 * Pad, RowH - 2), max, s, i == hover);
                }
            }
            g.ResetClip();

            // rodapé
            int fy = h - FooterH;
            using (var b = new SolidBrush(HeaderBg)) g.FillRectangle(b, 0, fy, w, FooterH);
            double dps = s.Seconds > 0 ? s.Total / s.Seconds : 0;
            string left = "Total " + Abbrev(s.Total) + "   DPS " + Abbrev(dps);
            var t = TimeSpan.FromSeconds(s.Seconds);
            string right = string.Format("{0:00}:{1:00}", (int)t.TotalMinutes, t.Seconds);
            var footRect = new Rectangle(8, fy, w - 16 - Grip, FooterH);
            TextRenderer.DrawText(g, left, fontSmall, footRect, TextCol, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, right, fontSmall, footRect, MutedCol, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
            if (!settings.Locked)
                using (var b = new SolidBrush(MutedCol))
                    for (int i = 0; i < 3; i++)
                        for (int j = 0; j <= i; j++)
                            g.FillRectangle(b, w - 4 - (i - j) * 3 - 2, h - 4 - j * 3 - 2, 2, 2);
        }

        void DrawButton(Graphics g, Rectangle r, string glyph, string name)
        {
            if (hoverButton == name)
                using (var b = new SolidBrush(Color.FromArgb(60, 255, 255, 255))) g.FillRectangle(b, r);
            TextRenderer.DrawText(g, glyph, fontSym, r, hoverButton == name ? TextCol : MutedCol,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        void DrawRow(Graphics g, SkillStat st, Rectangle row, long max, Snapshot s, bool hot)
        {
            var iconRect = new Rectangle(row.X, row.Y, IconSz, IconSz);
            var bar = new Rectangle(row.X + IconSz + 3, row.Y, row.Width - IconSz - 3, row.Height);
            using (var b = new SolidBrush(RowBg)) g.FillRectangle(b, bar);
            int fill = max > 0 ? (int)(bar.Width * (double)st.Total / max) : 0;
            var col = SkillColor(st.Skill);
            if (hot) col = ControlPaint.Light(col, 0.3f);
            using (var b = new LinearGradientBrush(bar, col, ControlPaint.Dark(col, 0.15f), LinearGradientMode.Vertical))
                g.FillRectangle(b, bar.X, bar.Y, Math.Max(fill, 2), bar.Height);

            double dps = s.Seconds > 0 ? st.Total / s.Seconds : 0;
            double pct = s.Total > 0 ? 100.0 * st.Total / s.Total : 0;
            var textRect = new Rectangle(bar.X + 5, bar.Y, bar.Width - 10, bar.Height);
            DrawShadowText(g, Abbrev(st.Total) + "  (" + Abbrev(dps) + "/s)", font, textRect, TextFormatFlags.Left);
            DrawShadowText(g, pct.ToString("0.0", PtBr) + "%", font, textRect, TextFormatFlags.Right);

            var icon = SkillDb.Icon(st.Skill);
            if (icon != null) g.DrawImage(icon, iconRect);
            else DrawPlaceholderIcon(g, iconRect, st.Skill);
        }

        void DrawShadowText(Graphics g, string text, Font f, Rectangle r, TextFormatFlags align)
        {
            var flags = align | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            r.Offset(1, 1);
            TextRenderer.DrawText(g, text, f, r, Color.FromArgb(10, 10, 12), flags);
            r.Offset(-1, -1);
            TextRenderer.DrawText(g, text, f, r, TextCol, flags);
        }

        void DrawPlaceholderIcon(Graphics g, Rectangle r, int skill)
        {
            using (var b = new SolidBrush(skill == 0 ? Color.FromArgb(70, 74, 86) : ControlPaint.Dark(SkillColor(skill), 0.2f))) g.FillRectangle(b, r);
            string glyph = skill == 0 ? "⚔" : Initials(SkillDb.Name(skill));
            TextRenderer.DrawText(g, glyph, skill == 0 ? fontSym : fontSmall, r, TextCol,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        static string Initials(string name)
        {
            var sb = new StringBuilder();
            foreach (var part in name.Split(' '))
                if (part.Length > 0 && char.IsLetter(part[0]) && sb.Length < 2) sb.Append(char.ToUpper(part[0]));
            return sb.ToString();
        }

        static Color SkillColor(int skill) { return skill == 0 ? BasicCol : Palette[skill % Palette.Length]; }

        static string Abbrev(double v)
        {
            if (v >= 1e9) return (v / 1e9).ToString("0.00", PtBr) + "B";
            if (v >= 1e6) return (v / 1e6).ToString("0.0", PtBr) + "M";
            if (v >= 1e3) return (v / 1e3).ToString("0.0", PtBr) + "K";
            return v.ToString("0", PtBr);
        }

        // ---------- tooltip ----------

        string TipFor(int index)
        {
            var s = snap;
            if (index < 0 || index >= s.Skills.Count) return null;
            var st = s.Skills[index];
            double pct = s.Total > 0 ? 100.0 * st.Total / s.Total : 0;
            var sb = new StringBuilder();
            sb.AppendLine(SkillDb.Name(st.Skill));
            sb.AppendLine(string.Format(PtBr, "Dano: {0:N0}  ({1:0.0}%)", st.Total, pct));
            sb.AppendLine(string.Format(PtBr, "Golpes: {0:N0}   Médio: {1:N0}   Máx: {2:N0}", st.Hits, st.Total / Math.Max(1, st.Hits), st.Max));
            if (st.Crits > 0) sb.AppendLine(string.Format(PtBr, "Críticos: {0:N0} ({1:0}%)", st.Crits, 100.0 * st.Crits / st.Hits));
            sb.Append(string.Format(PtBr, "DPS: {0:N0}", s.Seconds > 0 ? st.Total / s.Seconds : 0));
            return sb.ToString();
        }

        void ShowTip()
        {
            var text = TipFor(hover);
            if (text == null) { HideTip(); return; }
            if (text == tipText) return;
            tipText = text;
            tipShownAt = DateTime.Now;
            var p = PointToClient(Cursor.Position);
            tip.Show(text, this, p.X + 16, p.Y + 18, 30000);
        }

        void RefreshTip()
        {
            // atualiza o conteúdo com o dano novo, sem piscar a cada tick
            if (hover >= 0 && (DateTime.Now - tipShownAt).TotalMilliseconds > 900) ShowTip();
        }

        void HideTip()
        {
            if (tipText == null) return;
            tipText = null;
            tip.Hide(this);
        }

        // ---------- mouse ----------

        string ButtonAt(Point p)
        {
            if (btnReset.Contains(p)) return "reset";
            if (btnMenu.Contains(p)) return "menu";
            if (btnClose.Contains(p)) return "close";
            return null;
        }

        int RowAt(Point p)
        {
            if (p.Y < HeaderH + 2 || p.Y >= ClientSize.Height - FooterH) return -1;
            int i = scroll + (p.Y - HeaderH - 2) / RowH;
            return i < snap.Skills.Count ? i : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var btn = ButtonAt(e.Location);
            int row = RowAt(e.Location);
            if (btn != hoverButton || row != hover)
            {
                hoverButton = btn;
                bool changed = row != hover;
                hover = row;
                if (changed) { tipText = null; ShowTip(); }
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
            hoverButton = null;
            HideTip();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && e.Y < HeaderH && ButtonAt(e.Location) == null && !settings.Locked)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) { menu.Show(this, e.Location); return; }
            switch (ButtonAt(e.Location))
            {
                case "reset": tracker.Reset(); snap = tracker.Snapshot(); Invalidate(); break;
                case "menu": menu.Show(this, new Point(btnMenu.Left, btnMenu.Bottom)); break;
                case "close": Close(); break;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll = Math.Max(0, scroll - Math.Sign(e.Delta));
            Invalidate();
        }

        // redimensionar pelas bordas de uma janela sem borda
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x84 /* WM_NCHITTEST */ || settings.Locked) return;
            int lp = m.LParam.ToInt32();
            var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            bool l = p.X < Grip, r = p.X >= ClientSize.Width - Grip, b = p.Y >= ClientSize.Height - Grip;
            if (r && b) m.Result = (IntPtr)17;
            else if (l && b) m.Result = (IntPtr)16;
            else if (r) m.Result = (IntPtr)11;
            else if (l) m.Result = (IntPtr)10;
            else if (b) m.Result = (IntPtr)15;
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            SaveBounds();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveBounds();
            base.OnFormClosing(e);
        }

        void SaveBounds()
        {
            if (WindowState != FormWindowState.Normal) return;
            settings.X = Left; settings.Y = Top; settings.W = Width; settings.H = Height;
            settings.Save();
        }
    }
}
