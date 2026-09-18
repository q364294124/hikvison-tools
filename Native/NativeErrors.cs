using System.Runtime.InteropServices;
using System.Text;

namespace HikDeployTool.Native;

/// <summary>SADP 错误码 → 中文说明（Sadp.h:86-138，基数 2000）</summary>
internal static class SadpError
{
    private static readonly Dictionary<uint, string> Map = new()
    {
        [0] = "成功",
        [2001] = "资源分配错误",
        [2002] = "SADP 未启动",
        [2003] = "本机没有可用网卡",
        [2004] = "获取网卡信息失败",
        [2005] = "参数错误",
        [2006] = "打开网卡失败（通常是没有管理员权限，或网卡被禁用）",
        [2007] = "发送数据失败",
        [2008] = "系统接口调用失败",
        [2009] = "设备拒绝处理该请求（常见于口令不对，或设备绑定了萤石云/Hik-Connect 平台）",
        [2010] = "安装 NPF 服务失败（需要管理员权限）",
        [2011] = "设备响应超时",
        [2012] = "创建 socket 失败",
        [2013] = "绑定 socket 失败",
        [2014] = "加入多播组失败",
        [2015] = "网络发送出错",
        [2016] = "网络接收出错",
        [2017] = "多播 XML 解析出错",
        [2018] = "设备已锁定，请稍后再试",
        [2019] = "设备未激活",
        [2020] = "密码为高风险密码，请更换强度更高的密码",
        [2021] = "设备已激活",
        [2022] = "加密串为空",
        [2023] = "导出文件已超期，请重新获取",
        [2024] = "密码错误",
        [2025] = "安全问题答案太长",
        [2026] = "无效的 GUID",
        [2027] = "安全问题答案错误",
        [2028] = "安全问题个数配置错误",
        [2030] = "加载 WinPcap/Npcap 失败（缺少 wpcap.dll，请安装 Npcap）",
        [2033] = "非法验证码",
        [2034] = "绑定错误的设备",
        [2035] = "超过最大绑定个数",
        [2036] = "邮箱不存在",
        [2038] = "未设置用于重置密码的邮箱",
        [2039] = "重置口令错误",
        [2040] = "没有权限：需要以管理员身份运行才能操作网卡",
        [2041] = "获取加密用的交换码失败",
        [2042] = "生成 RSA 公私钥失败",
        [2043] = "BASE64 编码错误",
        [2044] = "BASE64 解码错误",
        [2045] = "AES 加密失败",
        [2046] = "未设置安全手机号",
        [2047] = "缓冲区长度不足",
        [2048] = "无效的网段范围（起止 IP 颠倒，或超过 4096 个地址）",
        [2049] = "SDK 内部缓存中找不到该设备（设备可能已下线）",
        [2050] = "探测资源忙，请稍后再试",
        [2051] = "该链路协议设备不支持此操作",
        [2052] = "中断超时等待",
        [2053] = "仅发现设备尚未登录",
        [2054] = "仅发现设备不支持该操作",
        [2055] = "已达到业务最大并发数量上限",
    };

    public static string Describe(uint code)
        => Map.TryGetValue(code, out var text) ? $"{text}（SADP {code}）" : $"未知错误（SADP {code}）";
}

