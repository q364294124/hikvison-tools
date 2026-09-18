using System.Runtime.InteropServices;

namespace HikDeployTool.Native;

/// <summary>
/// Sadp.dll 的 P/Invoke 声明（来源：HCSadpSDK/include/Sadp.h 第 825-893 行）。
///
/// 调用约定：头文件中 <c>#define CALLBACK __stdcall</c>，在 Win64 下 __stdcall 被忽略
/// （Win64 只有一种调用约定），导出名为未修饰的 "SADP_xxx"。
/// 本工具仅发布 x64 版本，故不需要 x86 的 "_Name@N" 修饰名。
/// </summary>
internal static class SadpNative
{
    internal const string Dll = "Sadp.dll";

    /// <summary>设备发现回调（V40）：lpDeviceInfo 指向 SADP_DEVICE_INFO_V40。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void DeviceFindCallbackV40(IntPtr lpDeviceInfo, IntPtr pUserData);

    /// <summary>网段扫描回调（V20）：lpDeviceInfo 指向 SADP_SUBNET_DEVICE_INFO_V20。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void SubnetDeviceFindCallbackV20(IntPtr lpDeviceInfo, IntPtr pUserData);

    // ------------------------- 服务生命周期 -------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_Start_V40(DeviceFindCallbackV40 pDeviceFindCallBack, int bInstallNPF, IntPtr pUserData);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int SADP_Stop();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int SADP_Clearup();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint SADP_GetLastError();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint SADP_GetSadpVersion();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int SADP_SetLogToFile(int nLogLevel, IntPtr strLogDir, int bAutoDel);

    // ------------------------- 设备操作 -------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_ActivateDevice(string sUniformDevID, string sCommand);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_ModifyDeviceNetParam(string sUniformDevID, string sPassword, ref SADP_DEV_NET_PARAM lpNetParam);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_ModifyDeviceNetParam_V40(string sUniformDevID, string sPassword,
        ref SADP_DEV_NET_PARAM lpNetParam, out SADP_DEV_RET_NET_PARAM lpRetNetParam, uint dwOutBuffSize);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_ResetDefaultPasswd(string sUniformDevID, string sCommand);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_ResetPasswd(string sUniformDevID, ref SADP_RESET_PARAM pResetParam);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_ResetPasswd_V40(string sUniformDevID, ref SADP_RESET_PARAM_V40 pResetParam,
        out SADP_RET_RESET_PARAM_V40 pRetResetParam);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_ResetPasswd_V50(string sUniformDevID, ref SADP_RESET_PARAM_V50 pResetParam,
        ref SADP_DEV_LOCK_INFO pLockInfo);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_GetDeviceConfig(string sUniformDevID, uint dwCommand, IntPtr lpInBuffer,
        uint dwinBuffSize, IntPtr lpOutBuffer, uint dwOutBuffSize);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_SetDeviceConfig(string sUniformDevID, uint dwCommand, IntPtr lpInBuffer,
        uint dwInBuffSize, IntPtr lpOutBuffer, uint dwOutBuffSize);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_GetDeviceConfigByMAC(string sUniformDevID, uint dwCommand, IntPtr lpInBuffer,
        uint dwinBuffSize, IntPtr lpOutBuffer, uint dwOutBuffSize);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_GetDiscoveryOnlyDeviceInfo(string sUniformDevID, out SADP_DISCOVERY_ONLY_DEVICE_INFO pDeviceInfo);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_SetLocalIP(string sUniformDevID, ref LOCAL_IP_INFO pLocalIPInfo);

    // ------------------------- 主动搜索 / 过滤 -------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int SADP_SendInquiry();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern void SADP_SetAutoRequestInterval(uint dwInterval);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int SADP_SetDeviceFilterRule(uint dwFilterRule, IntPtr lpInBuff, uint dwInBuffLen);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern void SADP_CancelTimeOutWait();

    // ------------------------- 跨网段 / 网段扫描 -------------------------
    //
    // 注意函数名对应关系（Sadp.h 第 856~862 行）：
    //   SADP_InquirySpecificSubnet(const SADP_SUBNET_INFO *)            ← 旧版结构体，未使用
    //   SADP_InquirySpecificSubnetAllDevice(const SADP_SUBNET_INFO_V20 *) ← 本工程使用
    // 两者名字相近但参数结构体不同，写错名字运行时才会炸（找不到入口点），务必对齐。

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int SADP_InquirySpecificSubnetAllDevice(ref SADP_SUBNET_INFO_V20 pSubnetInfoV20);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int SADP_GetInquirySpecificSubnetAllDeviceStatus(ref SADP_SUBNET_STATUS pStatus);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern int SADP_StopInquirySpecificSubnetAllDevice();

    // ------------------------- V50 带认证能力 -------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_LoginDevice_V50(string sUniformDevID, ref SADP_LOGIN_PARAM_V50 pLoginParam,
        out SADP_LOGIN_RET_INFO pLoginRetInfo);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_LogoutDevice_V50(string sUniformDevID);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int SADP_GetDeviceCapabilities_V50(string sUniformDevID, out SADP_DEV_CAPABILITY_V50 pDevCapabilities);
}
