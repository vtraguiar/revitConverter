using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VerificadorRevit
{
    // Atualização pelas versões publicadas no GitHub (Releases): baixa o .zip, confere o SHA-256 e troca os arquivos.
    static class Updater
    {
        const string LatestUrl = "https://api.github.com/repos/vtraguiar/revitConverter/releases/latest";

        public sealed class Release { public Version Version; public string Notes, ZipUrl, ShaUrl; }

        public static Version Current => Normalize(Assembly.GetExecutingAssembly().GetName().Version);
        static string Exe => Assembly.GetExecutingAssembly().Location;
        static string Dir => Path.GetDirectoryName(Exe);

        static Version Normalize(Version v) => new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

        static HttpClient Http()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("VerificadorRevit/" + Current); // o GitHub exige User-Agent
            return http;
        }

        // Apaga o .exe antigo que a atualização anterior deixou renomeado.
        public static void Cleanup()
        {
            try { File.Delete(Exe + ".old"); } catch { }
        }

        // null = já está na versão mais recente (ou ainda não há versão publicada).
        public static async Task<Release> Check()
        {
            using (var http = Http())
            using (var res = await http.GetAsync(LatestUrl))
            {
                if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
                res.EnsureSuccessStatusCode();
                var d = (System.Collections.Generic.Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(await res.Content.ReadAsStringAsync());
                if (!Version.TryParse(((string)d["tag_name"]).TrimStart('v', 'V'), out var latest) || Normalize(latest) <= Current) return null;

                var assets = ((object[])d["assets"]).Cast<System.Collections.Generic.Dictionary<string, object>>().ToList();
                var zip = assets.FirstOrDefault(a => ((string)a["name"]).EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (zip == null) return null;
                var sha = assets.FirstOrDefault(a => (string)a["name"] == (string)zip["name"] + ".sha256");
                return new Release
                {
                    Version = Normalize(latest),
                    Notes = d["body"] as string ?? "",
                    ZipUrl = (string)zip["browser_download_url"],
                    ShaUrl = sha?["browser_download_url"] as string
                };
            }
        }

        public static async Task Install(Release r)
        {
            if (r.ShaUrl == null) throw new Exception("A versão publicada não tem o código de verificação (.sha256); a atualização foi cancelada.");
            try { File.WriteAllText(Path.Combine(Dir, ".teste-escrita"), ""); File.Delete(Path.Combine(Dir, ".teste-escrita")); }
            catch { throw new Exception($"Sem permissão para gravar em \"{Dir}\".\n\nMova a pasta do programa para Documentos ou para a Área de Trabalho e tente de novo."); }

            var zipPath = Path.Combine(Path.GetTempPath(), $"VerificadorRevit-{r.Version}.zip");
            var staging = Path.Combine(Path.GetTempPath(), "VerificadorRevit-" + Guid.NewGuid().ToString("N"));
            using (var http = Http())
            {
                var expected = (await http.GetStringAsync(r.ShaUrl)).Trim().Split(' ', '\t', '\n')[0].ToLowerInvariant();
                using (var res = await http.GetAsync(r.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    res.EnsureSuccessStatusCode();
                    using (var fs = File.Create(zipPath)) await res.Content.CopyToAsync(fs);
                }
                string actual;
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(zipPath))
                    actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                if (actual != expected) throw new Exception("O arquivo baixado não confere com o código de verificação; a atualização foi cancelada.");
            }

            ZipFile.ExtractToDirectory(zipPath, staging);
            var newExe = Directory.GetFiles(staging, Path.GetFileName(Exe), SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new Exception("O pacote baixado não contém o programa.");
            var src = Path.GetDirectoryName(newExe);

            // O Windows não deixa sobrescrever o .exe em uso, mas deixa renomear.
            Cleanup();
            File.Move(Exe, Exe + ".old");
            try
            {
                foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories).OrderBy(f => f == newExe)) // o .exe por último
                {
                    var dest = Path.Combine(Dir, f.Substring(src.Length + 1));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(f, dest, true);
                }
            }
            catch
            {
                if (!File.Exists(Exe)) File.Move(Exe + ".old", Exe);
                throw;
            }
            try { Directory.Delete(staging, true); File.Delete(zipPath); } catch { }

            Process.Start(Exe);
            Application.Exit();
        }
    }
}
