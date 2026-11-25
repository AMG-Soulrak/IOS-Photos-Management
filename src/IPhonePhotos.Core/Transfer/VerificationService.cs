using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;

namespace IPhonePhotos.Core.Transfer;

/// <param name="AdoptedUnverified">Archivos adoptados por nombre, tamaño y fecha que siguen sin contrastar con el iPhone.</param>
public sealed record VerifyResult(int Checked, int Verified, int Corrupt, int MissingLocal, IReadOnlyList<string> Problems,
    int AdoptedUnverified = 0);

/// <summary>
/// Verificación de lo copiado (la app nunca borra del iPhone: el informe sirve para que el usuario decida).
///  - Local: relee el archivo del PC y comprueba que su hash coincide con el calculado durante la copia.
///  - Profunda: además relee el archivo del iPhone y comprueba que su contenido es idéntico al del PC.
/// </summary>
public sealed class VerificationService
{
    private readonly ActivityLog _log;

    public VerificationService(ActivityLog log) => _log = log;

    public async Task<VerifyResult> VerifyLocalAsync(ManifestStore store, string destinationRoot, bool recheckVerified,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var todo = store.All()
            .Where(r => r.State == FileState.Downloaded || (recheckVerified && r.State == FileState.Verified))
            .ToList();

        var problems = new List<string>();
        int ok = 0, corrupt = 0, missing = 0, done = 0, unverified = 0;
        long bytesTotal = todo.Sum(r => r.Size), bytesDone = 0;

        foreach (var rec in todo)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(destinationRoot, rec.EffectiveLocalRelativePath);
            progress?.Report(new TransferProgress(TransferPhase.Verifying, done, todo.Count, bytesDone, bytesTotal,
                rec.RelativePath, 0, rec.Size, 0));

            if (!File.Exists(path))
            {
                missing++;
                problems.Add($"Falta en el PC: {rec.EffectiveLocalRelativePath}");
                ResetForRedownload(rec);
                store.Upsert(rec);
            }
            else if (rec.Size > 0 && new FileInfo(path).Length != rec.Size)
            {
                corrupt++;
                problems.Add($"Tamaño distinto en el PC: {rec.EffectiveLocalRelativePath}");
                ResetForRedownload(rec);
                store.Upsert(rec);
            }
            else
            {
                var hash = await FileHasher.HashFileAsync(path, ct).ConfigureAwait(false);
                if (rec.Sha256 == null)
                {
                    // Sin hash de la copia (adoptado por nombre, tamaño y fecha en ≤ v1.4): no hay con qué comparar.
                    // Nunca pasa a Verified por esto; solo la verificación a fondo lo contrasta con el iPhone.
                    rec.State = FileState.AdoptedUnverified;
                    store.Upsert(rec);
                    unverified++;
                }
                else if (string.Equals(rec.Sha256, hash, StringComparison.Ordinal))
                {
                    rec.State = FileState.Verified;
                    rec.VerifiedAtUtc = DateTime.UtcNow;
                    store.Upsert(rec);
                    ok++;
                }
                else
                {
                    corrupt++;
                    problems.Add($"El contenido del PC ha cambiado desde la copia: {rec.EffectiveLocalRelativePath}");
                    ResetForRedownload(rec);
                    store.Upsert(rec);
                }
            }

            done++;
            bytesDone += rec.Size;
        }

        progress?.Report(new TransferProgress(TransferPhase.Done, done, todo.Count, bytesDone, bytesTotal, null, 0, 0, 0));
        foreach (var p in problems) _log.Warn(p);
        // Los adoptados sin verificar (de antes o de ahora) se cuentan aparte: no son ni correctos ni dañados.
        unverified = store.InState(FileState.AdoptedUnverified).Count;
        _log.Info($"Verificación local: {ok} correctos, {corrupt} dañados, {missing} que faltan (de {todo.Count}); " +
                  $"{unverified} adoptados sin verificar.");
        return new VerifyResult(todo.Count, ok, corrupt, missing, problems, unverified);
    }

    /// <summary>Relee los archivos desde el iPhone y comprueba que coinciden con los del PC.</summary>
    public async Task<VerifyResult> VerifyDeepAsync(IDeviceBackend backend, ManifestStore store, string destinationRoot,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var remote = new Dictionary<string, RemoteFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var rf in backend.EnumerateMedia(ct)) remote[rf.RelativePath] = rf;

        var todo = store.All()
            .Where(r => r.IsCopied && !r.VerifiedDeep && remote.ContainsKey(r.RelativePath))
            .ToList();

        var problems = new List<string>();
        int ok = 0, bad = 0, done = 0;
        long bytesTotal = todo.Sum(r => r.Size), bytesDone = 0;

        foreach (var rec in todo)
        {
            ct.ThrowIfCancellationRequested();
            var rf = remote[rec.RelativePath];
            progress?.Report(new TransferProgress(TransferPhase.Verifying, done, todo.Count, bytesDone, bytesTotal,
                rec.RelativePath, 0, rec.Size, 0));

            var path = Path.Combine(destinationRoot, rec.EffectiveLocalRelativePath);
            if (!File.Exists(path))
            {
                bad++;
                problems.Add($"Falta en el PC: {rec.EffectiveLocalRelativePath}");
                ResetForRedownload(rec);
                store.Upsert(rec);
            }
            else
            {
                var localHash = await FileHasher.HashFileAsync(path, ct).ConfigureAwait(false);
                using var hs = new HashingWriteStream(null);
                await backend.CopyToAsync(rf, hs, ct).ConfigureAwait(false);
                var remoteHash = hs.GetHashHex();

                if (string.Equals(localHash, remoteHash, StringComparison.Ordinal))
                {
                    rec.Sha256 = localHash;
                    rec.State = FileState.Verified;
                    rec.VerifiedDeep = true;
                    rec.VerifiedAtUtc = DateTime.UtcNow;
                    store.Upsert(rec);
                    ok++;
                }
                else
                {
                    bad++;
                    problems.Add($"El contenido del iPhone y el del PC no coinciden (se volverá a descargar aparte): {rec.RelativePath}");
                    // Antes el registro seguía en Verified y nunca se volvía a descargar (I4). El archivo dudoso no se toca.
                    ResetForRedownload(rec);
                    store.Upsert(rec);
                }
            }

            done++;
            bytesDone += rec.Size;
        }

        progress?.Report(new TransferProgress(TransferPhase.Done, done, todo.Count, bytesDone, bytesTotal, null, 0, 0, 0));
        foreach (var p in problems) _log.Warn(p);
        _log.Info($"Verificación profunda: {ok} idénticos, {bad} con problemas (de {todo.Count}).");
        return new VerifyResult(todo.Count, ok, bad, 0, problems);
    }

    private static void ResetForRedownload(FileRecord rec)
    {
        rec.State = FileState.Discovered;
        rec.Sha256 = null;
        rec.VerifiedAtUtc = null;
        rec.VerifiedDeep = false;
        // Se descarta el nombre local: la nueva copia irá a un nombre libre y no se toca el archivo dudoso.
        rec.LocalRelativePath = null;
    }
}
