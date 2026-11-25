using IPhonePhotos.Core;
using Xunit;

namespace IPhonePhotos.Tests;

public class LibraryGateTests
{
    [Fact]
    public void Only_one_writing_operation_at_a_time()
    {
        using (var first = LibraryGate.TryEnter("descargar"))
        {
            Assert.NotNull(first);
            Assert.Null(LibraryGate.TryEnter("organizar"));
            Assert.Contains("descargar", LibraryGate.BusyMessage);
        }
        using var again = LibraryGate.TryEnter("organizar");
        Assert.NotNull(again);
    }
}
