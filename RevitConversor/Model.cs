using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.DB;

namespace RevitConversor;

// Tudo em unidades internas do Revit (pés, radianos) e coordenadas internas do modelo.
public class ModelData
{
    public string Source;
    public Dictionary<string, UnitData> Units = new();   // spec TypeId -> unidade
    public List<ParamData> ProjectInfo = new();
    public List<MaterialData> Materials = new();
    public List<LevelData> Levels = new();
    public List<GridData> Grids = new();
    public Dictionary<long, TypeData> Types = new();
    public List<ElemData> Elements = new();
    public Dictionary<string, int> NotConverted = new(); // categoria -> quantidade
}

public class UnitData { public string Unit; public double Accuracy; }
public class MaterialData { public string Name; public byte[] Color; public int Transparency, Shininess, Smoothness; }
public class LevelData { public long Id; public string Name; public double Elevation; }
public class GridData { public string Name; public double[] Curve; }
public class LayerData { public int Function; public double Width; public string Material; }

public class TypeData
{
    public string Class, Kind, Family, Name;
    public List<LayerData> Layers;
    public int CoreFirst = -1, CoreLast = -1;
    public List<ParamData> Params;
}

public class ElemData
{
    public long Id, TypeId = -1, LevelId = -1, HostId = -1;
    public string Kind, Cat, CatName, Structural;
    public double[] Curve, Point, Facing, Hand, Rect;
    public List<List<double[]>> Loops;
    public List<double?> Slopes;
    public bool FlipF, FlipH;
    public List<ParamData> Params;
    public List<MeshChunk> Mesh;
}

public class ParamData
{
    public string Name, BuiltIn, Guid, S, Mat;
    public int? I;
    public double? D;
    public long? Level;
}

public class MeshChunk { public string Mat; public double[] V; public int[] T; }

static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

static class Geo
{
    public static double[] F(params XYZ[] ps) => ps.SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
    public static XYZ X(double[] a, int i) => new(a[i], a[i + 1], a[i + 2]);

    // Linha = 6 números, arco = 9 (início, fim, ponto do meio). Outras curvas viram segmentos.
    public static IEnumerable<double[]> Cs(Curve c)
    {
        if (c is Line l) { yield return F(l.GetEndPoint(0), l.GetEndPoint(1)); yield break; }
        if (c is Arc a && a.IsBound) { yield return F(a.GetEndPoint(0), a.GetEndPoint(1), a.Evaluate(0.5, true)); yield break; }
        var pts = c.Tessellate();
        for (int i = 0; i + 1 < pts.Count; i++) yield return F(pts[i], pts[i + 1]);
    }

    public static Curve ToCurve(double[] p) =>
        p.Length == 6 ? Line.CreateBound(X(p, 0), X(p, 3)) : Arc.Create(X(p, 0), X(p, 3), X(p, 6));
}
