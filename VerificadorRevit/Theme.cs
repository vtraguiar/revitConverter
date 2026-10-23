using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VerificadorRevit
{
    // Cores e controles no estilo da interface clara do Revit (faixa de opções, paletas, navegador).
    static class Theme
    {
        public static readonly Color Window = Color.FromArgb(238, 238, 238);
        public static readonly Color TabStrip = Color.FromArgb(231, 231, 231);
        public static readonly Color RibbonBody = Color.FromArgb(247, 247, 247);
        public static readonly Color PanelTitle = Color.FromArgb(227, 227, 227);
        public static readonly Color PaletteBar = Color.FromArgb(218, 218, 218);
        public static readonly Color Line = Color.FromArgb(200, 200, 200);
        public static readonly Color Accent = Color.FromArgb(6, 150, 215);    // azul Autodesk
        public static readonly Color Hover = Color.FromArgb(220, 235, 247);
        public static readonly Color Pressed = Color.FromArgb(196, 221, 243);
        public static readonly Color Muted = Color.FromArgb(102, 102, 102);

        // Botão grande da faixa de opções: ícone em cima, texto embaixo.
        public static Button RibbonButton(string text, Image icon, EventHandler click)
        {
            var b = new Button
            {
                Text = text, Image = icon, TextImageRelation = TextImageRelation.ImageAboveText,
                FlatStyle = FlatStyle.Flat, Size = new Size(84, 72), Margin = new Padding(2),
                BackColor = RibbonBody, TextAlign = ContentAlignment.BottomCenter, ImageAlign = ContentAlignment.TopCenter,
                Padding = new Padding(0, 4, 0, 0)
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Hover;
            b.FlatAppearance.MouseDownBackColor = Pressed;
            b.Click += click;
            return b;
        }

        // Grupo da faixa de opções: botões em cima e o nome do painel na faixa cinza de baixo.
        public static Control RibbonPanel(string title, params Control[] buttons)
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(4, 4, 4, 0), BackColor = RibbonBody };
            row.Controls.AddRange(buttons);
            var label = new Label { Text = title, Dock = DockStyle.Bottom, Height = 18, TextAlign = ContentAlignment.MiddleCenter, BackColor = PanelTitle, ForeColor = Muted };
            var panel = new Panel { Width = buttons.Length * 88 + 10, Dock = DockStyle.Left, Padding = new Padding(0, 0, 1, 0), BackColor = Line };
            panel.Controls.Add(row);
            panel.Controls.Add(label);
            return panel;
        }

        // Grade de parâmetro/valor da paleta Propriedades (somente leitura).
        public static DataGridView PropertyGrid()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackgroundColor = Color.White,
                RowHeadersVisible = false, ColumnHeadersVisible = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, GridColor = Line, CellBorderStyle = DataGridViewCellBorderStyle.Single
            };
            g.RowTemplate.Height = 22;
            g.DefaultCellStyle.SelectionBackColor = Hover;
            g.DefaultCellStyle.SelectionForeColor = Color.Black;
            g.Columns.Add("p", "Parâmetro");
            g.Columns.Add("v", "Valor");
            g.Columns[0].FillWeight = 45;
            g.Columns[1].FillWeight = 55;
            g.Columns[1].DefaultCellStyle.ForeColor = Muted;
            return g;
        }

        public static void CategoryRow(DataGridView g, string title)
        {
            var row = g.Rows[g.Rows.Add(title, "")];
            row.DefaultCellStyle.BackColor = PaletteBar;
            row.DefaultCellStyle.SelectionBackColor = PaletteBar;
            row.DefaultCellStyle.Font = new Font(g.Font, FontStyle.Bold);
            row.DefaultCellStyle.ForeColor = Color.Black;
            row.DefaultCellStyle.SelectionForeColor = Color.Black;
        }

        // Cabeçalho de paleta ("Propriedades", "Navegador de arquivos").
        public static Label PaletteHeader(string text) => new Label
        {
            Text = text, Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0), BackColor = PaletteBar, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold)
        };
    }

    // Ícones desenhados em código: nada de arquivos de imagem para distribuir.
    static class Icons
    {
        static Bitmap Draw(int size, Action<Graphics> paint)
        {
            var b = new Bitmap(size, size);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                paint(g);
            }
            return b;
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, radius, radius, 180, 90);
            p.AddArc(r.Right - radius, r.Y, radius, radius, 270, 90);
            p.AddArc(r.Right - radius, r.Bottom - radius, radius, radius, 0, 90);
            p.AddArc(r.X, r.Bottom - radius, radius, radius, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Bitmap Folder(int s = 32) => Draw(s, g =>
        {
            float k = s / 32f;
            using (var back = new SolidBrush(Color.FromArgb(222, 168, 52)))
            using (var front = new SolidBrush(Color.FromArgb(246, 200, 90)))
            {
                g.FillPath(back, Rounded(new RectangleF(2 * k, 6 * k, 12 * k, 6 * k), 3 * k));
                g.FillPath(back, Rounded(new RectangleF(2 * k, 9 * k, 28 * k, 18 * k), 3 * k));
                g.FillPath(front, Rounded(new RectangleF(2 * k, 12 * k, 28 * k, 16 * k), 3 * k));
            }
        });

        // Quadrado azul com "R": arquivo de projeto. Com selo laranja: converter para outra versão.
        public static Bitmap RevitFile(int s = 32, string badge = null, bool valid = true) => Draw(s, g =>
        {
            float k = s / 32f;
            var box = new RectangleF(3 * k, 3 * k, 26 * k, 26 * k);
            using (var fill = new SolidBrush(valid ? Theme.Accent : Color.FromArgb(160, 160, 160)))
                g.FillPath(fill, Rounded(box, 6 * k));
            using (var f = new Font("Arial", 15 * k, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("R", f, Brushes.White, box, sf);
            if (badge == null) return;
            var dot = new RectangleF(15 * k, 15 * k, 17 * k, 17 * k);
            using (var orange = new SolidBrush(Color.FromArgb(232, 118, 34)))
                g.FillEllipse(orange, dot);
            using (var f = new Font("Arial", 8 * k, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(badge, f, Brushes.White, dot, sf);
        });

        // Cubo isométrico: IFC.
        public static Bitmap Cube(int s = 32) => Draw(s, g =>
        {
            float k = s / 32f;
            PointF P(float x, float y) => new PointF(x * k, y * k);
            using (var top = new SolidBrush(Color.FromArgb(129, 199, 132)))
            using (var left = new SolidBrush(Color.FromArgb(56, 142, 60)))
            using (var right = new SolidBrush(Color.FromArgb(76, 175, 80)))
            {
                g.FillPolygon(top, new[] { P(16, 3), P(29, 10), P(16, 17), P(3, 10) });
                g.FillPolygon(left, new[] { P(3, 10), P(16, 17), P(16, 30), P(3, 23) });
                g.FillPolygon(right, new[] { P(16, 17), P(29, 10), P(29, 23), P(16, 30) });
            }
        });

        public static Bitmap Key(int s = 32) => Draw(s, g =>
        {
            float k = s / 32f;
            using (var pen = new Pen(Color.FromArgb(200, 150, 30), 4 * k))
            {
                g.DrawEllipse(pen, 4 * k, 4 * k, 12 * k, 12 * k);
                g.DrawLine(pen, 14 * k, 14 * k, 28 * k, 28 * k);
                g.DrawLine(pen, 22 * k, 22 * k, 26 * k, 18 * k);
                g.DrawLine(pen, 25.5f * k, 25.5f * k, 29 * k, 22 * k);
            }
        });
    }
}
