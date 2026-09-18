using System.Net;

namespace HikDeployTool.Services;

/// <summary>IPv4 小工具：地址递增、同网段判断、广播地址计算。</summary>
public static class IpHelper
{
    public static bool IsValid(string? ip)
        => !string.IsNullOrWhiteSpace(ip)
           && IPAddress.TryParse(ip.Trim(), out var addr)
           && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    /// <summary>把 IPv4 文本转成 uint（大端语义）。</summary>
    public static bool TryToUInt(string? ip, out uint value)
    {
        value = 0;
        if (!IPAddress.TryParse(ip?.Trim() ?? string.Empty, out var addr)) return false;
        var b = addr.GetAddressBytes();
        if (b.Length != 4) return false;
        value = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        return true;
    }

    public static string FromUInt(uint value)
        => $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";

    /// <summary>在基址上加偏移，溢出 32 位时返回 false（避免绕回 0.0.0.0）。</summary>
    public static bool TryAddOffset(string baseIp, long offset, out string result)
    {
        result = string.Empty;
        if (!TryToUInt(baseIp, out var v)) return false;
        long next = v + offset;
        if (next < 0 || next > uint.MaxValue) return false;
        result = FromUInt((uint)next);
        return true;
    }

    /// <summary>掩码前缀长度（如 255.255.255.0 → 24），掩码非法时返回 -1。</summary>
    public static int MaskToPrefixLength(string? mask)
    {
        if (!TryToUInt(mask, out var v)) return -1;
        int bits = 0;
        bool zeroSeen = false;
        for (int i = 31; i >= 0; i--)
        {
            bool one = ((v >> i) & 1) == 1;
            if (one)
            {
                if (zeroSeen) return -1;   // 出现 1 在 0 之后 → 掩码不连续
                bits++;
            }
            else zeroSeen = true;
        }
        return bits;
    }

    public static bool IsSameSubnet(string ip1, string ip2, string mask)
    {
        if (!TryToUInt(ip1, out var a) || !TryToUInt(ip2, out var b) || !TryToUInt(mask, out var m)) return false;
        return (a & m) == (b & m);
    }

    /// <summary>该网段的广播地址（用于提示扫描范围）。</summary>
    public static string BroadcastOf(string ip, string mask)
    {
        if (!TryToUInt(ip, out var a) || !TryToUInt(mask, out var m)) return string.Empty;
        return FromUInt((a & m) | ~m);
    }
}
