using System.Runtime.InteropServices;
using Iced.Intel;

namespace EuterpeMpTap;

/// <summary>
/// Minimal, self-contained x64 native detour used by the tap.
///
/// The original implementation shipped <c>MinHook.NET</c> (~230 KB) inside the mod, which made
/// the single-file DLL grow to ~400 KB — too big for Euterpe's taste. This replacement uses the
/// <c>Iced</c> decoder (already loaded by MelonLoader for Il2CppInterop) to relocate the patched
/// prologue and write a 14-byte absolute jump, so nothing extra has to be shipped at all.
///
/// The API intentionally mirrors the tiny part of MinHook's <c>HookEngine</c> that the tap used:
/// <c>CreateHook(target, detour)</c> returns the original function as a delegate, and
/// <c>EnableHooks()</c>/<c>DisableHooks()</c>/<c>Dispose()</c> control the patch.
/// </summary>
internal sealed class HookEngine : IDisposable
{
    private readonly List<NativeDetour> _hooks = new();
    private bool _restored;

    internal T CreateHook<T>(IntPtr target, T detour) where T : Delegate
    {
        var hook = NativeDetour.Install(target, detour);
        _hooks.Add(hook);
        return (T)hook.Original;
    }

    // Hooks are live as soon as they are created; kept for MinHook-compatible call sites.
    internal void EnableHooks()
    {
    }

    internal void DisableHooks()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        for (int i = _hooks.Count - 1; i >= 0; i--)
        {
            try
            {
                _hooks[i].Restore();
            }
            catch
            {
                // never break shutdown because one patch failed to restore
            }
        }
    }

    public void Dispose()
    {
        try
        {
            DisableHooks();
        }
        catch
        {
            // ignored
        }

        foreach (var hook in _hooks)
        {
            hook.FreeTrampoline();
        }

        _hooks.Clear();
    }
}

/// <summary>One 14-byte absolute jump patch plus the relocated trampoline back to the original code.</summary>
internal sealed class NativeDetour
{
    private const int PatchSize = 14; // FF 25 00 00 00 00 | dq destination
    private static readonly byte[] JumpIndirect = { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 };

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageExecuteReadWrite = 0x40;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll")]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private readonly IntPtr _target;
    private readonly byte[] _originalBytes;
    private readonly IntPtr _trampoline;
    private readonly int _trampolineSize;
    private bool _restored;

    internal Delegate Original { get; }

    private NativeDetour(IntPtr target, Delegate detour, byte[] originalBytes, IntPtr trampoline, int trampolineSize, Delegate original)
    {
        _target = target;
        _originalBytes = originalBytes;
        _trampoline = trampoline;
        _trampolineSize = trampolineSize;
        Original = original;

        // Keep the reverse-P/Invoke thunk of the detour rooted for as long as the patch is live.
        GC.KeepAlive(detour);
        _detour = detour;
    }

    private readonly Delegate _detour;

    internal static NativeDetour Install(IntPtr target, Delegate detour)
    {
        if (IntPtr.Size != 8)
        {
            throw new PlatformNotSupportedException("native detour currently supports x64 only");
        }

        if (target == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(target));
        }

        // 1) Decode the prologue and work out how many whole instructions the 14-byte jump needs.
        var prologue = new byte[64];
        Marshal.Copy(target, prologue, 0, prologue.Length);
        var instructions = new List<Instruction>();
        var decoder = Decoder.Create(64, prologue, (ulong)target.ToInt64());
        int copiedLength = 0;
        while (copiedLength < PatchSize)
        {
            var instruction = decoder.Decode();
            if (instruction.IsInvalid)
            {
                throw new InvalidOperationException("cannot decode the target prologue");
            }

            instructions.Add(instruction);
            copiedLength += instruction.Length;
        }

        // 2) Allocate the trampoline close to the target: an x64 prologue may contain
        // RIP-relative operands, which the block encoder can only re-encode when the
        // trampoline stays within ±2 GB of the original instruction.
        var trampoline = AllocateNear(target, 4096);
        if (trampoline == IntPtr.Zero)
        {
            throw new InvalidOperationException($"VirtualAlloc failed (error {Marshal.GetLastWin32Error()})");
        }

