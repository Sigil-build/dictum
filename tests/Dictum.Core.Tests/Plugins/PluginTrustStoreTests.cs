using Dictum.Core.Diagnostics;
using Dictum.Plugins;
using Dictum.Testing;

namespace Dictum.Core.Tests.Plugins;

public sealed class PluginTrustStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ListLog _log = new();

    public void Dispose() => _temp.Dispose();

    private string TrustFile => _temp.File("plugin-trust.json");

    private PluginTrustStore NewStore() => new(TrustFile, _log);

    private (string Folder, string Program) Plugin(string name)
    {
        var folder = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(folder);
        var program = Path.Combine(folder, "plugin.exe");
        File.WriteAllText(program, "program " + name);
        return (folder, program);
    }

    [Fact]
    public void AnUnknownPluginIsNotConfirmed()
    {
        var (folder, program) = Plugin("a");

        Assert.Equal(PluginTrust.NotConfirmed, NewStore().Check("a", folder, program));
    }

    [Fact]
    public void AConfirmedPluginStaysConfirmedUntilItsProgramChanges()
    {
        var (folder, program) = Plugin("a");
        var store = NewStore();

        store.Confirm("a", folder, program);
        Assert.Equal(PluginTrust.Confirmed, store.Check("a", folder, program));
        Assert.Equal(PluginTrust.Confirmed, store.Check("a", folder + Path.DirectorySeparatorChar, program));

        using (var stream = new FileStream(program, FileMode.Append))
        {
            stream.WriteByte(1);
        }

        Assert.Equal(PluginTrust.Changed, store.Check("a", folder, program));
    }

    [Fact]
    public void TheSamePluginFromAnotherFolderIsNotConfirmed()
    {
        var (folder, program) = Plugin("a");
        var (otherFolder, otherProgram) = Plugin("b");
        var store = NewStore();
        store.Confirm("a", folder, program);

        Assert.Equal(PluginTrust.NotConfirmed, store.Check("a", otherFolder, otherProgram));
    }

    [Fact]
    public void ForgetRemovesTheRecord()
    {
        var (folder, program) = Plugin("a");
        var store = NewStore();
        store.Confirm("a", folder, program);

        store.Forget("a");

        Assert.Equal(PluginTrust.NotConfirmed, store.Check("a", folder, program));
    }

    [Fact]
    public void ADamagedFileTrustsNothingAndIsReplacedOnConfirm()
    {
        var (folder, program) = Plugin("a");
        File.WriteAllText(TrustFile, "}{ garbage");
        var store = NewStore();

        Assert.Equal(PluginTrust.NotConfirmed, store.Check("a", folder, program));
        Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning);

        store.Confirm("a", folder, program);

        Assert.Equal(PluginTrust.Confirmed, NewStore().Check("a", folder, program));
    }

    [Theory]
    [InlineData("""{"version":1,"custom":{"a":null}}""")]
    [InlineData("""{"version":1,"custom":{"a":{}}}""")]
    [InlineData("""{"version":1,"custom":{"a":{"folder":"bad\u0000path","executable":"x","sha256":"y"}}}""")]
    public void ARecordOfTheWrongShapeIsNotConfirmedAndIsReplacedOnConfirm(string json)
    {
        var (folder, program) = Plugin("a");
        File.WriteAllText(TrustFile, json);
        var store = NewStore();

        Assert.Equal(PluginTrust.NotConfirmed, store.Check("a", folder, program));
        Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning);

        store.Confirm("a", folder, program);

        Assert.Equal(PluginTrust.Confirmed, NewStore().Check("a", folder, program));
    }

    [Fact]
    public void RecordsSurviveANewStore()
    {
        var (folderA, programA) = Plugin("a");
        var (folderB, programB) = Plugin("b");
        var store = NewStore();
        store.Confirm("a", folderA, programA);
        store.Confirm("b", folderB, programB);
        store.Forget("a");

        var fresh = NewStore();

        Assert.Equal(PluginTrust.NotConfirmed, fresh.Check("a", folderA, programA));
        Assert.Equal(PluginTrust.Confirmed, fresh.Check("b", folderB, programB));
    }

    [Fact]
    public void TheHashIsLowercaseSha256Hex()
    {
        var file = _temp.File("x.bin");
        File.WriteAllText(file, "abc");

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", PluginTrustStore.HashFile(file));
    }
}
