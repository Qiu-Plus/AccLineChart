using System.Runtime.InteropServices;
using System.Text;

namespace EuterpeMpTap;

/// <summary>
/// Zero-connection tap for Euterpe: hooks the only WebSocket-receive API the Euterpe
/// native module uses (WinHTTP's <c>WinHttpWebSocketReceive</c>) and copies every
/// plaintext frame. It never opens a socket, never reads session.dat and never talks to
/// the Euterpe server, so the game's own multiplayer connection is untouched
/// (no "联机断开/重连" popup).
///
/// The Euterpe native payload (Euterpe.Hot.dll) resolves these WinHTTP WebSocket
/// functions dynamically, confirmed by its literal strings:
///   winhttp.dll / WinHttpWebSocketCompleteUpgrade / WinHttpWebSocketSend /
///   WinHttpWebSocketReceive / WinHttpWebSocketClose / WinHttpWebSocketQueryCloseStatus
/// </summary>
public static class WinHttpWsTap
{
    private const string WinHttpModuleName = "winhttp.dll";

    // WINHTTP_WEB_SOCKET_*_BUFFER_TYPE
    private const int BinaryMessage = 0;
    private const int BinaryFragment = 1;
    private const int Utf8Message = 2;
    private const int Utf8Fragment = 3;
    private const int CloseBuffer = 4;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint WinHttpWebSocketReceiveFn(IntPtr webSocket, IntPtr buffer, uint bufferLength,
        out uint bytesRead, out int bufferType);

    private static readonly object Sync = new();
    private static readonly Dictionary<IntPtr, MemoryStream> PendingText = new();
    private static readonly Dictionary<IntPtr, MemoryStream> PendingBinary = new();

    private static HookEngine? _engine;
    private static WinHttpWebSocketReceiveFn? _original;
    private static WinHttpWebSocketReceiveFn? _detour; // keep the delegate rooted
    private static IntPtr _receiveAddress;
    private static bool _started;
    private static bool _faulted;

    /// <summary>Raised on the network thread for every complete UTF-8 text message.</summary>
    public static event Action<string>? TextMessageReceived;
    /// <summary>Raised on the network thread for every complete binary message (usually none).</summary>
    public static event Action<byte[]>? BinaryMessageReceived;
    /// <summary>Diagnostic log (hook install / receive errors).</summary>
    public static event Action<string>? Log;

    // --- diagnostics: what the native client sends / requests ---
    public static event Action<string>? OutgoingText;
    public static event Action<string>? HttpRequest;
    public static event Action<string>? HttpRead;
    public static bool NetworkDiagnostics;

    public static bool IsStarted { get { lock (Sync) return _started; } }
    public static bool HasFaulted { get { lock (Sync) return _faulted; } }
    public static string? LastError { get; private set; }

    /// <summary>Installs the hook. Safe to call more than once; never throws.</summary>
    public static bool Start()
    {
        lock (Sync)
        {
            if (_started) return true;
            if (_faulted) return false;

            try
            {
                // Load the module first: Euterpe loads winhttp lazily, and the game may
                // initialise mods before the first HTTP request happens.
                var winhttp = System.Runtime.InteropServices.NativeLibrary.Load(WinHttpModuleName);
                _receiveAddress = System.Runtime.InteropServices.NativeLibrary.GetExport(winhttp, "WinHttpWebSocketReceive");

                _engine = new HookEngine();
                _detour = new WinHttpWebSocketReceiveFn(Detour);
                _original = _engine.CreateHook(_receiveAddress, _detour);

                // The send hook is always installed (tiny): the outgoing JSON tells us when the
                // game leaves a multiplayer room, so the chart can drop the stale players.
                var send = System.Runtime.InteropServices.NativeLibrary.GetExport(winhttp, "WinHttpWebSocketSend");
                _sendDetour = new WinHttpWebSocketSendFn(DetourSend);
                _sendOriginal = _engine.CreateHook(send, _sendDetour);

                if (NetworkDiagnostics)
                {
                    var connect = System.Runtime.InteropServices.NativeLibrary.GetExport(winhttp, "WinHttpConnect");
                    _connectDetour = new WinHttpConnectFn(DetourConnect);
                    _connectOriginal = _engine.CreateHook(connect, _connectDetour);

                    var open = System.Runtime.InteropServices.NativeLibrary.GetExport(winhttp, "WinHttpOpenRequest");
                    _openDetour = new WinHttpOpenRequestFn(DetourOpenRequest);
                    _openOriginal = _engine.CreateHook(open, _openDetour);

                    var read = System.Runtime.InteropServices.NativeLibrary.GetExport(winhttp, "WinHttpReadData");
                    _readDetour = new WinHttpReadDataFn(DetourRead);
                    _readOriginal = _engine.CreateHook(read, _readDetour);
                }

                _engine.EnableHooks();

                _started = true;
                Log?.Invoke($"[tap] hooked WinHttpWebSocketReceive at 0x{_receiveAddress.ToInt64():x}");
                return true;
            }
            catch (Exception ex)
            {
                _faulted = true;
                LastError = ex.GetType().Name + ": " + ex.Message;
                Log?.Invoke("[tap] hook failed: " + LastError);
                SafeDispose();
                return false;
            }
        }
    }

