using System.Runtime.InteropServices;

namespace HikDeployTool.Native;

/// <summary>
/// SADP SDK 非托管结构体定义（来源：HCSadpSDK/include/Sadp.h）。
///
/// 【重要】头文件中没有任何 #pragma pack，因此 MSVC 采用默认对齐（Pack=8），
/// 这里统一使用 LayoutKind.Sequential 且 **不显式指定 Pack**，让 CLR 使用同一套
/// 自然对齐规则，才能保证逐字段偏移与 C 端一致。
/// 所有定长 char 数组一律用 byte[] + ByValArray 声明，避免 C# string 编组引入的
/// 隐藏指针/长度前缀问题。
/// </summary>
internal static class SadpConst
{
    // ---- 长度宏 ----
    public const int MAX_DEVICE_CODE = 128;
    public const int MAX_DEVICE_CODE_V31 = 512;
    public const int MAX_QR_CODES = 256;
    public const int MAX_QR_CODES_V31 = 1024;
    public const int MAX_ENCRYPT_CODE = 256;
    public const int MAX_ENCRYPT_CODE_V31 = 1024;
    public const int MAX_GUID_LEN = 128;
    public const int MAX_GUID_LEN_V31 = 512;
    public const int MAX_FILE_PATH_LEN = 260;
    public const int MAX_PASS_LEN = 16;
    public const int MAX_PASS_LEN_V31 = 128;
    public const int MAX_ANSWER_LEN = 256;
    public const int MAX_QUESTION_LIST_LEN = 32;
    public const int MAX_USERNAME_LEN = 32;
    public const int MAX_MAILBOX_LEN = 128;
    public const int MAX_PHONE_NUMBER_LEN = 16;
    public const int MAX_UNLOCK_CODE_RANDOM_LEN = 256;

    // ---- 设备信息回调事件类型（SADP_DEVICE_INFO.iResult） ----
    public const int SADP_ADD = 1;         // 新设备上线
    public const int SADP_UPDATE = 2;      // 在线设备参数/状态变化
    public const int SADP_DEC = 3;         // 设备下线
    public const int SADP_RESTART = 4;     // 下过线的设备重新上线
    public const int SADP_UPDATEFAIL = 5;  // 设备更新失败

    // ---- 配置命令（SADP_GetDeviceConfig / SADP_SetDeviceConfig 的 dwCommand） ----
    // 数值必须与 Sadp.h 第 27~56 行的 #define 完全一致，写错一个就变成"读另一个命令"
    // 且不会报错（设备端只会回"不支持"或给回不一样的结构体），非常难查。
    public const uint SADP_GET_DEVICE_CODE = 1;
    public const uint SADP_GET_ENCRYPT_STRING = 2;
    public const uint SADP_GET_GUID = 5;
    public const uint SADP_RESTORE_INACTIVE = 14;
    public const uint SADP_SET_USER_MAILBOX = 20;
    public const uint SADP_GET_QR_CODES = 21;                  // 邮箱重置二维码（旧版 256 字节）
    public const uint SADP_GET_PASSWORD_RESET_TYPE = 27;
    public const uint SADP_GET_PHONE_QR_CODES = 29;
    public const uint SADP_GET_DEVICE_CODE_V31 = 30;
    public const uint SADP_GET_ENCRYPT_STRING_V31 = 31;
    public const uint SADP_GET_GUID_V31 = 32;
    public const uint SADP_GET_QR_CODES_V31 = 33;              // 邮箱重置二维码（1024 字节）
    public const uint SADP_GET_MANAGER_PHONE_NUMBER = 34;
    public const uint SADP_SET_MANAGER_PHONE_NUMBER = 35;
    public const uint SADP_GET_USER_MAILBOX = 37;

    // ---- 设备过滤规则 ----
    public const uint SADP_DISPLAY_ALL = 0;
    public const uint SADP_FILTER_EZVIZ = 0x01;
    public const uint SADP_FILTER_OEM = 0x02;
    public const uint SADP_FILTER_EZVIZ_OEM = 0x03;
    public const uint SADP_ONLY_DISPLAY_OEM = 0xfffffffd;
    public const uint SADP_ONLY_DISPLAY_EZVIZ = 0xfffffffe;

