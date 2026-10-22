using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using static RevitConversor.Geo;

namespace RevitConversor;

class Importer
{
    static readonly string[] Order = { "Wall", "Floor", "Ceiling", "Roof", "Opening", "Instance", "Shape", "Room" };

    readonly Document doc;
    readonly ModelData m;
    readonly string dir;
    readonly Dictionary<long, ElementId> levels = new(), elems = new(), types = new();
    readonly Dictionary<string, ElementId> mats = new();
    readonly Dictionary<string, int[]> stats = new(); // categoria -> [origem, nativo, sem parametria, falhou]
    readonly List<string> notes = new();
    Dictionary<string, string> rfa;
    public string Summary;

    public Importer(Document doc, ModelData m, string dir) { this.doc = doc; this.m = m; this.dir = dir; }

    public string Run()
    {
        using var group = new TransactionGroup(doc, "Importar modelo convertido");
        group.Start();

        Note("Unidades e informações", Tx("Unidades e informações", () =>
        {
            var u = doc.GetUnits();
            foreach (var kv in m.Units)
                try { u.SetFormatOptions(new ForgeTypeId(kv.Key), new FormatOptions(new ForgeTypeId(kv.Value.Unit)) { Accuracy = kv.Value.Accuracy }); }
                catch { }
            doc.SetUnits(u);
            SetParams(doc.ProjectInformation, m.ProjectInfo);
        }));

        foreach (var mat in Of<Material>()) mats.TryAdd(mat.Name, mat.Id);
        Tx("Materiais", () =>
        {
            foreach (var d in m.Materials.Where(d => !mats.ContainsKey(d.Name)))
                try
                {
                    var mat = (Material)doc.GetElement(Material.Create(doc, d.Name));
                    if (d.Color != null) mat.Color = new Color(d.Color[0], d.Color[1], d.Color[2]);
                    mat.Transparency = d.Transparency; mat.Shininess = d.Shininess; mat.Smoothness = d.Smoothness;
                    mats[d.Name] = mat.Id;
                }
                catch (Exception ex) { notes.Add($"Material {d.Name}: {ex.Message}"); }
        });

        Levels();

        foreach (var g in m.Grids)
            Note("Eixo " + g.Name, Tx("Eixo", () =>
            {
                var c = ToCurve(g.Curve);
                var grid = c is Line l ? Grid.Create(doc, l) : Grid.Create(doc, (Arc)c);
                grid.Name = g.Name;
            }));

        foreach (var d in m.Elements.OrderBy(d => Array.IndexOf(Order, d.Kind))) Element(d);

        Views();
        group.Assimilate();
        return Report();
    }

    void Levels()
    {
        var existing = Of<Level>().OrderBy(l => l.ProjectElevation).ToList();
        Note("Níveis", Tx("Níveis", () =>
        {
            for (int i = 0; i < existing.Count; i++) existing[i].Name = "conv_tmp_" + i;
            var src = m.Levels.OrderBy(l => l.Elevation).ToList();
            for (int i = 0; i < src.Count; i++)
            {
                var lvl = i < existing.Count ? existing[i] : Level.Create(doc, src[i].Elevation);
                doc.Regenerate();
                lvl.Elevation += src[i].Elevation - lvl.ProjectElevation;
                try { lvl.Name = src[i].Name; } catch (Exception ex) { notes.Add($"Nível {src[i].Name}: {ex.Message}"); }
                levels[src[i].Id] = lvl.Id;
            }
        }));
        // Níveis do template que sobraram (a vista ativa pode impedir a remoção de um deles).
        foreach (var l in existing.Where(l => !levels.ContainsValue(l.Id)))
            Note("Remover nível do template " + l.Name, Tx("Remover nível", () => doc.Delete(l.Id)));
    }

