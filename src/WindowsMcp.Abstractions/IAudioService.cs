namespace WindowsMcp.Abstractions;

public interface IAudioService
{
    /// <summary>
    /// Get the current default-playback volume (0-100) and muted state via Core Audio.
    /// </summary>
    Task<AudioState> GetAsync(CancellationToken ct = default);

    /// <summary>Set the default-playback master volume (0-100).</summary>
    Task SetVolumeAsync(int level0to100, CancellationToken ct = default);

    /// <summary>Set the default-playback muted state (true setter, not a toggle).</summary>
    Task SetMutedAsync(bool muted, CancellationToken ct = default);
}

public record AudioState(int Level, bool Muted);
