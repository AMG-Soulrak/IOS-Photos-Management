using System.Diagnostics;
using System.Text;
using Xunit;

namespace IPhonePhotos.Tests;

/// <summary>Comandos principales del CLI, ejecutando el iphotos.exe compilado contra una carpeta que hace de iPhone.</summary>
public class CliTests
{
    private static string Exe =>
        Path.Combine(AppContext.BaseDirectory.Replace(
            Path.Combine("tests", "IPhonePhotos.Tests"), Path.Combine("src", "IPhonePhotos.Cli")), "iphotos.exe");

    private static (int Code, string Out) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "el CLI no terminó en 60 s");
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    [Fact]
    public void Help_lists_the_commands_and_no_longer_offers_to_delete()
    {
        var (code, output) = Run("--help");
        Assert.Equal(0, code);
        foreach (var cmd in new[] { "download", "verify", "organize", "livephotos", "album-add", "duplicados", "descartes", "personas" })
            Assert.Contains(cmd, output);
        Assert.DoesNotContain("probe-delete", output);
        Assert.DoesNotContain("--confirm BORRAR", output);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("probe-delete")]
    public void Removed_delete_commands_fail_with_a_clear_message(string cmd)
    {
        var (code, output) = Run(cmd, "x");
        Assert.Equal(64, code);
        Assert.Contains("ya no borra", output);
    }

    [Fact]
    public void Download_verify_and_organize_work_end_to_end_on_a_folder()
    {
        using var sb = new Sandbox();
        sb.AddLibrary(4);

        var (code, output) = Run("download", sb.Dest, "--source-folder", sb.Source);
        Assert.True(code == 0, output);
        Assert.Equal(4, sb.DestFiles().Count());
        Assert.True(File.Exists(Path.Combine(sb.Dest, "ultima-copia.txt")));

        (code, output) = Run("verify", sb.Dest, "--source-folder", sb.Source, "--deep");
        Assert.True(code == 0, output);
        Assert.Contains("4 idénticos", output);

        // organize sin --apply solo simula: no mueve nada.
        var before = sb.DestFiles().OrderBy(f => f).ToList();
        (code, output) = Run("organize", sb.Dest);
        Assert.True(code == 0, output);
        Assert.Contains("SIMULACIÓN", output);
        Assert.Equal(before, sb.DestFiles().OrderBy(f => f).ToList());
    }

    [Fact]
    public void Unknown_command_is_an_error()
    {
        var (code, _) = Run("no-existe");
        Assert.Equal(64, code);
    }
}
