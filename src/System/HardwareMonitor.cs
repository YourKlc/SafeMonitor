using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices; // ★★★ 新增：引用用于内存修剪的库
using System.Runtime; // GCSettings / LargeObjectHeapCompactionMode
using System.Reflection; // ★★★ 新增：用于反射关闭历史记录
using LibreHardwareMonitor.Hardware;
using SafeMonitor.src.Core;
using System.Linq;
using System.Threading;

namespace SafeMonitor.src.SystemServices
{
    public sealed class HardwareMonitor : IDisposable
    {
        #region Singleton & Events
        public static HardwareMonitor? Instance { get; private set; }
        public event Action? OnValuesUpdated;
        #endregion

        #region Private Fields
        private readonly Settings _cfg;
        private readonly Computer _computer;
        private readonly object _lock = new object();

        // 拆分出的子服务
        private readonly SensorMap _sensorMap;
        private readonly NetworkManager _networkManager;
        private readonly DiskManager _diskManager;
        private readonly CpuTelemetryProvider _cpuTelemetry;
        private readonly HardwareValueProvider _valueProvider;

        // 性能计数器管理器
        private readonly PerformanceCounterManager _perfCounterManager;

        private readonly Dictionary<string, float> _lastValidMap = new();
        
        // 状态标记 (防止并发重载)
        private volatile bool _isReloading = false;
        private volatile bool _isOpening = false;

        // 计时器相关
        private long _tickCounter = 0;
        private double _secondAccumulator = 0;
        private long _secondsCounter = 0;
        private DateTime _lastTrafficTime = DateTime.Now;
        #endregion

        #region Public Properties
        // [新增] 允许 UI 层访问原始硬件树（用于硬件信息面板）
        public IComputer ComputerInstance => _computer;
        public object SyncLock => _lock;
        #endregion

        #region Constructor
        public HardwareMonitor(Settings cfg)
        {
            _cfg = cfg;
            Instance = this;

            // 1. 初始化 Computer
            // ★★★ 用户态采集（不使用任何内核驱动）★★★
            // - CPU 温度/电压/功耗、主板/SuperIO、风扇/水泵、磁盘 SMART 均需内核驱动，相关节点已禁用。
            // - CPU 负载/频率、内存、磁盘读写、网络 均走 Windows 性能计数器（无需内核驱动）。
            // - GPU 走官方用户态 SDK (NVIDIA NVAPI / AMD ADL / Intel IGCL)。
            // - 电池 走 Windows 电源 API。
            _computer = new Computer()
            {
                IsCpuEnabled = false,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsNetworkEnabled = true,
                IsStorageEnabled = false,
                IsMotherboardEnabled = false,
                IsControllerEnabled = false,
                IsBatteryEnabled = true,
                IsPsuEnabled = false
            };

            // ★★★ [新增] 1. 初始化计数器管理器 (必须在 ValueProvider 之前) ★★★
            _perfCounterManager = new PerformanceCounterManager();

            // 2. 初始化服务
            _sensorMap = new SensorMap();
            _networkManager = new NetworkManager();
            _diskManager = new DiskManager();

            // CPU 功耗/温度：按厂商走官方用户态 SDK（AMD Ryzen Master / Intel PresentMon）
            _cpuTelemetry = new CpuTelemetryProvider();

            _valueProvider = new HardwareValueProvider(
                _computer,
                cfg,
                _sensorMap,
                _networkManager,
                _diskManager,
                _perfCounterManager,
                _lock,
                _lastValidMap,
                _cpuTelemetry
            );

            // 3. 异步启动 (唯一优化：不卡UI)
            InitializeAsync();
        }
        #endregion

        #region Public Methods

        public float? Get(string key)
        {
            return _isOpening ? _valueProvider.GetStartupValue(key) : _valueProvider.GetValue(key);
        }

        public string GetNetworkIP() => _networkManager.GetCurrentIP();

