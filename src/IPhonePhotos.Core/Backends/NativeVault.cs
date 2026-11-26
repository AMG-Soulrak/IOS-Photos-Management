using System.Security.Cryptography;

namespace IPhonePhotos.Core.Backends;

/// <summary>
/// Copia las bibliotecas nativas de libimobiledevice a una carpeta propia
/// (%LOCALAPPDATA%\IPhonePhotos\native\1.3.17) y solo las acepta si su SHA-256 coincide con el del paquete
/// iMobileDevice-net 1.3.17 (win-x64). AFC carga solo desde esa carpeta: una DLL plantada en %TEMP%\.net o en
/// DOTNET_BUNDLE_EXTRACT_BASE_DIR con otro contenido se rechaza.
/// Límite: otro proceso del mismo usuario puede escribir en %LOCALAPPDATA%; entre la comprobación y la carga queda
/// una ventana mínima (se verifica de nuevo justo antes de cargar).
/// </summary>
public static class NativeVault
{
    public const string PackageVersion = "1.3.17";

    /// <summary>SHA-256 de las DLL de runtimes\win-x64\native del paquete iMobileDevice-net 1.3.17. Al cambiar de versión, regenerar.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["bz2.dll"] = "D1DE667597C11CA5760D3BAAA758AAF08CCF6FB2B1E5BAE2EACE7659337AF789",
        ["getopt.dll"] = "F36763D439AA23DDF110F1A841ACD4B0196AE7228DB07089CEA0D62629B863BB",
        ["iconv-2.dll"] = "E8A4A09FD639B87927B83D14FE2485A03FE3ECC813C24EE8BDD2B12FA3E425BF",
        ["ideviceactivation.dll"] = "775174BE149419CF203E243220AE44E0E671277F32D4AAABD39BB261E05AA8F8",
        ["imobiledevice-net-lighthouse.dll"] = "4A9BCE0575C2FC46CF12DC01E3E1EB0CB27DD09E7785B5FDB7572F368C964E83",
        ["imobiledevice.dll"] = "7E4A42C84BC1F8FB5CF23A72090BAA5E467D89981226FF6877218AFF71200B19",
        ["irecovery.dll"] = "279AD12A64F0ADD484DF98723BFAC3E5261FC748F23079BA2FC3753B2AF97306",
        ["libcrypto-1_1-x64.dll"] = "3F1BB0435A1765A98547553F0111C7F0F8FC26CE3E88EC517C0532952A320970",
        ["libcurl.dll"] = "F20CC3046966F0BAE39A1AF164F1D8FED1801A31A583450B1ADEFE3014DEEEFA",
        ["libssl-1_1-x64.dll"] = "47933A7710438C748C1D4996952F2DA91A2188D5280C6057B760E9BE12AD0F0B",
        ["libusb-1.0.dll"] = "71934C68D8B9F7CEE6DB0CFE0C5F23E08571B9127DC51DFB07E3CA74200F6EA4",
        ["libusb0.dll"] = "35DC5F39C7DB74E8F769FC75AECE2A842BA20962BE853A35B40D05A283202FB6",
        ["libxml2.dll"] = "D33968EAB7035AB6B4D44306B631958419C2F3FC1DB97AE768E5C25B95BA7602",
        ["lzma.dll"] = "D809F75F16C8CEEB343CCC9398F9DA74C02BDAB270867F747F2372F048B13C25",
        ["pcre.dll"] = "39C87232F7512E76FEB6E42376DB00626E237628D5C88A492D088613478A8E12",
        ["pcreposix.dll"] = "299AC749BDA81AA93985141203F603AC880D3FC9F0D8A84E5597A020C24F5353",
        ["plist.dll"] = "F4921856D858124E6EBDDB9C71AAF03182009690D8140C0EB1B54087E4109010",
        ["pthreadVC3.dll"] = "D3E4E6CA4BD83CA057E874B2C15947B21A90E11600290E61908EF0C73C236712",
        ["readline.dll"] = "8C3FFF60AB4EE2458BCC2ED9638985F6AF1A99C061CB60DACA3E9A781B7B35AD",
        ["usbmuxd.dll"] = "349A3D0C855AA6592A581D3161ADA03A67527738A38E6C5805F5492BEC81FF75",
        ["vcruntime140.dll"] = "B989172491BCF631322D87D7B812FB5598B8FCDD1E2A30C119F5265080CD13B8",
        ["zip.dll"] = "5D9CF87EB136E5699FEF3DF425607153B13EFA24B726EA982A1A8F80EB124091",
        ["zlib1.dll"] = "B45BE7FA62570A92BDC0AB5B965B7E2B703B2551B0AB082B753281B9860DC31F",
    };

    /// <summary>Las que hacen falta para AFC; si alguna falta o no coincide, no se carga nada.</summary>
    private static readonly string[] Required = { "imobiledevice.dll", "plist.dll", "usbmuxd.dll" };

    public static string DefaultTarget =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPhonePhotos", "native", PackageVersion);

    /// <summary>
    /// Copia a <paramref name="target"/> las DLL conocidas encontradas en las carpetas de origen (la primera con
    /// imobiledevice.dll válido) y devuelve <paramref name="target"/> si todas las necesarias quedan verificadas.
    /// </summary>
    public static string? Prepare(IEnumerable<string> sources, string target, out string? error)
    {
        error = null;
        Directory.CreateDirectory(target);
        foreach (var name in KnownHashes.Keys)
        {
            var dest = Path.Combine(target, name);
            if (Verify(dest)) continue;
            foreach (var src in sources)
            {
                var bytes = ReadIfKnown(Path.Combine(src, name));
                if (bytes == null) continue;
                var tmp = dest + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, dest, overwrite: true);
                break;
            }
        }

        var bad = Required.Where(n => !Verify(Path.Combine(target, n))).ToList();
        if (bad.Count == 0) return target;
        error = $"No se encuentran copias válidas (SHA-256 del paquete {PackageVersion}) de: {string.Join(", ", bad)}.";
        return null;
    }

    /// <summary>¿Existe el archivo y su SHA-256 es el conocido para su nombre?</summary>
    public static bool Verify(string path) => ReadIfKnown(path) != null;

    private static byte[]? ReadIfKnown(string path)
    {
        try
        {
            if (!KnownHashes.TryGetValue(Path.GetFileName(path), out var expected) || !File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            return Convert.ToHexString(SHA256.HashData(bytes)) == expected ? bytes : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
