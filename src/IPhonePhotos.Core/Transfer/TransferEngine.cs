using System.Diagnostics;
using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Organize;

namespace IPhonePhotos.Core.Transfer;

/// <summary>
/// Motor de copia con reanudación. Dos pasos:
///  1) <see cref="Scan"/> compara lo que hay en el dispositivo con el manifiesto y la carpeta de destino
///     y decide qué ya está copiado y qué falta.
///  2) <see cref="RunAsync"/> descarga lo pendiente: a un archivo .part, comprobando el tamaño, calculando
///     el hash durante la copia y renombrando solo al final. Un archivo cortado nunca se confunde con uno completo.
/// </summary>
public sealed class TransferEngine
{
    private readonly ActivityLog _log;

    public TransferEngine(ActivityLog log) => _log = log;

    // ------------------------------------------------------------------ Escaneo

    public ScanResult Scan(IDeviceBackend backend, ManifestStore store, TransferOptions options,
        IProgress<string>? status, CancellationToken ct)
    {
        var result = new ScanResult();
        var seen = new HashSet<string>();
        var n = 0;

        // Todo lo que ya hay en la biblioteca, en la carpeta que sea. Se lee una sola vez y solo si hace falta.
        LibraryIndex? library = null;
        string? InLibrary(RemoteFile rf)
        {
            if (options.Adopt == AdoptMode.None) return null;
            library ??= LibraryIndex.Build(options.DestinationRoot, ct);
            return library.Find(rf);
        }

        foreach (var rf in backend.EnumerateMedia(ct))
        {
            ct.ThrowIfCancellationRequested();
            // Seguridad: lo que diga el dispositivo o la copia de seguridad nunca puede escribir fuera del destino.
            if (!SafePath.IsInside(options.DestinationRoot, rf.RelativePath))
            {
                _log.Warn($"Ruta rechazada (saldría de la carpeta de destino): {rf.RelativePath}");
                result.Rejected++;
                continue;
            }
            result.All.Add(rf);
            result.TotalBytes += rf.Size;
            if (++n % 250 == 0) status?.Report($"Escaneando… {n} archivos");

            var rec = FindMatch(store, rf);
            if (rec != null)
            {
                seen.Add(rec.Id);
                // Los registros «Deleted» de v1.4 ya llegan migrados a Verified (ManifestStore.Open).
                if (rec.IsCopied)
                {
                    if (LocalOk(options.DestinationRoot, rec))
                    {
                        result.AlreadyDone++;
                        result.AlreadyDoneBytes += rf.Size;
                        continue;
                    }

                    // Movida o renombrada fuera de la app: si está en otra carpeta de la biblioteca, se anota su nueva ruta
                    // en vez de descargarla otra vez.
                    if (InLibrary(rf) is { } moved)
                    {
                        _log.Info($"Ya la tienes en otra carpeta, no se vuelve a descargar: {rf.RelativePath} → {moved}");
                        rec.LocalRelativePath = moved;
                        store.Upsert(rec);
                        result.AlreadyDone++;
                        result.AlreadyDoneBytes += rf.Size;
                        continue;
                    }

                    _log.Warn($"Falta en el PC o ha cambiado de tamaño, se volverá a descargar: {rf.RelativePath}");
                    ResetToDiscovered(rec);
                    store.Upsert(rec);
                }

                result.Pending.Add(new PendingItem(rf, rec, false));
                result.PendingBytes += rf.Size;
                continue;
            }

            // Archivo nuevo para el manifiesto.
            var newRec = NewRecord(rf, backend.Device);
            var existing = Path.Combine(options.DestinationRoot, rf.RelativePath);
            var sameRel = options.Adopt != AdoptMode.None
                          && File.Exists(existing)
                          && rf.Size > 0
                          && new FileInfo(existing).Length == rf.Size
                          && FileIdentity.DatesMatch(rf.Modified, File.GetLastWriteTime(existing));
            // Si no está en la misma ruta, se busca en el resto de la biblioteca (álbumes, Año\Mes, «Nuevos»…) por nombre, tamaño y fecha.
            var libraryPath = sameRel ? rf.RelativePath : InLibrary(rf);
            var candidate = libraryPath != null;

            if (candidate && options.Adopt == AdoptMode.Fast)
            {
                // Mismo nombre, tamaño y fecha: no se descarga otra vez, pero su contenido no se ha comparado con el iPhone.
                if (!string.Equals(libraryPath, rf.RelativePath, StringComparison.OrdinalIgnoreCase)) newRec.LocalRelativePath = libraryPath;
                newRec.State = FileState.AdoptedUnverified;
                newRec.DownloadedAtUtc = DateTime.UtcNow;
                newRec.Note = "adoptado, sin verificar (nombre, tamaño y fecha)";
                store.Upsert(newRec);
                seen.Add(newRec.Id);
                result.Adopted++;
                result.AlreadyDone++;
                result.AlreadyDoneBytes += rf.Size;
                continue;
            }

            store.Upsert(newRec);
            seen.Add(newRec.Id);
            result.Pending.Add(new PendingItem(rf, newRec, candidate, libraryPath));
            result.PendingBytes += rf.Size;
        }

        // Archivos que el manifiesto da por copiados pero ya no están en el dispositivo (informativo).
        // Solo se comparan los de este mismo dispositivo: la carpeta de destino puede recibir varios iPhone.
        var key = backend.Device.SerialKey;
        result.MissingOnDevice = store.All().Count(r =>
            r.IsCopied
            && !seen.Contains(r.Id)
            && string.Equals(r.DeviceKey ?? "", key, StringComparison.Ordinal));

        return result;
    }