        byte[] code;
        using (var stream = new MemoryStream())
        {
            var writer = new StreamCodeWriter(stream);
            var block = new InstructionBlock(writer, instructions, (ulong)trampoline.ToInt64());
            if (!BlockEncoder.TryEncode(64, block, out string? error, out _))
            {
                throw new InvalidOperationException("trampoline relocation failed: " + error);
            }

            var relocated = stream.ToArray();
            var jumpBack = BuildJump(ulong.MaxValue); // placeholder, filled below
            jumpBack = BuildJump((ulong)(target.ToInt64() + copiedLength));
            code = new byte[relocated.Length + jumpBack.Length];
            Buffer.BlockCopy(relocated, 0, code, 0, relocated.Length);
            Buffer.BlockCopy(jumpBack, 0, code, relocated.Length, jumpBack.Length);
        }

        Marshal.Copy(code, 0, trampoline, code.Length);
        FlushInstructionCache(GetCurrentProcess(), trampoline, (UIntPtr)code.Length);

        var original = Marshal.GetDelegateForFunctionPointer(trampoline, detour.GetType());

        // 3) Patch the target entry with an absolute indirect jump to our detour.
        var originalBytes = new byte[PatchSize];
        Marshal.Copy(target, originalBytes, 0, PatchSize);

        var patch = BuildJump((ulong)Marshal.GetFunctionPointerForDelegate(detour).ToInt64());
        if (!VirtualProtect(target, (UIntPtr)PatchSize, PageExecuteReadWrite, out uint oldProtect))
        {
            throw new InvalidOperationException($"VirtualProtect(target) failed (error {Marshal.GetLastWin32Error()})");
        }

        try
        {
            Marshal.Copy(patch, 0, target, PatchSize);
        }
        finally
        {
            VirtualProtect(target, (UIntPtr)PatchSize, oldProtect, out _);
        }

        FlushInstructionCache(GetCurrentProcess(), target, (UIntPtr)PatchSize);

        return new NativeDetour(target, detour, originalBytes, trampoline, code.Length, original);
    }

    private static IntPtr AllocateNear(IntPtr target, int size)
    {
        const long granularity = 0x10000;
        const long maxDistance = 0x3F000000; // stay well inside the ±2 GB RIP-relative range
        long baseAddress = target.ToInt64();

        // Already-free memory right above/below the target is the common case.
        for (long delta = granularity; delta < maxDistance; delta += granularity)
        {
            foreach (long candidate in new[] { baseAddress + delta, baseAddress - delta })
            {
                var address = VirtualAlloc((IntPtr)candidate, (UIntPtr)size, MemCommit | MemReserve, PageExecuteReadWrite);
                if (address != IntPtr.Zero)
                {
                    return address;
                }
            }
        }

        return VirtualAlloc(IntPtr.Zero, (UIntPtr)size, MemCommit | MemReserve, PageExecuteReadWrite);
    }

    private static byte[] BuildJump(ulong destination)
    {
        var patch = new byte[PatchSize];
        Buffer.BlockCopy(JumpIndirect, 0, patch, 0, JumpIndirect.Length);
        BitConverter.TryWriteBytes(new Span<byte>(patch, JumpIndirect.Length, sizeof(ulong)), destination);
        return patch;
    }

    internal void Restore()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        if (!VirtualProtect(_target, (UIntPtr)PatchSize, PageExecuteReadWrite, out uint oldProtect))
        {
            return;
        }

        try
        {
            Marshal.Copy(_originalBytes, 0, _target, PatchSize);
        }
        finally
        {
            VirtualProtect(_target, (UIntPtr)PatchSize, oldProtect, out _);
        }

        FlushInstructionCache(GetCurrentProcess(), _target, (UIntPtr)PatchSize);
    }

    internal void FreeTrampoline()
    {
        if (_trampoline != IntPtr.Zero && _trampolineSize > 0)
        {
            VirtualFree(_trampoline, UIntPtr.Zero, MemRelease);
        }
    }
}
