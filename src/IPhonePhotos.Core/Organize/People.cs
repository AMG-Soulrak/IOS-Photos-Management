using System.Collections.Concurrent;
using System.Text.Json;
using FaceAiSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using IPhonePhotos.Core.Manifest;

namespace IPhonePhotos.Core.Organize;

/// <summary>Una cara encontrada en una foto: dónde está (en proporción de la foto) y su «huella» numérica para compararla con otras.</summary>
public sealed record FaceRecord(int Id, string PhotoKey, float X, float Y, float W, float H, float Confidence, float[] Embedding);

/// <summary>Las caras que se parecen (se supone que son la misma persona), con el nombre que le haya puesto el usuario, si lo hay.</summary>
public sealed class PersonGroup
{
    public int Number { get; set; }

    /// <summary>Nombre CONFIRMADO por el usuario (nunca se pone solo).</summary>
    public string? Name { get; set; }

    /// <summary>Nombre que la app cree que es (se parece a una persona ya nombrada). Solo se propone: el usuario lo confirma.</summary>
    public string? SuggestedName { get; set; }
    public float[] Centroid { get; set; } = Array.Empty<float>();
    public List<FaceRecord> Faces { get; } = new();
    public string Display => Name ?? $"Persona {Number}";
    /// <summary>La cara más grande y segura: la que se enseña como retrato.</summary>
    public FaceRecord Best => Faces.OrderByDescending(f => f.Confidence * f.W * f.H).First();
    public int PhotoCount => Faces.Select(f => f.PhotoKey).Distinct().Count();
}

/// <summary>
/// Reconocimiento de personas, todo en el PC: SCRFD encuentra las caras y ArcFace calcula una huella de 512 números de cada una.
/// Las caras con huellas parecidas se agrupan como una misma persona. El análisis se puede interrumpir: guarda el avance.
/// DATOS SENSIBLES (biometría, art. 9 RGPD: puede haber fotos de terceros o de trabajo): desde v1.5 se guardan FUERA de la carpeta
/// de fotos, en %LOCALAPPDATA%\IPhonePhotos\people\&lt;huella de la carpeta&gt;, para que no viajen con las fotos a la nube o a un USB;
/// solo se analiza tras activarlo el usuario (<see cref="Enable"/>) y <see cref="DeleteAll"/> lo borra todo.
/// shortcut: el agrupado es automático y no perfecto: puede separar a una persona en dos grupos (poniendo el mismo nombre a los
/// dos se unen) o mezclar a dos parecidas; niños pequeños y gemelos son lo más difícil. Con muchas fotos habría que usar un
/// agrupado más fino (p. ej. por vecinos más cercanos), pero para unos miles de caras esto basta.
/// </summary>
public static class PeopleService
{
    /// <summary>Parecido mínimo (coseno) para añadir una cara a un grupo; en la guía del modelo, ≥ 0,42 es «la misma persona».</summary>
    public const float JoinThreshold = 0.45f;
    private const float MergeThreshold = 0.55f;
    private const float NameThreshold = 0.50f;
    /// <summary>Huella prácticamente igual a la que el usuario nombró (el mismo grupo, quizá con alguna cara más).</summary>
    private const float ConfirmedThreshold = 0.90f;
    private const int MinFacePixels = 40;

    /// <summary>Carpeta base de los datos de Personas (fuera de la carpeta de fotos). Se puede cambiar en pruebas.</summary>
    public static string DataRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPhonePhotos", "people");

    /// <summary>Datos de una biblioteca: una subcarpeta por carpeta de fotos (huella de su ruta; no guarda la ruta en claro).</summary>
    public static string Folder(string root)
    {
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\').ToLowerInvariant())))[..16].ToLowerInvariant();
        return Path.Combine(DataRoot, id);
    }

