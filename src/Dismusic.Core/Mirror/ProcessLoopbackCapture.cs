using System.Runtime.InteropServices;

namespace Dismusic.Core.Mirror;

/// <summary>
/// Captures the audio of one process tree (or of everything except one process tree) using the
/// Windows 10 2004+ process-loopback virtual device. No virtual audio driver needed.
/// Output is always 48 kHz, stereo, 32-bit float interleaved; Windows converts to it.
/// </summary>
public sealed class ProcessLoopbackCapture : ICaptureSource
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    int ICaptureSource.SampleRate => SampleRate;

    /// <summary>Raised on the capture thread with interleaved samples. The buffer is reused; copy what you keep.</summary>
    public event Action<float[], int>? DataAvailable;

    /// <summary>Raised when capture ends unexpectedly (with the error) or after <see cref="Stop"/> (null).</summary>
    public event Action<Exception?>? Stopped;

    private readonly uint _processId;
    private readonly bool _includeTree;
    private Thread? _thread;
    private volatile bool _stopRequested;

    public ProcessLoopbackCapture(uint processId, bool includeProcessTree)
    {
        _processId = processId;
        _includeTree = includeProcessTree;
    }

    /// <summary>Starts capture and blocks until the stream is running, or throws if it could not be started.</summary>
    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("Capture already started.");
        Exception? startError = null;
        using var started = new ManualResetEventSlim();
        _thread = new Thread(() => Run(started, e => startError = e))
        {
            IsBackground = true,
            Name = "Dismusic loopback capture",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!started.Wait(TimeSpan.FromSeconds(10)))
        {
            _stopRequested = true;
            throw new TimeoutException("Windows did not start the loopback capture stream in time.");
        }
        if (startError is not null)
        {
            _thread.Join(2000);
            _thread = null;
            throw new InvalidOperationException($"Could not start audio capture: {startError.Message}", startError);
        }
    }

    public void Stop()
    {
        _stopRequested = true;
        if (_thread is { } t && t != Thread.CurrentThread) t.Join(3000);
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Run(ManualResetEventSlim started, Action<Exception> reportStartError)
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        bool running = false;
        Exception? failure = null;
        using var bufferReady = new AutoResetEvent(false);
        try
        {
            client = Activate(_processId, _includeTree);
            var format = new WaveFormatEx
            {
                wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 32,
                nBlockAlign = Channels * 4,
                nAvgBytesPerSec = SampleRate * Channels * 4,
                cbSize = 0,
            };
            const uint flags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                               AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            Check(client.Initialize(0 /* shared */, flags, 200 * 10_000 /* 200 ms */, 0, ref format, IntPtr.Zero), "Initialize");
            Check(client.SetEventHandle(bufferReady.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            var iid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref iid, out var service), "GetService");
            capture = (IAudioCaptureClient)service;
            Check(client.Start(), "Start");
            running = true;
        }
        catch (Exception ex)
        {
            reportStartError(ex);
            started.Set();
            Release(capture, client);
            return;
        }
        started.Set();

        var buffer = new float[SampleRate * Channels / 10];
        try
        {
            while (!_stopRequested)
            {
                bufferReady.WaitOne(100);
                while (!_stopRequested)
                {
                    Check(capture.GetNextPacketSize(out uint packetFrames), "GetNextPacketSize");
                    if (packetFrames == 0) break;
                    Check(capture.GetBuffer(out var data, out uint frames, out uint bufferFlags, out _, out _), "GetBuffer");
                    int samples = (int)frames * Channels;
                    if (buffer.Length < samples) buffer = new float[samples];
                    if ((bufferFlags & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                        Array.Clear(buffer, 0, samples);
                    else
                        Marshal.Copy(data, buffer, 0, samples);
                    Check(capture.ReleaseBuffer(frames), "ReleaseBuffer");
                    DataAvailable?.Invoke(buffer, samples);
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            Log.Error("Loopback capture stopped", ex);
        }
        finally
        {
            if (running) client.Stop();
            Release(capture, client);
            Stopped?.Invoke(failure);
        }
    }

    private static void Release(params object?[] comObjects)
    {
        foreach (var o in comObjects)
            if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }

    private static IAudioClient Activate(uint processId, bool includeTree)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = processId,
            ProcessLoopbackMode = includeTree ? 0 : 1, // INCLUDE / EXCLUDE_TARGET_PROCESS_TREE
        };
        IntPtr pParams = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activationParams, pParams, false);
            Marshal.StructureToPtr(new PropVariantBlob
            {
                vt = 65, // VT_BLOB
                cbSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                pBlobData = pParams,
            }, pVariant, false);

            var handler = new ActivationHandler();
            var iid = typeof(IAudioClient).GUID;
            int hr = ActivateAudioInterfaceAsync(VirtualProcessLoopbackDevice, ref iid, pVariant, handler, out var operation);
            Check(hr, "ActivateAudioInterfaceAsync");
            if (!handler.Completed.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Audio interface activation timed out.");
            GC.KeepAlive(operation);
            Check(handler.ActivateResult, "Process loopback activation");
            return (IAudioClient)handler.Interface!;
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pParams);
        }
    }

    // ---- Interop ----

    private const string VirtualProcessLoopbackDevice = "VAD\\Process_Loopback";
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>PROPVARIANT holding a BLOB. Sequential layout matches the native union on x86 and x64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort vt;
        public ushort reserved1, reserved2, reserved3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    /// <summary>Marker: the completion handler must be agile (callable from any apartment).</summary>
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject;

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Completed = new();
        public int ActivateResult;
        public object? Interface;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                int hr = operation.GetActivateResult(out ActivateResult, out var unknown);
                if (hr < 0) ActivateResult = hr;
                Interface = unknown;
            }
            catch (Exception ex)
            {
                ActivateResult = ex.HResult;
            }
            finally
            {
                Completed.Set();
            }
            return 0;
        }
    }
}
