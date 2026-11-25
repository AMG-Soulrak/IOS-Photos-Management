using IPhonePhotos.Core.Manifest;
using IPhonePhotos.Core.Organize;
using Xunit;

namespace IPhonePhotos.Tests;

/// <summary>
/// Personas trata datos biométricos: fuera de la carpeta de fotos, solo con activación, borrables y sin nombres automáticos.
/// Usa una carpeta de datos temporal (no toca %LOCALAPPDATA%).
/// </summary>
[Collection("Personas")] // DataRoot es estático: estas pruebas no se ejecutan en paralelo entre sí
public sealed class PeopleTests : IDisposable
{
    private readonly Sandbox _sb = new();
    private readonly string _oldRoot = PeopleService.DataRoot;

    public PeopleTests()
    {
        PeopleService.DataRoot = Path.Combine(_sb.Root, "localappdata", "people");
        PeopleService.Disable();
    }

    public void Dispose()
    {
        PeopleService.DataRoot = _oldRoot;
        _sb.Dispose();
    }

    private static float[] Vec(params (int Index, float Value)[] parts)
    {
        var v = new float[8];
        foreach (var (i, x) in parts) v[i] = x;
        var n = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / n).ToArray();
    }

    [Fact]
    public void People_data_lives_outside_the_photo_folder()
    {
        var folder = PeopleService.Folder(_sb.Dest);
        Assert.StartsWith(PeopleService.DataRoot, folder);
        Assert.False(folder.StartsWith(_sb.Dest, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(16, Path.GetFileName(folder).Length); // huella de la ruta, no la ruta en claro
    }

    [Fact]
    public void Analysis_refuses_to_run_until_the_user_enables_it()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PeopleService.Index(_sb.Dest, Array.Empty<AlbumItem>(), null, default));
        Assert.Contains("desactivado", ex.Message);
        PeopleService.Enable();
        Assert.True(PeopleService.IsEnabled);
    }

    [Fact]
    public void Old_data_inside_the_photo_folder_is_moved_out_and_delete_all_removes_everything()
    {
        var legacy = PeopleService.LegacyFolder(_sb.Dest);
        Directory.CreateDirectory(Path.Combine(legacy, "crops"));
        File.WriteAllText(Path.Combine(legacy, "names.json"), "{}");
        File.WriteAllBytes(Path.Combine(legacy, "crops", "1.jpg"), new byte[] { 1, 2, 3 });

        PeopleService.Load(_sb.Dest);

        Assert.False(Directory.Exists(legacy)); // ya no queda nada junto a las fotos
        Assert.True(File.Exists(Path.Combine(PeopleService.Folder(_sb.Dest), "crops", "1.jpg")));

        PeopleService.DeleteAll(_sb.Dest);
        Assert.False(Directory.Exists(PeopleService.Folder(_sb.Dest)));
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void A_look_alike_is_only_suggested_never_named_automatically()
    {
        var anna = new[] { new FaceRecord(1, "a1", 0, 0, .3f, .3f, .9f, Vec((0, 1))), new FaceRecord(2, "a2", 0, 0, .3f, .3f, .9f, Vec((0, 1), (1, .05f))) };
        var groups = PeopleService.Group(_sb.Dest, anna);
        PeopleService.SetName(_sb.Dest, Assert.Single(groups), "Ana");

        // Las mismas caras: el grupo que el usuario nombró.
        Assert.Equal("Ana", Assert.Single(PeopleService.Group(_sb.Dest, anna)).Name);

        // Otra persona que se parece (coseno ~0,7): solo se propone.
        var lookAlike = new[] { new FaceRecord(3, "b1", 0, 0, .3f, .3f, .9f, Vec((0, 1), (2, 1))), new FaceRecord(4, "b2", 0, 0, .3f, .3f, .9f, Vec((0, 1), (2, 1.02f))) };
        var g = Assert.Single(PeopleService.Group(_sb.Dest, lookAlike));
        Assert.Null(g.Name);
        Assert.Equal("Ana", g.SuggestedName);
    }
}
