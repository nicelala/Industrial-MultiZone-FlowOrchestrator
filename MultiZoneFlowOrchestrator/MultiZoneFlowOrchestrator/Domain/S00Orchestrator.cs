using System;
using System.Threading;
using System.Threading.Tasks;
using MultiZoneFlowOrchestrator.Core;
using MultiZoneFlowOrchestrator.Safety;

namespace MultiZoneFlowOrchestrator.Domain
{
    /// <summary>
    /// S00 全機總控調度大腦
    /// 實作星狀拓撲公文轉發、分區生產工單獨立監管與非阻塞結批哨兵
    /// </summary>
    public class S00Orchestrator : IDisposable
    {
        private CancellationTokenSource _cts;
        private Task _orchestrationTask;
        private readonly object _stateLock = new object();

        // 管轄之核心子站
        public S01_InfeedStation S01 { get; }
        public S02_WashTrayStation S02 { get; }

        // 分區工單帳本 (單一真理來源 SSoT)
        public LotSchedule Schedule_WashTray { get; set; }
        public LotSchedule Schedule_Collect { get; set; }

        /// <summary>
        /// 是否啟用分區生產模式 (True: 雙軌獨立結批; False: 單機全線模式)
        /// </summary>
        public bool IsDualZoneMode { get; set; } = true;

        /// <summary>
        /// 全機狀態 (SECS / SEMI E10 準則)
        /// </summary>
        public MachineState SystemState { get; private set; } = MachineState.Init;

        /// <summary>
        /// 分區獨立結批完工事件 (供 UI 層以 BeginInvoke 彈窗並更新圖表)
        /// 參數: (完工分區, 完工工單物件)
        /// </summary>
        public event Action<EmProductionZone, LotSchedule> ZoneLotFinished;

        /// <summary>
        /// 全機狀態變更廣播
        /// </summary>
        public event Action<MachineState> SystemStateChanged;

        /// <summary>
        /// 系統核心 Log 廣播
        /// </summary>
        public event Action<string> SystemLogDispatched;

        public S00Orchestrator()
        {
            // 實例化子站
            this.S01 = new S01_InfeedStation();
            this.S02 = new S02_WashTrayStation();

            // ⭐ 星狀拓撲公文郵筒路由綁定 (Star Topology Routing)
            this.S01.OutboxDispatcher = (cmd) => RouteStationCommand(cmd, targetStation: "S02");
            this.S02.OutboxDispatcher = (cmd) => RouteStationCommand(cmd, targetStation: "S01");

            // 轉發子站日誌至系統總日誌
            this.S01.LogDispatched += (station, msg) => DispatchLog($"[{station}] {msg}");
            this.S02.LogDispatched += (station, msg) => DispatchLog($"[{station}] {msg}");

            // 監聽子站警報
            this.S01.StateChanged += OnSubStationStateChanged;
            this.S02.StateChanged += OnSubStationStateChanged;

            // 監聽安全門硬體訊號異動
            SafetyDoorManager.Instance.DoorStateChanged += OnSafetyDoorStateChanged;
        }

        /// <summary>
        /// 啟動全機與所有子站狀態機
        /// </summary>
        public void Start()
        {
            lock (_stateLock)
            {
                if (_orchestrationTask != null && !_orchestrationTask.IsCompleted) return;

                _cts = new CancellationTokenSource();

                // 同步工單帳本指針至子站
                SyncSchedulesToStations();

                // 啟動子站背景 Task
                this.S01.Start();
                this.S02.Start();

                // 啟動 S00 總調度輪詢迴圈
                _orchestrationTask = Task.Run(() => OrchestrationLoopAsync(_cts.Token));

                UpdateSystemState(MachineState.Running);
                DispatchLog("S00 主控總調度已發車，全線站點進入 Running 狀態。");
            }
        }

        /// <summary>
        /// 全機優雅停止
        /// </summary>
        public void Stop()
        {
            lock (_stateLock)
            {
                _cts?.Cancel();
                this.S01.Stop();
                this.S02.Stop();
                UpdateSystemState(MachineState.Paused);
                DispatchLog("S00 主控收到停止指令，全機轉入 Paused。");
            }
        }

        /// <summary>
        /// 全機警報復歸
        /// </summary>
        public void ResetAlarm()
        {
            lock (_stateLock)
            {
                this.S01.ResetAlarm();
                this.S02.ResetAlarm();
                UpdateSystemState(MachineState.Idle);
                DispatchLog("S00 全機警報已復歸，站點信箱清空，重回 Idle 待命。");
            }
        }

        /// <summary>
        /// S00 核心輪詢迴圈：執行分區獨立結批哨兵巡檢
        /// </summary>
        private async Task OrchestrationLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (this.SystemState == MachineState.Running)
                    {
                        // 1. 同步工單至站點 (支援動態下單)
                        SyncSchedulesToStations();

                        // 2. 分區獨立結批哨兵檢查
                        if (this.IsDualZoneMode)
                        {
                            TryHandleZoneLotFinish(EmProductionZone.WashTray);
                            TryHandleZoneLotFinish(EmProductionZone.Collect);
                        }
                        else
                        {
                            TryHandleZoneLotFinish(EmProductionZone.All);
                        }

                        // 3. 全線工單皆已清空且站點皆回到待命時，全機收尾轉為 Idle
                        CheckAllCompletedAndReturnIdle();
                    }