    /// <summary>Dónde estaban los datos hasta v1.4: dentro de la carpeta de fotos (viajaban con ellas).</summary>
    public static string LegacyFolder(string root) => Path.Combine(ManifestStore.MetaFolder(root), "people");

    public static string CropPath(string root, int faceId) => Path.Combine(Folder(root), "crops", faceId + ".jpg");
    private static string FacesFile(string root) => Path.Combine(Folder(root), "faces.bin");
    private static string NamesFile(string root) => Path.Combine(Folder(root), "names.json");
    private static string ConsentFile => Path.Combine(DataRoot, "activado.txt");

    // ------------------------------------------------------------------ Activación voluntaria

    /// <summary>Texto del aviso que el usuario debe aceptar antes del primer análisis.</summary>
    public const string Notice =
        "Personas analiza las caras de tus fotos para agruparlas por persona. Las huellas faciales son datos biométricos " +
        "(datos sensibles según el RGPD), también los de otras personas que salgan en tus fotos: úsalo solo con fotos en las que " +
        "tengas derecho a hacerlo y, si hay terceros o fotos de trabajo, con su consentimiento.\n\n" +
        "Todo se hace en este PC: no se envía nada a internet. Los datos se guardan fuera de la carpeta de fotos, en " +
        "%LOCALAPPDATA%\\IPhonePhotos\\people, y puedes borrarlos cuando quieras con «Borrar todos los datos de Personas». " +
        "La app nunca pone nombres sola: solo te propone y tú confirmas.";

    public static bool IsEnabled => File.Exists(ConsentFile);

    public static void Enable()
    {
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(ConsentFile, $"Personas activado por el usuario el {DateTime.Now:yyyy-MM-dd HH:mm}.\r\n");
    }

    public static void Disable()
    {
        if (File.Exists(ConsentFile)) File.Delete(ConsentFile);
    }

    // ------------------------------------------------------------------ Análisis

    /// <summary>Versión del formato de faces.bin: 2 añade en la cabecera el modelo con el que se calcularon las huellas.</summary>
    private const int FormatVersion = 2;

    public sealed class Store
    {
        public HashSet<string> Done { get; } = new();
        public List<FaceRecord> Faces { get; } = new();
        public int NextId => Faces.Count == 0 ? 1 : Faces.Max(f => f.Id) + 1;

        /// <summary>Modelo con el que se calcularon las huellas (nombre y tamaño del .onnx). Null en datos de v1.4.</summary>
        public string? ModelId { get; set; }
    }