    // ---- 密码重置类型（SADP_RESET_PARAM_V40/V50 的 byResetType，Sadp.h:464） ----
    public const byte RESET_BY_FILE = 2;        // 导入/导出文件（含"设备码→官方工具→授权文件"闭环）
    public const byte RESET_BY_QRCODE = 3;      // 二维码重置（二维码内容 = 设备码）
    public const byte RESET_BY_GUID = 4;        // GUID 字符串
    public const byte RESET_BY_QUESTION = 5;    // 安全问题
    public const byte RESET_BY_MAILBOX = 6;     // 预留邮箱（需 szMailBoxAddr + szCode）
    public const byte RESET_BY_PHONE = 7;       // 手机扫码（需 szPhoneNo + szCode）
}

/// <summary>SADP_DEVICE_INFO（Sadp.h:151）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_DEVICE_INFO
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public byte[] szSeries;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] szSerialNO;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)] public byte[] szMAC;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] szIPv4Address;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] szIPv4SubnetMask;
    public uint dwDeviceType;
    public uint dwPort;
    public uint dwNumberOfEncoders;
    public uint dwNumberOfHardDisk;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] szDeviceSoftwareVersion;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] szDSPVersion;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] szBootTime;
    public int iResult;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)] public byte[] szDevDesc;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)] public byte[] szOEMinfo;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] szIPv4Gateway;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 46)] public byte[] szIPv6Address;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 46)] public byte[] szIPv6Gateway;
    public byte byIPv6MaskLen;
    public byte bySupport;
    public byte byDhcpEnabled;
    public byte byDeviceAbility;
    public ushort wHttpPort;
    public ushort wDigitalChannelNum;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] szCmsIPv4;
    public ushort wCmsPort;
    public byte byOEMCode;          // 0-基线设备 1-OEM设备
    public byte byActivated;        // 0-已激活 1-未激活
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)] public byte[] szBaseDesc;
    public byte bySupport1;
    public byte byHCPlatform;
    public byte byEnableHCPlatform;
    public byte byEZVIZCode;        // 0-基线设备 1-萤石设备
    public uint dwDetailOEMCode;
    public byte byModifyVerificationCode;
    public byte byMaxBindNum;
    public ushort wOEMCommandPort;
    public byte bySupportWifiRegion;
    public byte byEnableWifiEnhancement;
    public byte byWifiRegion;
    public byte bySupport2;
}

/// <summary>SADP_DEVICE_INFO_V40（Sadp.h:237）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_DEVICE_INFO_V40
{
    public SADP_DEVICE_INFO struSadpDeviceInfo;
    public byte byLicensed;
    public byte bySystemMode;
    public byte byControllerType;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] szEhmoeVersion;
    public byte bySpecificDeviceType;
    public uint dwSDKOverTLSPort;
    public byte bySecurityMode;
    public byte bySDKServerStatus;
    public byte bySDKOverTLSServerStatus;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_USERNAME_LEN + 1)] public byte[] szUserName;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)] public byte[] szWifiMAC;
    public byte byDataFromMulticast;         // 0-链路 1-多播
    public byte bySupportEzvizUnbind;
    public byte bySupportCodeEncrypt;
    public byte bySupportPasswordResetType;
    public byte byEZVIZBindStatus;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] szPhysicalAccessVerification;
    public ushort wHttpsPort;
    public byte bySupportEzvizUserToken;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] szDevDescEx;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] szSerialNOEx;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] szManufacturer;
    public byte bySupportResetPwByPhoneNo;
    public byte byRes1;
    public byte byDiscoveryOnly;             // 1-仅支持SADP发现
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 46)] public byte[] szLocalIPv6Address;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] szLocalIPv4Address;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] szUniformDevID;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)] public byte[] szDevUUID;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] szSecuritySuite;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)] public byte[] byRes;
}

/// <summary>SADP_DEV_NET_PARAM（Sadp.h:290）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_DEV_NET_PARAM
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] szIPv4Address;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] szIPv4SubNetMask;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] szIPv4Gateway;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] szIPv6Address;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] szIPv6Gateway;
    public ushort wPort;
    public byte byIPv6MaskLen;
    public byte byDhcpEnable;
    public ushort wHttpPort;
    public uint dwSDKOverTLSPort;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 122)] public byte[] byRes;
}

/// <summary>SADP_DEV_RET_NET_PARAM（Sadp.h:307）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_DEV_RET_NET_PARAM
{
    public byte byRetryModifyTime;
    public byte bySurplusLockTime;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)] public byte[] byRes;
}

