using System.Diagnostics;
using System.Runtime.InteropServices;
using Voxta.Modules.Tools;

namespace Voxta.Modules.KeyboardShortcuts;

internal sealed class WindowsKeyboard
{
    private static readonly ushort[] ModifierKeys = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];
    private static readonly int InputSize = Marshal.SizeOf<Input>();
    private readonly object _inputLock = new();

    public void Send(Shortcut shortcut, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
            throw new ModuleToolInputException("Keyboard Shortcuts requires a local interactive Windows desktop.");

        var keys = shortcut.Keys;
        var input = keys.Select(key => CreateInput(key, release: false)).Concat(ReleaseKeys(keys)).ToArray();

        lock (_inputLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = GetForegroundWindow();
            if (window == 0)
                throw new ModuleToolInputException("There is no active window on the local desktop.");
            if (shortcut.ForegroundApp is { } app && !IsApp(window, app))
                throw new ModuleToolInputException($"No keys sent. Bring {app} to the foreground, then try again.");
            if (ModifierKeys.Any(IsDown) || IsDown(keys[^1]))
                throw new ModuleToolInputException("Release the held shortcut or modifier keys before trying again.");
            if (GetForegroundWindow() != window)
                throw new ModuleToolInputException("The active window changed before the shortcut could be sent.");

            cancellationToken.ThrowIfCancellationRequested();
            var sent = (int)SendInput((uint)input.Length, input, InputSize);
            if (sent == input.Length) return;
            if (sent == 0)
                throw new ModuleToolInputException("Windows did not accept the shortcut. Check the desktop and target app permissions.");

            // Release only keys whose down event was accepted and whose up event was not.
            var held = Math.Min(sent, keys.Length) - Math.Max(0, sent - keys.Length);
            var release = ReleaseKeys(keys.Take(held)).ToArray();
            var released = SendInput((uint)release.Length, release, InputSize);
            throw new ModuleToolInputException(released == release.Length
                ? "Windows accepted only part of the shortcut. Check the target app before trying again."
                : "Windows accepted only part of the shortcut. Press and release the shortcut keys to clear any held state.");
        }
    }

    private static bool IsApp(nint window, string expected)
    {
        if (GetWindowThreadProcessId(window, out var processId) == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName + ".exe", expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool IsDown(ushort key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static IEnumerable<Input> ReleaseKeys(IEnumerable<ushort> keys) =>
        keys.Reverse().Select(key => CreateInput(key, release: true));

    private static Input CreateInput(ushort key, bool release)
    {
        const uint inputKeyboard = 1;
        const uint extendedKey = 1;
        const uint keyUp = 2;
        var extended = key is >= 0x21 and <= 0x28 or 0x2C or 0x2D or 0x2E or 0x5B or 0x6F or 0x90 or >= 0xB0 and <= 0xB3;
        return new Input
        {
            Type = inputKeyboard,
            Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = (extended ? extendedKey : 0) | (release ? keyUp : 0) } },
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        // The native INPUT union must also have room for its larger mouse member.
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}