    /// <summary>Removes the hook.</summary>
    public static void Stop()
    {
        lock (Sync)
        {
            if (!_started) return;
            SafeDispose();
            _started = false;
            Log?.Invoke("[tap] hook removed");
        }
    }

    private static void SafeDispose()
    {
        try { _engine?.DisableHooks(); } catch { /* ignore */ }
        try { _engine?.Dispose(); } catch { /* ignore */ }
        _engine = null;
        _original = null;
        _detour = null;
        lock (Sync)
        {
            PendingText.Clear();
            PendingBinary.Clear();
            PendingOutgoing.Clear();
        }
    }

    private static uint Detour(IntPtr webSocket, IntPtr buffer, uint bufferLength, out uint bytesRead, out int bufferType)
    {
        var original = _original;
        if (original == null)
        {
            // Hook was disabled while a call was in flight; fall back to the raw API.
            return RawReceive(webSocket, buffer, bufferLength, out bytesRead, out bufferType);
        }

        var result = original(webSocket, buffer, bufferLength, out bytesRead, out bufferType);

        if (result == 0 && bytesRead > 0)
        {
            try
            {
                Capture(webSocket, buffer, (int)bytesRead, bufferType);
            }
            catch (Exception ex)
            {
                // Never break the game's socket because our tap failed.
                Log?.Invoke("[tap] capture failed: " + ex.Message);
            }
        }
        return result;
    }

