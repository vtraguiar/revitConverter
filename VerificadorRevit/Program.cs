using System;
using System.Collections.Generic;
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
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            var form = new MainForm();
            form.Load += (s, e) => form.AddFiles(args); // permite "Abrir com" e arrastar sobre o .exe
            Application.Run(form);
        }
    }

    sealed class MainForm : Form
    {
        readonly TreeView browser = new TreeView { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, HideSelection = false, ShowNodeToolTips = true, Sorted = true, ItemHeight = 20 };
        readonly DataGridView props = Theme.PropertyGrid();
        readonly PictureBox typeIcon = new PictureBox { Dock = DockStyle.Left, Width = 44, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.White };
        readonly Label typeName = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.White };
        readonly PictureBox preview = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.White };
        readonly Label viewTab = new Label { Dock = DockStyle.Left, AutoSize = false, Width = 260, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), BackColor = Color.White, AutoEllipsis = true };
        readonly Label hint = new Label { Dock = DockStyle.Bottom, Height = 84, BackColor = Color.White, ForeColor = Theme.Muted, Padding = new Padding(16, 8, 16, 8) };
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Pronto" };
        readonly ToolStripProgressBar progress = new ToolStripProgressBar { Style = ProgressBarStyle.Marquee, Visible = false };
        readonly Control ribbon;

        public MainForm()
        {
            Text = "Verificador e conversor de arquivos do Revit";
            Size = new Size(1100, 680);
            MinimumSize = new Size(800, 500);
            Font = new Font("Segoe UI", 9f);
            BackColor = Theme.Window;

            browser.ImageList = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
            browser.ImageList.Images.AddRange(new Image[] { Icons.Folder(16), Icons.RevitFile(16), Icons.RevitFile(16, valid: false) });
            browser.AfterSelect += (s, e) => ShowDetails(e.Node.Tag as RvtInfo);

            // Paletas à esquerda, como no Revit: Propriedades em cima, navegador embaixo.
            var typeBox = new Panel { Dock = DockStyle.Top, Height = 48 };
            typeBox.Controls.Add(typeName);
            typeBox.Controls.Add(typeIcon);
            var propsPalette = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            propsPalette.Controls.Add(props);
            propsPalette.Controls.Add(typeBox);
            propsPalette.Controls.Add(Theme.PaletteHeader("Propriedades"));
            var browserPalette = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            browserPalette.Controls.Add(browser);
            browserPalette.Controls.Add(Theme.PaletteHeader("Navegador de arquivos"));
            var palettes = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.Line, SplitterWidth = 4 };
            palettes.Panel1.Controls.Add(propsPalette);
            palettes.Panel2.Controls.Add(browserPalette);

            // Área de desenho: aba da vista em cima, miniatura no meio, orientação embaixo.
            var viewBar = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Theme.TabStrip, Padding = new Padding(4, 3, 0, 0) };
            viewBar.Controls.Add(viewTab);
            var canvas = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            canvas.Controls.Add(preview);
            canvas.Controls.Add(hint);
            canvas.Controls.Add(viewBar);

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, BackColor = Theme.Line, SplitterWidth = 4 };
            split.Panel1.Controls.Add(palettes);
            split.Panel2.Controls.Add(canvas);
            Load += (s, e) => { split.SplitterDistance = 320; palettes.SplitterDistance = palettes.Height * 3 / 5; }; // antes do Load o tamanho ainda é o padrão

            var bar = new StatusStrip { BackColor = Theme.TabStrip };
            bar.Items.Add(status);
            bar.Items.Add(progress);
            bar.Items.Add(new ToolStripStatusLabel("Versão " + Updater.Current.ToString(3)) { ForeColor = Theme.Muted });

            ribbon = BuildRibbon();
            Controls.Add(split);
            Controls.Add(ribbon);
            Controls.Add(bar);

            Shown += async (s, e) => await CheckForUpdate(manual: false);
            AcceptDrops(this);
            ShowDetails(null);
        }

        Control BuildRibbon()
        {
            var fileMenu = new ContextMenuStrip();
            fileMenu.Items.Add("Abrir arquivos...", Icons.Folder(16), (s, e) => OpenFiles());
            fileMenu.Items.Add("Chaves da Autodesk...", Icons.Key(16), (s, e) => AskKeys());
            fileMenu.Items.Add("Verificar atualizações", null, async (s, e) => await CheckForUpdate(manual: true));
            fileMenu.Items.Add(new ToolStripSeparator());
            fileMenu.Items.Add("Sair", null, (s, e) => Close());

            var fileTab = new Label { Text = "Arquivo", Width = 76, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleCenter, BackColor = Theme.Accent, ForeColor = Color.White, Cursor = Cursors.Hand };
            fileTab.Click += (s, e) => fileMenu.Show(fileTab, new Point(0, fileTab.Height));
            var tab = new Label { Text = "Conversor", Width = 96, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleCenter, BackColor = Theme.RibbonBody };
            var tabs = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Theme.TabStrip, Padding = new Padding(0, 3, 0, 0) };
            tabs.Controls.Add(tab);
            tabs.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 4, BackColor = Theme.TabStrip });
            tabs.Controls.Add(fileTab);

            // Dock à esquerda: o último adicionado fica mais à esquerda.
            var body = new Panel { Dock = DockStyle.Top, Height = 98, BackColor = Theme.RibbonBody };
            body.Controls.Add(Theme.RibbonPanel("Configurações",
                Theme.RibbonButton("Chaves da\nAutodesk", Icons.Key(), (s, e) => AskKeys())));
            body.Controls.Add(Theme.RibbonPanel("Converter",
                Theme.RibbonButton("Converter\npara 2025", Icons.RevitFile(32, "25"), async (s, e) => await ConvertTo2025()),
                Theme.RibbonButton("Exportar\nIFC", Icons.Cube(), async (s, e) => await ExportIfc())));
            body.Controls.Add(Theme.RibbonPanel("Arquivos",
                Theme.RibbonButton("Abrir\narquivos", Icons.Folder(), (s, e) => OpenFiles())));

            var r = new Panel { Dock = DockStyle.Top, Height = 26 + 98 + 1, BackColor = Theme.Line };
            r.Controls.Add(body);
            r.Controls.Add(tabs);
            return r;
        }

        // Arquivos soltos sobre qualquer parte da janela (cada controle recebe o próprio evento).
        void AcceptDrops(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            c.DragDrop += (s, e) => AddFiles((string[])e.Data.GetData(DataFormats.FileDrop));
            foreach (Control child in c.Controls) AcceptDrops(child);
        }

        // Ao abrir, verifica em silêncio (sem internet, segue normalmente); pelo menu, sempre responde.
        async Task CheckForUpdate(bool manual)
        {
            Updater.Cleanup();
            Updater.Release r;
            try { r = await Updater.Check(); }
            catch (Exception ex)
            {
                if (manual) MessageBox.Show(this, "Não foi possível verificar atualizações: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (r == null)
            {
                if (manual) MessageBox.Show(this, $"Você já usa a versão mais recente ({Updater.Current.ToString(3)}).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var notes = r.Notes.Length > 600 ? r.Notes.Substring(0, 600) + "..." : r.Notes;
            if (MessageBox.Show(this, $"Há uma versão nova do programa: {r.Version.ToString(3)} (você usa a {Updater.Current.ToString(3)}).\n\n{notes}\n\nAtualizar agora? O programa vai fechar e abrir de novo.",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
                return;

            ribbon.Enabled = false;
            progress.Visible = true;
            status.Text = "Baixando a atualização...";
            try { await Updater.Install(r); }
            catch (Exception ex)
            {
                status.Text = "Atualização não concluída.";
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                ribbon.Enabled = true;
                progress.Visible = false;
            }
        }

        RvtInfo Selected => browser.SelectedNode?.Tag as RvtInfo;

        void OpenFiles()
        {
            using (var dlg = new OpenFileDialog { Filter = "Arquivos do Revit (*.rvt;*.rfa;*.rte;*.rft)|*.rvt;*.rfa;*.rte;*.rft|Todos (*.*)|*.*", Multiselect = true })
                if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
        }

        // Navegador agrupado por versão, como as categorias do Navegador de projeto.
        public void AddFiles(string[] paths)
        {
            TreeNode last = null;
            browser.BeginUpdate();
            foreach (var p in paths.Where(File.Exists))
            {
                var info = RvtInfo.Read(p);
                foreach (TreeNode g in browser.Nodes) g.Nodes.RemoveByKey(p); // reaberto: atualiza
                var group = info.Version != null ? "Revit " + info.Version : "Não reconhecidos";
                var node = browser.Nodes[group] ?? browser.Nodes.Add(group, group, 0, 0);
                int icon = info.Version != null ? 1 : 2;
                last = node.Nodes.Add(p, Path.GetFileName(p), icon, icon);
                last.Tag = info;
                last.ToolTipText = p;
                node.Expand();
            }
            foreach (var empty in browser.Nodes.Cast<TreeNode>().Where(g => g.Nodes.Count == 0).ToList()) empty.Remove();
            browser.EndUpdate();
            if (last != null) browser.SelectedNode = last;
        }

        void ShowDetails(RvtInfo i)
        {
            preview.Image?.Dispose();
            typeIcon.Image?.Dispose();
            props.Rows.Clear();
            if (i != null)
                foreach (var g in FileProps.Rows(i).GroupBy(r => r.Item1))
                {
                    Theme.CategoryRow(props, g.Key);
                    foreach (var r in g) props.Rows.Add(r.Item2, r.Item3);
                }
            typeIcon.Image = Icons.RevitFile(32, valid: i?.Version != null);
            typeName.Text = i == null ? "Nenhum arquivo selecionado" : i.Version == null ? "Arquivo não reconhecido" : $"{FileProps.Kind(i.File)} do Revit {i.Version}";
            viewTab.Text = i == null ? "Início" : Path.GetFileName(i.File);

            preview.Image = Icons.RevitFile(96, valid: false);
            if (i?.Preview != null)
                try
                {
                    using (var ms = new MemoryStream(i.Preview))
                    using (var img = Image.FromStream(ms))
                    {
                        preview.Image.Dispose();
                        preview.Image = new Bitmap(img, img.Width * 2, img.Height * 2); // a miniatura do Revit tem só 128 px
                    }
                }
                catch { }

            hint.Text = i == null ? "Clique em Abrir arquivos ou arraste arquivos .rvt / .rfa para esta janela."
                : i.Version == null ? i.Error
                : $"Abre no Revit {i.Version} e nas versões mais recentes. Não abre nas versões anteriores.\n\n" +
                  "Para usar no Revit 2025, clique em Converter para 2025. Para enviar a quem usa outra versão ou outro programa, clique em Exportar IFC.";
        }

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

            ribbon.Enabled = false;
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
                ribbon.Enabled = true;
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

    // Linhas da paleta Propriedades, com os nomes de categoria usados no Revit.
    static class FileProps
    {
        public static string Kind(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".rfa": return "Família";
                case ".rte": return "Template de projeto";
                case ".rft": return "Template de família";
                default: return "Projeto";
            }
        }

        public static IEnumerable<Tuple<string, string, string>> Rows(RvtInfo i)
        {
            Tuple<string, string, string> R(string cat, string name, string value) => Tuple.Create(cat, name, value ?? "—");
            const string Identity = "Dados de identidade", Compat = "Compatibilidade", Other = "Outros";
            yield return R(Identity, "Nome do arquivo", Path.GetFileName(i.File));
            yield return R(Identity, "Tipo", Kind(i.File));
            yield return R(Identity, "Versão do Revit", i.Version != null ? "Revit " + i.Version : null);
            yield return R(Identity, "Build", i.Build);
            yield return R(Identity, "Situação", i.Error ?? "Arquivo do Revit válido");
            yield return R(Compat, "Abre em", i.Version != null ? $"Revit {i.Version} ou mais recente" : null);
            yield return R(Compat, "Converter para 2025",
                i.Version == "2026" ? "Disponível" : int.TryParse(i.Version, out var v) && v <= 2025 ? "Não precisa: já abre no 2025" : "Indisponível");
            yield return R(Other, "Trabalho compartilhado",
                i.Worksharing == null ? null : i.Worksharing.StartsWith("Not enabled") ? "Não" : "Sim (" + i.Worksharing + ")");
            yield return R(Other, "Idioma ao salvar", i.Locale);
            yield return R(Other, "Último local salvo", i.LastSavePath);
            yield return R(Other, "Pasta", Path.GetDirectoryName(i.File));
        }
    }
}
