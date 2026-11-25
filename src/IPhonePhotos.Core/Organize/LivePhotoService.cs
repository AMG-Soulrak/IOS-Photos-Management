using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;

namespace IPhonePhotos.Core.Organize;

/// <summary>
/// Un Live Photo del iPhone son DOS archivos con el mismo nombre en la misma carpeta: la foto (.HEIC/.JPG) y un
/// clip de ~3 s (.MOV). Esta limpieza aparta esos clips a una carpeta de respaldo; NO los borra.
/// Funciona sobre cualquier carpeta ya descargada (no hace falta el iPhone). Un .MOV sin foto hermana es un vídeo
/// normal y nunca se toca.
/// </summary>
public sealed class LivePhotoService
{
    /// <summary>Carpeta (dentro de la elegida) donde se apartan los clips de los Live Photos.</summary>
    public const string BackupFolder = "_LivePhotos-MOV";

    /// <summary>Máxima diferencia de fecha entre la foto y su clip para considerarlos pareja.</summary>
    public static readonly TimeSpan PairTolerance = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> StillExtensions = new(StringComparer.OrdinalIgnoreCase) { ".heic", ".jpg", ".jpeg" };

    private readonly ActivityLog _log;

    public LivePhotoService(ActivityLog log) => _log = log;

    public sealed record Pair(LocalMedia Photo, LocalMedia Video);

    /// <summary>
    /// Empareja por carpeta + nombre. Solo se empareja si hay exactamente una foto y un vídeo con ese nombre en esa
    /// carpeta y sus fechas coinciden (a menos de <see cref="PairTolerance"/>).
    /// </summary>
    public static List<Pair> FindPairs(IEnumerable<LocalMedia> files)
    {
        var groups = files.GroupBy(f => (Dir: f.Directory.ToLowerInvariant(), Stem: f.Stem.ToLowerInvariant()));

        var pairs = new List<Pair>();
        foreach (var g in groups)
        {
            var photos = g.Where(f => StillExtensions.Contains(f.Extension)).ToList();
            var videos = g.Where(f => f.Extension == ".mov").ToList();
            if (photos.Count != 1 || videos.Count != 1) continue;
            if ((photos[0].Date - videos[0].Date).Duration() > PairTolerance) continue;
            pairs.Add(new Pair(photos[0], videos[0]));
        }
        return pairs;
    }

    /// <summary>Calcula qué clips se apartarían. No toca nada.</summary>
    public MovePlan Plan(string root, CancellationToken ct = default)
    {
        var plan = new MovePlan { Scanned = MediaScanner.Scan(root, ct) };
        var pairs = FindPairs(plan.Scanned);

        foreach (var pair in pairs)
            plan.Items.Add(new MoveItem(pair.Video.RelativePath, Path.Combine(BackupFolder, pair.Video.RelativePath), pair.Video.Size));

        plan.AlreadyInPlace = MediaScanner.CountSetAside(root);

        var pairedVideos = pairs.Select(p => p.Video.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loneVideos = plan.Scanned.Count(f => f.Extension == ".mov" && !pairedVideos.Contains(f.RelativePath));
        plan.Notes.Add($"{pairs.Count} Live Photos detectados. {loneVideos} vídeos .MOV sin foto hermana se consideran vídeos normales y no se tocan.");
        return plan;
    }

    public MoveResult Apply(string root, ManifestStore? store, MovePlan plan,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var result = FileMover.Apply(root, store, plan, _log, progress, ct);
        result.ReportPath = FileMover.WriteReport(root, "livephotos", "Limpieza de Live Photos (los .MOV se apartan, no se borran)", plan, result);
        _log.Info($"Live Photos: {result.Moved} clips apartados a {BackupFolder}, {result.Failed.Count} fallidos.");
        return result;
    }
}