                    await Task.Delay(20, token); // 20ms 高頻總控輪詢
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DispatchLog($"[S00 輪詢異常] {ex.Message}");
                }
            }
        }

        /// <summary>
        /// ⭐【分區獨立結批核心處理器】
        /// 當單一分區達標時，只結算該區、觸發專屬彈窗，並銷毀該區帳本，絕不干涉另一分區生產
        /// </summary>
        private bool TryHandleZoneLotFinish(EmProductionZone zone)
        {
            var schedule = GetZoneSchedule(zone);

            // 門禁 1：檢查工單是否已存在且達標/停止
            if (schedule == null || !schedule.IsFinish)
            {
                return false;
            }

            // 門禁 2：檢查該分區所屬站點是否處於安全閒置狀態
            if (!IsZoneClearForLotEnd(zone))
            {
                return false;
            }

            DispatchLog($"[分區結批觸發] 分區 {zone} 工單 ({schedule.LotId}) 生產完工！累計產量: {schedule.CurrentCount}");

            // 觸發分區完工廣播 (供 UI 顯示獨立對話框)
            ZoneLotFinished?.Invoke(zone, schedule);

            // 銷毀該分區工單，防止重複觸發
            ClearZoneSchedule(zone);
            return true;
        }

        /// <summary>
        /// 星狀拓撲公文轉發器
        /// </summary>
        private void RouteStationCommand(FlowCommand<StationCmd> cmd, string targetStation)
        {
            if (cmd == null) return;

            DispatchLog($"[S00 轉發公文] 來源: {cmd.Sender} ➔ 目標: {targetStation} | 指令: {cmd.Command}");

            if (targetStation == "S02")
            {
                this.S02.Mailbox.Enqueue(cmd);
            }
            else if (targetStation == "S01")
            {
                this.S01.Mailbox.Enqueue(cmd);
            }
        }

        private bool IsZoneClearForLotEnd(EmProductionZone zone)
        {
            switch (zone)
            {
                case EmProductionZone.WashTray:
                    // 水洗換 Tray 區：S02 必須完成當前拍節回到 Idle
                    return this.S02.CurrentState == MachineState.Idle;

                case EmProductionZone.Collect:
                    // 收盤區：S01 必須完成堆疊回到 Idle
                    return this.S01.CurrentState == MachineState.Idle;

                default:
                    return this.S01.CurrentState == MachineState.Idle && this.S02.CurrentState == MachineState.Idle;
            }
        }

        private LotSchedule GetZoneSchedule(EmProductionZone zone)
        {
            switch (zone)
            {
                case EmProductionZone.WashTray:
                    return this.Schedule_WashTray;
                case EmProductionZone.Collect:
                    return this.Schedule_Collect;
                default:
                    return this.Schedule_WashTray ?? this.Schedule_Collect;
            }
        }

        private void ClearZoneSchedule(EmProductionZone zone)
        {
            if (zone == EmProductionZone.WashTray || zone == EmProductionZone.All)
            {
                this.Schedule_WashTray = null;
                this.S02.CurrentSchedule = null;
            }
            if (zone == EmProductionZone.Collect || zone == EmProductionZone.All)
            {
                this.Schedule_Collect = null;
                this.S01.CurrentSchedule = null;
            }
        }

        private void SyncSchedulesToStations()
        {
            // 動態綁定 S01 的排程 (在席工單動態路由)
            if (this.S01.CurrentSchedule == null)
            {
                this.S01.CurrentSchedule = this.Schedule_Collect ?? this.Schedule_WashTray;
            }

            // 動態綁定 S02 的排程 (水洗專屬帳本)
            if (this.S02.CurrentSchedule == null)
            {
                this.S02.CurrentSchedule = this.Schedule_WashTray;
            }
        }

        private void CheckAllCompletedAndReturnIdle()
        {
            if (this.Schedule_WashTray == null && this.Schedule_Collect == null)
            {
                if (this.S01.CurrentState == MachineState.Idle && this.S02.CurrentState == MachineState.Idle)
                {
                    UpdateSystemState(MachineState.Idle);
                    DispatchLog("全線工單已完成結批，全機站點淨空，系統安全返回 Idle 待命。");
                }
            }
        }

        private void OnSubStationStateChanged(string stationName, MachineState state)
        {
            if (state == MachineState.Alarm)
            {
                UpdateSystemState(MachineState.Alarm);
                DispatchLog($"[全機警報聯動] 站點 {stationName} 發生警報，S00 總控介入中斷！");
            }
        }

        private void OnSafetyDoorStateChanged(SafetyDoorZone zone, bool isOpened)
        {
            if (isOpened)
            {
                DispatchLog($"[安全門警報] {zone} 安全門遭開啟！");
            }
            else
            {
                DispatchLog($"[安全門正常] {zone} 安全門已妥善關閉。");
            }
        }

        private void UpdateSystemState(MachineState state)
        {
            lock (_stateLock)
            {
                if (this.SystemState == state) return;
                this.SystemState = state;
            }
            SystemStateChanged?.Invoke(state);
        }

        private void DispatchLog(string msg)
        {
            SystemLogDispatched?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            this.S01?.Dispose();
            this.S02?.Dispose();
        }
    }
}