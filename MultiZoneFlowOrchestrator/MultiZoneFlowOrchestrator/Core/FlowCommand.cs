using System;

namespace MultiZoneFlowOrchestrator.Core
{
    /// <summary>
    /// 公文生命週期狀態
    /// </summary>
    public enum CommandState
    {
        Pending,     // 等待處理
        Executing,   // 執行中
        Completed,   // 順利完工
        Interrupted  // 異常中斷 (Alarm)
    }

    /// <summary>
    /// 強型別跨站通訊公文模型
    /// </summary>
    /// <typeparam name="TCmd">必須為列舉型別 (Enum)，強制在編譯期固化指令名稱，消滅 Magic String</typeparam>
    public class FlowCommand<TCmd> where TCmd : struct, Enum
    {
        private readonly object _syncLock = new object();

        /// <summary>
        /// 指令名稱 (例如：S02_回入料、S06_取料)
        /// </summary>
        public TCmd Command { get; }

        /// <summary>
        /// 派工來源站點或分區
        /// </summary>
        public string Sender { get; }

        /// <summary>
        /// 當前公文狀態
        /// </summary>
        public CommandState State { get; private set; }

        /// <summary>
        /// 建立時間戳記 (用於超時檢核與 Telemetry 追蹤)
        /// </summary>
        public DateTime CreatedTime { get; }

        public FlowCommand(TCmd command, string sender)
        {
            this.Command = command;
            this.Sender = sender;
            this.State = CommandState.Pending;
            this.CreatedTime = DateTime.Now;
        }

        /// <summary>
        /// 標記公文開始執行
        /// </summary>
        public void MarkExecuting()
        {
            lock (_syncLock)
            {
                if (this.State == CommandState.Pending)
                {
                    this.State = CommandState.Executing;
                }
            }
        }

        /// <summary>
        /// 標記公文完工
        /// </summary>
        public void MarkCompleted()
        {
            lock (_syncLock)
            {
                this.State = CommandState.Completed;
            }
        }

        /// <summary>
        /// 標記公文異常中斷
        /// </summary>
        public void MarkInterrupted()
        {
            lock (_syncLock)
            {
                this.State = CommandState.Interrupted;
            }
        }

        /// <summary>
        /// 【架構防呆核心】：過橋抽板機制。
        /// 完工後將公文指標安全歸零 (null)，防止幽靈指令重入或重複執行
        /// </summary>
        public static void RanToCompletion(ref FlowCommand<TCmd> cmd)
        {
            if (cmd != null)
            {
                cmd.MarkCompleted();
                cmd = null; // 斷開參照，交由 GC 回收，防止時序錯亂
            }
        }
    }
}