    private static void Capture(IntPtr webSocket, IntPtr buffer, int count, int type)
    {
        switch (type)
        {
            case Utf8Fragment:
            case Utf8Message:
            {
                MemoryStream ms;
                lock (Sync)
                {
                    if (!PendingText.TryGetValue(webSocket, out ms!))
                        PendingText[webSocket] = ms = new MemoryStream();
                }
                ms.Write(BufferSnapshot(buffer, count), 0, count);
                if (type == Utf8Message)
                {
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);
                    TextMessageReceived?.Invoke(text);
                }
                break;
            }
            case BinaryFragment:
            case BinaryMessage:
            {
                MemoryStream ms;
                lock (Sync)
                {
                    if (!PendingBinary.TryGetValue(webSocket, out ms!))
                        PendingBinary[webSocket] = ms = new MemoryStream();
                }
                ms.Write(BufferSnapshot(buffer, count), 0, count);
                if (type == BinaryMessage)
                {
                    var bytes = ms.ToArray();
                    ms.SetLength(0);
                    BinaryMessageReceived?.Invoke(bytes);
                }
                break;
            }
            case CloseBuffer:
                lock (Sync)
                {
                    PendingText.Remove(webSocket);
                    PendingBinary.Remove(webSocket);
                }
                break;
        }
    }

    private static byte[] BufferSnapshot(IntPtr buffer, int count)
    {
        var bytes = new byte[count];
        Marshal.Copy(buffer, bytes, 0, count);
        return bytes;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint WinHttpWebSocketSendFn(IntPtr webSocket, int bufferType, IntPtr buffer, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WinHttpConnectFn(IntPtr session, IntPtr serverName, ushort port, uint reserved);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WinHttpOpenRequestFn(IntPtr connect, IntPtr verb, IntPtr path, IntPtr version,
        IntPtr referrer, IntPtr acceptTypes, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool WinHttpReadDataFn(IntPtr handle, IntPtr buffer, uint toRead, out uint read);

    private static WinHttpWebSocketSendFn? _sendOriginal, _sendDetour;
    private static WinHttpConnectFn? _connectOriginal, _connectDetour;
    private static WinHttpOpenRequestFn? _openOriginal, _openDetour;
    private static WinHttpReadDataFn? _readOriginal, _readDetour;
    private static int _sendLogged;

    private static readonly Dictionary<IntPtr, MemoryStream> PendingOutgoing = new();

    private static uint DetourSend(IntPtr webSocket, int bufferType, IntPtr buffer, uint length)
    {
        try
        {
            CaptureOutgoing(webSocket, bufferType, buffer, length);
            if (NetworkDiagnostics && _sendLogged < 400)
            {
                _sendLogged++;
                var text = bufferType == 2 && length > 0
                    ? Encoding.UTF8.GetString(BufferSnapshot(buffer, (int)Math.Min(length, 400)))
                    : $"<binary {length}>";
                OutgoingText?.Invoke(text);
            }
        }
        catch { }
        return _sendOriginal?.Invoke(webSocket, bufferType, buffer, length) ?? 0;
    }

    private static void CaptureOutgoing(IntPtr webSocket, int bufferType, IntPtr buffer, uint length)
    {
        if (length == 0 || buffer == IntPtr.Zero) return;
        switch (bufferType)
        {
            case Utf8Fragment:
            case Utf8Message:
            {
                MemoryStream ms;
                lock (Sync)
                {
                    if (!PendingOutgoing.TryGetValue(webSocket, out ms!))
                        PendingOutgoing[webSocket] = ms = new MemoryStream();
                }

                ms.Write(BufferSnapshot(buffer, (int)length), 0, (int)length);
                if (bufferType == Utf8Message)
                {
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);
                    try { OutgoingText?.Invoke(text); } catch { /* observers must not break the game */ }
                }
                break;
            }
            case CloseBuffer:
                lock (Sync) PendingOutgoing.Remove(webSocket);
                break;
        }
    }

    private static IntPtr DetourConnect(IntPtr session, IntPtr serverName, ushort port, uint reserved)
    {
        try
        {
            if (NetworkDiagnostics && serverName != IntPtr.Zero)
                HttpRequest?.Invoke("connect " + ReadUtf16(serverName) + ":" + port);
        }
        catch { }
        return _connectOriginal?.Invoke(session, serverName, port, reserved) ?? IntPtr.Zero;
    }

    private static IntPtr DetourOpenRequest(IntPtr connect, IntPtr verb, IntPtr path, IntPtr version,
        IntPtr referrer, IntPtr acceptTypes, int flags)
    {
        try
        {
            if (NetworkDiagnostics)
                HttpRequest?.Invoke(ReadUtf16(verb) + " " + ReadUtf16(path));
        }
        catch { }
        return _openOriginal?.Invoke(connect, verb, path, version, referrer, acceptTypes, flags) ?? IntPtr.Zero;
    }

    private static bool DetourRead(IntPtr handle, IntPtr buffer, uint toRead, out uint read)
    {
        read = 0;
        var original = _readOriginal;
        if (original == null) return false;
        var ok = original(handle, buffer, toRead, out read);
        try
        {
            if (NetworkDiagnostics && ok && read > 0)
                HttpRead?.Invoke("read " + read + " bytes");
        }
        catch { }
        return ok;
    }

    private static string ReadUtf16(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return "";
        var len = 0;
        while (Marshal.ReadInt16(ptr, len * 2) != 0 && len < 512) len++;
        return Marshal.PtrToStringUni(ptr, len) ?? "";
    }

    [DllImport(WinHttpModuleName, SetLastError = true)]
    private static extern uint WinHttpWebSocketReceive(IntPtr webSocket, IntPtr buffer, uint bufferLength,
        out uint bytesRead, out int bufferType);

    private static uint RawReceive(IntPtr webSocket, IntPtr buffer, uint bufferLength, out uint bytesRead, out int bufferType) =>
        WinHttpWebSocketReceive(webSocket, buffer, bufferLength, out bytesRead, out bufferType);
}
