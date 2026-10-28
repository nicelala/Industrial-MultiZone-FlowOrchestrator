using System;
using System.Collections.Generic;
using System.Linq;

namespace MultiZoneFlowOrchestrator.Core
{
    /// <summary>
    /// 工業級執行緒安全公文佇列 (Thread-Safe Command Mailbox)
    /// 封裝底層互斥鎖，提供原子化領取 (TakeAway) 與清空防護
    /// </summary>
    /// <typeparam name="TCmd">強型別命令列舉</typeparam>
    public class ConcurrentCommandQueue<TCmd> where TCmd : struct, Enum
    {
        private readonly object _lockQueue = new object();
        private readonly List<FlowCommand<TCmd>> _commandList = new List<FlowCommand<TCmd>>();

        /// <summary>
        /// 當前信箱中等待處理的公文數量
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lockQueue)
                {
                    return _commandList.Count;
                }
            }
        }

        /// <summary>
        /// 投遞新公文進信箱 (寄信)
        /// </summary>
        public void Enqueue(FlowCommand<TCmd> command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            lock (_lockQueue)
            {
                _commandList.Add(command);
            }
        }

        /// <summary>
        /// 【架構精華：原子消費 (TakeAway)】
        /// 在單一 Lock 臨界區內同時完成「條件查詢」與「自清單中拔除」，
        /// 徹底避免多執行緒在檢查與刪除的時間差內發生 Race Condition
        /// </summary>
        /// <param name="predicate">公文篩選條件 (例如指定命令型態)</param>
        /// <returns>符合條件的公文物件；若無則回傳 null</returns>
        public FlowCommand<TCmd> TakeAway(Func<FlowCommand<TCmd>, bool> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            lock (_lockQueue)
            {
                var target = _commandList.FirstOrDefault(predicate);
                if (target != null)
                {
                    _commandList.Remove(target);
                    target.MarkExecuting(); // 取出時立即鎖定為執行中
                }
                return target;
            }
        }

        /// <summary>
        /// 窺視符合條件的公文 (不移出佇列)
        /// </summary>
        public FlowCommand<TCmd> Peek(Func<FlowCommand<TCmd>, bool> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            lock (_lockQueue)
            {
                return _commandList.FirstOrDefault(predicate);
            }
        }

        /// <summary>
        /// 執行強制復歸 (Home) 或緊急停止時的安全清空
        /// </summary>
        public void Clear()
        {
            lock (_lockQueue)
            {
                foreach (var cmd in _commandList)
                {
                    cmd.MarkInterrupted();
                }
                _commandList.Clear();
            }
        }
    }
}