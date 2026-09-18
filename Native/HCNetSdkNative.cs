using System.Runtime.InteropServices;

namespace HikDeployTool.Native;

/// <summary>
/// HCNetSDK.dll 的 P/Invoke 声明。
/// 本工具取"交付运维/验收 + 实时预览"需要的子集：
/// 初始化、登录、ISAPI 透传、抓图、登出、预览（RealPlay_V40 / StopRealPlay）。
/// 预览渲染交给 SDK 内部的 PlayCtrl（hPlayWnd 模式），不做解码回调。
/// </summary>
internal static class HCNetSdkNative
{
    internal const string Dll = "HCNetSDK.dll";

    // ------------------------- 生命周期 -------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int NET_DVR_Init();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int NET_DVR_Cleanup();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint NET_DVR_GetLastError();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern IntPtr NET_DVR_GetErrorMsg(ref int pErrorNo);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int NET_DVR_SetSDKInitCfg(int enumType, IntPtr lpInBuff);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int NET_DVR_SetConnectTime(uint dwWaitTime, uint dwTryTimes);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int NET_DVR_SetReconnect(uint dwInterval, int bEnableRecon);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int NET_DVR_SetLogToFile(uint nLogLevel, IntPtr strLogDir, int bAutoDel);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint NET_DVR_GetSDKVersion();

    // ------------------------- 登录 / 登出 -------------------------

    // 字符串参数一律走 IntPtr + AnsiString（CP_ACP），避免 .NET 版本间 ANSI 编组行为差异
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int NET_DVR_Login_V30(IntPtr sDVRIP, ushort wDVRPort, IntPtr sUserName,
        IntPtr sPassword, ref NET_DVR_DEVICEINFO_V30 lpDeviceInfo);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int NET_DVR_Login_V40(ref NET_DVR_USER_LOGIN_INFO pLoginInfo, ref NET_DVR_DEVICEINFO_V40 lpDeviceInfo);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int NET_DVR_Logout(int lUserID);

    // ------------------------- 配置 / 能力 / 透传 -------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int NET_DVR_GetDVRConfig(int lUserID, uint dwCommand, int lChannel,
        IntPtr lpOutBuffer, uint dwOutBufferSize, ref uint lpBytesReturned);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int NET_DVR_GetDeviceAbility(int lUserID, uint dwAbilityType, IntPtr pInBuf,
        uint dwInLength, IntPtr pOutBuf, uint dwOutLength);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int NET_DVR_STDXMLConfig(int lUserID, ref NET_DVR_XML_CONFIG_INPUT lpInputParam,
        ref NET_DVR_XML_CONFIG_OUTPUT lpOutputParam);

    // ------------------------- 抓图 -------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int NET_DVR_CaptureJPEGPicture(int lUserID, int lChannel,
        ref NET_DVR_JPEGPARA lpJpegPara, IntPtr sPicFileName);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int NET_DVR_CaptureJPEGPicture_NEW(int lUserID, int lChannel,
        ref NET_DVR_JPEGPARA lpJpegPara, byte[] sJpegPicBuffer, uint dwPicSize, ref uint lpSizeReturned);

    // ------------------------- 实时预览 -------------------------
    // 不需要回调（fRealDataCallBack_V30 传 NULL）：SDK 内部直接用 PlayCtrl
    // 把码流解码渲染到 hPlayWnd，本工程不自己解码，也就不必 P/Invoke PlayM4_*。

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int NET_DVR_RealPlay_V40(int lUserID, ref NET_DVR_PREVIEWINFO lpPreviewInfo,
        IntPtr fRealDataCallBack_V30, IntPtr pUser);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int NET_DVR_StopRealPlay(int lRealHandle);

    // ------------------------- 音频 -------------------------

    /// <summary>
    /// 打开客户端预览音频（HCNetSDK.h）。hPlayWnd 解码模式下音频不走
    /// PlayM4 的单通道开关，用这个全局开关：对所有在播画面生效，
    /// 同一时刻 SDK 只混播一路声音（多格在播时是哪路由 SDK 决定）。
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern bool NET_DVR_ClientAudioStart();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern bool NET_DVR_ClientAudioStop();

    // ------------------------- 云台 -------------------------

    /// <summary>云台控制（按住转动）：dwStop 0-开始 1-停止；dwSpeed 1-7。lUserID 登录句柄，lChannel 通道号。</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern bool NET_DVR_PTZControlWithSpeed_Other(int lUserID, int lChannel, uint dwPTZCommand, uint dwStop, uint dwSpeed);
}

/// <summary>NET_DVR_USER_LOGIN_INFO（HCNetSDK.h:13785），x64 版布局</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NET_DVR_USER_LOGIN_INFO
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NetSdkConst.MAX_DEV_ADDR_LEN)] public byte[] sDeviceAddress;
    public byte byUseTransport;
    public ushort wPort;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NetSdkConst.LOGIN_USERNAME_MAX_LEN)] public byte[] sUserName;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NetSdkConst.LOGIN_PASSWD_MAX_LEN)] public byte[] sPassword;
    public IntPtr cbLoginResult;
    public IntPtr pUser;
    public int bUseAsynLogin;
    public byte byProxyType;
    public byte byUseUTCTime;
    public byte byLoginMode;
    public byte byHttps;
    public int iProxyID;
    public byte byVerifyMode;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public byte[] byRes1;
    public IntPtr cbLoginResultV40;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 109)] public byte[] byRes3;
}
