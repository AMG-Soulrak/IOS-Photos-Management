namespace IPhonePhotos.Core.Organize;

/// <summary>
/// Lógica de la vista grande de fotos, sin interfaz: en qué foto del conjunto visible estamos (anterior/siguiente da la vuelta)
/// y el zoom (null = ajustada a la ventana; 1 = 100 %).
/// </summary>
public sealed class PreviewNavigator
{
    public const double MinZoom = 0.1, MaxZoom = 8, Step = 1.25;

    public PreviewNavigator(int count, int index)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        Count = count;
        Index = Math.Clamp(index, 0, count - 1);
    }

    public int Count { get; }
    public int Index { get; private set; }

    /// <summary>Null: ajustada a la ventana. Si no, factor sobre el tamaño real (1 = 100 %).</summary>
    public double? Zoom { get; private set; }

    public string Position => $"{Index + 1} de {Count}";

    public int Next() { Index = (Index + 1) % Count; Zoom = null; return Index; }
    public int Previous() { Index = (Index - 1 + Count) % Count; Zoom = null; return Index; }

    /// <param name="fitScale">Escala con la que se ve la foto ajustada ahora (para que el primer «+» parta de lo que se ve).</param>
    public double ZoomIn(double fitScale) => Set((Zoom ?? fitScale) * Step);
    public double ZoomOut(double fitScale) => Set((Zoom ?? fitScale) / Step);
    public double ActualSize() => Set(1);
    public void Fit() => Zoom = null;

    private double Set(double z) { Zoom = Math.Clamp(z, MinZoom, MaxZoom); return Zoom.Value; }
}
