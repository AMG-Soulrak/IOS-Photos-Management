using System.Text;
using IPhonePhotos.Core;
using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Conversion;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Organize;
using IPhonePhotos.Core.Transfer;

namespace IPhonePhotos.Cli;

internal static class Program
{
    private const string Help = """
        iphotos - copia las fotos del iPhone/iPad al PC (portable, reanudable)

        USO
          iphotos devices                          Lista los dispositivos y el estado del controlador de Apple
          iphotos probe [--report archivo.txt]     Prueba la conexión (solo lectura) y guarda un informe
          iphotos download <destino>               Descarga lo que falte (se puede cortar y repetir)
              [--adopt none|fast|deep]             Reutilizar fotos ya presentes en el destino (por defecto: fast)
              [--organize]                         Guarda las fotos nuevas en carpetas Año\Mes
              [--nuevos]                           Guarda las fotos nuevas en la carpeta «Nuevos»
              [--dry-run]                          Solo muestra qué haría
          iphotos verify <destino> [--deep]        Comprueba lo copiado (--deep relee también el iPhone)
          iphotos organize <carpeta> [--apply]     Ordena en Año\Mes las fotos de CUALQUIER carpeta ya descargada (sin --apply solo simula)
          iphotos livephotos <carpeta> [--apply]   Aparta los .MOV de Live Photos a _LivePhotos-MOV (no los borra; sin --apply solo simula)
              (ambos funcionan sin iPhone ni manifiesto; si la carpeta es de esta app, mantienen su control al día)
          iphotos jpg <carpeta> [destino] [--quality 92]   Crea una copia JPG de cada .HEIC en otra carpeta (por defecto "<carpeta> (JPG)"); no toca los originales
          iphotos album <carpeta>                  Resumen del álbum (fechas, GPS, lugares). No mueve nada
          iphotos album-add <carpeta> <álbum> <foto relativa>...   Mueve esas fotos al álbum (carpeta «Álbumes\<álbum>»)
          iphotos duplicados <carpeta> [--distancia 6]   Lista fotos repetidas o parecidas. No mueve nada
          iphotos descartes <carpeta>              Lista capturas y posibles documentos. No mueve nada
          iphotos personas <carpeta> [--max 150] [--activar]   Analiza caras (datos biométricos: pide activarlo; necesita los modelos)
          iphotos frame <clip.mov> <segundos> <salida.jpg>   Saca un fotograma de un Live Photo

        La aplicación nunca borra nada del iPhone (desde v1.5). Para liberar espacio, verifica la copia y borra a mano en Fotos.

        TÉCNICAS (se prueban solas y se elige la mejor; si una falla se pasa a la siguiente)
          wpd      Windows Portable Devices: como el Explorador de Windows
          afc      Acceso directo al iPhone (libimobiledevice), solo lectura
          backup   Última copia de seguridad de Apple Devices/iTunes (foto fija, solo si lo demás falla)

        OPCIONES COMUNES
          --device N          Elegir el dispositivo N de la lista (por defecto el único)
          --only wpd|afc|backup   Usar solo esa técnica (para diagnosticar)
          --no-snapshot       No recurrir nunca a la copia de seguridad
          --backup-folder P   Carpeta adicional donde buscar copias de seguridad
          --source-folder P   Usar la carpeta P como si fuera el iPhone (para pruebas)
          --include-non-apple Mostrar también dispositivos que no son de Apple

        Sin permisos de administrador. Todo lo que guarda va dentro de la carpeta de destino (.iphotos).
        """;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Help);
            return 0;
        }

        var a = new Args(args);
        var log = new ActivityLog();
        log.Entry += e => Console.WriteLine($"{e.Time:HH:mm:ss} {Tag(e.Level)} {e.Message}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nCancelando… (lo copiado hasta ahora queda guardado)");
            cts.Cancel();
        };

        var providers = new List<IDeviceBackendProvider>();
        if (a.Flag("source-folder") is { } folder) providers.Add(new LocalFolderProvider(folder));
        else
        {
            providers.Add(new WpdProvider { IncludeNonApple = a.Has("include-non-apple") });
            providers.Add(new AfcProvider());
            providers.Add(new BackupProvider(a.Flag("backup-folder") is { } bf ? new[] { bf } : null));
        }
        var selector = new BackendSelector(providers, log);

        try
        {
            return a.Command switch
            {
                "devices" => Devices(selector),
                "probe" => await ProbeAsync(selector, a, cts.Token),
                "download" => await DownloadAsync(selector, log, a, cts.Token),
                "verify" => await VerifyAsync(selector, log, a, cts.Token),
                "delete" or "probe-delete" => Fail("Eliminado en v1.5: la aplicación ya no borra nada del iPhone. Usa «verify» y borra a mano desde la app Fotos."),
                "organize" => Organize(log, a, cts.Token),
                "livephotos" => LivePhotos(log, a, cts.Token),
                "jpg" => Jpg(log, a, cts.Token),
                "album" => AlbumSummary(a, cts.Token),
                "frame" => Frame(a),
                "duplicados" => Duplicates(a, cts.Token),
                "descartes" => Discards(a, cts.Token),
                "personas" => People(a, cts.Token),
                "album-add" => AlbumAdd(log, a, cts.Token),
                _ => Fail($"Comando desconocido: {a.Command}. Usa --help.")
            };
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Cancelado.");
            return 130;
        }
        catch (DeviceLostException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 3;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Errores esperables (confirmación incorrecta, ruta mal escrita, disco lleno…): mensaje claro, sin traza técnica.
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 2;
        }
    }

    // ------------------------------------------------------------------ Comandos

    private static int Devices(BackendSelector selector)
    {
        var driver = AppleDriverCheck.Check();
        Console.WriteLine($"Controlador de Apple: {driver.Message}");
        if (selector.Providers.Any(p => p is AfcProvider))
        {
            Console.WriteLine(AfcProvider.EnsureNativeLibraries(out var afcError)
                ? "Técnica AFC: bibliotecas de libimobiledevice cargadas."
                : $"Técnica AFC no disponible: {afcError}");
        }
        foreach (var p in selector.Providers)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var n = p.Discover().Count;
            Console.WriteLine($"  {p.Key,-7} {n} dispositivo(s) visibles  ({sw.ElapsedMilliseconds} ms)");
        }
        var list = selector.DiscoverAll();
        if (list.Count == 0)
        {
            Console.WriteLine("No se ve ningún dispositivo. Desbloquea el iPhone, usa un cable de datos y pulsa \"Confiar\".");
            return 1;
        }
        for (var i = 0; i < list.Count; i++)
            Console.WriteLine($"  [{i}] {list[i].DisplayName}   técnicas: {selector.DescribeTechniques(list[i])}");
        return 0;
    }

    private static async Task<int> ProbeAsync(BackendSelector selector, Args a, CancellationToken ct)
    {
        var driver = AppleDriverCheck.Check();
        Console.WriteLine($"Controlador de Apple: {driver.Message}");
        var devices = selector.DiscoverAll();
        if (devices.Count == 0)
        {
            Console.WriteLine("No se ve ningún dispositivo.");
            var empty = BackendSelector.FormatReport(driver, Array.Empty<ProbeResult>());
            if (a.Flag("report") is { } r0) File.WriteAllText(r0, empty, Encoding.UTF8);
            return 1;
        }

        var results = new List<ProbeResult>();
        foreach (var d in devices)
        {
            foreach (var t in selector.Techniques(d, a.Flag("only")))
            {
                Console.WriteLine($"Probando {BackendSelector.PublicName(d)} con {t.Name}…");
                results.Add(await selector.ProbeAsync(t, ct));
            }
        }

        var text = BackendSelector.FormatReport(driver, results);
        Console.WriteLine();
        Console.WriteLine(text);
        var path = a.Flag("report") ?? Path.Combine(AppContext.BaseDirectory, "informe-conexion.txt");
        try
        {
            File.WriteAllText(path, text, Encoding.UTF8);
            Console.WriteLine($"Informe guardado en: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(No se pudo guardar el informe: {ex.Message})");
        }
        return results.Any(r => r.Opened && r.CanReadSample) ? 0 : 2;
    }

    private static async Task<int> DownloadAsync(BackendSelector selector, ActivityLog log, Args a, CancellationToken ct)
    {
        var dest = a.Positional(0) ?? throw new ArgumentException("Falta la carpeta de destino.");
        var device = PickDevice(selector, a);
        if (device == null) return 1;

        var options = new TransferOptions
        {
            DestinationRoot = Path.GetFullPath(dest),
            OnlyTechnique = a.Flag("only"),
            OrganizeByDate = a.Has("organize"),
            NewFolder = a.Has("nuevos") ? AlbumService.InboxFolder : null,
            AllowSnapshotFallback = !a.Has("no-snapshot"),
            Adopt = a.Flag("adopt")?.ToLowerInvariant() switch
            {
                "none" => AdoptMode.None,
                "deep" => AdoptMode.Deep,
                _ => AdoptMode.Fast
            }
        };

        if (a.Has("dry-run"))
        {
            Directory.CreateDirectory(options.DestinationRoot);
            var (backend, _) = await selector.OpenBestAsync(device, false, ct, options.OnlyTechnique);
            using (backend)
            using (var store = ManifestStore.Open(options.DestinationRoot))
            {
                var scan = new TransferEngine(log).Scan(backend, store, options, new Progress<string>(Console.WriteLine), ct);
                Console.WriteLine($"[SIMULACIÓN] En el dispositivo: {scan.All.Count} archivos ({Format.Bytes(scan.TotalBytes)}).");
                Console.WriteLine($"  Ya copiados : {scan.AlreadyDone} ({Format.Bytes(scan.AlreadyDoneBytes)})");
                Console.WriteLine($"  Pendientes  : {scan.Pending.Count} ({Format.Bytes(scan.PendingBytes)})");
            }
            return 0;
        }

        var started = DateTime.Now;
        var progress = new Progress<TransferProgress>(p =>
        {
            if (p.Phase == TransferPhase.Downloading && p.FilesTotal > 0)
                Console.Write($"\r  {p.FilesDone}/{p.FilesTotal} archivos · {Format.Bytes(p.BytesDone)}/{Format.Bytes(p.BytesTotal)} · {Format.Bytes((long)p.BytesPerSecond)}/s   ");
        });

        var summary = await new TransferSession(selector, log).RunAsync(device, options, progress, new Progress<string>(s => { }), ct);
        Console.WriteLine();
        Console.WriteLine($"Terminado en {Format.Duration(DateTime.Now - started)}: {summary.Copied} copiados ({Format.Bytes(summary.BytesCopied)}), " +
                          $"{summary.AlreadyDone} ya estaban, {summary.Failed} con error, {summary.Reconnects} reconexiones.");
        if (summary.TechniquesUsed.Count > 0) Console.WriteLine($"Técnica(s) usada(s): {string.Join(", ", summary.TechniquesUsed)}");
        if (summary.SnapshotWarning != null) Console.WriteLine($"AVISO: {summary.SnapshotWarning}");
        foreach (var f in summary.FailedFiles) Console.WriteLine($"  ERROR: {f}");
        return summary.Failed == 0 ? 0 : 1;
    }

    private static async Task<int> VerifyAsync(BackendSelector selector, ActivityLog log, Args a, CancellationToken ct)
    {
        var dest = Path.GetFullPath(a.Positional(0) ?? throw new ArgumentException("Falta la carpeta de destino."));
        var device = PickDevice(selector, a);
        if (device == null) return 1;
        var (backend, _) = await selector.OpenBestAsync(device, false, ct, a.Flag("only"));
        using (backend)
        using (var store = ManifestStore.Open(dest))
        {
            var svc = new VerificationService(log);
            var local = await svc.VerifyLocalAsync(store, dest, true, new Progress<TransferProgress>(Dot), ct);
            Console.WriteLine();
            Console.WriteLine($"Local: {local.Verified} correctos, {local.Corrupt} dañados, {local.MissingLocal} que faltan.");
            if (a.Has("deep"))
            {
                var deep = await svc.VerifyDeepAsync(backend, store, dest, new Progress<TransferProgress>(Dot), ct);
                Console.WriteLine();
                Console.WriteLine($"Profunda: {deep.Verified} idénticos al iPhone, {deep.Corrupt} con problemas.");
                return local.Corrupt + local.MissingLocal + deep.Corrupt == 0 ? 0 : 1;
            }
            return local.Corrupt + local.MissingLocal == 0 ? 0 : 1;
        }
    }

    // Estos dos comandos trabajan sobre CUALQUIER carpeta con fotos ya descargadas: no necesitan el iPhone ni que
    // la carpeta la haya creado esta aplicación. Si tiene manifiesto (.iphotos), se mantiene al día.
    private static int Organize(ActivityLog log, Args a, CancellationToken ct)
    {
        var dir = FolderArg(a);
        var svc = new OrganizeService(log);
        var plan = svc.Plan(dir, ct);
        Console.WriteLine($"Se organizarían {plan.Items.Count} archivos ({Format.Bytes(plan.TotalBytes)}) en carpetas Año\\Mes. Ya en su sitio: {plan.AlreadyInPlace}.");
        return RunMove(plan, a, dir, "organizar", "Organización por Año\\Mes",
            store => svc.Apply(dir, store, plan, new Progress<TransferProgress>(Dot), ct));
    }

    private static int LivePhotos(ActivityLog log, Args a, CancellationToken ct)
    {
        var dir = FolderArg(a);
        var svc = new LivePhotoService(log);
        var plan = svc.Plan(dir, ct);
        Console.WriteLine($"Se apartarían {plan.Items.Count} clips .MOV de Live Photos a '{LivePhotoService.BackupFolder}' ({Format.Bytes(plan.TotalBytes)}). Ya apartados: {plan.AlreadyInPlace}.");
        return RunMove(plan, a, dir, "livephotos", "Limpieza de Live Photos (los .MOV se apartan, no se borran)",
            store => svc.Apply(dir, store, plan, new Progress<TransferProgress>(Dot), ct));
    }

    // iphotos frame <clip.mov> <segundos> <salida.jpg>: saca un fotograma de un clip de Live Photo.
    private static int Frame(Args a)
    {
        var clip = Path.GetFullPath(a.Positional(0) ?? throw new ArgumentException("Falta el clip .MOV."));
        var seconds = double.Parse(a.Positional(1) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var output = Path.GetFullPath(a.Positional(2) ?? throw new ArgumentException("Falta el JPG de salida."));
        using var grabber = new LiveFrameGrabber();
        grabber.OpenAsync(clip).GetAwaiter().GetResult();
        Console.WriteLine($"Clip: {grabber.Width}x{grabber.Height}, {grabber.Duration.TotalSeconds:0.00} s");
        LiveFrameGrabber.SaveJpeg(grabber.GrabAsync(TimeSpan.FromSeconds(seconds)).GetAwaiter().GetResult(), output);
        Console.WriteLine($"Fotograma guardado: {output}");
        return 0;
    }

    // iphotos album-add <carpeta> <álbum> <foto relativa>...: crea el álbum (carpeta) y mueve esas fotos a él (con su clip de Live Photo).
    private static int AlbumAdd(ActivityLog log, Args a, CancellationToken ct)
    {
        var dir = FolderArg(a);
        var album = AlbumService.Create(dir, a.Positional(1) ?? throw new ArgumentException("Falta el nombre del álbum."));
        var wanted = new List<string>();
        for (var i = 2; a.Positional(i) is { } p; i++) wanted.Add(p);
        var items = Album.Load(dir, null, ct).Items.Where(it => wanted.Contains(it.Media.RelativePath, StringComparer.OrdinalIgnoreCase)).ToList();
        using var store = FileMover.OpenExistingManifest(dir);
        var result = new AlbumService(log).Add(dir, store, album, items, null, ct);
        Console.WriteLine($"Álbum «{album}»: {items.Count} fotos pedidas, {result.Moved} archivos movidos, {result.Failed.Count} fallidos.");
        return result.Failed.Count == 0 ? 0 : 1;
    }

    // iphotos duplicados <carpeta> [--distancia 6]: lista grupos de fotos repetidas o muy parecidas. No mueve nada.
    private static int Duplicates(Args a, CancellationToken ct)
    {
        var dir = FolderArg(a);
        var distance = int.TryParse(a.Flag("distancia"), out var d) ? d : DuplicateService.DefaultDistance;
        var groups = DuplicateService.Find(dir, Album.Load(dir, null, ct).Items, distance, null, ct);
        var extra = groups.Sum(g => g.Photos.Count - 1);
        Console.WriteLine($"{groups.Count} grupos; {extra} fotos sobrarían ({Format.Bytes(groups.Sum(g => g.Photos.Where((_, i) => i != g.Recommended).Sum(p => p.Size)))}).");
        Console.WriteLine($"Tamaños de grupo: " + string.Join(", ", groups.GroupBy(g => g.Photos.Count).OrderBy(x => x.Key).Select(x => $"{x.Key} fotos x{x.Count()}")));
        foreach (var g in groups.Take(5))
            Console.WriteLine("  " + string.Join(" | ", g.Photos.Select((p, i) =>
                $"{(i == g.Recommended ? "*" : "")}{Path.GetFileName(p.Item.Media.RelativePath)} puntos {p.Score} [{p.Notes}] nitidez {p.Stats.Sharpness:0}/{p.Stats.LocalSharpness:0} quemado {p.Stats.Clipped:P0} brillo {p.Stats.MeanLuma:0} contraste {p.Stats.Contrast:0} caras {(p.Faces == null ? "?" : p.Faces.Faces)} ojos cerrados {(p.Faces == null ? "?" : p.Faces.ClosedEyes)}")));
        Console.WriteLine(DuplicateService.FacesAvailable ? "Modelos de caras: cargados." : "Modelos de caras: NO disponibles.");
        return 0;
    }

    // iphotos descartes <carpeta>: lista capturas de pantalla y posibles documentos/tickets. No mueve nada.
    private static int Discards(Args a, CancellationToken ct)
    {
        var dir = FolderArg(a);
        var found = DiscardService.Find(dir, Album.Load(dir, null, ct).Items, null, ct);
        Console.WriteLine($"Capturas: {found.Count(c => c.Kind == DiscardKind.Screenshot)}; posibles documentos: {found.Count(c => c.Kind == DiscardKind.Document)} ({Format.Bytes(found.Sum(c => c.Item.Media.Size))}).");
        foreach (var c in found.Where(c => c.Kind == DiscardKind.Document).Take(40)) Console.WriteLine($"  doc {c.Item.Media.RelativePath} | {c.Reason}");
        foreach (var c in found.Where(c => c.Kind == DiscardKind.Screenshot).Take(5)) Console.WriteLine($"  captura {c.Item.Media.RelativePath}");
        return 0;
    }

    // iphotos personas <carpeta> [--max 150]: analiza caras (las N fotos más recientes) y lista las personas encontradas.
    private static int People(Args a, CancellationToken ct)
    {
        var dir = FolderArg(a);
        var max = int.TryParse(a.Flag("max"), out var m) ? m : 150;
        if (!PeopleService.IsEnabled)
        {
            Console.WriteLine(PeopleService.Notice);
            if (!a.Has("activar")) { Console.WriteLine("\nPersonas está desactivado. Si estás de acuerdo, repite el comando con --activar."); return 1; }
            PeopleService.Enable();
        }
        if (!PeopleService.ModelsAvailable()) { Console.WriteLine(FaceModels.MissingMessage); return 1; }
        var items = Album.Load(dir, null, ct).Items.OrderByDescending(i => i.Date).Take(max).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var store = PeopleService.Index(dir, items, null, ct);
        Console.WriteLine($"{store.Done.Count} fotos analizadas, {store.Faces.Count} caras, {sw.Elapsed.TotalSeconds:0} s.");
        var groups = PeopleService.Group(dir, store.Faces);
        Console.WriteLine($"{groups.Count} grupos; con 2 o más caras: {groups.Count(g => g.Faces.Count >= 2)}.");
        foreach (var g in groups.Take(12)) Console.WriteLine($"  {g.Display}: {g.Faces.Count} caras en {g.PhotoCount} fotos");
        return 0;
    }

    private static int AlbumSummary(Args a, CancellationToken ct)
    {
        var data = Album.Load(FolderArg(a), null, ct);
        var withGps = data.Items.Count(i => i.Lat != null);
        Console.WriteLine($"{data.Items.Count} fotos; con GPS: {withGps}; con fecha de toma distinta de la del archivo: {data.Items.Count(i => i.Date != i.Media.Date)}.");
        Console.WriteLine(data.HasPlaceNames ? "Nombres de lugares: sí." : "Nombres de lugares: no descargados.");
        foreach (var g in Album.ByPlace(data.Items).Take(8)) Console.WriteLine($"  lugar {g.Name}: {g.Items.Count}");
        foreach (var g in Album.ByDate(data.Items).Take(3)) Console.WriteLine($"  mes {g.Name}: {g.Items.Count}");
        var s = data.Items.FirstOrDefault(i => i.Lat != null);
        if (s != null) Console.WriteLine($"  ejemplo: {s.Media.RelativePath} -> {s.Lat:0.0000}, {s.Lon:0.0000}, {s.Date}");
        return 0;
    }

    private static int Jpg(ActivityLog log, Args a, CancellationToken ct)
    {
        var dir = FolderArg(a);
        var target = Path.GetFullPath(a.Positional(1) ?? dir.TrimEnd('\\') + " (JPG)");
        var quality = int.TryParse(a.Flag("quality"), out var q) && q is >= 1 and <= 100 ? q : 92;
        var last = 0;
        var result = new HeicToJpgService(log).Convert(dir, target, quality,
            new Progress<(int Done, int Total)>(p => { if (p.Done - last >= 100 || p.Done == p.Total) { last = p.Done; Console.WriteLine($"  {p.Done} de {p.Total}"); } }), ct);
        Console.WriteLine($"Convertidas: {result.Converted}. Ya existían: {result.Skipped}. Fallidas: {result.Failed.Count}. Destino: {target}");
        foreach (var f in result.Failed.Take(10)) Console.WriteLine($"  fallida: {f}");
        return result.Failed.Count == 0 ? 0 : 1;
    }

    private static string FolderArg(Args a)
    {
        var dir = Path.GetFullPath(a.Positional(0) ?? throw new ArgumentException("Falta la carpeta con las fotos."));
        if (!Directory.Exists(dir)) throw new ArgumentException($"No existe la carpeta: {dir}");
        return dir;
    }

    private static int RunMove(MovePlan plan, Args a, string dir, string reportPrefix, string title, Func<ManifestStore?, MoveResult> apply)
    {
        foreach (var n in plan.Notes.Take(10)) Console.WriteLine($"  - {n}");
        if (plan.Notes.Count > 10) Console.WriteLine($"  … y {plan.Notes.Count - 10} avisos más (están en el informe).");
        if (!a.Has("apply"))
        {
            var report = FileMover.WriteReport(dir, reportPrefix, title, plan, null);
            Console.WriteLine("[SIMULACIÓN] No se ha movido nada. Para hacerlo, repite el comando con --apply.");
            if (report != null) Console.WriteLine($"Informe: {report}");
            return 0;
        }

        using var store = FileMover.OpenExistingManifest(dir);
        var result = apply(store);
        Console.WriteLine();
        Console.WriteLine($"Movidos: {result.Moved} ({Format.Bytes(result.BytesMoved)}). Registros corregidos: {result.Reconciled}. Fallidos: {result.Failed.Count}." +
                          (store != null ? " (manifiesto de la aplicación actualizado)" : ""));
        foreach (var f in result.Failed.Take(10)) Console.WriteLine($"  fallido: {f}");
        if (result.ReportPath != null) Console.WriteLine($"Informe: {result.ReportPath}");
        return result.Failed.Count == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ Utilidades

    private static DeviceDescriptor? PickDevice(BackendSelector selector, Args a)
    {
        var devices = selector.DiscoverAll();
        if (devices.Count == 0)
        {
            Console.WriteLine("No se ve ningún dispositivo. Desbloquea el iPhone, usa un cable de datos y pulsa \"Confiar\".");
            var driver = AppleDriverCheck.Check();
            if (!driver.Running) Console.WriteLine(driver.Message);
            return null;
        }
        if (int.TryParse(a.Flag("device"), out var idx))
        {
            if (idx < 0 || idx >= devices.Count) { Console.WriteLine($"No existe el dispositivo {idx}."); return null; }
            return devices[idx];
        }
        if (devices.Count > 1)
        {
            Console.WriteLine("Hay varios dispositivos; elige uno con --device N:");
            for (var i = 0; i < devices.Count; i++) Console.WriteLine($"  [{i}] {devices[i].DisplayName}");
            return null;
        }
        return devices[0];
    }

    private static void Dot(TransferProgress p)
    {
        if (p.FilesTotal > 0) Console.Write($"\r  {p.FilesDone}/{p.FilesTotal} archivos · {Format.Bytes(p.BytesDone)}/{Format.Bytes(p.BytesTotal)}   ");
    }

    private static string Tag(LogLevel l) => l switch { LogLevel.Warn => "[aviso]", LogLevel.Error => "[ERROR]", _ => "[info] " };

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 64;
    }

    private sealed class Args
    {
        private readonly List<string> _positional = new();
        private readonly Dictionary<string, string?> _flags = new(StringComparer.OrdinalIgnoreCase);

        public Args(string[] args)
        {
            Command = args[0];
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i].StartsWith("--"))
                {
                    var key = args[i][2..];
                    var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--");
                    _flags[key] = hasValue ? args[++i] : null;
                }
                else _positional.Add(args[i]);
            }
        }

        public string Command { get; }
        public string? Positional(int i) => i < _positional.Count ? _positional[i] : null;
        public bool Has(string key) => _flags.ContainsKey(key);
        public string? Flag(string key) => _flags.TryGetValue(key, out var v) ? v : null;
    }
}
