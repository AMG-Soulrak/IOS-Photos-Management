using System.Diagnostics;
using IPhonePhotos.Core;
using IPhonePhotos.Core.Backends;
using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Models;
using IPhonePhotos.Core.Transfer;
using Xunit;
using Xunit.Abstractions;

namespace IPhonePhotos.Tests;

/// <summary>
/// Escalado N / 2N / 4N con datos sintéticos, para MEDIR antes de optimizar. Fuera de CI (Category=Perf):
///   dotnet test --filter Category=Perf --logger "console;verbosity=detailed"
/// Solo falla si el coste crece claramente peor que lineal (4N más de 8 veces N), señal de un O(n²).
/// </summary>
[Trait("Category", "Perf")]
public class PerfTests
{
    private readonly ITestOutputHelper _out;
    public PerfTests(ITestOutputHelper output) => _out = output;

    private static (TimeSpan Download, TimeSpan Rescan) Measure(int n)
    {
        using var sb = new Sandbox();
        for (var i = 0; i < n; i++) sb.AddPhoto($"DCIM\\{100 + i / 500}APPLE\\IMG_{i:00000}.HEIC", 2048, seed: i + 1);
        var provider = new LocalFolderProvider(sb.Source);
        var log = new ActivityLog();
        var options = new TransferOptions { DestinationRoot = sb.Dest };

        var clock = Stopwatch.StartNew();
        var summary = new TransferSession(new BackendSelector(new[] { provider }, log), log)
            .RunAsync(provider.Discover().Single(), options, null, null, default).GetAwaiter().GetResult();
        var download = clock.Elapsed;
        Assert.Equal(n, summary.Copied);

        clock.Restart();
        using (var backend = provider.Open(provider.Discover().Single()))
        using (var store = ManifestStore.Open(sb.Dest))
            Assert.Equal(n, new TransferEngine(log).Scan(backend, store, options, null, default).AlreadyDone);
        return (download, clock.Elapsed);
    }

    [Fact]
    public void Copy_and_rescan_scale_roughly_linearly()
    {
        Measure(50); // calentamiento (JIT, LiteDB)
        var sizes = new[] { 300, 600, 1200 };
        var results = sizes.Select(Measure).ToList();
        for (var i = 0; i < sizes.Length; i++)
            _out.WriteLine($"N={sizes[i],5}: descarga {results[i].Download.TotalMilliseconds,8:F0} ms · re-escaneo {results[i].Rescan.TotalMilliseconds,8:F0} ms");

        Assert.True(results[2].Download < results[0].Download * 8, "la descarga crece peor que lineal");
        Assert.True(results[2].Rescan < results[0].Rescan * 8 + TimeSpan.FromMilliseconds(200), "el re-escaneo crece peor que lineal");
    }

    /// <summary>
    /// Lo que cuesta la recarga que hace la ventana tras mover fotos a un álbum: Album.Load con la caché de metadatos ya
    /// hecha, más el movimiento de 50 fotos con el manifiesto. Sirve para decidir si merece la pena no recargar.
    /// </summary>
    [Fact]
    public void Album_reload_after_a_move_scales_roughly_linearly()
    {
        (TimeSpan Cold, TimeSpan Warm, TimeSpan Move) Run(int n)
        {
            using var sb = new Sandbox();
            for (var i = 0; i < n; i++) sb.AddPhoto($"DCIM\\{100 + i / 500}APPLE\\IMG_{i:00000}.JPG", 2048, seed: i + 1);
            var root = sb.Source;
            var clock = Stopwatch.StartNew();
            var data = IPhonePhotos.Core.Organize.Album.Load(root);
            var cold = clock.Elapsed;
            clock.Restart();
            IPhonePhotos.Core.Organize.Album.Load(root);
            var warm = clock.Elapsed;
            clock.Restart();
            new IPhonePhotos.Core.Organize.AlbumService(new ActivityLog()).Add(root, null, "Viaje", data.Items.Take(50));
            return (cold, warm, clock.Elapsed);
        }

        Run(50);
        var sizes = new[] { 300, 600, 1200 };
        var results = sizes.Select(Run).ToList();
        for (var i = 0; i < sizes.Length; i++)
            _out.WriteLine($"N={sizes[i],5}: Album.Load en frío {results[i].Cold.TotalMilliseconds,6:F0} ms · recarga {results[i].Warm.TotalMilliseconds,6:F0} ms · mover 50 {results[i].Move.TotalMilliseconds,6:F0} ms");
        Assert.True(results[2].Warm < results[0].Warm * 8 + TimeSpan.FromMilliseconds(200), "la recarga crece peor que lineal");
    }
}
