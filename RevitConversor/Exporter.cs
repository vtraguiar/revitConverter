using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using static RevitConversor.Geo;

namespace RevitConversor;

class Exporter
{
    readonly Document doc;
    readonly ModelData m = new();
    readonly Options opts = new() { DetailLevel = ViewDetailLevel.Fine };

    public Exporter(Document doc) => this.doc = doc;

    public ModelData Run()
    {
        m.Source = $"{doc.Application.VersionName} — {doc.Title}";

        var units = doc.GetUnits();
        foreach (var spec in new[] { SpecTypeId.Length, SpecTypeId.Area, SpecTypeId.Volume, SpecTypeId.Angle })
        {
            var fo = units.GetFormatOptions(spec);
            m.Units[spec.TypeId] = new UnitData { Unit = fo.GetUnitTypeId().TypeId, Accuracy = fo.Accuracy };
        }
        m.ProjectInfo = Params(doc.ProjectInformation);

        foreach (var mat in Of<Material>())
            m.Materials.Add(new MaterialData
            {
                Name = mat.Name,
                Color = mat.Color.IsValid ? new[] { mat.Color.Red, mat.Color.Green, mat.Color.Blue } : null,
                Transparency = mat.Transparency, Shininess = mat.Shininess, Smoothness = mat.Smoothness
            });

        foreach (var l in Of<Level>())
            m.Levels.Add(new LevelData { Id = l.Id.Value, Name = l.Name, Elevation = l.ProjectElevation });

        foreach (var g in Of<Grid>())
            if (Cs(g.Curve).ToList() is { Count: 1 } c) m.Grids.Add(new GridData { Name = g.Name, Curve = c[0] });

        foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            Export(e);
        return m;
    }

    void Export(Element e)
    {
        var cat = e.Category;
        if (cat == null || e is Level || e is Grid) return;
        if (e is View) { Skip("Vistas e folhas"); return; }
        if (e is RevitLinkInstance || e is ImportInstance) { Skip("Vínculos e CAD importado"); return; }
        if (e.ViewSpecific || cat.CategoryType != CategoryType.Model) { Skip(cat.Name); return; }
        // Gerados automaticamente pelo hospedeiro ou pela família-mãe.
        if (e is Panel || e is Mullion || e is FamilyInstance { SuperComponent: not null } || e is Wall { IsStackedWall: true }) return;

        var d = e switch
        {
            Room r => r.Location is LocationPoint p ? new ElemData { Kind = "Room", Point = F(p.Point) } : null,
            Wall w => WallD(w),
            Floor f => Slab("Floor", f.SketchId),
            Ceiling c => Slab("Ceiling", c.SketchId),
            FootPrintRoof r => RoofD(r),
            Opening o => OpeningD(o),
            FamilyInstance fi => InstD(fi),
            _ => null
        };
        if (d?.Kind is not ("Room" or "Opening"))
        {
            var mesh = Mesh(e);
            if (mesh == null) { if (d == null) { Skip(cat.Name + " (sem geometria)"); return; } }
            d ??= new ElemData { Kind = "Shape" };
            d.Mesh = mesh;
        }

        d.Id = e.Id.Value;
        d.Cat = cat.BuiltInCategory.ToString();
        d.CatName = cat.Name;
        if (d.LevelId < 0 && e.LevelId != ElementId.InvalidElementId) d.LevelId = e.LevelId.Value;
        if (d.Kind != "Shape") d.TypeId = AddType(e.GetTypeId());
        d.Params = Params(e);
        m.Elements.Add(d);
    }

    ElemData WallD(Wall w)
    {
        var d = new ElemData { Kind = "Wall", FlipF = w.Flipped };
        if (w.SketchId != ElementId.InvalidElementId && doc.GetElement(w.SketchId) is Sketch sk)
            d.Loops = Loops(sk.Profile);  // perfil editado
        else if (w.Location is LocationCurve lc && Cs(lc.Curve).ToList() is { Count: 1 } c)
            d.Curve = c[0];
        else return null;
        return d;
    }

    ElemData Slab(string kind, ElementId sketchId) =>
        doc.GetElement(sketchId) is Sketch sk ? new ElemData { Kind = kind, Loops = Loops(sk.Profile) } : null;

    ElemData RoofD(FootPrintRoof r)
    {
        var d = new ElemData { Kind = "Roof", Loops = new(), Slopes = new() };
        foreach (ModelCurveArray loop in r.GetProfiles())
        {
            var l = new List<double[]>();
            foreach (ModelCurve mc in loop)
            {
                if (Cs(mc.GeometryCurve).ToList() is not { Count: 1 } c) return null;
                l.Add(c[0]);
                d.Slopes.Add(r.get_DefinesSlope(mc) ? r.get_SlopeAngle(mc) : null);
            }
            d.Loops.Add(l);
        }
        return d;
    }

    ElemData OpeningD(Opening o)
    {
        if (o.Host == null) return null; // shafts: fora do escopo
        var d = new ElemData { Kind = "Opening", HostId = o.Host.Id.Value };
        if (o.IsRectBoundary) d.Rect = F(o.BoundaryRect[0], o.BoundaryRect[1]);
        else d.Loops = new() { o.BoundaryCurves.Cast<Curve>().SelectMany(Cs).ToList() };
        return d;
    }