/// <summary>SADP_RESET_PARAM（Sadp.h:371）—— 旧版重置接口入参，兼容 设备码 / 授权文件 两种方式</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_RESET_PARAM
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_ENCRYPT_CODE)] public byte[] szCode;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_FILE_PATH_LEN)] public byte[] szAuthFile;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_PASS_LEN)] public byte[] szPassword;
    public byte byEnableSyncIPCPW;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 511)] public byte[] byRes;
}

/// <summary>SADP_SINGLE_SECURITY_QUESTION_CFG（Sadp.h:418）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_SINGLE_SECURITY_QUESTION_CFG
{
    public uint dwSize;
    public uint dwId;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_ANSWER_LEN)] public byte[] szAnswer;
    public byte byMark;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 127)] public byte[] byRes;
}

/// <summary>SADP_SECURITY_QUESTION_CFG（Sadp.h:427）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_SECURITY_QUESTION_CFG
{
    public uint dwSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_QUESTION_LIST_LEN)]
    public SADP_SINGLE_SECURITY_QUESTION_CFG[] struSecurityQuestion;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_PASS_LEN)] public byte[] szPassword;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] byRes;
}

/// <summary>SADP_RESET_PARAM_V40（Sadp.h:442）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_RESET_PARAM_V40
{
    public uint dwSize;
    public byte byResetType;          // 2-导入/导出文件 3-二维码 4-GUID 5-安全问题 6-邮箱 7-手机扫码
    public byte byEnableSyncIPCPW;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public byte[] byRes2;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_PASS_LEN)] public byte[] szPassword;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_ENCRYPT_CODE)] public byte[] szCode;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_FILE_PATH_LEN)] public byte[] szAuthFile;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_GUID_LEN)] public byte[] szGUID;
    public SADP_SECURITY_QUESTION_CFG struSecurityQuestionCfg;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] byRes;
}

/// <summary>SADP_DEV_LOCK_INFO（Sadp.h:509）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_DEV_LOCK_INFO
{
    public byte byRetryTime;
    public byte bySurplusLockTime;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)] public byte[] byRes;
}

/// <summary>
/// SADP_RESET_PARAM_V50（Sadp.h:456）。
/// 相比 V40，密码/口令/GUID 字段都换成了 V31 长度（128/1024/512），
/// 这是现代设备（要求 8~16 位以上复杂密码）唯一可靠的重置入参结构。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_RESET_PARAM_V50
{
    public uint dwSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_PASS_LEN_V31)] public byte[] szPassword;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_ENCRYPT_CODE_V31)] public byte[] szCode;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_FILE_PATH_LEN)] public byte[] szAuthFile;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_GUID_LEN_V31)] public byte[] szGUID;
    public SADP_SECURITY_QUESTION_CFG struSecurityQuestionCfg;
    public byte byResetType;
    public byte byEnableSyncIPCPW;
    public ushort wGUIDLen;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_MAILBOX_LEN)] public byte[] szMailBoxAddr;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_PHONE_NUMBER_LEN)] public byte[] szPhoneNo;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 364)] public byte[] byRes;
}

/// <summary>SADP_RET_RESET_PARAM_V40（Sadp.h:472）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_RET_RESET_PARAM_V40
{
    public byte byRetryGUIDTime;
    public byte bySurplusLockTime;
    public byte bRetryTimeValid;
    public byte bLockTimeValid;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 252)] public byte[] byRes;
}

/// <summary>SADP_DISCOVERY_ONLY_DEVICE_INFO（Sadp.h:274）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_DISCOVERY_ONLY_DEVICE_INFO
{
    public uint dwPort;
    public uint dwSDKOverTLSPort;
    public uint dwNumberOfEncoders;
    public ushort wHttpPort;
    public byte byEnableHCPlatform;
    public byte byEZVIZBindStatus;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] szDSPVersion;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] szBootTime;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] szPhysicalAccessVerification;
    public byte byVerificationCodeType;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 127)] public byte[] byRes;
}