    void Element(ElemData d)
    {
        var st = stats.TryGetValue(d.CatName, out var s) ? s : stats[d.CatName] = new int[4];
        st[0]++;

        // Tipos são resolvidos em transação própria para não sumirem se o elemento falhar.
        var tid = d.Kind is "Shape" or "Room" or "Opening" ? null : Type(d.TypeId);
        ElementId id = null;
        var err = d.Kind == "Shape" ? "sem equivalente nativo" : Tx(d.Kind, () => id = Create(d, tid));
        if (err == null)
        {
            elems[d.Id] = id;
            st[1]++;
            Note($"{Label(d)} (id {d.Id}) parâmetros", Tx("Parâmetros", () => { SetParams(doc.GetElement(id), d.Params); After(d, id); }));
            return;
        }
        if (d.Mesh != null && Tx("Forma", () => id = Shape(d)) == null)
        {
            elems[d.Id] = id;
            st[2]++;
            if (d.Kind != "Shape") notes.Add($"{d.CatName} {Label(d)} (id {d.Id}): {err} → criado sem parametria");
            return;
        }
        st[3]++;
        notes.Add($"FALHOU {d.CatName} {Label(d)} (id {d.Id}): {err}");
    }

    ElementId Create(ElemData d, ElementId tid)
    {
        var lvl = Lvl(d.LevelId);
        switch (d.Kind)
        {
            case "Wall":
                Need(tid, "tipo de parede"); Need(lvl, "nível");
                return (d.Loops != null
                    ? Wall.Create(doc, d.Loops.SelectMany(l => l).Select(ToCurve).ToList(), tid, lvl, false)
                    : Wall.Create(doc, ToCurve(d.Curve), tid, lvl, 10, 0, d.FlipF, false)).Id;
            case "Floor":
                return Floor.Create(doc, CurveLoops(d), Need(tid, "tipo de piso"), Need(lvl, "nível")).Id;
            case "Ceiling":
                return Ceiling.Create(doc, CurveLoops(d), Need(tid, "tipo de forro"), Need(lvl, "nível")).Id;
            case "Roof":
            {
                var ca = new CurveArray();
                foreach (var c in d.Loops.SelectMany(l => l)) ca.Append(ToCurve(c));
                var roof = doc.Create.NewFootPrintRoof(ca, (Level)doc.GetElement(Need(lvl, "nível")),
                    (RoofType)doc.GetElement(Need(tid, "tipo de telhado")), out ModelCurveArray edges);
                for (int i = 0; i < edges.Size && i < d.Slopes.Count; i++)
                {
                    var mc = edges.get_Item(i);
                    roof.set_DefinesSlope(mc, d.Slopes[i] != null);
                    if (d.Slopes[i] is double slope) roof.set_SlopeAngle(mc, slope);
                }
                return roof.Id;
            }
            case "Opening":
            {
                var host = doc.GetElement(Need(Host(d), "hospedeiro"));
                if (d.Rect != null) return doc.Create.NewOpening((Wall)host, X(d.Rect, 0), X(d.Rect, 3)).Id;
                var ca = new CurveArray();
                foreach (var c in d.Loops[0]) ca.Append(ToCurve(c));
                return doc.Create.NewOpening(host, ca, true).Id;
            }
            case "Room":
            {
                var p = X(d.Point, 0);
                return doc.Create.NewRoom((Level)doc.GetElement(Need(lvl, "nível")), new UV(p.X, p.Y)).Id;
            }
            case "Instance":
                return Instance(d, (FamilySymbol)doc.GetElement(Need(tid, $"família não disponível no Revit {doc.Application.VersionNumber}")));
        }
        throw new InvalidOperationException("tipo de elemento desconhecido");
    }

