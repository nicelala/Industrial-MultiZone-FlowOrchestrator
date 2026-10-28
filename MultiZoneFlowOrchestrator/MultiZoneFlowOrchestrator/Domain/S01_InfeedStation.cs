using System;
using System.Threading;
using System.Threading.Tasks;
using MultiZoneFlowOrchestrator.Core;
using MultiZoneFlowOrchestrator.Safety;

namespace MultiZoneFlowOrchestrator.Domain
{
    /// <summary>
    /// S01 站點內部工步狀態列舉
    /// </summary>
    public enum EmS01Working
    {
        Idle,         // 待命，等待進料訊號
        Load,         // 輸送帶進料拉料中
        WaitTrayInfo, // 檢查工單動態路由 (分流收盤或水洗)
        Stack,        // 收盤模式：執行滿盤堆疊與計數
        WaitUnload,   // 水洗模式：等待 S02 允許出料回執
        Unload        // 水洗模式：推料出站並通知 S02
    }

    /// <summary>
    /// S01 清洗區入料站狀態機
    /// 具備常閉 B 接點門禁互鎖、動態工單路由與過橋抽板交握機制
    /// </summary>
    public class S01_InfeedStation : StationTaskBase<StationCmd>
    {
        private EmS01Working _workingMode = EmS01Working.Idle;

        /// <summary>
        /// 當前生產工單帳本 (由 S00 總控注入之 SSoT)
        /// </summary>
        public LotSchedule CurrentSchedule { get; set; }

        /// <summary>
        /// 自動進料模擬開關 (展示時若無外部指令，可自動循環進料)
        /// </summary>
        public bool AutoFeedEnabled { get; set; } = false;

        /// <summary>
        /// 跨站公文發送委派 (直接對接下游 S02 或 S00 總控)
        /// </summary>
        public Action<FlowCommand<StationCmd>> OutboxDispatcher { get; set; }

        public S01_InfeedStation() : base("S01_清洗區入料站")
        {
        }

        /// <summary>
        /// 核心工步輪詢實作 (10ms 週期無阻塞運轉)
        /// </summary>
        protected override async Task ProcessCycleStepAsync(CancellationToken token)
        {
            // 🚨 全域第一道防線：常閉 B 接點安全互鎖 (Interlock Guard)
            // 若前維護門遭開啟且機構處於動作狀態，立即拋出 Alarm 中斷
            if (!SafetyDoorManager.Instance.IsZoneSafe(SafetyDoorZone.FrontDoor))
            {
                if (_workingMode != EmS01Working.Idle && CurrentState == MachineState.Running)
                {
                    RaiseAlarm("前側維護門開啟！常閉 B 接點斷開，安全互鎖觸發，強制中斷機構動作！");
                    _workingMode = EmS01Working.Idle;
                    return;
                }
            }

            switch (_workingMode)
            {
                case EmS01Working.Idle:
                    // 1. 檢查信箱是否有入料公文 (或模擬自動補料)
                    var cmd = Mailbox.TakeAway(x => x.Command == StationCmd.S01_AskLoadReady);
                    if (cmd != null || AutoFeedEnabled)
                    {
                        FlowCommand<StationCmd>.RanToCompletion(ref cmd); // 過橋抽板，銷毀指針
                        _workingMode = EmS01Working.Load;
                        DispatchLog("S01 收到進料命令，切換至 Load 工步，啟動入料輸送帶。");
                    }
                    break;

                case EmS01Working.Load:
                    // 2. 模擬拉料進站動作 (耗時 600ms)
                    await Task.Delay(600, token);

                    DispatchLog("物料已進入 S01 在席檢知，進入 WaitTrayInfo 進行分流判定。");
                    _workingMode = EmS01Working.WaitTrayInfo;
                    break;

                case EmS01Working.WaitTrayInfo:
                    // 3. 【動態路由分流點】：依當前工單分區決定走向
                    if (CurrentSchedule == null || CurrentSchedule.IsFinish)
                    {
                        DispatchLog("當前無有效排程或已達標結批，S01 停止取料，回到 Idle。");
                        _workingMode = EmS01Working.Idle;
                        break;
                    }

                    if (CurrentSchedule.Zone == EmProductionZone.Collect)
                    {
                        // 路線 A：收盤工單 -> 直接在 S01 內部堆疊
                        DispatchLog($"[路由分流] 識別為收盤工單 ({CurrentSchedule.LotId})，轉入 Stack 堆疊工步。");
                        _workingMode = EmS01Working.Stack;
                    }
                    else
                    {
                        // 路線 B：水洗工單 -> 發送信件向 S02 請求出料
                        DispatchLog($"[路由分流] 識別為水洗工單 ({CurrentSchedule.LotId})，向 S02 發送 S02_AskUnload 請求。");
                        OutboxDispatcher?.Invoke(new FlowCommand<StationCmd>(StationCmd.S02_AskUnload, this.StationName));
                        _workingMode = EmS01Working.WaitUnload;
                    }
                    break;

                case EmS01Working.Stack:
                    // 4. 收盤堆疊：模擬頂升與堆盤動作 (耗時 400ms)
                    await Task.Delay(400, token);

                    CurrentSchedule?.IncrementCount(1);
                    DispatchLog($"[產量累計] 收盤堆疊完成 1 盤，當前累計: {CurrentSchedule.CurrentCount} / {CurrentSchedule.TargetQty}");
                    _workingMode = EmS01Working.Idle;
                    break;

                case EmS01Working.WaitUnload:
                    // 5. 等待 S02 接收完成或允許出料回執
                    var unloadDoneCmd = Mailbox.TakeAway(x => x.Command == StationCmd.S02_UnloadDone);
                    if (unloadDoneCmd != null)
                    {
                        FlowCommand<StationCmd>.RanToCompletion(ref unloadDoneCmd);
                        _workingMode = EmS01Working.Unload;
                    }
                    break;

                case EmS01Working.Unload:
                    // 6. 模擬出料推料至 S02 (耗時 500ms)
                    await Task.Delay(500, token);

                    DispatchLog("S01 推料完畢，向 S02 發送 S02_NotifyTraysIn 完成交握閉環。");
                    OutboxDispatcher?.Invoke(new FlowCommand<StationCmd>(StationCmd.S02_NotifyTraysIn, this.StationName));
                    _workingMode = EmS01Working.Idle;
                    break;
            }
        }
    }
}