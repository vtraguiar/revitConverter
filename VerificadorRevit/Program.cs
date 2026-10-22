using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VerificadorRevit
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm();
            form.Load += (s, e) => form.AddFiles(args); // permite "Abrir com" e arrastar sobre o .exe
            Application.Run(form);
        }
    }

    sealed class MainForm : Form
    {
        readonly ListView list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
        readonly PictureBox preview = new PictureBox { Dock = DockStyle.Top, Height = 200, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
        readonly TextBox details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 10f) };
        readonly FlowLayoutPanel top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly ToolStripProgressBar progress = new ToolStripProgressBar { Style = ProgressBarStyle.Marquee, Visible = false };

        public MainForm()
        {
            Text = "Verificador e conversor de arquivos do Revit";
            Size = new Size(1000, 600);
            MinimumSize = new Size(700, 420);
            Font = new Font("Segoe UI", 9.5f);
            AllowDrop = true;

            list.Columns.Add("Arquivo", 340);
            list.Columns.Add("Versão", 90);
            list.Columns.Add("Abre em", 200);
            list.SelectedIndexChanged += (s, e) => ShowDetails(Selected);

            AddButton("Abrir arquivos...", (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = "Arquivos do Revit (*.rvt;*.rfa;*.rte;*.rft)|*.rvt;*.rfa;*.rte;*.rft|Todos (*.*)|*.*", Multiselect = true })
                    if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
            });
            AddButton("Converter para Revit 2025", async (s, e) => await ConvertTo2025());
            AddButton("Exportar IFC (universal)", async (s, e) => await ExportIfc());
            AddButton("Chaves da Autodesk...", (s, e) => AskKeys());
            top.Controls.Add(new Label { Text = "ou arraste arquivos .rvt / .rfa para a janela", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(12, 8, 0, 0) });

            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0) };
            right.Controls.Add(details);
            right.Controls.Add(preview);

            var split = new SplitContainer { Dock = DockStyle.Fill };
            Load += (s, e) => split.SplitterDistance = ClientSize.Width * 3 / 5; // antes do Load o tamanho ainda é o padrão
            split.Panel1.Controls.Add(list);
            split.Panel2.Controls.Add(right);

            var bar = new StatusStrip();
            bar.Items.Add(status);
            bar.Items.Add(progress);

            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(bar);

            DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (s, e) => AddFiles((string[])e.Data.GetData(DataFormats.FileDrop));
            ShowDetails(null);
        }

        RvtInfo Selected => list.SelectedItems.Count > 0 ? (RvtInfo)list.SelectedItems[0].Tag : null;

        void AddButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 0, 6, 0) };
            b.Click += click;
            top.Controls.Add(b);
        }

        public void AddFiles(string[] paths)
        {
            ListViewItem last = null;
            foreach (var p in paths.Where(File.Exists))
            {
                var info = RvtInfo.Read(p);
                last = new ListViewItem(new[]
                {
                    Path.GetFileName(p),
                    info.Version != null ? "Revit " + info.Version : "—",
                    info.Version != null ? $"Revit {info.Version} ou mais recente" : (info.Error ?? "—")
                }) { Tag = info, ToolTipText = p };
                list.Items.Add(last);
            }
            if (last != null) { list.SelectedItems.Clear(); last.Selected = true; last.EnsureVisible(); }
        }

        void ShowDetails(RvtInfo i)
        {
            preview.Image?.Dispose();
            preview.Image = null;
            if (i == null)
            {
                details.Text = "Escolha ou arraste um arquivo do Revit para ver em que versão ele foi salvo.";
                return;
            }
            if (i.Preview != null)
                try { using (var ms = new MemoryStream(i.Preview)) preview.Image = new Bitmap(Image.FromStream(ms)); } catch { }

            var nl = Environment.NewLine;
            if (i.Version == null) { details.Text = i.File + nl + nl + i.Error; return; }
            details.Text =
                $"Arquivo: {i.File}{nl}{nl}" +
                $"Versão: Revit {i.Version}{nl}" +
                $"Build: {i.Build ?? "—"}{nl}" +
                $"Trabalho compartilhado: {Translate(i.Worksharing)}{nl}" +
                $"Idioma ao salvar: {i.Locale ?? "—"}{nl}" +
                $"Último local salvo: {i.LastSavePath ?? "—"}{nl}{nl}" +
                $"Abre no Revit {i.Version} e em versões mais recentes.{nl}" +
                $"Não abre em versões anteriores: o Revit não converte arquivos para versões mais antigas.{nl}{nl}" +
                "Converter para Revit 2025: reconstrói o modelo na nuvem da Autodesk. Paredes, pisos, forros, telhados, níveis " +
                "e ambientes voltam editáveis; portas, janelas e móveis sem família no 2025 voltam como forma, sem parametria. " +
                "Folhas, cotas e anotações não voltam. O relatório lista tudo." + nl + nl +
                "Exportar IFC: formato aberto que qualquer versão e outros programas BIM abrem, mas sem parametria.";
        }

        static string Translate(string ws) =>
            ws == null ? "—" : ws.StartsWith("Not enabled") ? "não" : "sim (" + ws + ")";

        async Task ConvertTo2025()
        {
            var i = Selected;
            if (!CheckFile(i)) return;
            if (i.Version != "2026")
            {
                MessageBox.Show(this, int.TryParse(i.Version, out var v) && v <= 2025
                        ? $"Este arquivo é do Revit {i.Version}: ele já abre no Revit 2025, não precisa converter."
                        : $"Por enquanto só é possível converter arquivos do Revit 2026 para o 2025 (este é do {i.Version}).",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var name = Path.GetFileNameWithoutExtension(i.File);
            var dest = AskSave(name + "_R2025.rvt", "Projeto do Revit (*.rvt)|*.rvt", i.File);
            if (dest == null) return;
            var report = Path.ChangeExtension(dest, ".relatorio.txt");
            var bundles = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nuvem");

            if (await Cloud(i, aps => aps.ToRevit2025(i.File, dest, report, bundles)))
            {
                MessageBox.Show(this, $"Pronto!\n\nProjeto: {dest}\nRelatório: {report}\n\nAbra o relatório para ver o que voltou editável, o que voltou sem parametria e o que não foi recriado.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                AddFiles(new[] { dest });
            }
        }

        async Task ExportIfc()
        {
            var i = Selected;
            if (!CheckFile(i)) return;
            var dest = AskSave(Path.GetFileNameWithoutExtension(i.File) + ".ifc", "IFC (*.ifc)|*.ifc", i.File);
            if (dest == null) return;
            if (await Cloud(i, aps => aps.ToIfc(i.File, dest)))
                MessageBox.Show(this, $"Pronto!\n\nIFC: {dest}\n\nNo Revit: Arquivo > Abrir > IFC.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        bool CheckFile(RvtInfo i)
        {
            if (i?.Version != null && i.File.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) return true;
            MessageBox.Show(this, "Selecione na lista um projeto do Revit (.rvt) válido.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        string AskSave(string fileName, string filter, string near)
        {
            using (var dlg = new SaveFileDialog { FileName = fileName, Filter = filter, InitialDirectory = Path.GetDirectoryName(near), OverwritePrompt = true })
                return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
        }

        // Confirma o envio, usa as chaves salvas e trava a janela enquanto a nuvem trabalha.
        async Task<bool> Cloud(RvtInfo i, Func<Aps, Task> work)
        {
            var keys = Keys.Load() ?? (AskKeys() ? Keys.Load() : null);
            if (keys == null) return false;

            var mb = new FileInfo(i.File).Length / (1024.0 * 1024.0);
            if (MessageBox.Show(this,
                    $"O arquivo \"{Path.GetFileName(i.File)}\" ({mb:0} MB) será enviado para os servidores da Autodesk (APS).\n\n" +
                    "• A Autodesk apaga os arquivos enviados automaticamente após 24 horas.\n" +
                    "• O uso entra na cota gratuita mensal da sua conta APS; acima dela, é cobrado pela Autodesk.\n\nContinuar?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;

            top.Enabled = false;
            progress.Visible = true;
            try
            {
                using (var aps = new Aps(keys[0], keys[1], msg => BeginInvoke((Action)(() => status.Text = msg))))
                    await work(aps);
                status.Text = "Concluído.";
                return true;
            }
            catch (Exception ex)
            {
                status.Text = "Falhou.";
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally
            {
                top.Enabled = true;
                progress.Visible = false;
            }
        }

        bool AskKeys()
        {
            var saved = Keys.Load();
            using (var f = new Form { Text = "Chaves da Autodesk (APS)", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(480, 230), Font = Font })
            {
                var help = new LinkLabel
                {
                    Text = "Crie um aplicativo gratuito em aps.autodesk.com/myapps (tipo \"Server-to-Server\") e copie as duas chaves abaixo.",
                    Location = new Point(12, 12), Size = new Size(456, 40)
                };
                help.LinkArea = new LinkArea(help.Text.IndexOf("aps.autodesk.com/myapps"), "aps.autodesk.com/myapps".Length);
                help.LinkClicked += (s, e) => System.Diagnostics.Process.Start("https://aps.autodesk.com/myapps");
                var id = new TextBox { Location = new Point(12, 80), Width = 456, Text = saved?[0] ?? "" };
                var secret = new TextBox { Location = new Point(12, 136), Width = 456, UseSystemPasswordChar = true, Text = saved?[1] ?? "" };
                var ok = new Button { Text = "Salvar", DialogResult = DialogResult.OK, Location = new Point(292, 184), Width = 84 };
                var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Location = new Point(384, 184), Width = 84 };
                f.Controls.AddRange(new Control[]
                {
                    help,
                    new Label { Text = "Client ID", Location = new Point(12, 60), AutoSize = true }, id,
                    new Label { Text = "Client Secret", Location = new Point(12, 116), AutoSize = true }, secret,
                    ok, cancel
                });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                if (f.ShowDialog(this) != DialogResult.OK) return false;
                if (id.Text.Trim().Length == 0 || secret.Text.Trim().Length == 0)
                {
                    MessageBox.Show(this, "Preencha as duas chaves.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                Keys.Save(id.Text, secret.Text);
                return true;
            }
        }
    }
}