/// <summary>SADP_SUBNET_INFO_V20（Sadp.h:695）跨网段/网段扫描参数</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_SUBNET_INFO_V20
{
    public uint dwSize;
    public byte byIPType;
    public byte byIPProbeEnable;
    public byte byPortProbeEnable;
    public byte byRes2;
    public ushort wSDKPort;
    public ushort wSDKOverTlsPort;
    public ushort wHttpPort;
    public ushort wHttpsPort;
    public ushort wStartPort;
    public ushort wStopPort;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] byRes1;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] szStartSubnetIP;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] szStopSubnetIP;
    public uint dwIPProbeThreadNum;
    public uint dwPortProbeThreadNum;
    public uint dwIPProbeInterval;
    public uint dwPortProbeInterval;
    public uint dwIPProbeTimeout;
    public uint dwPortProbeConnectTimeout;
    public uint dwProtocolProbeTimeout;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] byRes;
}

/// <summary>SADP_SUBNET_DEVICE_INFO_V20（Sadp.h:685）网段扫描结果</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_SUBNET_DEVICE_INFO_V20
{
    public byte byProtocolType;      // 0-私有协议 1-ISAPI 2-OTAP
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)] public byte[] byRes1;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] szIPv4Address;
    public uint dwPort;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 996)] public byte[] byRes;
}

/// <summary>SADP_SUBNET_STATUS（Sadp.h:733）网段扫描进度</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_SUBNET_STATUS
{
    public uint dwSize;
    public byte byStatus;      // 1-搜索中 2-搜索完成
    public byte byProgress;    // 0~100
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] byRes;
}

/// <summary>SADP_LOGIN_PARAM_V50（Sadp.h:760）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_LOGIN_PARAM_V50
{
    public uint dwSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] szUserName;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] szPassword;
    public uint dwTimeout;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] byRes;
}

/// <summary>SADP_LOGIN_RET_INFO（Sadp.h:769）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_LOGIN_RET_INFO
{
    public uint dwSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] szSerialNumber;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] szDevDescEx;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] szFirmwareVersion;
    public uint dwDeviceType;
    public byte byLockStatus;      // 0-不支持 1-未锁定 2-已锁定
    public byte byRetryTimes;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public byte[] byRes1;
    public uint dwLockTime;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 120)] public byte[] byRes;
}

/// <summary>SADP_DEV_CAPABILITY_V50（Sadp.h:784）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_DEV_CAPABILITY_V50
{
    public uint dwSize;
    public byte bySupportIPv6;
    public byte bySupportModifyIPv6;
    public byte bySupportLock;
    public byte bySupportFactoryReset;
    public byte byHCPlatform;
    public byte byModifyVerificationCode;
    public byte bySupportEzvizUnbind;
    public byte bySupportEzvizUserToken;
    public byte bySupportReset2;
    public byte bySupportGuidReset;
    public byte bySupportQuestionReset;
    public byte bySupportMailReset;
    public byte bySupportPhoneReset;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 111)] public byte[] byRes;
}

/// <summary>LOCAL_IP_INFO（Sadp.h:751）</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LOCAL_IP_INFO
{
    public byte byIPType;
    public byte byScopeID;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] szIP;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] byRes;
}

// ===================================================================================
// 密码救援（设备码 / 二维码）相关结构体
//
// 这几只结构体的共同点是：**首字段是一个"长度/尺寸"字段**。
// 这带来两个必须注意的坑：
//   1) 很多命令要求调用方在入参里把 dwSize 填成 sizeof(结构体)，SDK 用它做版本校验；
//      漏填会被判成"参数错误(2005)"，表现为"读不到设备码"。
//   2) 出参里的 dwXxxSize 才是真实内容长度，**不能**用 strlen 猜 ——
//      内容里可能带不受 \0 约束的字段。
// ===================================================================================

/// <summary>
/// SADP_SAFE_CODE（Sadp.h:315）——旧版设备码，配合 SADP_GET_DEVICE_CODE 使用。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_SAFE_CODE
{
    public uint dwCodeSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_DEVICE_CODE)] public byte[] szDeviceCode;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] byRes;
}

/// <summary>
/// SADP_SAFE_CODE_V31（Sadp.h:323）——现代设备的设备码，512 字节。
/// 设备码是一段 Base64 XML（<c>&lt;code&gt;&lt;cmd&gt;...&lt;/cmd&gt;&lt;encryptedString&gt;...&lt;/code&gt;</c>），
/// 它同时也是"二维码重置"时二维码要承载的内容，见 RESET_BY_QRCODE。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_SAFE_CODE_V31
{
    public uint dwCodeSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_DEVICE_CODE_V31)] public byte[] szDeviceCode;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] byRes;
}