    // ------------------------------------------------------------------ Descarga

    public async Task<TransferSummary> RunAsync(
        IDeviceBackend backend,
        ManifestStore store,
        ScanResult scan,
        TransferOptions options,
        TransferSummary summary,
        IProgress<TransferProgress>? progress,
        CancellationToken ct)
    {
        if (!summary.BaselineSet)
        {
            summary.AlreadyDone = scan.AlreadyDone;
            summary.Adopted = scan.Adopted;
            summary.BaselineSet = true;
        }

        var total = scan.Pending.Count;
        var bytesTotal = scan.PendingBytes;
        var done = 0;
        long bytesDone = 0;
        var clock = Stopwatch.StartNew();

        void Report(string? file, long cur, long size) =>
            progress?.Report(new TransferProgress(TransferPhase.Downloading, done, total, bytesDone + cur, bytesTotal,
                file, cur, size, clock.Elapsed.TotalSeconds > 0.5 ? (bytesDone + cur) / clock.Elapsed.TotalSeconds : 0));

        Report(null, 0, 0);

        foreach (var item in scan.Pending)
        {
            ct.ThrowIfCancellationRequested();
            var rf = item.Remote;
            var rec = item.Record;
            Report(rf.RelativePath, 0, rf.Size);

            try
            {
                if (item.AdoptCandidate && options.Adopt == AdoptMode.Deep &&
                    await TryDeepAdoptAsync(backend, store, rec, rf, item.AdoptPath ?? rf.RelativePath, options, ct).ConfigureAwait(false))
                {
                    summary.Adopted++;
                    summary.AlreadyDone++;
                }
                else
                {
                    await DownloadOneAsync(backend, store, rec, rf, options,
                        cur => Report(rf.RelativePath, cur, rf.Size), ct).ConfigureAwait(false);
                    summary.Copied++;
                    summary.BytesCopied += rf.Size;
                    if (!summary.TechniquesUsed.Contains(backend.Device.Technique))
                        summary.TechniquesUsed.Add(backend.Device.Technique);
                }
                // Si antes había fallado con otra técnica y ahora se ha copiado, deja de contar como fallo.
                summary.FailedFiles.Remove(rf.RelativePath);
            }
            catch (OperationCanceledException)
            {
                rec.State = FileState.Discovered;
                store.Upsert(rec);
                throw;
            }
            catch (DeviceLostException)
            {
                rec.State = FileState.Discovered;
                store.Upsert(rec);
                throw;
            }
            catch (Exception ex)
            {
                rec.State = FileState.Failed;
                rec.LastError = ex.Message;
                store.Upsert(rec);
                if (!summary.FailedFiles.Contains(rf.RelativePath)) summary.FailedFiles.Add(rf.RelativePath);
                _log.Error($"No se pudo copiar {rf.RelativePath} ({backend.Device.Technique}): {ex.Message}");
            }

            done++;
            bytesDone += rf.Size;
            Report(rf.RelativePath, 0, rf.Size);
        }

        progress?.Report(new TransferProgress(TransferPhase.Done, done, total, bytesDone, bytesTotal, null, 0, 0,
            clock.Elapsed.TotalSeconds > 0.5 ? bytesDone / clock.Elapsed.TotalSeconds : 0));
        return summary;
    }

