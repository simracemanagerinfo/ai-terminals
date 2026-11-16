using System.Runtime.InteropServices;

Console.WriteLine("KEYPROBE_READY");
Console.Out.Flush();
var handle = GetStdHandle(-10);
if (handle == IntPtr.Zero || handle == new IntPtr(-1))
    throw new Exception("No console input handle");
var buffer = new INPUT_RECORD[1];
for (var count = 0; count < 30; count++)
{
    if (!ReadConsoleInputW(handle, buffer, 1, out uint read))
        throw new Exception("ReadConsoleInput failed: " + Marshal.GetLastWin32Error());
    if (read != 1 || buffer[0].EventType != 1 || !buffer[0].KeyEvent.KeyDown) continue;
    var key = buffer[0].KeyEvent;
    Console.WriteLine($"KEYPROBE_EVENT VK={key.VirtualKeyCode} CHAR={(int)key.UnicodeChar} CONTROL={key.ControlKeyState}");
    Console.Out.Flush();
    if (key.VirtualKeyCode == 74 && key.UnicodeChar == '\n' && (key.ControlKeyState & 8) != 0)
    {
        Console.WriteLine("KEYPROBE_OK");
        Console.Out.Flush();
        return;
    }
}
throw new Exception("Did not receive Win32 Ctrl+J key event");

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr GetStdHandle(int handleType);
[DllImport("kernel32.dll", SetLastError = true)]
static extern bool ReadConsoleInputW(
    IntPtr input, [Out] INPUT_RECORD[] buffer, uint length, out uint read);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct KEY_EVENT_RECORD
{
    [MarshalAs(UnmanagedType.Bool)] public bool KeyDown;
    public ushort RepeatCount;
    public ushort VirtualKeyCode;
    public ushort VirtualScanCode;
    public char UnicodeChar;
    public uint ControlKeyState;
}

[StructLayout(LayoutKind.Explicit, Size = 20)]
struct INPUT_RECORD
{
    [FieldOffset(0)] public ushort EventType;
    [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
}
