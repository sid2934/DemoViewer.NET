#region

using System.Text;
using System.Text.Json;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Tests;

/// <summary>
///     A user-edited store file with a UTF-8 BOM still parses.
///     <para>
///         These files are hand-editable and several editors write a BOM. The store reads moved from
///         <c>File.ReadAllText</c>, which strips one, to raw bytes handed to <c>Utf8JsonReader</c>,
///         which treats it as content and throws. Nothing caught that, because nothing in the suite
///         wrote a BOM.
///     </para>
/// </summary>
public class StoredFileBomTests
{
    private sealed record Payload(string Name);

    [Test]
    public async Task AStoreFileWithABom_StillDeserialises()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-bom-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """{"Name":"ok"}""", new UTF8Encoding(true));

            byte[] raw = await File.ReadAllBytesAsync(path);
            await Assert.That(raw[0]).IsEqualTo((byte)0xEF).Because("the fixture must actually carry a BOM");

            StoredFile file = StoredFile.At(path);
            byte[]? bytes = file.Read();

            await Assert.That(bytes).IsNotNull();
            Payload? parsed = JsonSerializer.Deserialize<Payload>(bytes!);
            await Assert.That(parsed!.Name).IsEqualTo("ok");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
