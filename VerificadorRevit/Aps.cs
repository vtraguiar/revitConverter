using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VerificadorRevit
{
    // Cliente mínimo do Autodesk Platform Services (APS):
    // armazenamento (OSS), Model Derivative (IFC) e Revit Automation (Revit rodando na nuvem).
    sealed class Aps : IDisposable
    {
        const string Host = "https://developer.api.autodesk.com";
        const string Da = Host + "/da/us-east/v3";
        static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        readonly HttpClient http = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromHours(2) };
        readonly string clientId, secret;
        readonly Action<string> log;
        string token, nickname;
        DateTime tokenExpires;

        public Aps(string clientId, string secret, Action<string> log)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            this.clientId = clientId; this.secret = secret; this.log = log;
        }

        // Bucket "transient": a Autodesk apaga os arquivos sozinha após 24 h.
        string Bucket
        {
            get
            {
                using (var sha = SHA1.Create())
                    return "conv-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(clientId))).Replace("-", "").ToLowerInvariant().Substring(0, 24);
            }
        }

        public async Task ToIfc(string rvt, string dest)
        {
            await EnsureBucket();
            var objectId = await Upload(rvt, Guid.NewGuid().ToString("N") + ".rvt");
            var urn = Convert.ToBase64String(Encoding.UTF8.GetBytes(objectId)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            log("Gerando IFC na nuvem...");
            await Json(HttpMethod.Post, Host + "/modelderivative/v2/designdata/job",
                new { input = new { urn }, output = new { formats = new[] { new { type = "ifc" } } } });

            Dictionary<string, object> manifest;
            while (true)
            {
                await Task.Delay(10000);
                manifest = await Json(HttpMethod.Get, $"{Host}/modelderivative/v2/designdata/{urn}/manifest");
                var status = manifest["status"] as string;
                log("Gerando IFC na nuvem: " + (manifest.TryGetValue("progress", out var p) ? p : status));
                if (status == "success") break;
                if (status == "failed" || status == "timeout")
                    throw new Exception("A Autodesk não conseguiu gerar o IFC.\n\n" + Js.Serialize(manifest));
            }
            var ifcUrn = FindUrn(manifest, ".ifc") ?? throw new Exception("A conversão terminou sem arquivo IFC.\n\n" + Js.Serialize(manifest));

            // O download do derivativo usa cookies assinados (CloudFront).
            using (var req = new HttpRequestMessage(HttpMethod.Get, $"{Host}/modelderivative/v2/designdata/{urn}/manifest/{Uri.EscapeDataString(ifcUrn)}/signedcookies"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
                using (var res = await http.SendAsync(req))
                {
                    var text = await res.Content.ReadAsStringAsync();
                    if (!res.IsSuccessStatusCode) throw new Exception("Falha ao baixar o IFC: " + text);
                    var cookie = string.Join("; ", res.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
                    log("Baixando IFC...");
                    await Save((string)Parse(text)["url"], dest, cookie);
                }
            }
        }

        public async Task ToRevit2025(string rvt, string destRvt, string destReport, string bundleDir)
        {
            await EnsureBucket();
            var id = Guid.NewGuid().ToString("N");
            await Upload(rvt, id + ".rvt");
            await Setup(bundleDir);

            log("Etapa 1 de 2: lendo o projeto no Revit 2026 da nuvem...");
            var model = await SignedPut(id + ".conv.json.gz");
            await Run("ConvExport", new Dictionary<string, object>
            {
                ["rvtFile"] = new { url = await SignedGet(id + ".rvt") },
                ["model"] = new { verb = "put", url = model.Item1 }
            });
            await Complete(id + ".conv.json.gz", model.Item2);

            log("Etapa 2 de 2: reconstruindo no Revit 2025 da nuvem...");
            var result = await SignedPut(id + "_2025.rvt");
            var report = await SignedPut(id + ".txt");
            await Run("ConvImport", new Dictionary<string, object>
            {
                ["model"] = new { url = await SignedGet(id + ".conv.json.gz") },
                ["result"] = new { verb = "put", url = result.Item1 },
                ["report"] = new { verb = "put", url = report.Item1 }
            });
            await Complete(id + "_2025.rvt", result.Item2);
            await Complete(id + ".txt", report.Item2);

            log("Baixando o resultado...");
            await Save(await SignedGet(id + "_2025.rvt"), destRvt, null);
            await Save(await SignedGet(id + ".txt"), destReport, null);
        }

        // Recria pacotes e atividades a cada conversão: simples e sempre igual ao que acompanha o .exe.
        async Task Setup(string bundleDir)
        {
            log("Preparando o Revit na nuvem...");
            nickname = Js.Deserialize<string>(await Send(HttpMethod.Get, Da + "/forgeapps/me"));
            foreach (var (year, activity, export) in new[] { (2026, "ConvExport", true), (2025, "ConvImport", false) })
            {
                var bundle = "ConvRevit" + year;
                var engine = "Autodesk.Revit+" + year;
                await Send(HttpMethod.Delete, $"{Da}/activities/{activity}", HttpStatusCode.NotFound);
                await Send(HttpMethod.Delete, $"{Da}/appbundles/{bundle}", HttpStatusCode.NotFound);

                var created = await Json(HttpMethod.Post, Da + "/appbundles", new { id = bundle, engine, description = "Conversor de versões" });
                var up = (Dictionary<string, object>)created["uploadParameters"];
                using (var form = new MultipartFormDataContent())
                {
                    foreach (var kv in (Dictionary<string, object>)up["formData"]) form.Add(new StringContent(kv.Value.ToString()), kv.Key);
                    form.Add(new ByteArrayContent(File.ReadAllBytes(Path.Combine(bundleDir, bundle + ".zip"))), "file", bundle + ".zip");
                    var res = await http.PostAsync((string)up["endpointURL"], form);
                    if (!res.IsSuccessStatusCode) throw new Exception("Falha ao enviar o pacote " + bundle + ": " + await res.Content.ReadAsStringAsync());
                }
                await Json(HttpMethod.Post, $"{Da}/appbundles/{bundle}/aliases", new { id = "prod", version = 1 });

                var al = $"/al \"$(appbundles[{bundle}].path)\"";
                var parameters = export
                    ? new Dictionary<string, object>
                    {
                        ["rvtFile"] = new { verb = "get", localName = "input.rvt", required = true },
                        ["model"] = new { verb = "put", localName = "model.conv.json.gz" }
                    }
                    : new Dictionary<string, object>
                    {
                        ["model"] = new { verb = "get", localName = "model.conv.json.gz", required = true },
                        ["result"] = new { verb = "put", localName = "result.rvt" },
                        ["report"] = new { verb = "put", localName = "relatorio.txt" }
                    };
                await Json(HttpMethod.Post, Da + "/activities", new
                {
                    id = activity,
                    commandLine = new[] { @"$(engine.path)\revitcoreconsole.exe " + (export ? "/i \"$(args[rvtFile].path)\" " : "") + al },
                    parameters,
                    engine,
                    appbundles = new[] { $"{nickname}.{bundle}+prod" },
                    description = "Conversor de versões"
                });
                await Json(HttpMethod.Post, $"{Da}/activities/{activity}/aliases", new { id = "prod", version = 1 });
            }
        }

        async Task Run(string activity, Dictionary<string, object> arguments)
        {
            var w = await Json(HttpMethod.Post, Da + "/workitems",
                new { activityId = $"{nickname}.{activity}+prod", arguments, limitProcessingTimeSec = 3600 });
            var id = (string)w["id"];
            var started = DateTime.Now;
            while (true)
            {
                await Task.Delay(10000);
                w = await Json(HttpMethod.Get, $"{Da}/workitems/{id}");
                var status = (string)w["status"];
                if (status == "success") return;
                if (status != "pending" && status != "inprogress")
                {
                    var report = w.TryGetValue("reportUrl", out var url) ? await http.GetStringAsync((string)url) : "";
                    throw new Exception($"O Revit na nuvem terminou com '{status}'.\n\nFinal do log:\n" +
                        (report.Length > 2500 ? report.Substring(report.Length - 2500) : report));
                }
                log($"{(status == "pending" ? "Na fila" : "Processando")}... {(int)(DateTime.Now - started).TotalMinutes} min");
            }
        }

        Task EnsureBucket() => Send(HttpMethod.Post, Host + "/oss/v2/buckets", HttpStatusCode.Conflict, new { bucketKey = Bucket, policyKey = "transient" });

        string Obj(string key) => $"{Host}/oss/v2/buckets/{Bucket}/objects/{Uri.EscapeDataString(key)}";

        async Task<string> Upload(string path, string key)
        {
            long size = new FileInfo(path).Length;
            int chunk = (int)Math.Max(5L << 20, (size + 24) / 25); // no máximo 25 partes numa só requisição
            int parts = (int)Math.Max(1, (size + chunk - 1) / chunk);
            var signed = await Json(HttpMethod.Get, $"{Obj(key)}/signeds3upload?parts={parts}&minutesExpiration=60");
            var urls = ((object[])signed["urls"]).Cast<string>().ToArray();
            using (var fs = File.OpenRead(path))
            {
                var buf = new byte[Math.Min(chunk, Math.Max(size, 1))];
                for (int i = 0; i < parts; i++)
                {
                    log($"Enviando {Path.GetFileName(path)}: {100 * i / parts}%");
                    int n = 0, r;
                    while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
                    var res = await http.PutAsync(urls[i], new ByteArrayContent(buf, 0, n));
                    if (!res.IsSuccessStatusCode) throw new Exception("Falha no envio do arquivo: " + await res.Content.ReadAsStringAsync());
                }
            }
            return (string)(await Complete(key, (string)signed["uploadKey"]))["objectId"];
        }

        async Task<string> SignedGet(string key) => (string)(await Json(HttpMethod.Get, $"{Obj(key)}/signeds3download?minutesExpiration=60"))["url"];

        async Task<Tuple<string, string>> SignedPut(string key)
        {
            var d = await Json(HttpMethod.Get, $"{Obj(key)}/signeds3upload?minutesExpiration=60");
            return Tuple.Create((string)((object[])d["urls"])[0], (string)d["uploadKey"]);
        }

        Task<Dictionary<string, object>> Complete(string key, string uploadKey) =>
            Json(HttpMethod.Post, $"{Obj(key)}/signeds3upload", new { uploadKey });

        async Task Save(string url, string dest, string cookie)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (cookie != null) req.Headers.Add("Cookie", cookie);
                using (var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!res.IsSuccessStatusCode) throw new Exception($"Falha no download ({(int)res.StatusCode}).");
                    var tmp = dest + ".baixando";
                    using (var fs = File.Create(tmp)) await res.Content.CopyToAsync(fs);
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(tmp, dest);
                }
            }
        }

        async Task<string> Token()
        {
            if (token != null && DateTime.UtcNow < tokenExpires) return token;
            using (var req = new HttpRequestMessage(HttpMethod.Post, Host + "/authentication/v2/token"))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + secret)));
                req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["scope"] = "data:read data:write data:create bucket:create bucket:read code:all"
                });
                using (var res = await http.SendAsync(req))
                {
                    var text = await res.Content.ReadAsStringAsync();
                    if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden || res.StatusCode == HttpStatusCode.BadRequest)
                        throw new Exception("As chaves da Autodesk foram recusadas. Confira o Client ID e o Client Secret.");
                    if (!res.IsSuccessStatusCode) throw new Exception("Falha ao autenticar na Autodesk: " + text);
                    var d = Parse(text);
                    token = (string)d["access_token"];
                    tokenExpires = DateTime.UtcNow.AddSeconds(Convert.ToInt32(d["expires_in"]) - 120);
                }
            }
            return token;
        }

        Task<string> Send(HttpMethod method, string url, HttpStatusCode alsoOk = HttpStatusCode.OK, object body = null) =>
            SendCore(method, url, body, alsoOk);

        async Task<Dictionary<string, object>> Json(HttpMethod method, string url, object body = null) =>
            Parse(await SendCore(method, url, body, HttpStatusCode.OK));

        async Task<string> SendCore(HttpMethod method, string url, object body, HttpStatusCode alsoOk)
        {
            using (var req = new HttpRequestMessage(method, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
                if (body != null) req.Content = new StringContent(Js.Serialize(body), Encoding.UTF8, "application/json");
                using (var res = await http.SendAsync(req))
                {
                    var text = await res.Content.ReadAsStringAsync();
                    if (!res.IsSuccessStatusCode && res.StatusCode != alsoOk)
                        throw new Exception($"A Autodesk recusou a operação ({(int)res.StatusCode}) em {new Uri(url).AbsolutePath}:\n{text}");
                    return text;
                }
            }
        }

        static Dictionary<string, object> Parse(string text) =>
            Js.DeserializeObject(text) as Dictionary<string, object> ?? new Dictionary<string, object>();

        static string FindUrn(object node, string ext)
        {
            if (node is Dictionary<string, object> d)
            {
                if (d.TryGetValue("urn", out var u) && u is string s && s.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return s;
                return d.Values.Select(v => FindUrn(v, ext)).FirstOrDefault(x => x != null);
            }
            return node is object[] a ? a.Select(v => FindUrn(v, ext)).FirstOrDefault(x => x != null) : null;
        }

        public void Dispose() => http.Dispose();
    }

    // Chaves guardadas criptografadas para o usuário do Windows (DPAPI), nunca em texto puro.
    static class Keys
    {
        static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VerificadorRevit", "chaves.dat");

        public static string[] Load()
        {
            try
            {
                var parts = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser)).Split('\n');
                return parts.Length == 2 ? parts : null;
            }
            catch { return null; }
        }

        public static void Save(string id, string secret)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(id.Trim() + "\n" + secret.Trim()), null, DataProtectionScope.CurrentUser));
        }
    }
}
