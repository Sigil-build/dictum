using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;
using Dictum.Setup;

namespace Dictum.App.ViewModels.Setup;

/// <summary>
/// Step 3 (spec 8): clean-up off, or a server the user already runs (URL, model, optional key). Next leaves with a server
/// only after "Test connection" passed for exactly the inputs on screen: an edit clears the result, and an answer that
/// comes back after an edit never passes the new inputs. The current key is kept only for the same server.
/// </summary>
public sealed partial class SetupCleanupStep : SetupStep
{
    /// <summary><c>init</c>'s default: llama-server's port.</summary>
    public const string DefaultUrl = "http://localhost:8080/v1";

    private readonly IServerProbe _probe;
    private readonly ExistingLlmServer? _current;
    private (string Url, string Model, string Key)? _tested;
    private bool _passed;

    public SetupCleanupStep(IServerProbe probe, ExistingLlmServer? current)
    {
        _probe = probe;
        _current = current;
        Url = current?.BaseUrl ?? DefaultUrl;
        Model = current?.Model ?? "";
        UseServer = current is not null;
        Update();
    }

    public override string Name => Strings.SetupStepCleanup;

    [ObservableProperty]
    public partial bool UseServer { get; set; }

    /// <summary>The "Off" radio; always the opposite of <see cref="UseServer"/>.</summary>
    [ObservableProperty]
    public partial bool Off { get; set; } = true;

    [ObservableProperty]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    public partial string Model { get; set; } = "";

    /// <summary>A typed key or reference; empty keeps the current key when the URL stays on the same server.</summary>
    [ObservableProperty]
    public partial string KeyText { get; set; } = "";

    [ObservableProperty]
    public partial string? KeyNote { get; private set; }

    [ObservableProperty]
    public partial string? TestResult { get; private set; }

    [ObservableProperty]
    public partial bool TestOk { get; private set; }

    [ObservableProperty]
    public partial bool Testing { get; private set; }

    /// <summary>
    /// What the plan gets: no server, or the server with its key as the config will hold it. A typed plain key becomes
    /// <paramref name="secretReference"/> and is returned separately, to be stored at the save.
    /// </summary>
    public (CleanupChoice Choice, ExistingLlmServer? Server, string? TypedKey) Answer(string secretReference)
    {
        if (!UseServer)
        {
            return (CleanupChoice.None, null, null);
        }

        var (url, model, typed) = Inputs();
        if (typed.Length == 0)
        {
            return (CleanupChoice.ExistingServer, new ExistingLlmServer(url, model, SetupPlanner.CarriedApiKey(_current, url)), null);
        }

        return ConfigValueResolver.IsReference(typed)
            ? (CleanupChoice.ExistingServer, new ExistingLlmServer(url, model, typed), null)
            : (CleanupChoice.ExistingServer, new ExistingLlmServer(url, model, secretReference), typed);
    }

    partial void OnUseServerChanged(bool value)
    {
        Off = !value;
        Update();
    }

    partial void OnOffChanged(bool value) => UseServer = !value;

    partial void OnUrlChanged(string value) => Update();

    partial void OnModelChanged(string value) => Update();

    partial void OnKeyTextChanged(string value) => Update();

    [RelayCommand]
    private async Task TestAsync()
    {
        var inputs = Inputs();
        Testing = true;
        _tested = null;
        TestResult = null;
        try
        {
            var check = await _probe.CheckAsync(inputs.Url, inputs.Model, KeyFor(inputs), CancellationToken.None);
            _tested = inputs;
            _passed = check.Ok;
            TestResult = check.Message;
        }
        finally
        {
            Testing = false;
            Update();
        }
    }

    private (string Url, string Model, string Key) Inputs() => (Url.Trim(), Model.Trim(), KeyText.Trim());

    private string? KeyFor((string Url, string Model, string Key) inputs) =>
        inputs.Key.Length > 0 ? inputs.Key : SetupPlanner.CarriedApiKey(_current, inputs.Url);

    private void Update()
    {
        var inputs = Inputs();
        var current = _tested == inputs;
        if (!current && !Testing)
        {
            TestResult = null;
        }

        TestOk = current && _passed;
        CanContinue = !UseServer || TestOk;
        KeyNote = UseServer && inputs.Key.Length == 0 && SetupPlanner.CarriedApiKey(_current, inputs.Url) is not null
            ? Strings.SetupCleanupKeyKept
            : null;
    }
}
