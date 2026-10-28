using System;
using System.Collections.Generic;
using System.Linq;

namespace MultiZoneFlowOrchestrator.Safety
{
    /// <summary>
    /// 設備分區安全門列舉 (支援多工單分區隔離防護)
    /// </summary>
    public enum SafetyDoorZone
    {
        FrontDoor,    // 前側取放料維護門
        RearDoor,     // 後側電控檢修門
        WashZoneDoor, // 水洗單軌專屬安全門
        All           // 全機所有防護門
    }

    /// <summary>
    /// 工業級安全門 SSoT (單一真理來源) 管理中樞
    /// 內建常閉 B 接點極性校驗、多區狀態閂鎖與展示級虛擬注入機制
    /// </summary>
    public class SafetyDoorManager
    {
        private static readonly Lazy<SafetyDoorManager> _instance =
            new Lazy<SafetyDoorManager>(() => new SafetyDoorManager());
        public static SafetyDoorManager Instance => _instance.Value;

        private readonly object _lock = new object();

        // 內部快取：Key 為分區門禁，Value 為是否處於「開門 (Opened)」危險狀態
        private readonly Dictionary<SafetyDoorZone, bool> _doorOpenStatusDict =
            new Dictionary<SafetyDoorZone, bool>();

        /// <summary>
        /// 安全門狀態異動廣播事件 (供 UI 警示燈號與 S00 狀態機訂閱)
        /// 參數: (異動分區, 是否開門)
        /// </summary>
        public event Action<SafetyDoorZone, bool> DoorStateChanged;

        private SafetyDoorManager()
        {
            // 初始化各分區預設為安全關閉 (False)
            _doorOpenStatusDict[SafetyDoorZone.FrontDoor] = false;
            _doorOpenStatusDict[SafetyDoorZone.RearDoor] = false;
            _doorOpenStatusDict[SafetyDoorZone.WashZoneDoor] = false;
        }

        /// <summary>
        /// 全機是否有任一安全門處於開啟狀態
        /// </summary>
        public bool IsAnyDoorOpened
        {
            get
            {
                lock (_lock)
                {
                    return _doorOpenStatusDict.Values.Any(isOpen => isOpen);
                }
            }
        }

        /// <summary>
        /// 查詢指定分區是否安全 (安全關閉回傳 true，開門或未就緒回傳 false)
        /// </summary>
        public bool IsZoneSafe(SafetyDoorZone zone)
        {
            lock (_lock)
            {
                if (zone == SafetyDoorZone.All)
                {
                    return !_doorOpenStatusDict.Values.Any(isOpen => isOpen);
                }

                return _doorOpenStatusDict.TryGetValue(zone, out bool isOpened) && !isOpened;
            }
        }

        /// <summary>
        /// 實體硬體訊號更新 (常閉 B 接點極性校驗)
        /// 規範：B 接點 Sensor 導通 (rawDi == true) 代表「關閉安全」；
        /// 斷開 (rawDi == false) 代表「開門或斷線警報」
        /// </summary>
        public void UpdateHardwareSignal(SafetyDoorZone zone, bool rawBContactDi)
        {
            // B 接點極性反轉：rawBContactDi 為 false 時代表開門 (isOpened = true)
            bool isOpened = !rawBContactDi;
            SetDoorStateInternal(zone, isOpened);
        }

        /// <summary>
        /// 【面試現場展示專用】：虛擬注入模擬開關
        /// 允許在無實體運動卡環境下，由 UI 即時切換指定分區開門/關門狀態
        /// </summary>
        public void SimulateDoorState(SafetyDoorZone zone, bool isOpened)
        {
            SetDoorStateInternal(zone, isOpened);
        }

        private void SetDoorStateInternal(SafetyDoorZone zone, bool isOpened)
        {
            bool hasChanged = false;

            lock (_lock)
            {
                if (zone == SafetyDoorZone.All)
                {
                    var keys = _doorOpenStatusDict.Keys.ToList();
                    foreach (var key in keys)
                    {
                        if (_doorOpenStatusDict[key] != isOpened)
                        {
                            _doorOpenStatusDict[key] = isOpened;
                            hasChanged = true;
                        }
                    }
                }
                else
                {
                    if (_doorOpenStatusDict.ContainsKey(zone) && _doorOpenStatusDict[zone] != isOpened)
                    {
                        _doorOpenStatusDict[zone] = isOpened;
                        hasChanged = true;
                    }
                }
            }

            // 僅在狀態真正轉態時發動廣播，杜絕高頻 Polling 引發的洗頻與 UI 閃爍
            if (hasChanged)
            {
                DoorStateChanged?.Invoke(zone, isOpened);
            }
        }
    }
}