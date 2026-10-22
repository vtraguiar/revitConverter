using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using DesignAutomationFramework;

namespace RevitConversor;

// Ponto de entrada no Revit Automation (nuvem da Autodesk): sem interface; arquivos no diretório de trabalho.
// Com documento de entrada (.rvt) → exporta; sem documento → importa num projeto novo.
public class DaApp : IExternalDBApplication
{
    const string ModelFile = "model.conv.json.gz";

    public ExternalDBApplicationResult OnStartup(ControlledApplication app)
    {
        DesignAutomationBridge.DesignAutomationReadyEvent += (s, e) =>
        {
            try
            {
                if (e.DesignAutomationData.RevitDoc is { } doc) Export(doc);
                else Import(e.DesignAutomationData.RevitApp);
                e.Succeeded = true;
            }
            catch (Exception ex) { Console.WriteLine("ERRO: " + ex); e.Succeeded = false; }
        };
        return ExternalDBApplicationResult.Succeeded;
    }

    public ExternalDBApplicationResult OnShutdown(ControlledApplication app) => ExternalDBApplicationResult.Succeeded;

    static void Export(Document doc)
    {
        var m = new Exporter(doc).Run();
        using (var gz = new GZipStream(File.Create(ModelFile), CompressionLevel.Fastest))
            JsonSerializer.Serialize(gz, m, Json.Options);
        Console.WriteLine($"Exportados {m.Elements.Count} elementos, {m.Types.Count} tipos.");
    }

    static void Import(Application app)
    {
        ModelData m;
        using (var gz = new GZipStream(File.OpenRead(ModelFile), CompressionMode.Decompress))
            m = JsonSerializer.Deserialize<ModelData>(gz, Json.Options);
        var doc = app.NewProjectDocument(UnitSystem.Metric);
        var imp = new Importer(doc, m, Directory.GetCurrentDirectory());
        File.WriteAllText("relatorio.txt", imp.Run(), Encoding.UTF8);
        doc.SaveAs(Path.GetFullPath("result.rvt"), new SaveAsOptions { OverwriteExistingFile = true });
        Console.WriteLine(imp.Summary);
    }
}
