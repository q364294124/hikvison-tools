using System.Runtime.InteropServices;
using System.Text;

namespace HikDeployTool.Native;

/// <summary>
/// 海康原生库的字符串编组助手。
///
/// 海康 SDK 的所有 <c>char*</c> 参数/字段都使用 **系统 ANSI 代码页**（中文 Windows 上是 GBK），
/// 而不是 UTF-8。.NET Core 默认不带 GBK 编码器（需要额外的 CodePages 包），
/// 所以这里直接调用 Win32 <c>WideCharToMultiByte / MultiByteToWideChar</c>（CP_ACP），
/// 既零依赖，又与原库行为完全一致。
/// </summary>
internal static class NativeText
{
    private const uint CP_ACP = 0;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int WideCharToMultiByte(uint codePage, uint dwFlags, string lpWideCharStr,
        int cchWideChar, byte[]? lpMultiByteStr, int cbMultiByte, IntPtr lpDefaultChar, IntPtr lpUsedDefaultChar);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int MultiByteToWideChar(uint codePage, uint dwFlags, byte[] lpMultiByteStr,
        int cbMultiByte, [Out] char[] lpWideCharStr, int cchWideChar);

    /// <summary>把 .NET 字符串编码成 ANSI(GBK) 字节。</summary>
    public static byte[] ToAnsi(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        int size = WideCharToMultiByte(CP_ACP, 0, text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero);
        if (size <= 0) return [];
        var buf = new byte[size];
        WideCharToMultiByte(CP_ACP, 0, text, text.Length, buf, size, IntPtr.Zero, IntPtr.Zero);
        return buf;
    }

    /// <summary>把 ANSI(GBK) 字节解码成 .NET 字符串（遇到 0 截断，并丢弃非法字符）。</summary>
    public static string FromAnsi(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return string.Empty;
        int len = Array.IndexOf(bytes, (byte)0);
        if (len < 0) len = bytes.Length;
        if (len == 0) return string.Empty;
        int chars = MultiByteToWideChar(CP_ACP, 0, bytes, len, null!, 0);
        if (chars <= 0) return string.Empty;
        var outChars = new char[chars];
        int written = MultiByteToWideChar(CP_ACP, 0, bytes, len, outChars, chars);
        if (written <= 0) return string.Empty;
        return new string(outChars, 0, written).TrimEnd('\0');
    }

    /// <summary>把字符串写入定长 ANSI 缓冲区，自动补 0 并保证不越界。</summary>
    public static void WriteFixed(byte[]? target, string? value)
    {
        if (target == null || target.Length == 0) return;
        Array.Clear(target, 0, target.Length);
        if (string.IsNullOrEmpty(value)) return;
        var raw = ToAnsi(value);
        int copy = Math.Min(raw.Length, target.Length - 1);
        Array.Copy(raw, target, copy);
        target[copy] = 0;
    }

    /// <summary>为 ByValArray 字段分配所需的托管数组（避免 null 导致的编组异常）。</summary>
    public static byte[] Buf(int size) => new byte[size];
}

/// <summary>
/// 把 .NET 字符串固定成 ANSI(GBK) 缓冲区，供 P/Invoke 以 <see cref="IntPtr"/> 传入。
///
/// 为什么不直接用 <c>string</c> + <c>CharSet.Ansi</c>：
/// .NET Core 之后"ANSI 编组到底用哪个代码页"变得不直观（不同平台/版本行为不一致），
/// 而设备名称、日志目录、抓图文件名里都可能出现中文。
/// 自己用 CP_ACP 转一次，行为就完全确定了。
/// </summary>
internal sealed class AnsiString : IDisposable
{
    public IntPtr Pointer { get; }

    public AnsiString(string? text)
    {
        var raw = NativeText.ToAnsi(text);
        Pointer = Marshal.AllocHGlobal(raw.Length + 1);
        if (raw.Length > 0) Marshal.Copy(raw, 0, Pointer, raw.Length);
        Marshal.WriteByte(Pointer, raw.Length, 0);
    }

    public void Dispose()
    {
        if (Pointer != IntPtr.Zero) Marshal.FreeHGlobal(Pointer);
    }
}