/// <summary>HCNetSDK 错误码 → 中文说明（只列交付/验收场景最常见的部分）</summary>
internal static class NetSdkError
{
    private static readonly Dictionary<uint, string> Map = new()
    {
        [1] = "用户名密码错误",
        [2] = "权限不足",
        [3] = "SDK 未初始化",
        [4] = "通道号错误",
        [5] = "资源不足，请减少并发或重启程序",
        [6] = "调用顺序错误",
        [7] = "连接设备失败，设备不在线或网络不可达",
        [8] = "向设备发送失败",
        [9] = "从设备接收数据失败",
        [10] = "接收数据超时，请检查网络或设备状态",
        [11] = "向设备发送数据失败",
        [12] = "导出数据出错",
        [13] = "设备返回数据错误",
        [14] = "找不到指定文件",
        [17] = "参数错误",
        [18] = "创建文件失败",
        [19] = "该用户不存在",
        [20] = "密码不匹配",
        [21] = "该用户无权限",
        [22] = "缓冲区不足",
        [23] = "输入的 IP 地址无效",
        [24] = "该功能不支持",
        [25] = "设备 IP 地址冲突",
        // 官方注释只写"DVR操作失败"，很宽泛。登录场景报 29 的高频原因
        // （按现场出现频率排）：设备登录连接数被其它客户端占满（iVMS-4200 /
        // 网页端挂着不断开）→ SDK 端口不对（不是 8000）→ 设备忙。
        // 描述里直接给排查方向，现场不用再翻文档。
        [29] = "设备操作失败（登录时常见：设备被其它客户端占用或连接数已满，先关掉 iVMS-4200/网页端再试；也检查 SDK 端口是否为 8000）",
        [30] = "操作超时",
        [33] = "该用户不存在",
        [34] = "子连接失败",
        [35] = "主连接失败",
        [36] = "超过了最大连接数",
        [37] = "预览失败",
        [40] = "用户的 IP 地址不匹配",
        [41] = "用户不存在",
        [42] = "该用户已经被占用",
        [45] = "设备未初始化",
        [46] = "设备初始化失败",
        [47] = "通道已打开",
        [48] = "通道未打开",
        [49] = "请求设备失败，设备未启动",
        [50] = "该用户已经登录",
        [51] = "未登录",
        [52] = "设备锁定中，请稍后再试",
        [53] = "报警通道错误",
        [55] = "配置文件写入失败",
        [62] = "该用户 IP 地址已存在",
        [64] = "该用户 IP 地址不存在",
        [65] = "该用户 IP 地址已被占用",
        [66] = "该用户已登录",
        [67] = "该用户未登录",
        [96] = "设备资源不足",
        [97] = "子设备连接失败",
        [100] = "调用接口失败",
        [102] = "设备锁定",
        [103] = "密码错误",
        [104] = "密码过期",
        [105] = "该用户不存在",
        [106] = "密码强度不够",
        [107] = "该用户已被锁定",
        [108] = "该 IP 已被锁定",
        [153] = "设备升级失败",
        [161] = "语言不匹配",
        [162] = "该用户密码已重置，请重新设置",
        [163] = "预览失败",
        [167] = "设备不支持私有协议",
    };

    public static string Describe(uint code)
    {
        if (Map.TryGetValue(code, out var text)) return $"{text}（SDK {code}）";

        // 优先向 SDK 自身要中文说明
        try
        {
            int errNo = (int)code;
            var ptr = HCNetSdkNative.NET_DVR_GetErrorMsg(ref errNo);
            if (ptr != IntPtr.Zero)
            {
                var msg = Marshal.PtrToStringAnsi(ptr);
                if (!string.IsNullOrWhiteSpace(msg)) return $"{msg}（SDK {code}）";
            }
        }
        catch
        {
            // 忽略：某些版本该接口不可用
        }
        return $"未知错误（SDK {code}）";
    }
}

/// <summary>
/// 结构体布局自检。
///
/// C# 与 C 的结构体只要"字段顺序 + 类型 + 定长数组长度"一致、对齐规则一致，
/// 托管大小就必须等于按头文件手工推算的字节数。这里把推算值固化下来，
/// 任何一处转录错误（数组长度写错、字段漏写）都会在启动时被立刻发现，
/// 而不是等运行到某个接口才表现为"数据错位/内存越界"。
/// </summary>
internal static class StructLayoutCheck
{
    public sealed record Item(string Name, int Actual, int Expected)
    {
        public bool Ok => Actual == Expected;
    }

