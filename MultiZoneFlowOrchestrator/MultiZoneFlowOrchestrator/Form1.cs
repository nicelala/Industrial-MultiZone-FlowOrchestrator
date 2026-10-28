using System;
using System.Drawing;
using System.Windows.Forms;
using MultiZoneFlowOrchestrator.Core;
using MultiZoneFlowOrchestrator.Domain;
using MultiZoneFlowOrchestrator.Safety;

namespace MultiZoneFlowOrchestrator
{
    public partial class Form1 : Form
    {
        // 核心調度大腦
        private S00Orchestrator _orchestrator;
        private Timer _uiRefreshTimer;

        // UI 控制項宣告
        private Label _lblSystemState;
        private Button _btnRun;
        private Button _btnStop;
        private Button _btnReset;
        private CheckBox _chkAutoFeed;
        private CheckBox _chkDualZone;

        // 水洗區控制項
        private Label _lblWashStatus;
        private ProgressBar _pbWash;
        private Button _btnWashStop;

        // 收盤區控制項
        private Label _lblCollectStatus;
        private ProgressBar _pbCollect;
        private Button _btnCollectStop;

        // 安全門模擬控制項
        private Button _btnToggleFrontDoor;
        private Button _btnToggleWashDoor;

        // Telemetry 日誌框
        private ListBox _lstLogs;

        public Form1()
        {
            InitializeComponent();
            InitializeDynamicUi();
            InitializeCoreEngine();
        }

        /// <summary>
        /// 純 C# 工業級深色主題動態排版
        /// </summary>
        private void InitializeDynamicUi()
        {
            this.Text = "工控分散式架構演示平台 - MultiZone Flow Orchestrator";
            this.Size = new Size(1024, 768);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(28, 28, 28);
            this.ForeColor = Color.White;
            this.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);

            // ==========================================
            // 1. 頂部狀態與主控區 (Panel)
            // ==========================================
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 85,
                BackColor = Color.FromArgb(37, 37, 38),
                Padding = new Padding(15)
            };

            _lblSystemState = new Label
            {
                Text = "STATE: INIT",
                Size = new Size(160, 48),
                Location = new Point(15, 18),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                BackColor = Color.DimGray,
                ForeColor = Color.White
            };

            _btnRun = new Button
            {
                Text = "啟動 Run",
                Size = new Size(110, 48),
                Location = new Point(190, 18),
                BackColor = Color.FromArgb(0, 122, 204),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };
            _btnRun.Click += (s, e) => _orchestrator.Start();

            _btnStop = new Button
            {
                Text = "停止 Stop",
                Size = new Size(110, 48),
                Location = new Point(310, 18),
                BackColor = Color.FromArgb(202, 81, 0),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };
            _btnStop.Click += (s, e) => _orchestrator.Stop();

            _btnReset = new Button
            {
                Text = "警報復歸 Reset",
                Size = new Size(130, 48),
                Location = new Point(430, 18),
                BackColor = Color.FromArgb(60, 60, 60),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };
            _btnReset.Click += (s, e) => _orchestrator.ResetAlarm();

