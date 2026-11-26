using System.Text.RegularExpressions;
using IPhonePhotos.Core.Manifest;

namespace IPhonePhotos.Core.Organize;

/// <summary>Una foto editada en el iPhone: la versión editada (IMG_E…), su original y, si está, el .AAE con los ajustes.</summary>
public sealed record EditPair(LocalMedia Edited, LocalMedia Original, LocalMedia? Sidecar);

/// <summary>
/// El iPhone guarda una foto editada como IMG_E1234.HEIC junto a la original IMG_1234.HEIC (y a veces IMG_1234.AAE o IMG_O1234.AAE).
/// No son repetidas: son la misma foto antes y después de editarla. Se puede «apartar la original» (y su .AAE) a
/// <see cref="Folder"/> para quedarse solo con la editada; nunca se borra nada y se simula antes.
/// </summary>
public sealed class EditedVersions
{
    public const string Folder = "_Originales-editadas";
    private static readonly Regex EditedName = new(@"^IMG_E(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ActivityLog _log;
    public EditedVersions(ActivityLog log) => _log = log;

    public static bool IsEdited(string path) => EditedName.IsMatch(Path.GetFileNameWithoutExtension(path));

    /// <summary>Nombre (sin extensión) de la original de una versión editada, o null si no lo es.</summary>
    public static string? OriginalStem(string path)
        => EditedName.Match(Path.GetFileNameWithoutExtension(path)) is { Success: true } m ? "IMG_" + m.Groups[1].Value : null;

    /// <summary>Parejas editada/original en la misma carpeta. La original es la foto (no el vídeo ni el .AAE) con el mismo número.</summary>
    public static List<EditPair> Find(IReadOnlyList<LocalMedia> files)
    {
        var byDirStem = files.ToLookup(f => (f.Directory.ToLowerInvariant(), f.Stem.ToLowerInvariant()));
        var pairs = new List<EditPair>();
        foreach (var edited in files.Where(f => f.Extension != ".aae" && f.Extension != ".mov" && IsEdited(f.RelativePath)))
        {
            var stem = OriginalStem(edited.RelativePath)!.ToLowerInvariant();
            var dir = edited.Directory.ToLowerInvariant();
            var original = byDirStem[(dir, stem)].FirstOrDefault(f => f.Extension is not ".aae" and not ".mov");
            if (original == null) continue;
            var sidecar = byDirStem[(dir, stem)].FirstOrDefault(f => f.Extension == ".aae")
                          ?? byDirStem[(dir, "img_o" + stem[4..])].FirstOrDefault(f => f.Extension == ".aae");
            pairs.Add(new EditPair(edited, original, sidecar));
        }
        return pairs;
    }

    /// <summary>Simulación: qué se movería a <see cref="Folder"/> (originales y sus .AAE). No toca nada.</summary>
    public static MovePlan PlanSetAsideOriginals(string root, CancellationToken ct = default)
    {
        var plan = new MovePlan { Scanned = MediaScanner.Scan(root, ct, includeAlbums: true) };
        var pairs = Find(plan.Scanned);
        foreach (var p in pairs)
            foreach (var f in new[] { p.Original, p.Sidecar }.OfType<LocalMedia>())
                plan.Items.Add(new MoveItem(f.RelativePath, Path.Combine(Folder, f.RelativePath), f.Size));
        plan.Notes.Add($"{pairs.Count} fotos tienen versión editada (IMG_E…). Se apartaría la original y su .AAE; la editada se queda donde está. No se borra nada.");
        return plan;
    }

    public MoveResult Apply(string root, ManifestStore? store, MovePlan plan, IProgress<Models.TransferProgress>? progress, CancellationToken ct)
    {
        var albums = AlbumService.List(root);
        var result = FileMover.Apply(root, store, plan, _log, progress, ct);
        AlbumService.EnsureFolders(root, albums);
        result.ReportPath = FileMover.WriteReport(root, "editadas", "Originales de fotos editadas apartadas a " + Folder, plan, result);
        _log.Info($"Editadas: {result.Moved} archivos apartados a {Folder}, {result.Failed.Count} fallidos.");
        return result;
    }
}