    ElementId Instance(ElemData d, FamilySymbol sym)
    {
        var lvl = Lvl(d.LevelId) is { } li ? (Level)doc.GetElement(li) : null;
        var host = d.HostId >= 0 ? doc.GetElement(Need(Host(d), "hospedeiro")) : null;
        var st = Enum.TryParse<StructuralType>(d.Structural, out var s) ? s : StructuralType.NonStructural;
        FamilyInstance fi;
        if (d.Curve != null)
            fi = doc.Create.NewFamilyInstance(ToCurve(d.Curve), sym, lvl ?? throw new InvalidOperationException("nível não encontrado"), st);
        else if (d.Point == null)
            throw new InvalidOperationException("sem localização");
        else
        {
            var p = X(d.Point, 0);
            fi = host != null
                ? (lvl != null ? doc.Create.NewFamilyInstance(p, sym, host, lvl, st) : doc.Create.NewFamilyInstance(p, sym, host, st))
                : (lvl != null ? doc.Create.NewFamilyInstance(p, sym, lvl, st) : doc.Create.NewFamilyInstance(p, sym, st));
        }
        doc.Regenerate();
        if (host != null)
        {
            if (fi.FacingFlipped != d.FlipF && fi.CanFlipFacing) fi.flipFacing();
            if (fi.HandFlipped != d.FlipH && fi.CanFlipHand) fi.flipHand();
        }
        else if (d.Curve == null && fi.Location is LocationPoint lp)
        {
            // Gira até a frente coincidir com a original; se a mão ficar invertida, espelha.
            var want = X(d.Facing, 0);
            var have = fi.FacingOrientation;
            var ang = Math.Atan2(have.CrossProduct(want).Z, have.DotProduct(want));
            if (Math.Abs(ang) > 1e-9) ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateUnbound(lp.Point, XYZ.BasisZ), ang);
            doc.Regenerate();
            if (!fi.HandOrientation.IsAlmostEqualTo(X(d.Hand, 0)))
                ElementTransformUtils.MirrorElements(doc, new[] { fi.Id }, Plane.CreateByNormalAndOrigin(fi.HandOrientation, lp.Point), false);
        }
        return fi.Id;
    }

    // Depois dos parâmetros: a linha de localização da parede pode ter mudado e deslocado a curva.
    void After(ElemData d, ElementId id)
    {
        if (doc.GetElement(id) is not Wall w || d.Curve == null) return;
        ((LocationCurve)w.Location).Curve = ToCurve(d.Curve);
        if (w.Flipped != d.FlipF) w.Flip();
    }

    ElementId Shape(ElemData d)
    {
        var b = new TessellatedShapeBuilder();
        foreach (var c in d.Mesh)
        {
            var mat = c.Mat != null && mats.TryGetValue(c.Mat, out var mi) ? mi : ElementId.InvalidElementId;
            b.OpenConnectedFaceSet(false);
            for (int i = 0; i + 2 < c.T.Length; i += 3)
            {
                var f = new TessellatedFace(new List<XYZ> { X(c.V, 3 * c.T[i]), X(c.V, 3 * c.T[i + 1]), X(c.V, 3 * c.T[i + 2]) }, mat);
                if (b.DoesFaceHaveEnoughLoopsAndVertices(f)) b.AddFace(f);
            }
            b.CloseConnectedFaceSet();
        }
        b.Target = TessellatedShapeBuilderTarget.AnyGeometry;
        b.Fallback = TessellatedShapeBuilderFallback.Mesh;
        b.Build();

        var cat = Enum.TryParse<BuiltInCategory>(d.Cat, out var bic) && DirectShape.IsValidCategoryId(new ElementId(bic), doc)
            ? new ElementId(bic) : new ElementId(BuiltInCategory.OST_GenericModel);
        var ds = DirectShape.CreateElement(doc, cat);
        ds.ApplicationId = "RevitConversor";
        ds.ApplicationDataId = d.Id.ToString();
        ds.SetShape(b.GetBuildResult().GetGeometricalObjects());
        ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"Convertido sem parametria: {Label(d)} (id {d.Id})");
        return ds.Id;
    }

    ElementId Type(long src)
    {
        if (src < 0) return null;
        if (types.TryGetValue(src, out var id)) return id;
        if (!m.Types.TryGetValue(src, out var t)) return types[src] = null;
        ElementId found = null;
        var err = Tx("Tipo " + t.Name, () => found = FindOrMakeType(t));
        if (err != null) notes.Add($"Tipo {t.Family} : {t.Name}: {err}");
        return types[src] = err == null ? found : null;
    }

    ElementId FindOrMakeType(TypeData t)
    {
        if (t.Class == nameof(FamilySymbol))
        {
            FamilySymbol Find() => Of<FamilySymbol>().FirstOrDefault(s => s.FamilyName == t.Family && s.Name == t.Name);
            var sym = Find();
            if (sym == null && !Of<Family>().Any(f => f.Name == t.Family) && Library().TryGetValue(t.Family, out var path))
            {
                try { doc.LoadFamily(path, out Family _); } catch { } // .rfa de versão mais nova não carrega
                sym = Find();
            }
            sym ??= Of<FamilySymbol>().FirstOrDefault(s => s.FamilyName == t.Family)?.Duplicate(t.Name) as FamilySymbol;
            if (sym == null) return null;
            if (!sym.IsActive) sym.Activate();
            SetParams(sym, t.Params);
            return sym.Id;
        }

        var same = new FilteredElementCollector(doc).WhereElementIsElementType().OfType<ElementType>()
            .Where(x => x.GetType().Name == t.Class && (t.Kind == null || (x as WallType)?.Kind.ToString() == t.Kind)).ToList();
        if (same.Count == 0) return null;
        var et = same.FirstOrDefault(x => x.Name == t.Name) ?? same[0].Duplicate(t.Name);
        if (t.Layers?.Count > 0 && et is HostObjAttributes h)
            try { h.SetCompoundStructure(Structure(t)); }
            catch (Exception ex) { notes.Add($"Camadas do tipo {t.Name}: {ex.Message}"); }
        SetParams(et, t.Params);
        return et.Id;
    }

    CompoundStructure Structure(TypeData t)
    {
        var layers = t.Layers.Select(l => new CompoundStructureLayer(l.Width, (MaterialFunctionAssignment)l.Function,
            l.Material != null && mats.TryGetValue(l.Material, out var mi) ? mi : ElementId.InvalidElementId)).ToList();
        var cs = CompoundStructure.CreateSimpleCompoundStructure(layers);
        if (t.CoreFirst >= 0)
        {
            cs.SetNumberOfShellLayers(ShellLayerType.Exterior, t.CoreFirst);
            cs.SetNumberOfShellLayers(ShellLayerType.Interior, layers.Count - 1 - t.CoreLast);
        }
        return cs;
    }

    // Famílias: pasta "familias" ao lado do .conv.json e bibliotecas configuradas no Revit.
    Dictionary<string, string> Library()
    {
        if (rfa != null) return rfa;
        rfa = new(StringComparer.OrdinalIgnoreCase);
        var roots = doc.Application.GetLibraryPaths().Values.Prepend(Path.Combine(dir, "familias"));
        foreach (var root in roots.Where(Directory.Exists))
            try { foreach (var f in Directory.EnumerateFiles(root, "*.rfa", SearchOption.AllDirectories)) rfa.TryAdd(Path.GetFileNameWithoutExtension(f), f); }
            catch { }
        return rfa;
    }

    void SetParams(Element e, List<ParamData> ps)
    {
        if (e == null || ps == null) return;
        foreach (var p in ps)
        {
            var par = p.BuiltIn != null && Enum.TryParse<BuiltInParameter>(p.BuiltIn, out var bip) ? e.get_Parameter(bip)
                : p.Guid != null ? e.get_Parameter(Guid.Parse(p.Guid))
                : e.LookupParameter(p.Name);
            if (par == null || par.IsReadOnly) continue;
            try
            {
                switch (par.StorageType)
                {
                    case StorageType.String when p.S != null && par.AsString() != p.S: par.Set(p.S); break;
                    case StorageType.Integer when p.I is int i && par.AsInteger() != i: par.Set(i); break;
                    case StorageType.Double when p.D is double v && Math.Abs(par.AsDouble() - v) > 1e-9: par.Set(v); break;
                    case StorageType.ElementId when p.Level is long l && levels.TryGetValue(l, out var lid) && par.AsElementId() != lid: par.Set(lid); break;
                    case StorageType.ElementId when p.Mat != null && mats.TryGetValue(p.Mat, out var mid): par.Set(mid); break;
                }
            }
            catch { } // valor recusado pela versão de destino: mantém o padrão
        }
    }

    void Views()
    {
        var vfts = Of<ViewFamilyType>().ToList();
        var plans = Of<ViewPlan>().Where(v => !v.IsTemplate).ToList();
        foreach (var li in levels.Values)
            foreach (var (family, type) in new[] { (ViewFamily.FloorPlan, ViewType.FloorPlan), (ViewFamily.CeilingPlan, ViewType.CeilingPlan) })
                Note("Vista do nível", Tx("Vista", () =>
                {
                    var v = plans.FirstOrDefault(p => p.GenLevel?.Id == li && p.ViewType == type);
                    if (v == null && vfts.FirstOrDefault(x => x.ViewFamily == family) is { } vft) v = ViewPlan.Create(doc, vft.Id, li);
                    if (v != null) try { v.Name = doc.GetElement(li).Name; } catch { }
                }));
    }

    string Report()
    {
        var sb = new StringBuilder($"Conversor — relatório\nOrigem: {m.Source}\nDestino: {doc.Application.VersionName}\n\n");
        sb.AppendLine("CATEGORIA | ORIGEM | NATIVO (editável) | SEM PARAMETRIA (forma) | FALHOU");
        foreach (var kv in stats.OrderByDescending(kv => kv.Value[0]))
            sb.AppendLine($"{kv.Key} | {kv.Value[0]} | {kv.Value[1]} | {kv.Value[2]} | {kv.Value[3]}");
        sb.AppendLine($"\nNíveis: {m.Levels.Count}   Eixos: {m.Grids.Count}   Materiais: {m.Materials.Count}");
        sb.AppendLine("\nNÃO RECONSTRUÍDO (fora do escopo)");
        foreach (var kv in m.NotConverted.OrderByDescending(kv => kv.Value)) sb.AppendLine($"  {kv.Key}: {kv.Value}");
        sb.AppendLine("  Sempre: folhas, cortes, vistas de detalhe, cotas, tags, tabelas, filtros, grupos, opções de projeto,");
        sb.AppendLine("  vínculos, coordenadas compartilhadas, parâmetros de projeto não compartilhados e IDs dos elementos.");
        sb.AppendLine("\nOCORRÊNCIAS");
        foreach (var n in notes) sb.AppendLine("  " + n);

        int[] tot = new int[4];
        foreach (var v in stats.Values) for (int i = 0; i < 4; i++) tot[i] += v[i];
        Summary = $"{tot[0]} elementos: {tot[1]} nativos, {tot[2]} sem parametria, {tot[3]} falharam.";
        return sb.ToString();
    }

    // Uma transação por operação: um erro do Revit desfaz só aquela operação.
    string Tx(string name, Action a)
    {
        using var t = new Transaction(doc, name);
        var quiet = new Quiet();
        var o = t.GetFailureHandlingOptions();
        o.SetFailuresPreprocessor(quiet);
        o.SetClearAfterRollback(true);
        t.SetFailureHandlingOptions(o);
        t.Start();
        try
        {
            a();
            return t.Commit() == TransactionStatus.Committed ? null : quiet.Error ?? "o Revit desfez a operação";
        }
        catch (Exception ex)
        {
            if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
            return ex.Message;
        }
    }

    void Note(string what, string err) { if (err != null) notes.Add($"{what}: {err}"); }

    ElementId Lvl(long src) => levels.TryGetValue(src, out var id) ? id : null;
    ElementId Host(ElemData d) => elems.TryGetValue(d.HostId, out var id) ? id : null;
    static ElementId Need(ElementId id, string what) => id ?? throw new InvalidOperationException(what + " não encontrado");
    List<CurveLoop> CurveLoops(ElemData d) => d.Loops.Select(l => CurveLoop.Create(l.Select(ToCurve).ToList())).ToList();
    string Label(ElemData d) => m.Types.TryGetValue(d.TypeId, out var t) ? $"{t.Family} : {t.Name}" : d.CatName;
    IEnumerable<T> Of<T>() where T : Element => new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>();
}

// Avisos são descartados; erros fazem a transação ser desfeita (e o elemento vira forma ou falha).
class Quiet : IFailuresPreprocessor
{
    public string Error;

    public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
    {
        foreach (var f in a.GetFailureMessages())
        {
            if (f.GetSeverity() == FailureSeverity.Warning) { a.DeleteWarning(f); continue; }
            Error = f.GetDescriptionText();
            return FailureProcessingResult.ProceedWithRollBack;
        }
        return FailureProcessingResult.Continue;
    }
}