        public void UpdateAll()
        {
            if (_isOpening) return;

            // [Fix #290] 标记是否需要触发重载（因硬件变更或故障）
            bool needsReload = false;

            try
            {
                // 1. 更新计时器
                double timeDelta = UpdateTiming();

                // 2. 计算更新需求
                var requirements = CheckUpdateRequirements();

                _valueProvider.OnUpdateTickStarted();

                lock (_lock)
                {
                    // [Optimization] 极简维护
                    // 仅每10分钟做一次兜底检查 (内部自动 Rebuild)，无需关心返回值。
                    // 即使重建了，ValueProvider 会在下一次取值时懒加载或直接从 Map 取，也不需要手动 PreCache。
                    _sensorMap.EnsureFresh(_computer, _cfg);

                    // 3秒一次 (慢速扫描)
                    bool isSlowScanTick = (_secondsCounter % 3 == 0); 
                    // 10秒一次 (磁盘后台扫描)
                    bool needDiskBgScan = (_secondsCounter % 10 == 0);

                    foreach (var hw in _computer.Hardware)
                    {
                        if (hw.HardwareType == HardwareType.Cpu && requirements.NeedCpu) { hw.Update(); continue; }
                        
                        // [Fix #290] 显卡防闪退保护：双显卡切换时 Update 可能抛出异常
                        if (IsGpu(hw) && requirements.NeedGpu) 
                        { 
                            if (!requirements.ForceAll && !ShouldUpdateGpuHardware(hw)) continue;

                            try { hw.Update(); }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[GPU Error] Update failed: {ex.Message}");
                                needsReload = true;
                            }
                            continue; 
                        }

                        if (hw.HardwareType == HardwareType.Memory && requirements.NeedMem) { hw.Update(); continue; }
                        if (hw.HardwareType == HardwareType.Battery && requirements.NeedBat) { hw.Update(); continue; }

                        if (hw.HardwareType == HardwareType.Network && requirements.NeedNet)
                        {
                            // ★★★ 修复：使用正确的 timeDelta，而不是 requirements.TimeDelta (它是0) ★★★
                            _networkManager.ProcessUpdate(hw, _cfg, timeDelta, isSlowScanTick);
                            continue;
                        }
                        if (hw.HardwareType == HardwareType.Storage && requirements.NeedDisk)
                        {
                            _diskManager.ProcessUpdate(hw, _cfg, isSlowScanTick, needDiskBgScan);
                            continue;
                        }
                        
                        // 递归更新主板 (Motherboard / SuperIO)
                        if (IsMoboOrCooler(hw) && requirements.NeedMobo)
                        {
                             // ★★★ [优化] 降低更新频率：主板传感器每 3 秒更新一次，减少 I/O 阻塞 ★★★
                             // [Fix] 只要是 SuperIO 这种慢速设备，都必须强制跟随慢速扫描周期
                             // (isSlowScanTick)，禁止高频更新。
                             if (isSlowScanTick)
                             {
                                 UpdateWithSubHardware(hw);
                             }
                             continue;
                        }
                    }
                }
                
                // 任务错峰执行 (调用 SystemOptimizer)
                SystemOptimizer.RunMaintenanceTasks(_secondsCounter);
                
                OnValuesUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                // 记录异常但不中断监控循环：单点硬件故障不应拖垮整轮刷新
                System.Diagnostics.Debug.WriteLine($"[UpdateAll] Error: {ex.Message}");
            }

            // [Fix #290] 硬件故障自动恢复
            // 如果检测到 GPU 丢失或驱动崩溃，触发安全重载以刷新硬件列表
            if (needsReload && !_isReloading)
            {
                _isReloading = true;
                System.Diagnostics.Debug.WriteLine("[HardwareMonitor] Hardware change detected, triggering reload...");
                
                // 异步延迟重载，给硬件切换留出缓冲时间
                Task.Run(async () => 
                {
                    try
                    {
                        await Task.Delay(2000); // 等待 2秒让系统硬件列表稳定
                        ReloadComputerSafe();
                    }
                    finally
                    {
                        _isReloading = false;
                    }
                });
            }
        }

        public void CleanMemory(Action<int>? onProgress = null) => SystemOptimizer.CleanMemory(onProgress);

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // ★★★ 核心修复：加锁！防止与正在运行的 UpdateAll() 冲突 ★★★
            lock (_lock)
            {
                try 
                {
                    // 将 Close 操作放入锁内，确保此时 UpdateAll 不在运行
                    _computer.Close(); 
                }
                catch (Exception ex)
                {
                    // 吞掉关闭时的异常，防止弹窗报错困扰用户
                    System.Diagnostics.Debug.WriteLine($"Dispose Error: {ex.Message}");
                }
            }

