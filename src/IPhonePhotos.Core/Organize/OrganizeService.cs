using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;

namespace IPhonePhotos.Core.Organize;

/// <summary>
/// Reordena en carpetas <c>Año\Mes</c> las fotos y vídeos de CUALQUIER carpeta ya descargada, según la fecha de cada
/// archivo (la que muestra el Explorador; las descargas de esta aplicación conservan la fecha del iPhone).
/// No hace falta el iPhone ni que la carpeta la haya creado esta aplicación. Siempre se puede simular antes
/// (<see cref="Plan"/> no toca nada). El clip de un Live Photo sigue a su foto, y los clips ya apartados por la limpieza de
/// Live Photos se dejan donde están.
/// </summary>
public sealed class OrganizeService
{
    private readonly ActivityLog _log;

    public OrganizeService(ActivityLog log) => _log = log;

    public MovePlan Plan(string root, CancellationToken ct = default)
        => Plan(root, f => DateLayout.Map(f.RelativePath, f.Date), ct);

    /// <summary>Organiza en carpetas Lugar y, dentro, Año, con el GPS de cada foto. Exige haber descargado antes los nombres de lugares.</summary>
    public MovePlan PlanByPlace(string root, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var album = Album.Load(root, progress, ct);
        if (!album.HasPlaceNames)
            throw new InvalidOperationException("Faltan los nombres de lugares. Ábrelos desde «Álbum…» y pulsa «Descargar nombres de lugares».");
        var placeOf = album.Items.ToDictionary(i => i.Media.RelativePath, StringComparer.OrdinalIgnoreCase);
        return Plan(root, f =>
        {
            placeOf.TryGetValue(f.RelativePath, out var item);
            var folder = Path.Combine(SafeName(item?.Place ?? Album.UnknownPlace), (item?.Date ?? f.Date).Year.ToString("0000"));
            return Path.Combine(folder, Path.GetFileName(f.RelativePath));
        }, ct);
    }

    private static string SafeName(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');

    /// <summary>El clip de un Live Photo acaba en la misma carpeta que su foto, sea cual sea el criterio.</summary>
    private MovePlan Plan(string root, Func<LocalMedia, string> map, CancellationToken ct)
    {
        var plan = new MovePlan { Scanned = MediaScanner.Scan(root, ct) };
        var photoOfVideo = LivePhotoService.FindPairs(plan.Scanned)
            .ToDictionary(p => p.Video.RelativePath, p => p.Photo, StringComparer.OrdinalIgnoreCase);

        foreach (var f in plan.Scanned)
        {
            var to = photoOfVideo.TryGetValue(f.RelativePath, out var photo)
                ? Path.Combine(Path.GetDirectoryName(map(photo))!, Path.GetFileName(f.RelativePath))
                : map(f);
            if (string.Equals(f.RelativePath, to, StringComparison.OrdinalIgnoreCase)) plan.AlreadyInPlace++;
            else plan.Items.Add(new MoveItem(f.RelativePath, to, f.Size));
        }

        var clashes = plan.Items.GroupBy(i => i.To, StringComparer.OrdinalIgnoreCase).Count(g => g.Count() > 1);
        if (clashes > 0)
            plan.Notes.Add($"{clashes} nombres coinciden en la misma carpeta de destino: los repetidos se renombrarán como «nombre (2)».");
        var undated = plan.Items.Count(i => i.To.StartsWith(DateLayout.NoDateFolder + Path.DirectorySeparatorChar));
        if (undated > 0)
            plan.Notes.Add($"{undated} archivos no tienen una fecha fiable y irán a «{DateLayout.NoDateFolder}».");
        return plan;
    }

    public MoveResult Apply(string root, ManifestStore? store, MovePlan plan,
        IProgress<TransferProgress>? progress, CancellationToken ct,
        string reportKind = "organizar", string reportTitle = "Organización por Año\\Mes")
    {
        var result = FileMover.Apply(root, store, plan, _log, progress, ct);
        result.ReportPath = FileMover.WriteReport(root, reportKind, reportTitle, plan, result);
        _log.Info($"Organizar: {result.Moved} movidos, {result.Reconciled} registros corregidos, {result.Failed.Count} fallidos.");
        return result;
    }
}