    private async Task DownloadOneAsync(IDeviceBackend backend, ManifestStore store, FileRecord rec, RemoteFile rf,
        TransferOptions options, Action<long> onBytes, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            rec.State = FileState.Downloading;
            rec.Attempts++;
            rec.LastError = null;
            rec.Technique = backend.Device.Technique;
            rec.DeviceKey = backend.Device.SerialKey;

            // Las fotos nuevas van a la carpeta «Nuevos» (si se pide); si no, con "organizar por fecha" a Año\Mes.
            var wanted = options.NewFolder != null ? Path.Combine(options.NewFolder, Path.GetFileName(rf.RelativePath))
                : options.OrganizeByDate ? DateLayout.Map(rf.RelativePath, rf.Modified) : rf.RelativePath;
            var localRel = rec.LocalRelativePath ?? ResolveFreeRelativePath(options.DestinationRoot, wanted);
            // LocalRelativePath viene del manifiesto (un archivo de la carpeta, manipulable): se comprueba antes de escribir.
            var target = SafePath.Join(options.DestinationRoot, localRel);

            // Un archivo final con ese nombre solo puede existir si un intento anterior se cortó justo
            // entre renombrar y guardar el estado. Si el tamaño cuadra, se reutiliza; si no, se busca otro nombre.
            if (File.Exists(target))
            {
                if (rf.Size > 0 && new FileInfo(target).Length == rf.Size)
                {
                    rec.LocalRelativePath = localRel;
                    rec.Sha256 = await FileHasher.HashFileAsync(target, ct).ConfigureAwait(false);
                    MarkDownloaded(store, rec, rf, "recuperado tras un corte");
                    return;
                }
                localRel = ResolveFreeRelativePath(options.DestinationRoot, wanted);
                target = SafePath.Join(options.DestinationRoot, localRel);
            }

            rec.LocalRelativePath = localRel;
            store.Upsert(rec);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var part = target + ".part";
            try { if (File.Exists(part)) File.Delete(part); } catch { /* se sobrescribe abajo */ }

            try
            {
                string hash;
                long written;
                await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
                {
                    using var hs = new HashingWriteStream(fs, onBytes);
                    await backend.CopyToAsync(rf, hs, ct).ConfigureAwait(false);
                    await hs.FlushAsync(ct).ConfigureAwait(false);
                    written = hs.BytesWritten;
                    hash = hs.GetHashHex();
                }

                if (rf.Size > 0 && written != rf.Size)
                    throw new IOException($"Tamaño recibido ({written}) distinto del esperado ({rf.Size}).");

                File.Move(part, target, overwrite: false);

                if (options.PreserveTimestamps && rf.Modified is { } m)
                {
                    try
                    {
                        File.SetLastWriteTime(target, m);
                        File.SetCreationTime(target, m);
                    }
                    catch { /* conservar la fecha es opcional */ }
                }

                rec.Sha256 = hash;
                MarkDownloaded(store, rec, rf, null);
                return;
            }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }

