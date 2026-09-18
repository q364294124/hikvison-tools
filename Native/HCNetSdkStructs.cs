using System.Runtime.InteropServices;

namespace HikDeployTool.Native;

/// <summary>HCNetSDK 头文件中的常量（来源：HCNetSDKV6.1.11.30/头文件/HCNetSDK.h）</summary>
internal static class NetSdkConst
{
    public const int SERIALNO_LEN = 48;
    public const int NAME_LEN = 32;
    public const int NET_SDK_MAX_FILE_PATH = 256;
    public const int MAX_DEV_ADDR_LEN = 129;
    public const int LOGIN_USERNAME_MAX_LEN = 64;
    public const int LOGIN_PASSWD_MAX_LEN = 64;

    /// <summary>NET_SDK_INIT_CFG_SDK_PATH</summary>
    public const int NET_SDK_INIT_CFG_SDK_PATH = 2;

    public const uint NET_DVR_GET_DEVICECFG = 100;
    public const uint NET_DVR_GET_TIMECFG = 118;

    // 抓图分辨率/质量
    public const ushort PIC_SIZE_AUTO = 0xff;
    public const ushort PIC_QUALITY_LOW = 0;   // 低
    public const ushort PIC_QUALITY_MID = 1;   // 较好
    public const ushort PIC_QUALITY_HIGH = 2;  // 一般
}

/// <summary>NET_DVR_DEVICEINFO_V30（HCNetSDK.h:13649），sizeof = 80</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NET_DVR_DEVICEINFO_V30
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NetSdkConst.SERIALNO_LEN)] public byte[] sSerialNumber;
    public byte byAlarmInPortNum;
    public byte byAlarmOutPortNum;
    public byte byDiskNum;
    public byte byDVRType;
    public byte byChanNum;
    public byte byStartChan;
    public byte byAudioChanNum;
    public byte byIPChanNum;
    public byte byZeroChanNum;
    public byte byMainProto;
    public byte bySubProto;
    public byte bySupport;
    public byte bySupport1;
    public byte bySupport2;
    public ushort wDevType;
    public byte bySupport3;
    public byte byMultiStreamProto;
    public byte byStartDChan;
    public byte byStartDTalkChan;
    public byte byHighDChanNum;
    public byte bySupport4;
    public byte byLanguageType;
    public byte byVoiceInChanNum;
    public byte byStartVoiceInChanNo;
    public byte bySupport5;
    public byte bySupport6;
    public byte byMirrorChanNum;
    public ushort wStartMirrorChanNo;
    public byte bySupport7;
    public byte byRes2;
}

/// <summary>NET_DVR_DEVICEINFO_V40（HCNetSDK.h:13739），sizeof = 344</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NET_DVR_DEVICEINFO_V40
{
    public NET_DVR_DEVICEINFO_V30 struDeviceV30;
    public byte bySupportLock;
    public byte byRetryLoginTime;
    public byte byPasswordLevel;
    public byte byProxyType;
    public uint dwSurplusLockTime;
    public byte byCharEncodeType;
    public byte bySupportDev5;
    public byte bySupport;
    public byte byLoginMode;
    public uint dwOEMCode;
    public int iResidualValidity;
    public byte byResidualValidity;
    public byte bySingleStartDTalkChan;
    public byte bySingleDTalkChanNums;
    public byte byPassWordResetLevel;
    public byte bySupportStreamEncrypt;
    public byte byMarketType;
    public byte byTLSCap;
    public byte byChildManage;
    public byte byPlaybackNewPosCap;
    public byte bySecondaryAuth;
    public byte byHttpsResult;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 233)] public byte[] byRes2;
}

/// <summary>NET_DVR_LOCAL_SDK_PATH（HCNetSDK.h:50593）配置 HCNetSDK 依赖库目录</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NET_DVR_LOCAL_SDK_PATH
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NetSdkConst.NET_SDK_MAX_FILE_PATH)] public byte[] sPath;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] byRes;
}

/// <summary>NET_DVR_JPEGPARA（HCNetSDK.h:12823），sizeof = 4</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NET_DVR_JPEGPARA
{
    public ushort wPicSize;
    public ushort wPicQuality;
}

