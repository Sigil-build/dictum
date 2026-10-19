using Dictum.Core.Configuration;

namespace Dictum.App.Overlay;

/// <summary>
/// The overlay's states from the daemon's events, timed by <see cref="OverlayTokens"/>. It owns no clock: every call
/// takes the time, and <see cref="Advance"/> returns the scene to draw for one frame.
/// </summary>
public sealed class OverlayStateMachine
{
    private readonly LevelMeter _meter = new();
    private TimeSpan _entered;
    private TimeSpan _shown;
    private TimeSpan? _idleAt;
    private IReadOnlyList<string> _languages = [];
    private string? _picked;
    private string? _message;

    public OverlayPhase Phase { get; private set; } = OverlayPhase.Hidden;

    public OverlayPalette Palette { get; set; } = OverlayPalette.Aurora;

    public bool ReducedMotion { get; set; }

    /// <summary><c>state: Recording</c>. <paramref name="languages"/> are the profile's codes; two or more show as a chip.</summary>
    public void Recording(TimeSpan now, IReadOnlyList<string> languages)
    {
        _languages = languages;
        _picked = null;
        _message = null;
        _meter.Reset(now);
        Enter(OverlayPhase.Listening, now);
    }

    /// <summary><c>state: Transcribing</c>.</summary>
    public void Transcribing(TimeSpan now)
    {
        if (Phase is OverlayPhase.Listening)
        {
            Enter(OverlayPhase.Transcribing, now);
        }
    }

    /// <summary><c>state: Processing</c>: a clean-up chain runs.</summary>
    public void Processing(TimeSpan now)
    {
        if (Phase is OverlayPhase.Listening or OverlayPhase.Transcribing)
        {
            Enter(OverlayPhase.CleaningUp, now);
        }
    }

    /// <summary><c>state: Idle</c>. <c>finished</c> follows at once; without it the overlay hides after <see cref="OverlayTokens.IdleGrace"/>.</summary>
    public void Idle(TimeSpan now)
    {
        if (Phase is OverlayPhase.Listening or OverlayPhase.Transcribing or OverlayPhase.CleaningUp)
        {
            _idleAt = now;
        }
    }

    /// <summary><c>finished: pasted</c> or <c>copied</c>. <paramref name="language"/> is the code Whisper picked.</summary>
    public void Done(TimeSpan now, string? language)
    {
        if (Phase is OverlayPhase.Hidden)
        {
            return;
        }

        _picked = _languages.Count > 1 && !string.IsNullOrEmpty(language) ? language.ToUpperInvariant() : null;
        Enter(OverlayPhase.Done, now);
    }

    /// <summary><c>finished: cancelled</c>.</summary>
    public void Cancelled(TimeSpan now)
    {
        if (Phase is not OverlayPhase.Hidden)
        {
            Enter(OverlayPhase.Cancelled, now);
        }
    }

    /// <summary><c>finished: error</c> or <c>empty</c>, with the line to show.</summary>
    public void Error(TimeSpan now, string message)
    {
        if (Phase is OverlayPhase.Hidden)
        {
            return;
        }

        _message = message;
        Enter(OverlayPhase.Error, now);
    }

    /// <summary>The link dropped, the daemon is stopping, or the overlay was turned off.</summary>
    public void Hide()
    {
        Phase = OverlayPhase.Hidden;
        _idleAt = null;
    }

    /// <summary>One rendered frame: ends timed states, smooths the level and says what to draw.</summary>
    /// <param name="rms">The newest audio frame. Only Listening hears it; every other state decays to silence.</param>
    public OverlayScene Advance(TimeSpan now, float rms, ReadOnlySpan<float> bands)
    {
        var since = (now - _entered).TotalSeconds;
        var lasts = Phase switch
        {
            OverlayPhase.Done => OverlayTokens.Done,
            OverlayPhase.Cancelled => OverlayTokens.Cancelled,
            OverlayPhase.Error => OverlayTokens.ErrorHold + OverlayTokens.ErrorFade,
            _ => double.PositiveInfinity,
        };
        if (since >= lasts || _idleAt is { } idle && (now - idle).TotalSeconds >= OverlayTokens.IdleGrace)
        {
            Hide();
        }

        if (Phase is OverlayPhase.Hidden)
        {
            return OverlayScene.Hidden;
        }

        var listening = Phase is OverlayPhase.Listening;
        _meter.Update(listening ? rms : 0, listening ? bands : ReadOnlySpan<float>.Empty, now);
        var means = new double[OverlayTokens.Waves.Count];
        for (var i = 0; i < means.Length; i++)
        {
            means[i] = WaveShapes.BandMean(_meter.Bands, OverlayTokens.Waves[i]);
        }

        return new OverlayScene(
            Phase,
            since,
            now.TotalSeconds,
            CubicBezier.Decelerate.Ease((now - _shown).TotalSeconds / OverlayTokens.Appear),
            _meter.Level,
            listening && _meter.Quiet,
            means,
            Chip(),
            _message,
            ReducedMotion,
            Palette);
    }

    private string? Chip() => Phase switch
    {
        OverlayPhase.Listening when _languages.Count > 1 => string.Join('/', _languages.Select(code => code.ToUpperInvariant())),
        OverlayPhase.Done => _picked,
        _ => null,
    };

    private void Enter(OverlayPhase phase, TimeSpan now)
    {
        if (Phase is OverlayPhase.Hidden)
        {
            _shown = now;
        }

        Phase = phase;
        _entered = now;
        _idleAt = null;
    }
}