            _chkAutoFeed = new CheckBox
            {
                Text = "S01 自動入料模擬",
                Location = new Point(580, 30),
                AutoSize = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Regular)
            };
            _chkAutoFeed.CheckedChanged += (s, e) =>
            {
                if (_orchestrator != null)
                {
                    _orchestrator.S01.AutoFeedEnabled = _chkAutoFeed.Checked;
                }
            };

            _chkDualZone = new CheckBox
            {
                Text = "啟用分區生產模式 (Dual-Zone)",
                Location = new Point(760, 30),
                AutoSize = true,
                Checked = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Regular)
            };
            _chkDualZone.CheckedChanged += (s, e) =>
            {
                if (_orchestrator != null)
                {
                    _orchestrator.IsDualZoneMode = _chkDualZone.Checked;
                }
            };

            topPanel.Controls.AddRange(new Control[] { _lblSystemState, _btnRun, _btnStop, _btnReset, _chkAutoFeed, _chkDualZone });

            // ==========================================
            // 2. 中間儀表區 (分區工單 + 安全防護模擬)
            // ==========================================
            var middlePanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 240,
                BackColor = Color.FromArgb(30, 30, 30),
                Padding = new Padding(15)
            };

            // 2A. 水洗換 Tray 區 GroupBox
            var gbWash = new GroupBox
            {
                Text = "【水洗換 Tray 區】工單 SSoT",
                Size = new Size(310, 210),
                Location = new Point(15, 15),
                ForeColor = Color.DeepSkyBlue,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };

            _lblWashStatus = new Label
            {
                Text = "工單: WASH-LOT-001\n進度: 0 / 5 盤",
                Location = new Point(15, 35),
                Size = new Size(280, 50),
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular)
            };

            _pbWash = new ProgressBar
            {
                Location = new Point(15, 95),
                Size = new Size(280, 25),
                Maximum = 5,
                Value = 0
            };

            _btnWashStop = new Button
            {
                Text = "手動結批 (Stop)",
                Location = new Point(15, 140),
                Size = new Size(130, 35),
                BackColor = Color.FromArgb(70, 70, 70),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                ForeColor = Color.White
            };
            _btnWashStop.Click += (s, e) =>
            {
                if (_orchestrator?.Schedule_WashTray != null)
                {
                    _orchestrator.Schedule_WashTray.IsStop = true;
                }
            };
            gbWash.Controls.AddRange(new Control[] { _lblWashStatus, _pbWash, _btnWashStop });

            // 2B. 收盤堆疊區 GroupBox
            var gbCollect = new GroupBox
            {
                Text = "【收盤堆疊區】工單 SSoT",
                Size = new Size(310, 210),
                Location = new Point(340, 15),
                ForeColor = Color.Orange,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };

            _lblCollectStatus = new Label
            {
                Text = "工單: COLLECT-LOT-888\n進度: 0 / 3 盤",
                Location = new Point(15, 35),
                Size = new Size(280, 50),
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular)
            };

            _pbCollect = new ProgressBar
            {
                Location = new Point(15, 95),
                Size = new Size(280, 25),
                Maximum = 3,
                Value = 0
            };

            _btnCollectStop = new Button
            {
                Text = "手動結批 (Stop)",
                Location = new Point(15, 140),
                Size = new Size(130, 35),
                BackColor = Color.FromArgb(70, 70, 70),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                ForeColor = Color.White
            };
            _btnCollectStop.Click += (s, e) =>
            {
                if (_orchestrator?.Schedule_Collect != null)
                {
                    _orchestrator.Schedule_Collect.IsStop = true;
                }
            };
            gbCollect.Controls.AddRange(new Control[] { _lblCollectStatus, _pbCollect, _btnCollectStop });

            // 2C. 安全門禁模擬 GroupBox (常閉 B 接點極性演示)
            var gbSafety = new GroupBox
            {
                Text = "【安全門禁互鎖模擬 (NC B接點)】",
                Size = new Size(330, 210),
                Location = new Point(665, 15),
                ForeColor = Color.LightCoral,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };

            _btnToggleFrontDoor = new Button
            {
                Text = "前維護門: [關閉/安全 (導通)]",
                Location = new Point(15, 35),
                Size = new Size(300, 42),
                BackColor = Color.DarkGreen,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };
            _btnToggleFrontDoor.Click += ToggleFrontDoor_Click;

            _btnToggleWashDoor = new Button
            {
                Text = "水洗安全門: [關閉/安全 (導通)]",
                Location = new Point(15, 90),
                Size = new Size(300, 42),
                BackColor = Color.DarkGreen,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };
            _btnToggleWashDoor.Click += ToggleWashDoor_Click;

            var lblSafetyTip = new Label
            {
                Text = "常閉 B 接點：導通=安全；開門/斷線=中斷拋警報",
                Location = new Point(15, 145),
                Size = new Size(300, 45),
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular)
            };

            gbSafety.Controls.AddRange(new Control[] { _btnToggleFrontDoor, _btnToggleWashDoor, lblSafetyTip });

            middlePanel.Controls.AddRange(new Control[] { gbWash, gbCollect, gbSafety });

            // ==========================================
            // 3. 底部 Telemetry 即時日誌視窗
            // ==========================================
            var bottomGroup = new GroupBox
            {
                Text = "即時系統 Telemetry 與站點交握公文日誌 (Non-blocking Console)",
                Dock = DockStyle.Fill,
                ForeColor = Color.LightSkyBlue,
                Padding = new Padding(10)
            };

            _lstLogs = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 9.5F, FontStyle.Regular),
                BorderStyle = BorderStyle.None,
                HorizontalScrollbar = true
            };

            bottomGroup.Controls.Add(_lstLogs);

            this.Controls.Add(bottomGroup);
            this.Controls.Add(middlePanel);
            this.Controls.Add(topPanel);
        }

        /// <summary>
        /// 初始化底層調度引擎與 SSoT 綁定
        /// </summary>
        private void InitializeCoreEngine()
        {
            _orchestrator = new S00Orchestrator();

            // 預設注入兩筆分區工單 (SSoT)
            _orchestrator.Schedule_WashTray = new LotSchedule("WASH-LOT-001", EmProductionZone.WashTray, targetQty: 5);
            _orchestrator.Schedule_Collect = new LotSchedule("COLLECT-LOT-888", EmProductionZone.Collect, targetQty: 3);

            // 訂閱全機狀態變更
            _orchestrator.SystemStateChanged += OnSystemStateChanged;

            // 訂閱系統總日誌
            _orchestrator.SystemLogDispatched += OnSystemLogDispatched;

            // ⭐ 訂閱分區獨立完工事件 (BeginInvoke 異步彈窗，絕不阻塞背景 Task)
            _orchestrator.ZoneLotFinished += OnZoneLotFinished;

            // UI 定期刷新計時器 (100ms 刷新進度條)
            _uiRefreshTimer = new Timer { Interval = 100 };
            _uiRefreshTimer.Tick += (s, e) => RefreshUiTelemetry();
            _uiRefreshTimer.Start();
        }

        private void OnSystemStateChanged(MachineState state)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke((Action)(() => OnSystemStateChanged(state)));
                return;
            }

            _lblSystemState.Text = $"STATE: {state.ToString().ToUpper()}";
            switch (state)
            {
                case MachineState.Running:
                    _lblSystemState.BackColor = Color.Green;
                    break;
                case MachineState.Paused:
                    _lblSystemState.BackColor = Color.Goldenrod;
                    break;
                case MachineState.Alarm:
                    _lblSystemState.BackColor = Color.Red;
                    break;
                case MachineState.Idle:
                    _lblSystemState.BackColor = Color.SteelBlue;
                    break;
                default:
                    _lblSystemState.BackColor = Color.DimGray;
                    break;
            }
        }

        private void OnSystemLogDispatched(string message)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke((Action)(() => OnSystemLogDispatched(message)));
                return;
            }

            _lstLogs.Items.Add(message);
            // 自動滾動到底部
            _lstLogs.TopIndex = _lstLogs.Items.Count - 1;

            // 防止日誌過長消耗過多記憶體
            if (_lstLogs.Items.Count > 1000)
            {
                _lstLogs.Items.RemoveAt(0);
            }
        }

        /// <summary>
        /// ⭐ 分區獨立結批彈窗處理 (Non-blocking UI Dispatch)
        /// </summary>
        private void OnZoneLotFinished(EmProductionZone zone, LotSchedule schedule)
        {
            this.BeginInvoke((Action)(() =>
            {
                string zoneTitle = zone == EmProductionZone.Collect ? "【收盤區】" : "【水洗換 Tray 區】";
                string finishMsg = $"{zoneTitle} 工單生產完畢！\n\n" +
                                   $"工單號碼: {schedule.LotId}\n" +
                                   $"目標數量: {schedule.TargetQty} 盤\n" +
                                   $"累計實產: {schedule.CurrentCount} 盤\n\n" +
                                   "說明：此分區已安全結批清線，另一分區不受干擾持續運作中。";

                MessageBox.Show(this, finishMsg, $"{zoneTitle} 獨立完工結批通知", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }));
        }

        private void RefreshUiTelemetry()
        {
            // 刷新水洗區資訊
            var washSched = _orchestrator?.Schedule_WashTray;
            if (washSched != null)
            {
                _lblWashStatus.Text = $"工單: {washSched.LotId}\n進度: {washSched.CurrentCount} / {washSched.TargetQty} 盤";
                _pbWash.Maximum = washSched.TargetQty;
                _pbWash.Value = Math.Min(washSched.CurrentCount, _pbWash.Maximum);
            }
            else
            {
                _lblWashStatus.Text = "工單: (已結批完工/淨空)\n進度: 0 / 0 盤";
                _pbWash.Value = 0;
            }

            // 刷新收盤區資訊
            var collectSched = _orchestrator?.Schedule_Collect;
            if (collectSched != null)
            {
                _lblCollectStatus.Text = $"工單: {collectSched.LotId}\n進度: {collectSched.CurrentCount} / {collectSched.TargetQty} 盤";
                _pbCollect.Maximum = collectSched.TargetQty;
                _pbCollect.Value = Math.Min(collectSched.CurrentCount, _pbCollect.Maximum);
            }
            else
            {
                _lblCollectStatus.Text = "工單: (已結批完工/淨空)\n進度: 0 / 0 盤";
                _pbCollect.Value = 0;
            }
        }

        private void ToggleFrontDoor_Click(object sender, EventArgs e)
        {
            bool isCurrentSafe = SafetyDoorManager.Instance.IsZoneSafe(SafetyDoorZone.FrontDoor);
            bool newOpenState = isCurrentSafe; // 若目前安全(關門)，按下後變成開門(true)

            SafetyDoorManager.Instance.SimulateDoorState(SafetyDoorZone.FrontDoor, newOpenState);

            if (newOpenState)
            {
                _btnToggleFrontDoor.Text = "前維護門: [開啟/危險 (斷開)]";
                _btnToggleFrontDoor.BackColor = Color.DarkRed;
            }
            else
            {
                _btnToggleFrontDoor.Text = "前維護門: [關閉/安全 (導通)]";
                _btnToggleFrontDoor.BackColor = Color.DarkGreen;
            }
        }

        private void ToggleWashDoor_Click(object sender, EventArgs e)
        {
            bool isCurrentSafe = SafetyDoorManager.Instance.IsZoneSafe(SafetyDoorZone.WashZoneDoor);
            bool newOpenState = isCurrentSafe;

            SafetyDoorManager.Instance.SimulateDoorState(SafetyDoorZone.WashZoneDoor, newOpenState);

            if (newOpenState)
            {
                _btnToggleWashDoor.Text = "水洗安全門: [開啟/危險 (斷開)]";
                _btnToggleWashDoor.BackColor = Color.DarkRed;
            }
            else
            {
                _btnToggleWashDoor.Text = "水洗安全門: [關閉/安全 (導通)]";
                _btnToggleWashDoor.BackColor = Color.DarkGreen;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _uiRefreshTimer?.Stop();
            _orchestrator?.Dispose();
            base.OnFormClosing(e);
        }
    }
}