                if (!backend.IsAlive())
                    throw new DeviceLostException("El dispositivo se ha desconectado.", ex);

                rec.LastError = ex.Message;
                store.Upsert(rec);
                if (attempt > options.Retries) throw;

                _log.Warn($"Reintento {attempt}/{options.Retries} de {rf.RelativePath}: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(options.RetryDelay.TotalMilliseconds * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> TryDeepAdoptAsync(IDeviceBackend backend, ManifestStore store, FileRecord rec,
        RemoteFile rf, string localRel, TransferOptions options, CancellationToken ct)
    {
        var path = Path.Combine(options.DestinationRoot, localRel);
        var localHash = await FileHasher.HashFileAsync(path, ct).ConfigureAwait(false);

        using var hs = new HashingWriteStream(null);
        await backend.CopyToAsync(rf, hs, ct).ConfigureAwait(false);
        var remoteHash = hs.GetHashHex();

        if (!string.Equals(localHash, remoteHash, StringComparison.Ordinal))
        {
            _log.Warn($"Mismo nombre pero distinto contenido, se descargará aparte: {rf.RelativePath}");
            return false;
        }

        rec.LocalRelativePath = localRel;
        rec.Sha256 = localHash;
        rec.VerifiedDeep = true;
        rec.VerifiedAtUtc = DateTime.UtcNow;
        MarkDownloaded(store, rec, rf, "adoptado (contenido idéntico)");
        rec.State = FileState.Verified;
        store.Upsert(rec);
        return true;
    }

    // ------------------------------------------------------------------ Utilidades

    private static void MarkDownloaded(ManifestStore store, FileRecord rec, RemoteFile rf, string? note)
    {
        rec.State = FileState.Downloaded;
        rec.DownloadedAtUtc = DateTime.UtcNow;
        rec.LastError = null;
        if (note != null) rec.Note = note;
        store.Upsert(rec);
    }

    private static FileRecord? FindMatch(ManifestStore store, RemoteFile rf)
        => store.FindByPath(rf.RelativePath)
            .FirstOrDefault(r => r.Size == rf.Size && FileIdentity.DatesMatch(r.Modified, rf.Modified));

    private static FileRecord NewRecord(RemoteFile rf, DeviceDescriptor device) => new()
    {
        Id = FileIdentity.MakeId(rf.RelativePath, rf.Size, rf.Modified),
        RelativePath = rf.RelativePath,
        Size = rf.Size,
        Modified = rf.Modified,
        State = FileState.Discovered,
        DeviceKey = device.SerialKey,
        Technique = device.Technique
    };

    private static void ResetToDiscovered(FileRecord rec)
    {
        rec.State = FileState.Discovered;
        rec.Sha256 = null;
        rec.VerifiedAtUtc = null;
        rec.VerifiedDeep = false;
        rec.DownloadedAtUtc = null;
    }

    private static bool LocalOk(string destinationRoot, FileRecord rec)
    {
        var path = Path.Combine(destinationRoot, rec.EffectiveLocalRelativePath);
        return File.Exists(path) && (rec.Size <= 0 || new FileInfo(path).Length == rec.Size);
    }

    /// <summary>Si ya hay un archivo con ese nombre (de otro contenido), usa "nombre (2).ext", "nombre (3).ext"…</summary>
    public static string ResolveFreeRelativePath(string destinationRoot, string relativePath)
    {
        if (!File.Exists(Path.Combine(destinationRoot, relativePath))) return relativePath;

        var dir = Path.GetDirectoryName(relativePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var ext = Path.GetExtension(relativePath);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(Path.Combine(destinationRoot, candidate))) return candidate;
        }
    }
}
