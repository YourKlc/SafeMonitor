using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SafeMonitor.src.SystemServices
{
    /// <summary>
    /// CPU 功耗 / 温度采集（用户态，不使用任何内核驱动）。
    ///
    /// 本类只负责**厂商官方用户态 SDK**：
    ///  - AMD → AMD Ryzen Master Monitoring SDK（AMDRyzenMasterMonitoringDLL.dll）
    ///          可读 温度 与 功耗(PPT)。需安装 AMD Ryzen Master。
    ///  - Intel → 官方无稳定的用户态 CPU 温度/功耗 SDK，本类不提供（返回 null）。
    ///
    /// 更通用的兜底来源（对所有平台有效，且**无需安装任何东西**）：
    ///  - 温度 → ACPI 热区（PerformanceCounterManager.GetCpuTemperatureFromThermalZone）
    ///  - 功耗 → Energy Meter / RAPL（PerformanceCounterManager.GetCpuPowerWatts，Intel 平台实测可用）
    /// 因此在 Intel 平台上，即使本类返回 null，UI 的 CPU.Temp / CPU.Power 仍能读到真实值。
    /// </summary>
    public sealed class CpuTelemetryProvider : IDisposable
    {
        public enum Vendor { Unknown, Intel, Amd }

        public Vendor CpuVendor { get; }

        private readonly AmdRyzenMasterBackend? _amd;

        // 采样节流：SDK 不宜高频调用（AMD 建议 ~1 次/秒）
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
            }
            catch { /* 任何初始化失败都保持不可用状态 */ }
        }

        /// <summary>本提供器是否有可用的厂商 SDK 后端（Intel 恒为 false）。</summary>
        public bool IsAvailable => _amd != null && _amd.IsAvailable;

        /// <summary>CPU 温度（℃）。仅 AMD 经 SDK 提供；其余平台由 ACPI 热区兜底。</summary>
        public float? GetTemperature()
        {
            Sample();
            return _cachedTemp;
        }

        /// <summary>CPU 功耗（W）。仅 AMD 经 SDK 提供；Intel 无用户态途径，返回 null。</summary>
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

            // ★★★ 安全加载：仅从 System32 搜索，防止程序目录幽灵 DLL 劫持 ★★★
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool FreeLibrary(IntPtr hModule);
            [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false)]
            private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

            private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate RMSystemInfo IsSupportedDelegate();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate bool PlatformInitDelegate();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate bool PlatformUninitDelegate();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate RMQuickStats ShortQueryDelegate();

            private readonly IntPtr _module;
            private readonly IsSupportedDelegate _isSupported;
            private readonly PlatformInitDelegate _platformInit;
            private readonly PlatformUninitDelegate _platformUninit;
            private readonly ShortQueryDelegate _shortQuery;
            private bool _disposed;

            public bool IsAvailable { get; private set; }

            private AmdRyzenMasterBackend(IntPtr module)
            {
                _module = module;
                _isSupported = GetDelegate<IsSupportedDelegate>(module, "IsSupported");
                _platformInit = GetDelegate<PlatformInitDelegate>(module, "PlatformInit");
                _platformUninit = GetDelegate<PlatformUninitDelegate>(module, "PlatformUninit");
                _shortQuery = GetDelegate<ShortQueryDelegate>(module, "ShortQuery");
            }

            public static AmdRyzenMasterBackend? TryCreate()
            {
                IntPtr module = IntPtr.Zero;
                try
                {
                    // 只从 System32 加载；找不到（未装 Ryzen Master）则降级，不影响 ACPI/RAPL 兜底
                    module = LoadLibraryEx("AMDRyzenMasterMonitoringDLL.dll", IntPtr.Zero, LOAD_LIBRARY_SEARCH_SYSTEM32);
                    if (module == IntPtr.Zero) return null;

                    var backend = new AmdRyzenMasterBackend(module);

                    var info = backend._isSupported();
                    // 必须是 AMD 且驱动服务就绪
                    if (info.AuthenticAMD == 0 || info.DriverService == 0)
                    {
                        backend.Dispose();
                        return null;
                    }

                    if (backend._platformInit())
                    {
                        backend.IsAvailable = true;
                        module = IntPtr.Zero; // 所有权已转移给 backend
                        return backend;
                    }

                    backend.Dispose();
                    return null;
                }
                catch (DllNotFoundException) { /* SDK 未安装 */ }
                catch (EntryPointNotFoundException) { }
                catch { }
                finally
                {
                    // 所有权未转移时，负责释放模块
                    if (module != IntPtr.Zero)
                    {
                        try { FreeLibrary(module); } catch { }
                    }
                }
                return null;
            }

            private static T GetDelegate<T>(IntPtr module, string name) where T : Delegate
            {
                IntPtr addr = GetProcAddress(module, name);
                if (addr == IntPtr.Zero) throw new EntryPointNotFoundException(name);
                return Marshal.GetDelegateForFunctionPointer<T>(addr);
            }

            public float? GetTemperature()
            {
                if (!IsAvailable) return null;
                try
                {
                    var s = _shortQuery();
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
                    var s = _shortQuery();
                    if (s.Init == 0) return null;
                    float p = s.fPPTValue;
                    return (p >= 0 && p < 1000) ? p : null;
                }
                catch { return null; }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (IsAvailable)
                {
                    try { _platformUninit(); } catch { }
                    IsAvailable = false;
                }
                if (_module != IntPtr.Zero)
                {
                    try { FreeLibrary(_module); } catch { }
                }
            }
        }
    }
}
