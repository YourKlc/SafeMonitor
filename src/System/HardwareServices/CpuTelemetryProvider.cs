using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SafeMonitor.src.SystemServices
{
    /// <summary>
    /// CPU 功耗 / 温度采集（用户态，不使用第三方内核驱动）。
    ///
    /// 按 CPU 厂商分派到官方用户态 SDK：
    ///  - AMD  → AMD Ryzen Master Monitoring SDK（AMDRyzenMasterMonitoringDLL.dll）
    ///            可读 温度 + 功耗(PPT)。需要安装 AMD Ryzen Master 驱动（SDK 自带）。
    ///  - Intel → Intel PresentMon Service（PresentMonAPI2.dll）
    ///            仅能读 CPU 功耗（PresentMon 不提供用户态 CPU 温度）。需要安装 PresentMon Service。
    ///
    /// 两个 SDK 都是动态加载：若机器上没有安装对应厂商软件/驱动，
    /// 所有读取都会安全地返回 null（UI 显示 "--"），不会抛异常或崩溃。
    ///
    /// 注意：本实现未经实机验证（无法在此环境测试），
    /// 尤其是 Intel PresentMon 的动态查询 blob 解析与 CPU 功耗指标枚举，
    /// 建议在真实硬件上校验后再发布。
    /// </summary>
    public sealed class CpuTelemetryProvider : IDisposable
    {
        public enum Vendor { Unknown, Intel, Amd }

        public Vendor CpuVendor { get; }

        private readonly AmdRyzenMasterBackend _amd;
        private readonly IntelPresentMonBackend _intel;

        // 采样节流：两个 SDK 都不宜高频调用（AMD 建议 ~1 次/秒）
        private readonly object _sync = new();
        private DateTime _lastSample = DateTime.MinValue;
        private float? _cachedTemp;
        private float? _cachedPower;
        private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(900);

        public CpuTelemetryProvider()
        {
            CpuVendor = DetectVendor();
            try
            {
                if (CpuVendor == Vendor.Amd)
                    _amd = AmdRyzenMasterBackend.TryCreate();
                else if (CpuVendor == Vendor.Intel)
                    _intel = IntelPresentMonBackend.TryCreate();
            }
            catch { /* 任何初始化失败都保持不可用状态 */ }
        }

        public bool IsAvailable => (_amd != null && _amd.IsAvailable) || (_intel != null && _intel.IsAvailable);

        /// <summary>CPU 温度（℃）。仅 AMD 可用；Intel 无用户态途径，返回 null。</summary>
        public float? GetTemperature()
        {
            Sample();
            return _cachedTemp;
        }

        /// <summary>CPU 功耗（W）。AMD / Intel 均可（依赖各自 SDK）。</summary>
        public float? GetPower()
        {
            Sample();
            return _cachedPower;
        }

        private void Sample()
        {
            lock (_sync)
            {
                if (DateTime.Now - _lastSample < SampleInterval) return;
                _lastSample = DateTime.Now;

                float? temp = null, power = null;
                try
                {
                    if (_amd != null)
                    {
                        temp = _amd.GetTemperature();
                        power = _amd.GetPower();
                    }
                    else if (_intel != null)
                    {
                        power = _intel.GetPower(); // Intel 无用户态温度
                    }
                }
                catch { }

                // 仅在新值有效时刷新缓存，避免单次采样失败导致显示抖动
                if (temp.HasValue) _cachedTemp = temp;
                if (power.HasValue) _cachedPower = power;
            }
        }

        private static Vendor DetectVendor()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                string vendor = key?.GetValue("VendorIdentifier") as string ?? "";
                if (vendor.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0) return Vendor.Intel;
                if (vendor.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0) return Vendor.Amd;
            }
            catch { }
            return Vendor.Unknown;
        }

        public void Dispose()
        {
            try { _amd?.Dispose(); } catch { }
            try { _intel?.Dispose(); } catch { }
        }

        // ==================================================================
        // AMD：Ryzen Master Monitoring SDK
        // ==================================================================
        private sealed class AmdRyzenMasterBackend : IDisposable
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct RMSystemInfo
            {
                public int Privileges;
                public int SupportedOS;
                public int DriverService;
                public int SupportedProc;
                public int AuthenticAMD;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct RMQuickStats
            {
                public int Init;
                public float fPPTValue;        // 功耗 (W)
                public float fEDCValue_VDD;
                public float fTDCValue_VDD;
                public double dTemperature;    // 温度 (℃)
                public double dPeakCoreVoltage;
                public double dAvgCoreVoltage;
            }

            [DllImport("AMDRyzenMasterMonitoringDLL.dll", CallingConvention = CallingConvention.Cdecl)]
            private static extern RMSystemInfo IsSupported();
            [DllImport("AMDRyzenMasterMonitoringDLL.dll", CallingConvention = CallingConvention.Cdecl)]
            private static extern bool PlatformInit();
            [DllImport("AMDRyzenMasterMonitoringDLL.dll", CallingConvention = CallingConvention.Cdecl)]
            private static extern bool PlatformUninit();
            [DllImport("AMDRyzenMasterMonitoringDLL.dll", CallingConvention = CallingConvention.Cdecl)]
            private static extern RMQuickStats ShortQuery();

            public bool IsAvailable { get; private set; }

            private AmdRyzenMasterBackend() { }

            public static AmdRyzenMasterBackend TryCreate()
            {
                try
                {
                    var info = IsSupported();
                    // 必须是 AMD 且驱动服务就绪
                    if (info.AuthenticAMD == 0 || info.DriverService == 0)
                        return null;

                    var backend = new AmdRyzenMasterBackend();
                    if (PlatformInit())
                    {
                        backend.IsAvailable = true;
                        return backend;
                    }
                }
                catch (DllNotFoundException) { /* SDK 未安装 */ }
                catch (EntryPointNotFoundException) { }
                catch { }
                return null;
            }

            public float? GetTemperature()
            {
                if (!IsAvailable) return null;
                try
                {
                    var s = ShortQuery();
                    if (s.Init == 0) return null;
                    double t = s.dTemperature;
                    // 合理性校验，剔除脏数据
                    return (t > 0 && t < 150) ? (float)t : null;
                }
                catch { return null; }
            }

            public float? GetPower()
            {
                if (!IsAvailable) return null;
                try
                {
                    var s = ShortQuery();
                    if (s.Init == 0) return null;
                    float p = s.fPPTValue;
                    return (p >= 0 && p < 1000) ? p : null;
                }
                catch { return null; }
            }

            public void Dispose()
            {
                if (!IsAvailable) return;
                try { PlatformUninit(); } catch { }
                IsAvailable = false;
            }
        }

        // ==================================================================
        // Intel：PresentMon Service（仅 CPU 功耗；无用户态 CPU 温度）
        // ==================================================================
        private sealed class IntelPresentMonBackend : IDisposable
        {
            // ---- PresentMonAPI2 最小子集（动态加载 PresentMonAPI2.dll）----
            private const int PM_STATUS_SUCCESS = 0;
            // PresentMon 指标枚举：CPU 功耗（不同服务版本枚举值可能不同，需实机校验）
            private const int PM_METRIC_CPU_POWER = 203; // 尽力而为：未经实机确认
            private const int PM_STAT_AVG = 0;

            private delegate int pmOpenSessionDelegate(out IntPtr pHandle);
            private delegate int pmCloseSessionDelegate(IntPtr handle);
            private delegate int pmStartTrackingProcessDelegate(IntPtr handle, uint processId);
            private delegate int pmStopTrackingProcessDelegate(IntPtr handle, uint processId);
            private delegate int pmRegisterDynamicQueryDelegate(IntPtr handle, out IntPtr pQueryHandle, IntPtr elements, ulong elementCount, double windowMs, double metricOffsetMs);
            private delegate int pmFreeDynamicQueryDelegate(IntPtr queryHandle);
            private delegate int pmPollDynamicQueryDelegate(IntPtr queryHandle, uint processId, IntPtr blob, out uint numSwapChains);

            [StructLayout(LayoutKind.Sequential)]
            private struct PM_QUERY_ELEMENT
            {
                public int metric;
                public int stat;
                public uint deviceId;
                public uint arrayIndex;
                public uint reserved;
                public uint reserved2;
            }

            private pmOpenSessionDelegate _open;
            private pmCloseSessionDelegate _close;
            private pmStartTrackingProcessDelegate _startTrack;
            private pmStopTrackingProcessDelegate _stopTrack;
            private pmRegisterDynamicQueryDelegate _register;
            private pmFreeDynamicQueryDelegate _free;
            private pmPollDynamicQueryDelegate _poll;

            private IntPtr _session = IntPtr.Zero;
            private IntPtr _query = IntPtr.Zero;
            private IntPtr _lib = IntPtr.Zero;
            private readonly uint _pid = (uint)Environment.ProcessId;

            public bool IsAvailable { get; private set; }

            private IntelPresentMonBackend() { }

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr LoadLibrary(string lpFileName);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool FreeLibrary(IntPtr hModule);
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
            private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

            private static T GetProc<T>(IntPtr lib, string name) where T : Delegate
            {
                IntPtr p = GetProcAddress(lib, name);
                if (p == IntPtr.Zero) return null;
                return Marshal.GetDelegateForFunctionPointer<T>(p);
            }

            public static IntelPresentMonBackend TryCreate()
            {
                IntPtr lib = IntPtr.Zero;
                try
                {
                    // PresentMon Service 安装路径（SDK 与 service 一同部署）
                    string[] candidates =
                    {
                        @"PresentMonAPI2.dll",
                        @"C:\Program Files\Intel\PresentMon\SDK\PresentMonAPI2.dll",
                        @"C:\Program Files\Intel\PresentMon\PresentMonAPI2.dll",
                    };
                    foreach (var c in candidates)
                    {
                        lib = LoadLibrary(c);
                        if (lib != IntPtr.Zero) break;
                    }
                    if (lib == IntPtr.Zero) return null;

                    var b = new IntelPresentMonBackend
                    {
                        _lib = lib,
                        _open = GetProc<pmOpenSessionDelegate>(lib, "pmOpenSession"),
                        _close = GetProc<pmCloseSessionDelegate>(lib, "pmCloseSession"),
                        _startTrack = GetProc<pmStartTrackingProcessDelegate>(lib, "pmStartTrackingProcess"),
                        _stopTrack = GetProc<pmStopTrackingProcessDelegate>(lib, "pmStopTrackingProcess"),
                        _register = GetProc<pmRegisterDynamicQueryDelegate>(lib, "pmRegisterDynamicQuery"),
                        _free = GetProc<pmFreeDynamicQueryDelegate>(lib, "pmFreeDynamicQuery"),
                        _poll = GetProc<pmPollDynamicQueryDelegate>(lib, "pmPollDynamicQuery"),
                    };

                    if (b._open == null || b._register == null || b._poll == null)
                    {
                        FreeLibrary(lib);
                        return null;
                    }

                    if (b._open(out b._session) != PM_STATUS_SUCCESS || b._session == IntPtr.Zero)
                    {
                        FreeLibrary(lib);
                        return null;
                    }

                    // 跟踪自身进程（系统级指标也挂在会话上）
                    b._startTrack?.Invoke(b._session, b._pid);

                    // 注册 CPU 功耗动态查询
                    var el = new PM_QUERY_ELEMENT { metric = PM_METRIC_CPU_POWER, stat = PM_STAT_AVG };
                    IntPtr elPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PM_QUERY_ELEMENT>());
                    try
                    {
                        Marshal.StructureToPtr(el, elPtr, false);
                        if (b._register(b._session, out b._query, elPtr, 1, 1000.0, 0.0) != PM_STATUS_SUCCESS || b._query == IntPtr.Zero)
                        {
                            b.Cleanup();
                            return null;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(elPtr);
                    }

                    b.IsAvailable = true;
                    return b;
                }
                catch
                {
                    if (lib != IntPtr.Zero) { try { FreeLibrary(lib); } catch { } }
                    return null;
                }
            }

            public float? GetPower()
            {
                if (!IsAvailable || _query == IntPtr.Zero) return null;
                IntPtr blob = IntPtr.Zero;
                try
                {
                    // blob 布局依指标而定，此处按 double 读取并做合理性校验
                    blob = Marshal.AllocHGlobal(4096);
                    int status = _poll(_query, _pid, blob, out uint _);
                    if (status != PM_STATUS_SUCCESS) return null;

                    double val = Marshal.PtrToStructure<double>(blob);
                    // 剔除脏数据（合理 CPU 功耗范围）
                    return (val >= 0 && val < 1000) ? (float)val : null;
                }
                catch { return null; }
                finally { if (blob != IntPtr.Zero) Marshal.FreeHGlobal(blob); }
            }

            private void Cleanup()
            {
                try { if (_query != IntPtr.Zero) { _free?.Invoke(_query); _query = IntPtr.Zero; } } catch { }
                try { if (_session != IntPtr.Zero) { _stopTrack?.Invoke(_session, _pid); _close?.Invoke(_session); _session = IntPtr.Zero; } } catch { }
            }

            public void Dispose()
            {
                IsAvailable = false;
                Cleanup();
                try { if (_lib != IntPtr.Zero) { FreeLibrary(_lib); _lib = IntPtr.Zero; } } catch { }
            }
        }
    }
}