/// <summary>
/// SADP_QR_CODES（Sadp.h:331）——邮箱重置密码二维码（旧版，二维码数据 256 字节）。
/// 入参要用 SADP_USER_MAILBOX 带邮箱地址。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_QR_CODES
{
    public uint dwCodeSize;
    public uint dwMailBoxSize;
    public uint dwServiceMailBoxSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_QR_CODES)] public byte[] szQrCodes;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_MAILBOX_LEN)] public byte[] szMailBoxAddr;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_MAILBOX_LEN)] public byte[] szServiceMailBoxAddr;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] byRes;
}

/// <summary>SADP_QR_CODES_V31（Sadp.h:343）——邮箱重置密码二维码（1024 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_QR_CODES_V31
{
    public uint dwCodeSize;
    public uint dwMailBoxSize;
    public uint dwServiceMailBoxSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_QR_CODES_V31)] public byte[] szQrCodes;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_MAILBOX_LEN)] public byte[] szMailBoxAddr;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_MAILBOX_LEN)] public byte[] szServiceMailBoxAddr;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] byRes;
}

/// <summary>
/// SADP_USER_MAILBOX（Sadp.h:492）——预留邮箱。
/// 读（SADP_GET_USER_MAILBOX）与写（SADP_SET_USER_MAILBOX）用的是同一只结构体，
/// 区别只是读的时候需要 szPassword，写的时候需要邮箱地址。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_USER_MAILBOX
{
    public uint dwSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_PASS_LEN)] public byte[] szPassword;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_MAILBOX_LEN)] public byte[] szMailBoxAddr;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] byRes;
}

/// <summary>
/// SADP_PASSWORD_RESET_TYPE_PARAM（Sadp.h:597）——设备"配置过哪些找回方式"。
/// 这个结构体非常有用：它能在下发重置之前就告诉用户"这台设备到底有没有配过邮箱/安全问题"，
/// 避免用户白试一次、白白消耗设备的尝试次数。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_PASSWORD_RESET_TYPE_PARAM
{
    public uint dwSize;
    public byte byEnable;                  // 0-未配置 1-（GUID/安全问题/安全邮箱/HC）配置过一种或多种
    public byte byGuidEnabled;             // 0-未导出过 GUID 1-导出过
    public byte bySecurityQuestionEnabled; // 0-未配置过安全问题 1-配置过
    public byte bySecurityMailBoxEnabled;  // 0-未配置过安全邮箱 1-配置过
    public byte byHikConnectEnabled;       // 0-未绑定 HikConnect 1-绑定过
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public byte[] byRes1;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] byRes;
}

/// <summary>SADP_PHONE_NUMBER_PARAM（Sadp.h:619）——管理员手机号（读取时需带密码，读不到不等于没设置）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_PHONE_NUMBER_PARAM
{
    public uint dwSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_PASS_LEN)] public byte[] szPassword;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_PHONE_NUMBER_LEN)] public byte[] szPhoneNo;
    public SADP_DEV_LOCK_INFO struLockInfo;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] byRes;
}

/// <summary>
/// SADP_PHONE_QR_CODES（Sadp.h:629）——手机扫码重置密码的二维码原料。
///
/// SDK 只回四样东西：域名、设备型号、二维码密文、有效期，
/// **二维码图像本身要上层自己拼**：
/// <c>sprintf("%s?code=D:%s**%s", szDomainName, szDevModel, szQrCodes)</c>
/// （见 demo/win/DlgResetPWPhone.cpp:110）。
/// 入参要先把 szPhoneNo 填上，ISAPI/OTAP 协议设备会用它去查对应的二维码。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_PHONE_QR_CODES
{
    public uint dwSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_QR_CODES)] public byte[] szDomainName;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] szDevModel;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_QR_CODES_V31)] public byte[] szQrCodes;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_PHONE_NUMBER_LEN)] public byte[] szPhoneNo;
    public uint dwValidTime;             // 二维码剩余有效时间（秒），0 表示不限
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 108)] public byte[] byRes;
}

/// <summary>SADP_GUID_FILE_V31（Sadp.h:641）——V31 GUID，附带导出次数与锁定信息。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SADP_GUID_FILE_V31
{
    public uint dwGUIDSize;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = SadpConst.MAX_GUID_LEN_V31)] public byte[] szGUID;
    public SADP_DEV_LOCK_INFO struDevLockInfo;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] byRes;
}
