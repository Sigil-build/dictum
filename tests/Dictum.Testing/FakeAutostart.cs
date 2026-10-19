using Dictum.Platform;

namespace Dictum.Testing;

/// <summary>A login entry in memory; never the real Run key, autostart folder or login items.</summary>
public sealed class FakeAutostart : IAutostart
{
    private LoginItem? _item;

    /// <summary>The entry that is installed; setting it also sets <see cref="Installed"/>.</summary>
    public LoginItem? Item
    {
        get => _item;
        set
        {
            _item = value;
            Installed = value?.App;
        }
    }

    public LoginApp? Installed { get; set; }

    public int Uninstalls { get; private set; }

    public Exception? Fails { get; set; }

    public string Location(LoginApp app) => "fake";

    public Task InstallAsync(LoginItem item, CancellationToken ct)
    {
        if (Fails is not null)
        {
            return Task.FromException(Fails);
        }

        Item = item;
        return Task.CompletedTask;
    }

    public Task UninstallAsync(CancellationToken ct)
    {
        if (Fails is not null)
        {
            return Task.FromException(Fails);
        }

        Item = null;
        Uninstalls++;
        return Task.CompletedTask;
    }
}
