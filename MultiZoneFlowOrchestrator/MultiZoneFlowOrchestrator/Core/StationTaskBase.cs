using System;
using System.Threading;
using System.Threading.Tasks;

namespace MultiZoneFlowOrchestrator.Core
{
    /// <summary>
    /// 機台與站點標準運行狀態 (SECS / SEMI E10 準則)
    /// </summary>
    public enum MachineState
    {
        Init,       // 初始化中
        Idle,       // 待命 (可接收新工單或啟動命令)
        Running,    // 自動生產中
        Paused,     // 優雅暫停 (完成當前拍節後靜止)
        Alarm,      // 警報中斷 (伺服煞車/動作急停)
        Finished    // 批次結算完工
    }

    /// <summary>
    /// 單站狀態機抽象基底 (純邏輯核心，與 UI 控制項完全解耦)
    /// </summary>
    /// <typeparam name="TCmd">站點專屬的強型別命令列舉</typeparam>
    public abstract class StationTaskBase<TCmd> : IDisposable where TCmd : struct, Enum
    {
        private CancellationTokenSource _cts;
        private Task _cycleTask;
        private readonly object _stateLock = new object();

        /// <summary>
        /// 站點識別名稱 (例如: "S01_清洗入料", "S02_上蓋分離")
        /// </summary>
        public string StationName { get; }

        /// <summary>
        /// 當前站點狀態
        /// </summary>
        public MachineState CurrentState { get; private set; } = MachineState.Init;

        /// <summary>
        /// 站點專屬的執行緒安全公文信箱
        /// </summary>
        public ConcurrentCommandQueue<TCmd> Mailbox { get; } = new ConcurrentCommandQueue<TCmd>();

        /// <summary>
        /// 狀態變更廣播事件 (供 UI 與 S00 總調度訂閱)
        /// </summary>
        public event Action<string, MachineState> StateChanged;

        /// <summary>
        /// 產線 Telemetry / Log 廣播事件
        /// </summary>
        public event Action<string, string> LogDispatched;

        protected StationTaskBase(string stationName)
        {
            this.StationName = stationName;
        }

        /// <summary>
        /// 啟動站點背景輪詢狀態機
        /// </summary>
        public void Start()
        {
            lock (_stateLock)
            {
                if (_cycleTask != null && !_cycleTask.IsCompleted) return;

                _cts = new CancellationTokenSource();
                _cycleTask = Task.Run(() => WorkerLoopAsync(_cts.Token));
                TransitionTo(MachineState.Idle);
                DispatchLog("站點狀態機已啟動，進入待命 (Idle) 狀態。");
            }
        }

        /// <summary>
        /// 停止站點狀態機
        /// </summary>
        public void Stop()
        {
            lock (_stateLock)
            {
                _cts?.Cancel();
                TransitionTo(MachineState.Paused);
                DispatchLog("站點狀態機收到停止訊號。");
            }
        }

        /// <summary>
        /// 觸發站點警報中斷
        /// </summary>
        public void RaiseAlarm(string alarmReason)
        {
            lock (_stateLock)
            {
                TransitionTo(MachineState.Alarm);
                DispatchLog($"[ALARM 異常] {alarmReason}");
            }
        }

        /// <summary>
        /// 排除警報並復歸至 Idle
        /// </summary>
        public void ResetAlarm()
        {
            lock (_stateLock)
            {
                if (CurrentState == MachineState.Alarm)
                {
                    Mailbox.Clear();
                    TransitionTo(MachineState.Idle);
                    DispatchLog("警報已清除，公文佇列已重置，重回 Idle。");
                }
            }
        }

        /// <summary>
        /// 狀態機核心輪詢迴圈
        /// </summary>
        private async Task WorkerLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 處於警報或暫停狀態時，抑制工步執行
                    if (CurrentState == MachineState.Running || CurrentState == MachineState.Idle)
                    {
                        await ProcessCycleStepAsync(token);
                    }

                    // 10ms 標準工控防抖輪詢週期，避免 CPU 100% 暴衝
                    await Task.Delay(10, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    RaiseAlarm($"狀態機執行例外: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 具體子站的工步處理實作 (由 S01、S02 等各站覆寫)
        /// </summary>
        protected abstract Task ProcessCycleStepAsync(CancellationToken token);

        /// <summary>
        /// 執行緒安全的狀態切換
        /// </summary>
        protected void TransitionTo(MachineState newState)
        {
            lock (_stateLock)
            {
                if (CurrentState == newState) return;
                CurrentState = newState;
            }
            StateChanged?.Invoke(this.StationName, newState);
        }

        /// <summary>
        /// 派發日誌
        /// </summary>
        protected void DispatchLog(string message)
        {
            LogDispatched?.Invoke(this.StationName, $"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }

        public virtual void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }
}