using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dictum.App.Resources;
using Dictum.App.Settings;
using Dictum.Core.Configuration;

namespace Dictum.App.ViewModels.Settings;

/// <summary>
/// Settings → Speech providers (spec 7): a card per provider; Add local Whisper or a remote server; Remove when no profile
/// uses it, together with its stored key. Cards survive reloads, so a field being typed in keeps its focus.
/// </summary>
public sealed partial class SpeechProvidersPage : SettingsPage
{
    private readonly SettingsSession _session;
    private readonly SpeechServices _services;
    private Dictionary<string, ProviderCard> _cards = [];

    public SpeechProvidersPage(SettingsSession session, SpeechServices services)
    {
        _session = session;
        _services = services;
        session.Changed += Load;
        Load();
    }

    public override string Title => Strings.SettingsSpeechProviders;

    [ObservableProperty]
    public partial IReadOnlyList<ProviderCard> Cards { get; private set; } = [];

    [ObservableProperty]
    public partial bool CanEdit { get; private set; }

    [RelayCommand]
    private void AddLocal()
    {
        var id = UniqueId(_session.Config, "local");
        var model = _services.Models(null).FirstOrDefault()?.Value ?? DefaultConfig.DefaultModel;
        _session.Edit(c =>
        {
            c.Stt[id] = new WhisperLocalSttConfig { Model = model, Vad = true };
            return c;
        });
    }

    [RelayCommand]
    private void AddRemote()
    {
        var id = UniqueId(_session.Config, "remote");
        _session.Edit(c =>
        {
            c.Stt[id] = new OpenAiCompatibleSttConfig { BaseUrl = "http://localhost:8080/v1", Model = "whisper-1" };
            return c;
        });
    }

    private void Remove(ProviderCard card)
    {
        var key = (_session.Config.Stt.GetValueOrDefault(card.Id) as OpenAiCompatibleSttConfig)?.ApiKey;
        _session.Edit(c =>
        {
            c.Stt.Remove(card.Id);
            return c;
        });
        _ = ApiKeys.RemoveAsync(_services.Secrets, card.SecretName, key, _services.Log, CancellationToken.None);
    }

    private void Load()
    {
        var config = _session.Config;
        CanEdit = _session.CanEdit;
        var cards = new List<ProviderCard>();
        foreach (var (id, stt) in config.Stt)
        {
            var kind = stt is WhisperLocalSttConfig ? ProviderKind.Local : ProviderKind.Remote;
            var card = _cards.GetValueOrDefault(id) is { } existing && existing.Kind == kind
                ? existing
                : new ProviderCard(id, kind, _session, _services, Remove);
            card.Load(config);
            cards.Add(card);
        }

        _cards = cards.ToDictionary(c => c.Id);
        if (!cards.SequenceEqual(Cards))
        {
            Cards = cards;
        }
    }

    private static string UniqueId(DictumConfig config, string stem)
    {
        if (!config.Stt.ContainsKey(stem))
        {
            return stem;
        }

        for (var n = 2; ; n++)
        {
            if (!config.Stt.ContainsKey($"{stem}-{n}"))
            {
                return $"{stem}-{n}";
            }
        }
    }
}
