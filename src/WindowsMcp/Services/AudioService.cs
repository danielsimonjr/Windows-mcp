using System.Runtime.InteropServices;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// Playback volume and mute via Core Audio (<c>IAudioEndpointVolume</c>) — a real
/// getter/setter, not SendKeys toggles.
/// </summary>
public sealed class AudioService : IAudioService
{
    public Task<AudioState> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var ep = AudioEndpoint.OpenDefault();
        return Task.FromResult(new AudioState(ScalarToPercent(ep.GetVolumeScalar()), ep.GetMuted()));
    }

    public Task SetVolumeAsync(int level0to100, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var ep = AudioEndpoint.OpenDefault();
        ep.SetVolumeScalar(PercentToScalar(level0to100));
        return Task.CompletedTask;
    }

    public Task SetMutedAsync(bool muted, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var ep = AudioEndpoint.OpenDefault();
        ep.SetMuted(muted);
        return Task.CompletedTask;
    }

    internal static int ScalarToPercent(float scalar) =>
        Math.Clamp((int)Math.Round(scalar * 100f), 0, 100);

    internal static float PercentToScalar(int percent) =>
        Math.Clamp(percent, 0, 100) / 100f;

    private sealed class AudioEndpoint : IDisposable
    {
        private readonly IAudioEndpointVolume _volume;
        private bool _disposed;

        private AudioEndpoint(IAudioEndpointVolume volume) => _volume = volume;

        public static AudioEndpoint OpenDefault()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, 0, IntPtr.Zero, out var raw);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
            return new AudioEndpoint((IAudioEndpointVolume)raw);
        }

        public float GetVolumeScalar()
        {
            _volume.GetMasterVolumeLevelScalar(out var level);
            return level;
        }

        public void SetVolumeScalar(float scalar) => _volume.SetMasterVolumeLevelScalar(scalar, Guid.Empty);

        public bool GetMuted()
        {
            _volume.GetMute(out var muted);
            return muted;
        }

        public void SetMuted(bool muted) => _volume.SetMute(muted, Guid.Empty);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Marshal.ReleaseComObject(_volume); } catch { /* COM teardown */ }
        }
    }

    private enum EDataFlow { eRender = 0 }
    private enum ERole { eMultimedia = 1 }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    // IMMDeviceEnumerator: EnumAudioEndpoints (slot 3) unused; GetDefaultAudioEndpoint is slot 4.
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void _VtblGap1_1();
        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
    }

    // IMMDevice: Activate is the first declared method (vtable slot 3).
    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    // IAudioEndpointVolume: skip Register/Unregister/GetChannelCount/SetMasterVolumeLevel (slots 3-6),
    // then Set/Get master scalar (7-9), skip 4 channel methods (10-13), then Set/Get mute (14-15).
    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void _VtblGap1_4();
        void SetMasterVolumeLevelScalar(float fLevel, Guid eventContext);
        void _VtblGap2_1();
        void GetMasterVolumeLevelScalar(out float pfLevel);
        void _VtblGap3_4();
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, Guid eventContext);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }
}