            _cpuTelemetry?.Dispose();
            _perfCounterManager.Dispose(); // ★★★ [新增] 释放计数器资源 ★★★
            _networkManager.ClearCache();
            _diskManager.ClearCache(); // 漏掉的，补上

            // 清空静态单例，避免悬垂引用被后续静态 API 误用
            if (ReferenceEquals(Instance, this)) Instance = null;
            GC.SuppressFinalize(this);
        }
        #endregion

        #region Private Helpers (Logic)

        private void InitializeAsync()
        {
            Task.Run(async () =>
            {
                _isOpening = true;
                try
                {
                    // ★★★ [新增] 启动计数器预热 (不阻塞主 UI) ★★★
                    _perfCounterManager.InitializeAsync();

                    lock (_lock)
                    {
                        // LHM 打开双 Nvidia 显卡时可能很慢。启动阶段 Get() 已改走性能计数器兜底，
                        // 这里继续加锁只会让设置页等硬件树稳定，不会拖住 CPU/MEM/DISK 首屏数据。
                        _computer.Open();
                        WarmUpMotherboardSensors();
                        WarmUpBatterySensors();

                        // 先建立映射，再由正常刷新循环更新数值，避免启动时预热所有 GPU 拖慢 CPU/MEM 展示。
                        _sensorMap.Rebuild(_computer, _cfg);
                        
                        // ★★★ [新增] 静态化预热：将所有传感器对象存入 Provider 缓存 ★★★
                        _valueProvider.PreCacheAllSensors(_sensorMap);
                    }

                    _isOpening = false;

                    // 首轮缓存就绪后再做历史记录禁用和内存整理，避免延后第一屏数据。
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000);

                        lock (_lock)
                        {
                            DisableSensorHistory();
                        }

                        // 优化 T1：启动后大扫除（改为非阻塞式，避免强制 STW 压缩 LOH 造成的 100~500ms 顿挫）
                        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(2, GCCollectionMode.Optimized, false);
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Init Error: {ex.Message}");
                }
                finally
                {
                    _isOpening = false;
                }
            });
        }

        private void WarmUpMotherboardSensors()
        {
            // 主板硬件树本来就是常开。启动时预热一次，保证主板温度即使暂未显示，
            // 也能在 SensorMap.Rebuild 阶段完成有效映射，后续打开显示项可直接读缓存。
            foreach (var hw in _computer.Hardware)
            {
                if (IsMotherboardSensorHardware(hw))
                {
                    try { UpdateWithSubHardware(hw); }
                    catch { }
                }
            }
        }

        private void WarmUpBatterySensors()
        {
            // 电池传感器在新版硬件库中可能需要先 Update 才会稳定暴露数值。
            // 只预热 Battery，避免恢复启动时全硬件 Update 带来的多显卡/磁盘卡顿。
            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType == HardwareType.Battery)
                {
                    try { hw.Update(); }
                    catch { }
                }
            }
        }

        private double UpdateTiming()
        {
            // 1. 统一心跳计数 (假设 UpdateAll 约 1秒调用一次)
            _tickCounter++;
            
            // 2. 计算精确时间差 (仅用于网速计算)
            DateTime now = DateTime.Now;
            double timeDelta = (now - _lastTrafficTime).TotalSeconds;
            _lastTrafficTime = now;
            if (timeDelta > 5.0) timeDelta = 0; // 防止休眠唤醒后的数据突刺

            // ★★★ [智能处理] 刷新率自适应逻辑 ★★★
            // 无论 UpdateAll 是 300ms 调一次还是 2s 调一次，
            // 我们都将时间累加，直到凑满 1秒，才增加 _secondsCounter。
            // 这样后续的 % 60, % 600 逻辑就是基于"真实时间"，而非"调用次数"。
            _secondAccumulator += timeDelta;
            while (_secondAccumulator >= 1.0)
            {
                _secondAccumulator -= 1.0;
                _secondsCounter++;
            }

            return timeDelta;
        }

        private (bool ForceAll, bool NeedCpu, bool NeedGpu, bool NeedMem, bool NeedNet, bool NeedDisk, bool NeedBat, bool NeedMobo, double TimeDelta) CheckUpdateRequirements()
        {
            // === [优化开始] 精细化判断更新需求 ===
        
            // 1. 获取计数器状态
            bool useCounter = _cfg.UseWinPerCounters && _perfCounterManager.IsInitialized;
            
            // 无强制全量更新需求（已移除网页端监控）
            bool forceAll = false;

            // 2. CPU: 总是需要 (因为 LHM 要读温度)
            bool needCpu = forceAll || _cfg.IsAnyEnabled("CPU");
            
            // 3. 显卡: 总是需要
            bool needGpu = forceAll || _cfg.IsAnyEnabled("GPU");
            
            // 4. ★★★ [优化 1] 内存: 如果走了计数器，就不需要 LHM 轮询了 ★★★
            bool needMem = forceAll || (_cfg.IsAnyEnabled("MEM") && !useCounter);
            
            // 5. 网络: 保持不变
            bool needNet = forceAll || _cfg.IsAnyEnabled("NET") || _cfg.IsAnyEnabled("DATA");
            
            // 7. 电池: 只有在开启时才更新
            bool needBat = forceAll || _cfg.IsAnyEnabled("BAT");

            // 6. ★★★ [优化 2] 磁盘: 智能判断 ★★★
            // 只有当 (没开计数器 OR 指定了特定盘 OR 需要看温度) 时，才需要 LHM 介入
            bool needDiskTemp = _cfg.IsAnyEnabled("DISK.Temp");
            bool hasSpecificDisk = !string.IsNullOrEmpty(_cfg.PreferredDisk);
            bool needDiskSpeed = _cfg.IsAnyEnabled("DISK") && (!useCounter || hasSpecificDisk);
            bool needDisk = forceAll || needDiskSpeed || needDiskTemp;

            // 判断主板更新需求
            bool needMobo = forceAll ||
                _cfg.IsAnyEnabled("MOBO") ||
                _cfg.IsAnyEnabled("CPU.Fan") ||
                _cfg.IsAnyEnabled("CPU.Pump") ||
                _cfg.IsAnyEnabled("CASE.Fan");
            
            return (forceAll, needCpu, needGpu, needMem, needNet, needDisk, needBat, needMobo, 0);
        }

        private void ReloadComputerSafe()
        {
            try
            {
                lock (_lock)
                {
                    // 1. 清理业务缓存
                    _networkManager.ClearCache();
                    _diskManager.ClearCache();
                    _sensorMap.Clear();
                    
                    _valueProvider.ClearCache();
                    
                    // 清理扫描器缓存
                    HardwareScanner.ClearCache();

                    // 2. 清理字符串池
                    UIUtils.ClearStringPool();

                    // 3. 关闭旧硬件服务
                    if (_computer != null)
                    {
                        _computer.Close();
                        // ★★★ 核心修复：手动清空硬件列表 ★★★
                        // LHM 的 Close() 不会清空列表，必须手动 Clear，否则再次 Open 会追加重复硬件
                        _computer.Hardware.Clear();
                    }
                    
                    _computer.Open();
                    WarmUpMotherboardSensors();
                    WarmUpBatterySensors();

                    DisableSensorHistory();
                }

                _sensorMap.Rebuild(_computer, _cfg);
                _valueProvider.PreCacheAllSensors(_sensorMap);

                // 4. 重载后轻量回收（不强制压缩，避免 STW）
                GC.Collect(2, GCCollectionMode.Optimized, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ReloadComputerSafe] Error: {ex.Message}");
            }
        }

        // =========================================================
        // ★★★ 核心修复：禁用所有传感器的历史记录 ★★★
        // 这将阻止 LibreHardwareMonitor 在内存中保留 24 小时的数据缓存
        // =========================================================
        private void DisableSensorHistory()
        {
            try { _computer.Accept(new DisableHistoryVisitor()); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MemoryFix] Failed: {ex.Message}"); }
        }

        // 递归更新子硬件，确保 SuperIO 刷新
        private void UpdateWithSubHardware(IHardware hw)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) UpdateWithSubHardware(sub);
        }

        private static bool IsGpu(IHardware hw)
        {
            return hw.HardwareType == HardwareType.GpuNvidia || 
                   hw.HardwareType == HardwareType.GpuAmd || 
                   hw.HardwareType == HardwareType.GpuIntel;
        }

        private bool ShouldUpdateGpuHardware(IHardware hw)
        {
            var activeGpu = _sensorMap.CachedGpu;
            if (activeGpu == null) return true;

            // 当前面板只有一组 GPU 指标，刷新非当前显卡只会增加启动和轮询耗时。
            return ReferenceEquals(hw, activeGpu);
        }

        private static bool IsMoboOrCooler(IHardware hw)
        {
            return hw.HardwareType == HardwareType.Motherboard || 
                   hw.HardwareType == HardwareType.SuperIO || 
                   hw.HardwareType == HardwareType.Cooler;
        }

        private static bool IsMotherboardSensorHardware(IHardware hw)
        {
            return hw.HardwareType == HardwareType.Motherboard ||
                   hw.HardwareType == HardwareType.SuperIO;
        }
        #endregion

        #region Static UI Helpers (Delegated to HardwareScanner)
        public static string GenerateSmartName(ISensor sensor, IHardware hardware)
        {
            var inst = Instance;
            if (inst == null) return sensor?.Name ?? hardware?.Name ?? "";
            return HardwareScanner.GenerateSmartName(sensor, hardware, inst._computer);
        }

        public static List<string> ListAllNetworks()
        {
            var inst = Instance;
            return inst == null ? new List<string>() : HardwareScanner.ListAllNetworks(inst._computer);
        }

        public static List<string> ListAllDisks()
        {
            var inst = Instance;
            return inst == null ? new List<string>() : HardwareScanner.ListAllDisks(inst._computer);
        }

        public static List<string> ListAllGpus()
        {
            var inst = Instance;
            if (inst == null) return new List<string>();
            lock (inst._lock)
                return HardwareScanner.ListAllGpus(inst._computer);
        }

        public static List<HardwareScanner.GpuOption> ListAllGpuOptions()
        {
            var inst = Instance;
            if (inst == null) return new List<HardwareScanner.GpuOption>();
            lock (inst._lock)
                return HardwareScanner.ListAllGpuOptions(inst._computer);
        }

        public static List<string> ListAllFans()
        {
            var inst = Instance;
            return inst == null ? new List<string>() : HardwareScanner.ListAllFans(inst._computer, inst._lock);
        }

        public static List<string> ListAllMoboTemps()
        {
            var inst = Instance;
            return inst == null ? new List<string>() : HardwareScanner.ListAllMoboTemps(inst._computer, inst._lock);
        }
        #endregion

        #region Inner Visitors
        // ★★★ 核心修复：禁用所有传感器的历史记录 ★★★
        /// <summary>
        /// 专用访问器：将所有 Sensor 的 ValuesTimeWindow 设为 0
        /// </summary>
        private class DisableHistoryVisitor : IVisitor
        {
            public void VisitComputer(IComputer computer) => computer.Traverse(this);
            public void VisitHardware(IHardware hardware)
            {
                foreach (var sub in hardware.SubHardware) sub.Accept(this);
                foreach (var sensor in hardware.Sensors) VisitSensor(sensor);
            }
            public void VisitSensor(ISensor sensor)
            {
                // ★ 关键：通过反射设置 ValuesTimeWindow = TimeSpan.Zero
                // 因为 ISensor 接口通常不暴露这个属性，它属于具体的 Sensor 类
                try
                {
                    var prop = sensor.GetType().GetProperty("ValuesTimeWindow");
                    if (prop != null && prop.CanWrite) prop.SetValue(sensor, TimeSpan.Zero);
                }
                catch { }
            }
            public void VisitParameter(IParameter parameter) { }
        }

        // 内部 Visitor 类，用于触发 LHM 清理逻辑
        private class HardwareVisitor : IVisitor
        {
            private readonly Action<IHardware> _action;
            public HardwareVisitor(Action<IHardware> action) { _action = action; }
            public void VisitComputer(IComputer computer) => computer.Traverse(this);
            public void VisitHardware(IHardware hardware)
            {
                _action(hardware);
                foreach (var sub in hardware.SubHardware) sub.Accept(this);
            }
            public void VisitSensor(ISensor sensor) { }
            public void VisitParameter(IParameter parameter) { }
        }
        #endregion
    }
}