    public static List<Item> Run()
    {
        return
        [
            new("SADP_DEVICE_INFO", Marshal.SizeOf<SADP_DEVICE_INFO>(), 500),
            new("SADP_DEVICE_INFO_V40", Marshal.SizeOf<SADP_DEVICE_INFO_V40>(), 1016),
            new("SADP_DEV_NET_PARAM", Marshal.SizeOf<SADP_DEV_NET_PARAM>(), 440),
            new("SADP_DEV_RET_NET_PARAM", Marshal.SizeOf<SADP_DEV_RET_NET_PARAM>(), 128),
            new("SADP_RESET_PARAM", Marshal.SizeOf<SADP_RESET_PARAM>(), 1044),
            new("SADP_SINGLE_SECURITY_QUESTION_CFG", Marshal.SizeOf<SADP_SINGLE_SECURITY_QUESTION_CFG>(), 392),
            new("SADP_SECURITY_QUESTION_CFG", Marshal.SizeOf<SADP_SECURITY_QUESTION_CFG>(), 13076),
            new("SADP_RESET_PARAM_V40", Marshal.SizeOf<SADP_RESET_PARAM_V40>(), 14256),
            new("SADP_RESET_PARAM_V50", Marshal.SizeOf<SADP_RESET_PARAM_V50>(), 15516),
            new("SADP_DEV_LOCK_INFO", Marshal.SizeOf<SADP_DEV_LOCK_INFO>(), 128),
            new("SADP_RET_RESET_PARAM_V40", Marshal.SizeOf<SADP_RET_RESET_PARAM_V40>(), 256),
            new("SADP_DISCOVERY_ONLY_DEVICE_INFO", Marshal.SizeOf<SADP_DISCOVERY_ONLY_DEVICE_INFO>(), 256),
            new("SADP_SUBNET_INFO_V20", Marshal.SizeOf<SADP_SUBNET_INFO_V20>(), 276),
            new("SADP_SUBNET_DEVICE_INFO_V20", Marshal.SizeOf<SADP_SUBNET_DEVICE_INFO_V20>(), 1024),
            new("SADP_SUBNET_STATUS", Marshal.SizeOf<SADP_SUBNET_STATUS>(), 12),
            new("SADP_LOGIN_PARAM_V50", Marshal.SizeOf<SADP_LOGIN_PARAM_V50>(), 264),
            new("SADP_LOGIN_RET_INFO", Marshal.SizeOf<SADP_LOGIN_RET_INFO>(), 312),
            new("SADP_DEV_CAPABILITY_V50", Marshal.SizeOf<SADP_DEV_CAPABILITY_V50>(), 128),
            new("LOCAL_IP_INFO", Marshal.SizeOf<LOCAL_IP_INFO>(), 178),
            new("SADP_SAFE_CODE", Marshal.SizeOf<SADP_SAFE_CODE>(), 260),
            new("SADP_SAFE_CODE_V31", Marshal.SizeOf<SADP_SAFE_CODE_V31>(), 1028),
            new("SADP_QR_CODES", Marshal.SizeOf<SADP_QR_CODES>(), 652),
            new("SADP_QR_CODES_V31", Marshal.SizeOf<SADP_QR_CODES_V31>(), 1548),
            new("SADP_USER_MAILBOX", Marshal.SizeOf<SADP_USER_MAILBOX>(), 276),
            new("SADP_PASSWORD_RESET_TYPE_PARAM", Marshal.SizeOf<SADP_PASSWORD_RESET_TYPE_PARAM>(), 76),
            new("SADP_PHONE_NUMBER_PARAM", Marshal.SizeOf<SADP_PHONE_NUMBER_PARAM>(), 292),
            new("SADP_PHONE_QR_CODES", Marshal.SizeOf<SADP_PHONE_QR_CODES>(), 1444),
            new("SADP_GUID_FILE_V31", Marshal.SizeOf<SADP_GUID_FILE_V31>(), 900),
            new("NET_DVR_DEVICEINFO_V30", Marshal.SizeOf<NET_DVR_DEVICEINFO_V30>(), 80),
            new("NET_DVR_DEVICEINFO_V40", Marshal.SizeOf<NET_DVR_DEVICEINFO_V40>(), 344),
            new("NET_DVR_LOCAL_SDK_PATH", Marshal.SizeOf<NET_DVR_LOCAL_SDK_PATH>(), 384),
            new("NET_DVR_JPEGPARA", Marshal.SizeOf<NET_DVR_JPEGPARA>(), 4),
            new("NET_DVR_PREVIEWINFO", Marshal.SizeOf<NET_DVR_PREVIEWINFO>(), 288),
            new("NET_DVR_USER_LOGIN_INFO", Marshal.SizeOf<NET_DVR_USER_LOGIN_INFO>(), 416),
            new("NET_DVR_XML_CONFIG_INPUT", Marshal.SizeOf<NET_DVR_XML_CONFIG_INPUT>(), 72),
            new("NET_DVR_XML_CONFIG_OUTPUT", Marshal.SizeOf<NET_DVR_XML_CONFIG_OUTPUT>(), 72),
        ];
    }
}
