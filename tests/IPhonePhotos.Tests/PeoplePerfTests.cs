using System.Diagnostics;
using IPhonePhotos.Core.Organize;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using Xunit.Abstractions;

namespace IPhonePhotos.Tests;

/// <summary>
/// Tiempo de Personas por foto sobre JPEG de 12 MP sintéticos (sin caras: mide decodificar + detector, no ArcFace;
/// incluye cargar los modelos). Necesita los modelos en bin\...\onnx (los deja NuGet al compilar). Fuera de CI.
/// Medido en nov. 2026 (24 hilos): 2 analizadores con modelos propios a la vez fueron ≈ 1,7 veces MÁS lentos que uno
/// (ONNX Runtime ya reparte cada análisis entre núcleos), así que el análisis sigue con un analizador.
/// </summary>
[Trait("Category", "Perf")]
[Collection("Personas")]
public sealed class PeoplePerfTests
{
    private readonly ITestOutputHelper _out;
    public PeoplePerfTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Analysis_time_per_photo()
    {
        using var sb = new Sandbox();
        var oldRoot = PeopleService.DataRoot;
        try
        {
            PeopleService.DataRoot = Path.Combine(sb.Root, "people");
            PeopleService.Enable();
            Assert.True(PeopleService.ModelsAvailable(), "faltan los modelos en bin\\onnx");
            var rnd = new Random(1);
            var pixels = new byte[4032 * 3024 * 3];
            Directory.CreateDirectory(Path.Combine(sb.Dest, "2024"));
            for (var i = 0; i < 48; i++)
            {
                rnd.NextBytes(pixels);
                using var img = Image.LoadPixelData<Rgb24>(pixels, 4032, 3024);
                img.SaveAsJpeg(Path.Combine(sb.Dest, "2024", $"IMG_{i:0000}.JPG"));
            }
            var items = Album.Load(sb.Dest).Items;

            var clock = Stopwatch.StartNew();
            var store = PeopleService.Index(sb.Dest, items, null, default);
            _out.WriteLine($"{items.Count} fotos en {clock.Elapsed.TotalSeconds:F1} s ({clock.Elapsed.TotalMilliseconds / items.Count:F0} ms/foto), {Environment.ProcessorCount} hilos");
            Assert.Equal(items.Count, store.Done.Count);
        }
        finally { PeopleService.DataRoot = oldRoot; }
    }
}
