namespace IPhonePhotos.Core;

/// <summary>
/// Una sola operación que escriba en la carpeta de fotos a la vez (descargar, verificar, organizar, mover a álbumes,
/// apartar repetidas o descartes). El manifiesto se abre en exclusiva y dos operaciones que mueven archivos a la vez
/// podrían pisarse: la segunda se rechaza con un mensaje en vez de esperar o fallar a medias.
/// </summary>
public static class LibraryGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Qué operación tiene la carpeta ahora mismo (para el mensaje).</summary>
    public static string? Holder { get; private set; }

    /// <summary>Entra sin esperar. Devuelve null si ya hay otra operación; si no, un objeto que la libera al desecharlo.</summary>
    public static IDisposable? TryEnter(string operation)
    {
        if (!Gate.Wait(0)) return null;
        Holder = operation;
        return new Releaser();
    }

    public static string BusyMessage =>
        $"Espera a que termine la operación en curso ({Holder ?? "otra tarea"}): solo una puede cambiar la carpeta de fotos a la vez.";

    private sealed class Releaser : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            Holder = null;
            Gate.Release();
        }
    }
}