    /// <summary>Mueve los datos de v1.4 (dentro de la carpeta de fotos) a su sitio nuevo, fuera de ella. No deja copia dentro.</summary>
    private static void MigrateLegacy(string root)
    {
        var old = LegacyFolder(root);
        if (!Directory.Exists(old)) return;
        var target = Folder(root);
        if (!Directory.Exists(target))
            foreach (var f in Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(target, Path.GetRelativePath(old, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to, overwrite: false);
            }
        Directory.Delete(old, recursive: true);
    }

    public static Store Load(string root)
    {
        var store = new Store();
        try
        {
            MigrateLegacy(root);
            if (!File.Exists(FacesFile(root))) return store;
            using var r = new BinaryReader(File.OpenRead(FacesFile(root)));
            var version = r.ReadInt32();
            if (version is not (1 or FormatVersion)) return store;
            if (version == FormatVersion) store.ModelId = r.ReadString();
            for (var n = r.ReadInt32(); n > 0; n--) store.Done.Add(r.ReadString());
            for (var n = r.ReadInt32(); n > 0; n--)
            {
                var id = r.ReadInt32();
                var key = r.ReadString();
                float x = r.ReadSingle(), y = r.ReadSingle(), w = r.ReadSingle(), h = r.ReadSingle(), c = r.ReadSingle();
                var emb = new float[r.ReadInt32()];
                for (var i = 0; i < emb.Length; i++) emb[i] = r.ReadSingle();
                store.Faces.Add(new FaceRecord(id, key, x, y, w, h, c, emb));
            }
        }
        catch { /* archivo cortado o ilegible: se empieza de nuevo con lo que se haya leído */ }
        return store;
    }

    private static void Save(string root, Store store)
    {
        Directory.CreateDirectory(Folder(root));
        var tmp = FacesFile(root) + ".tmp";
        using (var w = new BinaryWriter(File.Create(tmp)))
        {
            w.Write(FormatVersion);
            w.Write(store.ModelId ?? "");
            w.Write(store.Done.Count);
            foreach (var k in store.Done) w.Write(k);
            w.Write(store.Faces.Count);
            foreach (var f in store.Faces)
            {
                w.Write(f.Id); w.Write(f.PhotoKey);
                w.Write(f.X); w.Write(f.Y); w.Write(f.W); w.Write(f.H); w.Write(f.Confidence);
                w.Write(f.Embedding.Length);
                foreach (var v in f.Embedding) w.Write(v);
            }
        }
        File.Move(tmp, FacesFile(root), overwrite: true);
    }

    /// <summary>True si están los modelos (detector y reconocimiento) en <see cref="FaceModels.Folder"/>.</summary>
    public static bool ModelsAvailable() => Indexer.TryCreate() is { } ix && Dispose(ix);
    private static bool Dispose(IDisposable d) { d.Dispose(); return true; }

    /// <summary>Analiza las fotos que aún no se han mirado. Se puede cortar con el token: lo ya hecho queda guardado.</summary>
    public static Store Index(string root, IReadOnlyList<AlbumItem> items, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        if (!IsEnabled)
            throw new InvalidOperationException("Personas está desactivado: actívalo primero (aviso sobre datos biométricos).");
        var store = Load(root);
        using var indexer = Indexer.TryCreate() ?? throw new InvalidOperationException(FaceModels.MissingMessage);
        if (store.ModelId != null && store.ModelId != indexer.ModelId)
        {
            // Huellas de otro modelo no se pueden comparar con las nuevas: se empieza de cero.
            DeleteAll(root);
            store = new Store();
        }
        store.ModelId = indexer.ModelId; // los datos de v1.4 no lo anotaban: se asume el modelo actual (el único que había)
        var todo = items.Where(i => !store.Done.Contains(Album.Key(i.Media))).ToList();
        if (todo.Count == 0) return store;

        Directory.CreateDirectory(Path.Combine(Folder(root), "crops"));
        var nextId = store.NextId;
        var done = 0;
        var lastSave = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, item =>
            {
                var key = Album.Key(item.Media);
                var faces = indexer.Analyze(Path.Combine(root, item.Media.RelativePath), key, () => Interlocked.Increment(ref nextId) - 1, CropPath, root);
                if (faces == null) return; // no se pudo leer: se reintentará la próxima vez
                lock (store)
                {
                    store.Faces.AddRange(faces);
                    store.Done.Add(key);
                    if (++done % 25 == 0) progress?.Report((done, todo.Count));
                    // faces.bin se reescribe entero: como mucho cada 30 s (antes cada 100 fotos), y siempre al terminar o parar.
                    if (lastSave.Elapsed > TimeSpan.FromSeconds(30)) { Save(root, store); lastSave.Restart(); }
                }
            });
        }
        finally
        {
            lock (store) Save(root, store); // también si se cortó: no se pierde lo analizado
        }
        progress?.Report((done, todo.Count));
        return store;
    }

    private sealed class Indexer : IDisposable
    {
        private readonly ScrfdDetector _detector;
        private readonly ArcFaceEmbeddingsGenerator _arcface;
        private readonly object _gate = new(); // los modelos no están pensados para usarse a la vez desde varios hilos

        private Indexer(ScrfdDetector d, ArcFaceEmbeddingsGenerator a, string modelId) { _detector = d; _arcface = a; ModelId = modelId; }

        /// <summary>Identifica el modelo de huellas (nombre y tamaño del archivo) para no mezclar huellas de modelos distintos.</summary>
        public string ModelId { get; }

        public static Indexer? TryCreate()
        {
            var scrfd = FaceModels.Find("scrfd_2.5g_kps.onnx");
            var arc = FaceModels.FindContaining("arcface");
            if (scrfd == null || arc == null) return null;
            try
            {
                return new Indexer(new ScrfdDetector(new ScrfdDetectorOptions { ModelPath = scrfd }),
                                   new ArcFaceEmbeddingsGenerator(new ArcFaceEmbeddingsGeneratorOptions { ModelPath = arc }),
                                   $"{Path.GetFileName(arc)}|{new FileInfo(arc).Length}");
            }
            catch { return null; }
        }

        public List<FaceRecord>? Analyze(string path, string key, Func<int> newId, Func<string, int, string> cropPath, string root)
        {
            try
            {
                using var image = FaceAnalyzer.Load(path);
                var result = new List<FaceRecord>();
                lock (_gate)
                {
                    foreach (var face in _detector.DetectFaces(image))
                    {
                        if (face.Landmarks == null || face.Box.Width < MinFacePixels || face.Box.Height < MinFacePixels) continue;
                        using var aligned = image.Clone();
                        ArcFaceEmbeddingsGenerator.AlignFaceUsingLandmarks(aligned, face.Landmarks);
                        var embedding = Normalize(_arcface.GenerateEmbedding(aligned));
                        var id = newId();
                        SaveCrop(image, face.Box, cropPath(root, id));
                        result.Add(new FaceRecord(id, key, face.Box.X / image.Width, face.Box.Y / image.Height,
                            face.Box.Width / image.Width, face.Box.Height / image.Height, face.Confidence ?? 0, embedding));
                    }
                }
                return result;
            }
            catch
            {
                return null;
            }
        }

        // Retrato cuadrado de la cara con algo de margen, para la lista de personas.
        private static void SaveCrop(Image<Rgb24> image, RectangleF box, string path)
        {
            var side = (int)Math.Min(Math.Max(box.Width, box.Height) * 1.5f, Math.Min(image.Width, image.Height));
            var x = (int)Math.Clamp(box.X + box.Width / 2 - side / 2f, 0, image.Width - side);
            var y = (int)Math.Clamp(box.Y + box.Height / 2 - side / 2f, 0, image.Height - side);
            using var crop = image.Clone(c => c.Crop(new Rectangle(x, y, side, side)).Resize(112, 112));
            crop.Save(path, new JpegEncoder { Quality = 85 });
        }

        public void Dispose()
        {
            (_detector as IDisposable)?.Dispose();
            (_arcface as IDisposable)?.Dispose();
        }
    }

    private static float[] Normalize(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return norm < 1e-9f ? v : v.Select(x => x / norm).ToArray();
    }

    private static float Dot(float[] a, float[] b)
    {
        var s = 0f;
        for (var i = 0; i < a.Length && i < b.Length; i++) s += a[i] * b[i];
        return s;
    }

    // ------------------------------------------------------------------ Agrupar y poner nombres

    /// <summary>Agrupa las caras por parecido y les pone el nombre que el usuario ya haya dado a esa persona. Más numerosos primero.</summary>
    public static List<PersonGroup> Group(string root, IReadOnlyList<FaceRecord> faces)
    {
        var groups = new List<PersonGroup>();
        var sums = new List<float[]>();
        foreach (var face in faces.OrderByDescending(f => f.Confidence * f.W * f.H))
        {
            var best = -1;
            var bestSim = JoinThreshold;
            for (var i = 0; i < groups.Count; i++)
            {
                var sim = Dot(groups[i].Centroid, face.Embedding);
                if (sim >= bestSim) { bestSim = sim; best = i; }
            }
            if (best < 0)
            {
                groups.Add(new PersonGroup { Centroid = face.Embedding });
                sums.Add((float[])face.Embedding.Clone());
                best = groups.Count - 1;
            }
            else
            {
                for (var k = 0; k < sums[best].Length; k++) sums[best][k] += face.Embedding[k];
                groups[best].Centroid = Normalize(sums[best]);
            }
            groups[best].Faces.Add(face);
        }

        // Grupos que en realidad son la misma persona (el reparto va por orden y puede separarla): se unen si se parecen mucho.
        for (var merged = true; merged;)
        {
            merged = false;
            for (var i = 0; i < groups.Count && !merged; i++)
                for (var j = i + 1; j < groups.Count && !merged; j++)
                    if (Dot(groups[i].Centroid, groups[j].Centroid) >= MergeThreshold)
                    {
                        groups[i].Faces.AddRange(groups[j].Faces);
                        for (var k = 0; k < sums[i].Length; k++) sums[i][k] += sums[j][k];
                        groups[i].Centroid = Normalize(sums[i]);
                        groups.RemoveAt(j);
                        sums.RemoveAt(j);
                        merged = true;
                    }
        }

        var names = LoadNames(root);
        foreach (var g in groups)
        {
            var match = names.Select(kv => (kv.Key, Sim: kv.Value.Max(c => Dot(c, g.Centroid)))).OrderByDescending(m => m.Sim).FirstOrDefault();
            if (match.Key == null) continue;
            // Nunca se pone un nombre sin confirmar: solo cuenta como nombrado el grupo que el usuario nombró (misma huella);
            // si solo se parece, se PROPONE y el usuario decide.
            if (match.Sim >= ConfirmedThreshold) g.Name = match.Key;
            else if (match.Sim >= NameThreshold) g.SuggestedName = match.Key;
        }
        var ordered = groups.OrderByDescending(g => g.Faces.Count).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Number = i + 1;
        return ordered;
    }

    private static Dictionary<string, List<float[]>> LoadNames(string root)
    {
        try
        {
            if (File.Exists(NamesFile(root)))
                return JsonSerializer.Deserialize<Dictionary<string, List<float[]>>>(File.ReadAllText(NamesFile(root))) ?? new();
        }
        catch { /* ilegible: se empieza sin nombres */ }
        return new();
    }

    /// <summary>
    /// Recuerda el nombre de una persona (el usuario lo confirma): se guarda cómo es su cara (la huella media del grupo). Si luego
    /// salen más grupos parecidos, se les PROPONE ese nombre, sin ponerlo. Dos grupos con el mismo nombre se enseñan juntos.
    /// </summary>
    public static void SetName(string root, PersonGroup group, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        var names = LoadNames(root);
        foreach (var kv in names.ToList()) // el grupo cambia de nombre: la huella no puede quedar en el nombre antiguo
            if (group.Name != null && kv.Key == group.Name) kv.Value.RemoveAll(c => Dot(c, group.Centroid) >= 0.9f);
        if (!names.TryGetValue(name, out var list)) names[name] = list = new();
        list.Add(group.Centroid);
        if (list.Count > 12) list.RemoveAt(0);
        foreach (var kv in names.Where(kv => kv.Value.Count == 0).ToList()) names.Remove(kv.Key);
        Directory.CreateDirectory(Folder(root));
        File.WriteAllText(NamesFile(root), JsonSerializer.Serialize(names));
        group.Name = name;
    }

    /// <summary>Borra todo lo aprendido de esa carpeta de fotos (caras, huellas, retratos y nombres), también los restos de v1.4.</summary>
    public static void DeleteAll(string root)
    {
        if (Directory.Exists(Folder(root))) Directory.Delete(Folder(root), recursive: true);
        if (Directory.Exists(LegacyFolder(root))) Directory.Delete(LegacyFolder(root), recursive: true);
    }
}
