using System.Runtime.InteropServices;
namespace m3nuxa.App;
internal static class KeyboardSender
{
    internal static void Send(string shortcut)
    {
        if (!HotkeyParser.TryParse(shortcut, out var modifiers, out var key)) throw new InvalidOperationException("Некорректное сочетание.");
        var keys = new List<ushort>();
        if ((modifiers & 2) != 0) keys.Add(0x11);
        if ((modifiers & 1) != 0) keys.Add(0x12);
        if ((modifiers & 4) != 0) keys.Add(0x10);
        if ((modifiers & 8) != 0) keys.Add(0x5b);
        keys.Add((ushort)key);
        var inputs = keys.Select(k => Input.Create(k, 0)).Concat(keys.AsEnumerable().Reverse().Select(k => Input.Create(k, 2))).ToArray();
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            var release = keys.AsEnumerable().Reverse().Select(k => Input.Create(k, 2)).ToArray();
            SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
            throw new InvalidOperationException("Не удалось отправить сочетание.");
        }
    }
    internal static void InsertText(string text)
    {
        foreach (var character in text)
        {
            var down = Input.Create(0, 4); down.Data.Keyboard.Scan = character;
            var up = Input.Create(0, 6); up.Data.Keyboard.Scan = character;
            var pair = new[] { down, up };
            if (SendInput(2, pair, Marshal.SizeOf<Input>()) != 2) throw new InvalidOperationException("Не удалось вставить весь текст.");
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input
    {
        public uint Type; public InputUnion Data;
        public static Input Create(ushort key, uint flags) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyData { Key = key, Flags = flags } } };
    }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public KeyData Keyboard;
        [FieldOffset(0)] public MouseData Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyData { public ushort Key, Scan; public uint Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Data, Flags, Time; public nint Extra; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
}
