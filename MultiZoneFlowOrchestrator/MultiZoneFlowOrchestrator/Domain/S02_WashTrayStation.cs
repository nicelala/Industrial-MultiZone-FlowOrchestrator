using System;
using System.Threading;
using System.Threading.Tasks;
using MultiZoneFlowOrchestrator.Core;
using MultiZoneFlowOrchestrator.Safety;

namespace MultiZoneFlowOrchestrator.Domain
{
    /// <summary>
    /// S02 站點內部工步狀態列舉
    /// </summary>
    public enum EmS02Working
    {
        Idle,         // 待命，等待上游 S01 出料請求 (AskUnload)
        WaitTraysIn,  // 已授權出料，等待 S01 推料到位通知 (NotifyTraysIn)
        Processing,   // 執行水洗換 Tray 實體機構動作
        Finish        // 單盤加工完成收尾，累加產量
    }

    /// <summary>
    /// S02 水洗換 Tray 站狀態機
    /// 實作水洗區專屬門禁互鎖、三方閉環交握與獨立工單產量累計
    /// </summary>
    public class S02_WashTrayStation : StationTaskBase<StationCmd>
    {
        private EmS02Working _workingMode = EmS02Working.Idle;

        /// <summary>
        /// 當前生產工單帳本 (由 S00 總控注入之水洗專屬工單)
        /// </summary>
        public LotSchedule CurrentSchedule { get; set; }

        /// <summary>
        /// 跨站公文發送委派 (回傳 S01 或通報 S00 總控)
        /// </summary>
        public Action<FlowCommand<StationCmd>> OutboxDispatcher { get; set; }

        public S02_WashTrayStation() : base("S02_水洗換Tray站")
        {
        }

        /// <summary>
        /// S02 核心工步輪詢實作 (10ms 週期無阻塞運轉)
        /// </summary>
        protected override async Task ProcessCycleStepAsync(CancellationToken token)
        {
            // 🚨 水洗專屬安全門禁守衛：檢查水洗防護門 (WashZoneDoor)
            if (!SafetyDoorManager.Instance.IsZoneSafe(SafetyDoorZone.WashZoneDoor))
            {
                if (_workingMode != EmS02Working.Idle && CurrentState == MachineState.Running)
                {
                    RaiseAlarm("水洗區安全門開啟！常閉 B 接點斷開，安全互鎖觸發，S02 停止動作！");
                    _workingMode = EmS02Working.Idle;
                    return;
                }
            }

            switch (_workingMode)
            {
                case EmS02Working.Idle:
                    // 1. 輪詢信箱是否收到來自 S01 的出料請求
                    var askCmd = Mailbox.TakeAway(x => x.Command == StationCmd.S02_AskUnload);
                    if (askCmd != null)
                    {
                        FlowCommand<StationCmd>.RanToCompletion(ref askCmd); // 過橋抽板，銷毀指針

                        DispatchLog("S02 收到 S01_AskUnload 請求，回覆放行回執 S02_UnloadDone。");
                        // 回覆 S01 允許出料
                        OutboxDispatcher?.Invoke(new FlowCommand<StationCmd>(StationCmd.S02_UnloadDone, this.StationName));

                        _workingMode = EmS02Working.WaitTraysIn;
                    }
                    break;

                case EmS02Working.WaitTraysIn:
                    // 2. 等待 S01 推料到位的通知公文
                    var inCmd = Mailbox.TakeAway(x => x.Command == StationCmd.S02_NotifyTraysIn);
                    if (inCmd != null)
                    {
                        FlowCommand<StationCmd>.RanToCompletion(ref inCmd); // 過橋抽板

                        DispatchLog("S02 收到 S02_NotifyTraysIn，物料到位，切入 Processing 執行水洗換 Tray。");
                        _workingMode = EmS02Working.Processing;
                    }
                    break;

                case EmS02Working.Processing:
                    // 3. 模擬水洗換 Tray 物理加工 (耗時 700ms)
                    await Task.Delay(700, token);

                    _workingMode = EmS02Working.Finish;
                    break;

                case EmS02Working.Finish:
                    // 4. 水洗單元完工扣帳：產量累加與 SSoT 狀態校驗
                    if (CurrentSchedule != null)
                    {
                        CurrentSchedule.IncrementCount(1);
                        DispatchLog($"[水洗產量累計] 換 Tray 完成 1 盤，當前進度: {CurrentSchedule.CurrentCount} / {CurrentSchedule.TargetQty}");
                    }
                    else
                    {
                        DispatchLog("警告：S02 完工時無綁定工單帳本 (Schedule is null)。");
                    }

                    _workingMode = EmS02Working.Idle;
                    break;
            }
        }
    }
}