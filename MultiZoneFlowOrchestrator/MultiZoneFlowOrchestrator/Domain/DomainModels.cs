using System;

namespace MultiZoneFlowOrchestrator.Domain
{
    /// <summary>
    /// 生產分區身分列舉 (落實區域隔離，支援雙工單獨立結批)
    /// </summary>
    public enum EmProductionZone
    {
        WashTray,   // 水洗換 Tray 區 (專屬流水線 A)
        Collect,    // 收盤堆疊區 (專屬流水線 B)
        All         // 全機單工單模式 (向後相容傳統架構)
    }

    /// <summary>
    /// 跨站交握強型別命令列舉
    /// </summary>
    public enum StationCmd
    {
        // 上游入料與 Tray 盤身分傳遞
        S01_AskLoadReady,   // 上游水洗機請求進料
        S01_TrayData,       // 載入當前 Tray 盤資料與 Recipe 綁定

        // 站對站進出料交握 (S01 與 S02 之間)
        S02_AskUnload,      // S01 詢問 S02 是否允許出料
        S02_NotifyTraysIn,  // S01 通知 S02 料盤已推進
        S02_UnloadDone,     // S02 確認接收完成，S01 可復歸

        // 系統控制與通用指令
        Sys_Pause,
        Sys_Resume,
        Sys_LotEnd
    }

    /// <summary>
    /// 單一真理來源 (SSoT) 工單帳本模型
    /// </summary>
    public class LotSchedule
    {
        private readonly object _counterLock = new object();

        public string LotId { get; set; }
        public EmProductionZone Zone { get; set; }
        public int TargetQty { get; set; }
        public int CurrentCount { get; private set; }

        public bool IsStop { get; set; }

        /// <summary>
        /// 結批判定：達到目標數量或被人工標記停止
        /// </summary>
        public bool IsFinish => (CurrentCount >= TargetQty && TargetQty > 0) || IsStop;

        public LotSchedule(string lotId, EmProductionZone zone, int targetQty)
        {
            this.LotId = lotId;
            this.Zone = zone;
            this.TargetQty = targetQty;
            this.CurrentCount = 0;
            this.IsStop = false;
        }

        /// <summary>
        /// 執行緒安全的產量累加
        /// </summary>
        public void IncrementCount(int qty = 1)
        {
            lock (_counterLock)
            {
                CurrentCount += qty;
            }
        }
    }
}