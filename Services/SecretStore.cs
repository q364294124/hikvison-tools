using System.Runtime.InteropServices;
using System.Text;

namespace HikDeployTool.Services;

/// <summary>
/// 本机凭据保护（Windows DPAPI，CurrentUser 范围）。
///
/// 台账里保存的设备登录口令用它加密后才落盘：assets.json 里是密文，
/// 只有同一台电脑、同一个 Windows 账号能解开——文件即使被拷到别的机器
/// 也拿不回明文。这是"入库设备预览免密直连"的安全前提。
/// </summary>
internal static class SecretStore
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    /// <summary>加密成 Base64 密文。空串原样返回（表示"没存过"）。</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(plain);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob
            {
                cbData = bytes.Length,
                pbData = Marshal.UnsafeAddrOfPinnedArrayElement(bytes, 0),
            };
            if (!CryptProtectData(ref input, "HikDeployTool", IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output))
                throw new InvalidOperationException($"DPAPI 加密失败（Win32 错误 {Marshal.GetLastWin32Error()}）");

            try
            {
                var blob = new byte[output.cbData];
                Marshal.Copy(output.pbData, blob, 0, blob.Length);
                return Convert.ToBase64String(blob);
            }
            finally
            {
                // DPAPI 的输出 blob 用 LocalAlloc 分配，FreeHGlobal 内部就是 LocalFree
                Marshal.FreeHGlobal(output.pbData);
            }
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>解开 Base64 密文。解不开（换机器 / 换账号 / 数据损坏）返回空串，不抛异常——</summary>
    /// <remarks>调用方拿空串就走"要求手输口令"的老路，体验平滑降级。</remarks>
    public static string Unprotect(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return string.Empty;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(cipher); }
        catch { return string.Empty; }
        if (bytes.Length == 0) return string.Empty;

        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob
            {
                cbData = bytes.Length,
                pbData = Marshal.UnsafeAddrOfPinnedArrayElement(bytes, 0),
            };
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output))
                return string.Empty;

            try
            {
                var blob = new byte[output.cbData];
                Marshal.Copy(output.pbData, blob, 0, blob.Length);
                return Encoding.UTF8.GetString(blob);
            }
            finally
            {
                Marshal.FreeHGlobal(output.pbData);
            }
        }
        finally
        {
            pin.Free();
        }
    }
}