/// <summary>NET_DVR_XML_CONFIG_INPUT（HCNetSDK.h:41848）ISAPI 透传入参，x64 sizeof = 72</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NET_DVR_XML_CONFIG_INPUT
{
    public uint dwSize;
    public IntPtr lpRequestUrl;
    public uint dwRequestUrlLen;
    public IntPtr lpInBuffer;
    public uint dwInBufferSize;
    public uint dwRecvTimeOut;
    public byte byForceEncrpt;
    public byte byNumOfMultiPart;
    public byte byMIMEType;
    public byte byRes1;
    public uint dwSendTimeOut;
    public IntPtr sPassword;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] byRes;
}

/// <summary>NET_DVR_XML_CONFIG_OUTPUT（HCNetSDK.h:41871）ISAPI 透传出参，x64 sizeof = 72</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NET_DVR_XML_CONFIG_OUTPUT
{
    public uint dwSize;
    public IntPtr lpOutBuffer;
    public uint dwOutBufferSize;
    public uint dwReturnedXMLSize;
    public IntPtr lpStatusBuffer;
    public uint dwStatusSize;
    public IntPtr lpDataBuffer;
    public byte byNumOfMultiPart;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 23)] public byte[] byRes;
}

/// <summary>
/// NET_DVR_PREVIEWINFO（HCNetSDK.h:29237 预览 V40 接口），x64 sizeof = 288。
///
/// 布局核对（默认 8 字节对齐）：
///   lChannel 0 / dwStreamType 4 / dwLinkMode 8 /（补 4 字节对齐）hPlayWnd 16 /
///   bBlocked 24 / bPassbackRecord 28 / byPreviewMode 32 / byStreamID[32] 33 /
///   byProtoType 65 / byRes1 66 / byVideoCodingType 67 / dwDisplayBufNum 68 /
///   byNPQMode 72 / byRecvMetaData 73 / byDataType 74 / byReconnect 75 / byRes[212] 76
///   → 76 + 212 = 288，且 288 % 8 == 0，无需尾部补齐。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NET_DVR_PREVIEWINFO
{
    /// <summary>通道号：DVR 通道号从 1 起，NVR 的 IP 通道从 byStartDChan 起（33 等）。</summary>
    public int lChannel;

    /// <summary>码流类型：0-主码流，1-子码流。</summary>
    public uint dwStreamType;

    /// <summary>取流协议：0-TCP（现场最稳），1-UDP，2-多播，4-RTP/RTSP 等。</summary>
    public uint dwLinkMode;

    /// <summary>播放窗口句柄。设为 0 表示不渲染（纯取流回调），本工具始终给它窗口。</summary>
    public IntPtr hPlayWnd;

    /// <summary>0-非阻塞取流，1-阻塞取流（连接失败会在约 5 秒后返回，适合手动发起的预览）。</summary>
    public uint bBlocked;

    /// <summary>0-不回传录像。</summary>
    public uint bPassbackRecord;

    /// <summary>预览模式：0-正常，1-延迟预览。</summary>
    public byte byPreviewMode;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] byStreamID;

    public byte byProtoType;
    public byte byRes1;
    public byte byVideoCodingType;
    public uint dwDisplayBufNum;
    public byte byNPQMode;
    public byte byRecvMetaData;
    public byte byDataType;
    public byte byReconnect;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 212)] public byte[] byRes;

    /// <summary>按本工具的用法生成一份干净的入参。</summary>
    public static NET_DVR_PREVIEWINFO Create(int channel, uint streamType, IntPtr playWnd)
        => new()
        {
            lChannel = channel,
            dwStreamType = streamType,
            dwLinkMode = 0,              // TCP
            hPlayWnd = playWnd,
            bBlocked = 1,                // 阻塞式：失败能拿到确定的错误码，现场更好排查
            bPassbackRecord = 0,
            byPreviewMode = 0,
            byStreamID = new byte[32],
            byProtoType = 0,
            byRes1 = 0,
            byVideoCodingType = 0,
            dwDisplayBufNum = 0,        // 默认缓冲
            byNPQMode = 0,
            byRecvMetaData = 0,
            byDataType = 0,
            byReconnect = 1,            // 单路自动重连（网络抖动时不至于黑屏到手动重来）
            byRes = new byte[212],
        };
}