    ElemData InstD(FamilyInstance fi)
    {
        var d = new ElemData
        {
            Kind = "Instance",
            HostId = fi.Host is { } h && h is not Level ? h.Id.Value : -1,
            Structural = fi.StructuralType.ToString(),
            FlipF = fi.FacingFlipped, FlipH = fi.HandFlipped,
            Facing = F(fi.FacingOrientation), Hand = F(fi.HandOrientation)
        };
        if (fi.Location is LocationPoint lp) d.Point = F(lp.Point);
        else if (fi.Location is LocationCurve lc && Cs(lc.Curve).ToList() is { Count: 1 } c) d.Curve = c[0];
        return d;
    }

    static List<List<double[]>> Loops(CurveArrArray profile) =>
        profile.Cast<CurveArray>().Select(ca => ca.Cast<Curve>().SelectMany(Cs).ToList()).ToList();

    long AddType(ElementId id)
    {
        if (id == ElementId.InvalidElementId) return -1;
        if (m.Types.ContainsKey(id.Value) || doc.GetElement(id) is not ElementType t) return id.Value;
        var td = new TypeData { Class = t.GetType().Name, Family = t.FamilyName, Name = t.Name, Params = Params(t) };
        if (t is WallType wt) td.Kind = wt.Kind.ToString();
        if (t is HostObjAttributes h && h.GetCompoundStructure() is { } cs)
        {
            td.Layers = cs.GetLayers().Select(l => new LayerData
            {
                Function = (int)l.Function, Width = l.Width, Material = doc.GetElement(l.MaterialId)?.Name
            }).ToList();
            td.CoreFirst = cs.GetFirstCoreLayerIndex();
            td.CoreLast = cs.GetLastCoreLayerIndex();
        }
        m.Types[id.Value] = td;
        return id.Value;
    }

    // Só parâmetros graváveis; referências a outros elementos só quando são nível ou material.
    List<ParamData> Params(Element e)
    {
        var list = new List<ParamData>();
        foreach (Parameter p in e.Parameters)
        {
            if (p.IsReadOnly || !p.HasValue) continue;
            var bip = (p.Definition as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID;
            var d = new ParamData
            {
                Name = p.Definition.Name,
                BuiltIn = bip != BuiltInParameter.INVALID ? bip.ToString() : null,
                Guid = p.IsShared ? p.GUID.ToString() : null
            };
            switch (p.StorageType)
            {
                case StorageType.String: d.S = p.AsString() ?? ""; break;
                case StorageType.Integer: d.I = p.AsInteger(); break;
                case StorageType.Double: d.D = p.AsDouble(); break;
                case StorageType.ElementId:
                    var r = doc.GetElement(p.AsElementId());
                    if (r is Level) d.Level = r.Id.Value;
                    else if (r is Material) d.Mat = r.Name;
                    else continue;
                    break;
                default: continue;
            }
            list.Add(d);
        }
        return list;
    }

    // Malha triangulada por material: base para o DirectShape quando a reconstrução nativa falha.
    List<MeshChunk> Mesh(Element e)
    {
        var ge = e.get_Geometry(opts);
        if (ge == null) return null;
        var chunks = new Dictionary<string, (List<double> v, List<int> t)>();
        Collect(ge, chunks);
        return chunks.Count == 0 ? null
            : chunks.Select(kv => new MeshChunk { Mat = kv.Key, V = kv.Value.v.ToArray(), T = kv.Value.t.ToArray() }).ToList();
    }

    void Collect(GeometryElement ge, Dictionary<string, (List<double> v, List<int> t)> chunks)
    {
        foreach (var o in ge)
        {
            if (o is GeometryInstance gi) Collect(gi.GetInstanceGeometry(), chunks);
            else if (o is Solid s) foreach (Face f in s.Faces) Add(f.Triangulate(), f.MaterialElementId, chunks);
            else if (o is Mesh me) Add(me, me.MaterialElementId, chunks);
        }
    }

    void Add(Mesh me, ElementId mat, Dictionary<string, (List<double> v, List<int> t)> chunks)
    {
        if (me == null || me.NumTriangles == 0) return;
        var key = doc.GetElement(mat)?.Name ?? "";
        if (!chunks.TryGetValue(key, out var c)) chunks[key] = c = (new(), new());
        int b = c.v.Count / 3;
        foreach (var p in me.Vertices) { c.v.Add(Math.Round(p.X, 6)); c.v.Add(Math.Round(p.Y, 6)); c.v.Add(Math.Round(p.Z, 6)); }
        for (int i = 0; i < me.NumTriangles; i++)
        {
            var t = me.get_Triangle(i);
            c.t.Add(b + (int)t.get_Index(0)); c.t.Add(b + (int)t.get_Index(1)); c.t.Add(b + (int)t.get_Index(2));
        }
    }

    IEnumerable<T> Of<T>() where T : Element => new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>();

    void Skip(string key) => m.NotConverted[key] = m.NotConverted.GetValueOrDefault(key) + 1;
}
