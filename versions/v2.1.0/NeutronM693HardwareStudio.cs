using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace NeutronM693HardwareStudio
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class MainForm : Form
    {
        private M693Device _device;
        private byte[] _originalProfile;
        private byte[] _pendingProfile;
        private readonly Dictionary<int, byte[]> _macroSlots = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, byte[]> _pendingMacroSlots = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, ParsedMacro> _parsedMacros = new Dictionary<int, ParsedMacro>();
        private readonly List<PhysicalButtonSpec> _physicalButtons = PhysicalButtonSpec.CreateM693();
        private string _loadedProfilePath;

        private Label lblStatus;
        private Label lblOfficial;
        private Label lblDevice;
        private Button btnConnect;
        private Button btnRead;
        private Button btnExport;
        private Button btnImport;
        private Button btnApply;
        private Button btnRawBackup;
        private TabControl tabs;

        private DataGridView gridButtons;
        private DataGridView gridMatrix;
        private ComboBox cmbSimpleAction;
        private Button btnAssignSimple;
        private TextBox txtCapture;
        private Button btnCapture;
        private Label lblCapture;

        private ComboBox cmbMacroClick;
        private NumericUpDown numMin;
        private NumericUpDown numTypical;
        private NumericUpDown numMax;
        private TrackBar trkSmooth;
        private Label lblSmooth;
        private Button btnGenerateMacro;
        private TextBox txtPreview;

        private ComboBox cmbPolling;
        private NumericUpDown numDebounce;
        private ComboBox cmbLod;
        private NumericUpDown numSleep;
        private CheckBox chkMotionSync;
        private CheckBox chkAngleSnap;
        private CheckBox chkRipple;
        private CheckBox chkLedOffMoving;

        private ComboBox cmbLedMode;
        private Button btnLedColor;
        private NumericUpDown numLedSpeed;
        private NumericUpDown numLedBrightness;
        private Color _ledColor = Color.Magenta;

        private NumericUpDown numDpiCount;
        private ComboBox cmbDpiActive;
        private readonly ComboBox[] cmbDpi = new ComboBox[8];
        private readonly Button[] btnDpiColor = new Button[8];
        private readonly Color[] dpiColors = new Color[8];

        private TextBox txtLog;
        private CaptureEngine _capture;
        private bool _loadingUi;
        private bool _dirtySensor;
        private bool _dirtyLed;
        private bool _dirtyDpi;

        // Dashboard / battery / local profile library
        private Label lblDashDevice;
        private Label lblDashBattery;
        private Label lblDashProfile;
        private Label lblDashMode;
        private ProgressBar prgBattery;
        private PictureBox picMouse;
        private TextBox txtDashSummary;
        private System.Windows.Forms.Timer batteryTimer;
        private ListBox lstProfileLibrary;
        private TextBox txtProfileName;
        private Label lblProfileInfo;

        // Extended button actions
        private NumericUpDown numFireInterval;
        private NumericUpDown numFireRepeat;

        // Windows pointer settings (separate from onboard mouse memory)
        private NumericUpDown numWinSpeed;
        private NumericUpDown numWinDoubleClick;
        private NumericUpDown numWinWheel;
        private CheckBox chkWinEnhance;

        // Advanced / RAW / macro inspection
        private ComboBox cmbRawPhysical;
        private TextBox txtRawType;
        private TextBox txtRawA;
        private TextBox txtRawB;
        private Label lblRawCurrent;
        private DataGridView gridMacros;

        public MainForm()
        {
            Text = "Neutron M693 Hardware Studio 2.1.0 MAX";
            Width = 1280;
            Height = 860;
            MinimumSize = new Size(1080, 720);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(24, 24, 26);
            ForeColor = Color.Gainsboro;
            try
            {
                string ico = Path.Combine(Application.StartupPath, "Assets", "appico.ico");
                if (File.Exists(ico)) Icon = new Icon(ico);
            }
            catch { }
            BuildUi();
            UpdateOfficialStatus();
            batteryTimer = new System.Windows.Forms.Timer();
            batteryTimer.Interval = 5000;
            batteryTimer.Tick += delegate { RefreshBatterySafe(); };
            batteryTimer.Start();
            FormClosing += delegate
            {
                if (batteryTimer != null) { batteryTimer.Stop(); batteryTimer.Dispose(); batteryTimer = null; }
                SafeDisposeDevice();
                if (_capture != null) _capture.Dispose();
            };
        }

        private void BuildUi()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 104, Padding = new Padding(10) };
            Controls.Add(top);

            lblStatus = new Label { Left = 10, Top = 10, Width = 780, Height = 22, Text = "Не підключено" };
            lblOfficial = new Label { Left = 10, Top = 34, Width = 780, Height = 22 };
            lblDevice = new Label { Left = 10, Top = 58, Width = 780, Height = 36, AutoEllipsis = true };
            top.Controls.Add(lblStatus);
            top.Controls.Add(lblOfficial);
            top.Controls.Add(lblDevice);

            btnConnect = MakeButton("Знайти M693", 805, 8, 150, 30, delegate { ConnectDevice(); });
            btnRead = MakeButton("Прочитати з миші", 965, 8, 175, 30, delegate { ReadFromMouse(); });
            btnExport = MakeButton("Експорт профілю", 805, 44, 150, 30, delegate { ExportProfile(); });
            btnImport = MakeButton("Імпорт профілю", 965, 44, 175, 30, delegate { ImportProfile(); });
            btnRawBackup = MakeButton("RAW backup", 805, 78, 150, 24, delegate { RawBackup(); });
            btnApply = MakeButton("ЗАСТОСУВАТИ В МИШКУ", 965, 78, 175, 24, delegate { ApplyToMouse(); });
            top.Controls.AddRange(new Control[] { btnConnect, btnRead, btnExport, btnImport, btnRawBackup, btnApply });

            tabs = new TabControl { Dock = DockStyle.Fill };
            Controls.Add(tabs);
            tabs.BringToFront();

            BuildDashboardTab();
            BuildButtonsTab();
            BuildDpiTab();
            BuildLightingTab();
            BuildSensorTab();
            BuildProfilesTab();
            BuildWindowsTab();
            BuildAdvancedTab();
            BuildLogTab();

            SetEditorsEnabled(false);
            UiTheme.Apply(this);
            RefreshProfileLibrary();

            cmbPolling.SelectedIndexChanged += delegate { if (!_loadingUi) _dirtySensor = true; };
            numDebounce.ValueChanged += delegate { if (!_loadingUi) _dirtySensor = true; };
            cmbLod.SelectedIndexChanged += delegate { if (!_loadingUi) _dirtySensor = true; };
            numSleep.ValueChanged += delegate { if (!_loadingUi) _dirtySensor = true; };
            chkMotionSync.CheckedChanged += delegate { if (!_loadingUi) _dirtySensor = true; };
            chkAngleSnap.CheckedChanged += delegate { if (!_loadingUi) _dirtySensor = true; };
            chkRipple.CheckedChanged += delegate { if (!_loadingUi) _dirtySensor = true; };
            chkLedOffMoving.CheckedChanged += delegate { if (!_loadingUi) _dirtySensor = true; };
            cmbLedMode.SelectedIndexChanged += delegate { if (!_loadingUi) _dirtyLed = true; };
            numLedSpeed.ValueChanged += delegate { if (!_loadingUi) _dirtyLed = true; };
            numLedBrightness.ValueChanged += delegate { if (!_loadingUi) _dirtyLed = true; };
            numDpiCount.ValueChanged += delegate { if (!_loadingUi) _dirtyDpi = true; };
            cmbDpiActive.SelectedIndexChanged += delegate { if (!_loadingUi) _dirtyDpi = true; };
            for (int di = 0; di < 8; di++)
                cmbDpi[di].SelectedIndexChanged += delegate { if (!_loadingUi) _dirtyDpi = true; };
        }

        private void BuildDashboardTab()
        {
            var page = new TabPage("Головна / Стан");
            tabs.TabPages.Add(page);

            var left = new Panel { Dock = DockStyle.Left, Width = 430, Padding = new Padding(18) };
            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18) };
            page.Controls.Add(right);
            page.Controls.Add(left);

            var title = new Label
            {
                Left = 18, Top = 16, Width = 390, Height = 44,
                Text = "REDRAGON TRIDENT M693",
                Font = new Font("Segoe UI Semibold", 17F, FontStyle.Bold),
                ForeColor = Color.FromArgb(238, 56, 56)
            };
            left.Controls.Add(title);

            picMouse = new PictureBox
            {
                Left = 30, Top = 72, Width = 350, Height = 500,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent
            };
            try
            {
                string pth = Path.Combine(Application.StartupPath, "Assets", "mouse_nr.png");
                if (File.Exists(pth)) picMouse.Image = Image.FromFile(pth);
            }
            catch { }
            left.Controls.Add(picMouse);

            int y = 20;
            right.Controls.Add(new Label { Left = 18, Top = y, Width = 620, Height = 28, Text = "Апаратний стан", Font = new Font("Segoe UI Semibold", 14F, FontStyle.Bold) });
            y += 45;
            lblDashDevice = new Label { Left = 18, Top = y, Width = 700, Height = 42, Text = "Пристрій: не підключено", AutoEllipsis = true };
            right.Controls.Add(lblDashDevice);
            y += 52;
            lblDashMode = new Label { Left = 18, Top = y, Width = 700, Height = 28, Text = "Режим: —" };
            right.Controls.Add(lblDashMode);
            y += 36;
            lblDashProfile = new Label { Left = 18, Top = y, Width = 700, Height = 28, Text = "Onboard profile: —" };
            right.Controls.Add(lblDashProfile);
            y += 38;

            var batGroup = new GroupBox { Left = 18, Top = y, Width = 690, Height = 105, Text = "Акумулятор" };
            lblDashBattery = new Label { Left = 16, Top = 27, Width = 300, Height = 25, Text = "Заряд: —" };
            prgBattery = new ProgressBar { Left = 16, Top = 57, Width = 455, Height = 22, Minimum = 0, Maximum = 100, Value = 0 };
            var btnBat = MakeButton("Оновити", 490, 50, 170, 31, delegate { RefreshBatterySafe(); });
            batGroup.Controls.AddRange(new Control[] { lblDashBattery, prgBattery, btnBat });
            right.Controls.Add(batGroup);
            y += 120;

            right.Controls.Add(new Label { Left = 18, Top = y, Width = 690, Height = 26, Text = "Поточний прочитаний профіль", Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold) });
            y += 31;
            txtDashSummary = new TextBox
            {
                Left = 18, Top = y, Width = 690, Height = 240,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9F)
            };
            right.Controls.Add(txtDashSummary);
            y += 252;

            var btnReset = MakeButton("Factory Reset миші", 18, y, 200, 34, delegate { FactoryResetMouse(); });
            var resetInfo = new Label { Left = 235, Top = y + 5, Width = 475, Height = 40, Text = "Перед скиданням програма робить backup. Використовуйте лише якщо справді треба повернути заводські налаштування." };
            right.Controls.Add(btnReset);
            right.Controls.Add(resetInfo);
        }

        private void BuildProfilesTab()
        {
            var page = new TabPage("Профілі");
            tabs.TabPages.Add(page);
            page.Controls.Add(new Label
            {
                Left = 20, Top = 18, Width = 900, Height = 42,
                Text = "Локальна бібліотека повних профілів: кнопки, DPI, RGB, сенсорні параметри та використані macro slots. Профіль завантажується у редактор і не пишеться в мишку, доки ви не натиснете Apply."
            });

            lstProfileLibrary = new ListBox { Left = 20, Top = 70, Width = 520, Height = 510 };
            lstProfileLibrary.DoubleClick += delegate { LoadSelectedLibraryProfile(); };
            page.Controls.Add(lstProfileLibrary);

            page.Controls.Add(new Label { Left = 570, Top = 78, Width = 180, Text = "Назва нового профілю:" });
            txtProfileName = new TextBox { Left = 570, Top = 102, Width = 360, Text = "My M693 Profile" };
            page.Controls.Add(txtProfileName);

            var bSave = MakeButton("Зберегти поточний", 570, 145, 220, 35, delegate { SaveCurrentToLibrary(); });
            var bLoad = MakeButton("Завантажити вибраний", 570, 190, 220, 35, delegate { LoadSelectedLibraryProfile(); });
            var bDelete = MakeButton("Видалити з бібліотеки", 570, 235, 220, 35, delegate { DeleteSelectedLibraryProfile(); });
            var bRefresh = MakeButton("Оновити список", 570, 280, 220, 35, delegate { RefreshProfileLibrary(); });
            page.Controls.AddRange(new Control[] { bSave, bLoad, bDelete, bRefresh });

            lblProfileInfo = new Label
            {
                Left = 570, Top = 340, Width = 560, Height = 190,
                Text = "Профілі лежать у папці Profiles поруч із програмою. Їх можна копіювати на інший ПК, архівувати та повертати пізніше. RAW backup перед Apply створюється окремо автоматично."
            };
            page.Controls.Add(lblProfileInfo);
        }

        private void BuildWindowsTab()
        {
            var page = new TabPage("Windows / Pointer");
            tabs.TabPages.Add(page);
            page.Controls.Add(new Label
            {
                Left = 20, Top = 18, Width = 950, Height = 50,
                Text = "Це системні параметри Windows, які офіційний Redragon Software теж показує окремо від onboard-пам'яті миші. Вони не записуються в M693 і застосовуються тільки після окремої кнопки нижче."
            });
            int y = 90;
            page.Controls.Add(new Label { Left = 35, Top = y + 5, Width = 230, Text = "Швидкість курсора Windows (1–20):" });
            numWinSpeed = new NumericUpDown { Left = 285, Top = y, Width = 100, Minimum = 1, Maximum = 20, Value = 10 };
            page.Controls.Add(numWinSpeed); y += 45;
            page.Controls.Add(new Label { Left = 35, Top = y + 5, Width = 230, Text = "Double-click time, ms:" });
            numWinDoubleClick = new NumericUpDown { Left = 285, Top = y, Width = 100, Minimum = 100, Maximum = 1200, Value = 500, Increment = 25 };
            page.Controls.Add(numWinDoubleClick); y += 45;
            page.Controls.Add(new Label { Left = 35, Top = y + 5, Width = 230, Text = "Scroll lines:" });
            numWinWheel = new NumericUpDown { Left = 285, Top = y, Width = 100, Minimum = 1, Maximum = 100, Value = 3 };
            page.Controls.Add(numWinWheel); y += 45;
            chkWinEnhance = new CheckBox { Left = 35, Top = y, Width = 360, Text = "Improve / Enhance pointer precision" };
            page.Controls.Add(chkWinEnhance); y += 55;

            var bRead = MakeButton("Прочитати Windows", 35, y, 220, 36, delegate { LoadWindowsSettings(); });
            var bApply = MakeButton("Застосувати в Windows", 275, y, 240, 36, delegate { ApplyWindowsSettings(); });
            page.Controls.AddRange(new Control[] { bRead, bApply });
            y += 55;
            page.Controls.Add(new Label
            {
                Left = 35, Top = y, Width = 720, Height = 80,
                Text = "Підказка: якщо вам потрібна максимально передбачувана поведінка в іграх, Enhance pointer precision зазвичай вимикають. Ця вкладка не впливає на DPI, polling, debounce або макроси, що зберігаються всередині M693."
            });
            LoadWindowsSettings();
        }

        private void BuildAdvancedTab()
        {
            var page = new TabPage("Advanced / RAW");
            tabs.TabPages.Add(page);
            page.Controls.Add(new Label
            {
                Left = 20, Top = 16, Width = 950, Height = 48,
                Text = "Експертний режим. Тут видно сирі 3-байтові action-коди та macro slots. Невідомі функції не губляться: Hardware Studio зберігає їх як є, доки ви самі не зміните конкретну кнопку."
            });

            var g = new GroupBox { Left = 20, Top = 72, Width = 520, Height = 190, Text = "RAW action фізичної кнопки" };
            g.Controls.Add(new Label { Left = 15, Top = 30, Width = 120, Text = "Кнопка:" });
            cmbRawPhysical = new ComboBox { Left = 140, Top = 25, Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var pb in _physicalButtons) cmbRawPhysical.Items.Add("#" + pb.Number + " " + pb.Name);
            if (cmbRawPhysical.Items.Count > 0) cmbRawPhysical.SelectedIndex = 0;
            cmbRawPhysical.SelectedIndexChanged += delegate { LoadRawActionFields(); };
            g.Controls.Add(cmbRawPhysical);
            lblRawCurrent = new Label { Left = 15, Top = 62, Width = 475, Height = 38, Text = "Current: —" };
            g.Controls.Add(lblRawCurrent);
            g.Controls.Add(new Label { Left = 15, Top = 111, Width = 58, Text = "Type" });
            g.Controls.Add(new Label { Left = 120, Top = 111, Width = 58, Text = "Arg1" });
            g.Controls.Add(new Label { Left = 225, Top = 111, Width = 58, Text = "Arg2" });
            txtRawType = new TextBox { Left = 15, Top = 132, Width = 80, Text = "00" };
            txtRawA = new TextBox { Left = 120, Top = 132, Width = 80, Text = "00" };
            txtRawB = new TextBox { Left = 225, Top = 132, Width = 80, Text = "00" };
            var bLoad = MakeButton("Зчитати", 320, 130, 80, 28, delegate { LoadRawActionFields(); });
            var bSet = MakeButton("Підготувати", 408, 130, 95, 28, delegate { SetRawActionFields(); });
            g.Controls.AddRange(new Control[] { txtRawType, txtRawA, txtRawB, bLoad, bSet });
            page.Controls.Add(g);

            var info = new GroupBox { Left = 560, Top = 72, Width = 560, Height = 190, Text = "Відомі action type" };
            info.Controls.Add(new Label
            {
                Left = 15, Top = 25, Width = 530, Height = 150,
                Text = "00 Disabled\r\n01 Mouse buttons (01/02/04/08/10)\r\n02 DPI loop/up/down\r\n03 Horizontal wheel\r\n04 Fire (Arg1=speed, Arg2=times; 0=hold)\r\n05 Key combo\r\n06 Macro (Arg1=slot, Arg2=mode FE/FD/FF/N)\r\n07 Polling switch\r\n0A DPI Lock\r\n0B Vertical wheel"
            });
            page.Controls.Add(info);

            var mg = new GroupBox { Left = 20, Top = 280, Width = 1100, Height = 390, Text = "Macro slots" };
            gridMacros = new DataGridView
            {
                Left = 15, Top = 28, Width = 1065, Height = 295,
                ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            gridMacros.Columns.Add("Slot", "Slot");
            gridMacros.Columns.Add("Ref", "Referenced by");
            gridMacros.Columns.Add("Name", "Name");
            gridMacros.Columns.Add("Events", "Events");
            gridMacros.Columns.Add("Checksum", "Checksum");
            gridMacros.Columns.Add("Dominant", "Dominant output");
            mg.Controls.Add(gridMacros);
            var bAll = MakeButton("Прочитати всі 16 macro slots", 15, 335, 240, 32, delegate { ReadAllMacroSlots(); });
            var bRefresh = MakeButton("Оновити таблицю", 270, 335, 190, 32, delegate { RefreshAdvancedUi(); });
            mg.Controls.AddRange(new Control[] { bAll, bRefresh });
            page.Controls.Add(mg);
        }

        private void LoadRawActionFields()
        {
            if (_pendingProfile == null || cmbRawPhysical == null || cmbRawPhysical.SelectedIndex < 0) return;
            var pb = _physicalButtons[cmbRawPhysical.SelectedIndex];
            byte[] r = M693Profile.GetButtonRecord(_pendingProfile, pb.Slot);
            txtRawType.Text = r[0].ToString("X2");
            txtRawA.Text = r[1].ToString("X2");
            txtRawB.Text = r[2].ToString("X2");
            lblRawCurrent.Text = "Current: " + DescribeAction(r, true) + "   RAW=" + Hex(r);
        }

        private byte ParseHexByteBox(TextBox box)
        {
            string t = (box.Text ?? "").Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            byte v;
            if (!byte.TryParse(t, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out v))
                throw new FormatException("Невірний HEX byte: " + box.Text);
            return v;
        }

        private void SetRawActionFields()
        {
            if (_pendingProfile == null || cmbRawPhysical.SelectedIndex < 0) return;
            try
            {
                byte t = ParseHexByteBox(txtRawType);
                byte a = ParseHexByteBox(txtRawA);
                byte b = ParseHexByteBox(txtRawB);
                var pb = _physicalButtons[cmbRawPhysical.SelectedIndex];
                M693Profile.SetButtonAction(_pendingProfile, pb.Slot, new byte[] { t, a, b });
                RefreshButtonGrids();
                RefreshAdvancedUi();
                RefreshDashboard();
                lblStatus.Text = "RAW action підготовлено. Ще НЕ записано — натисніть Apply.";
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "RAW action", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void ReadAllMacroSlots()
        {
            if (!EnsureDevice()) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                for (int slot = 0; slot < 16; slot++)
                {
                    try
                    {
                        byte[] raw = _device.ReadBytes((ushort)(M693Profile.MacroBase + slot * M693Profile.MacroSlotSize), M693Profile.MacroSlotSize);
                        _macroSlots[slot] = raw;
                        ParsedMacro pm = ParsedMacro.Parse(slot, raw);
                        _parsedMacros[slot] = pm;
                    }
                    catch (Exception ex) { Log("Macro slot " + slot + " warning: " + ex.Message); }
                }
                RefreshAdvancedUi();
            }
            finally { Cursor = Cursors.Default; }
        }

        private void RefreshAdvancedUi()
        {
            LoadRawActionFields();
            if (gridMacros == null) return;
            gridMacros.Rows.Clear();
            for (int slot = 0; slot < 16; slot++)
            {
                ParsedMacro pm;
                if (!_parsedMacros.TryGetValue(slot, out pm))
                {
                    gridMacros.Rows.Add(slot, ReferencedBy(slot), "—", "—", "—", "—");
                    continue;
                }
                gridMacros.Rows.Add(slot, ReferencedBy(slot), string.IsNullOrEmpty(pm.Name) ? "(empty/unknown)" : pm.Name, pm.EventCount, pm.ChecksumOk ? "OK" : "BAD", pm.DominantOutput);
            }
        }

        private string ReferencedBy(int macroSlot)
        {
            if (_pendingProfile == null) return "";
            var refs = new List<string>();
            foreach (var pb in _physicalButtons)
            {
                byte[] r = M693Profile.GetButtonRecord(_pendingProfile, pb.Slot);
                if (r[0] == 6 && r[1] == macroSlot) refs.Add("#" + pb.Number);
            }
            return string.Join(", ", refs.ToArray());
        }

        private void RefreshDashboard()
        {
            if (lblDashDevice == null) return;
            lblDashDevice.Text = _device == null ? "Пристрій: не підключено" : "Пристрій: " + _device.Description;
            lblDashMode.Text = _device == null ? "Режим: —" : "Режим: " + (_device.ProductId == 0xFA7C ? "2.4 GHz receiver" : "USB cable / command HID");
            if (_pendingProfile == null)
            {
                txtDashSummary.Text = "Спочатку знайдіть M693 і прочитайте профіль.";
                return;
            }
            var sb = new StringBuilder();
            int poll = M693Profile.GetPair(_pendingProfile, M693Profile.AddrPolling, 1);
            int hz = poll == 8 ? 125 : poll == 4 ? 250 : poll == 2 ? 500 : 1000;
            int dpiCount = M693Profile.GetPair(_pendingProfile, M693Profile.AddrDpiCount, 5);
            int active = M693Profile.GetPair(_pendingProfile, M693Profile.AddrDpiActive, 0);
            sb.AppendLine("Polling: " + hz + " Hz");
            sb.AppendLine("Debounce: " + M693Profile.GetPair(_pendingProfile, M693Profile.AddrDebounce, 8) + " ms");
            sb.AppendLine("LOD: " + M693Profile.GetPair(_pendingProfile, M693Profile.AddrLod, 1));
            sb.AppendLine("DPI stages: " + dpiCount + ", active: " + (active + 1));
            sb.AppendLine("Buttons:");
            foreach (var pb in _physicalButtons)
            {
                sb.AppendLine("  #" + pb.Number + " " + pb.Name + " -> " + DescribeAction(M693Profile.GetButtonRecord(_pendingProfile, pb.Slot), true));
            }
            if (!string.IsNullOrEmpty(_loadedProfilePath)) sb.AppendLine("Loaded file: " + _loadedProfilePath);
            txtDashSummary.Text = sb.ToString();
        }

        private void RefreshBatterySafe()
        {
            if (lblDashBattery == null) return;
            if (_device == null || _device.IsClosed)
            {
                lblDashBattery.Text = "Заряд: —";
                prgBattery.Value = 0;
                lblDashProfile.Text = "Onboard profile: —";
                return;
            }
            try
            {
                BatteryInfo b = _device.GetBatteryInfo();
                prgBattery.Value = Math.Max(0, Math.Min(100, b.Percent));
                lblDashBattery.Text = "Заряд: " + b.Percent + "%" + (b.Charging ? "  •  заряджається" : "");
                try
                {
                    int pi = _device.GetCurrentProfileIndex();
                    lblDashProfile.Text = "Onboard profile index: " + pi;
                }
                catch { lblDashProfile.Text = "Onboard profile: не відповів"; }
            }
            catch (Exception ex)
            {
                lblDashBattery.Text = "Заряд: немає відповіді від firmware";
                Log("Battery warning: " + ex.Message);
            }
        }

        private void FactoryResetMouse()
        {
            if (!EnsureDevice()) return;
            if (MessageBox.Show(this, "Factory Reset поверне налаштування M693 до заводських. Перед цим буде створено RAW backup. Продовжити?", "M693 Factory Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            if (MessageBox.Show(this, "Підтвердьте ще раз: виконати апаратний Factory Reset?", "Підтвердження", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                string dir = Path.Combine(Application.StartupPath, "Backups");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "AUTO_BEFORE_FACTORY_RESET_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".m693profile.json");
                byte[] live = _device.ReadBytes(0, M693Profile.ProfileLength);
                SaveLiveBackup(path, live, null);
                _device.FactoryReset();
                Thread.Sleep(700);
                ReadFromMouse();
                MessageBox.Show(this, "Factory Reset виконано. Backup: " + path, "M693", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Factory Reset", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { Cursor = Cursors.Default; }
        }

        private string ProfileLibraryDir()
        {
            string dir = Path.Combine(Application.StartupPath, "Profiles");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private void RefreshProfileLibrary()
        {
            if (lstProfileLibrary == null) return;
            string selected = lstProfileLibrary.SelectedItem == null ? null : lstProfileLibrary.SelectedItem.ToString();
            lstProfileLibrary.Items.Clear();
            foreach (string f in Directory.GetFiles(ProfileLibraryDir(), "*.m693profile.json").OrderBy(x => x))
                lstProfileLibrary.Items.Add(Path.GetFileName(f));
            if (selected != null)
            {
                int i = lstProfileLibrary.Items.IndexOf(selected);
                if (i >= 0) lstProfileLibrary.SelectedIndex = i;
            }
            if (lblProfileInfo != null) lblProfileInfo.Text = "Profiles: " + lstProfileLibrary.Items.Count + "\r\nПапка: " + ProfileLibraryDir() + "\r\nПодвійний клік по профілю — завантажити у редактор.";
        }

        private string SafeProfileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "M693_Profile";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim();
            if (name.Length > 80) name = name.Substring(0, 80);
            return name;
        }

        private void SaveCurrentToLibrary()
        {
            if (_pendingProfile == null) { MessageBox.Show(this, "Спочатку прочитайте або імпортуйте профіль."); return; }
            SyncEditorsToPending();
            string name = SafeProfileName(txtProfileName == null ? null : txtProfileName.Text);
            string path = Path.Combine(ProfileLibraryDir(), name + ".m693profile.json");
            if (File.Exists(path) && MessageBox.Show(this, "Профіль уже існує. Перезаписати?", "Profiles", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            SaveProfileFile(path, true);
            RefreshProfileLibrary();
            MessageBox.Show(this, "Збережено: " + path, "Profiles", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private string SelectedLibraryProfilePath()
        {
            if (lstProfileLibrary == null || lstProfileLibrary.SelectedItem == null) return null;
            return Path.Combine(ProfileLibraryDir(), lstProfileLibrary.SelectedItem.ToString());
        }

        private void LoadSelectedLibraryProfile()
        {
            string path = SelectedLibraryProfilePath();
            if (path == null) { MessageBox.Show(this, "Виберіть профіль у списку."); return; }
            try { LoadProfileFromPath(path); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Profiles", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void DeleteSelectedLibraryProfile()
        {
            string path = SelectedLibraryProfilePath();
            if (path == null) return;
            if (MessageBox.Show(this, "Видалити локальний профіль " + Path.GetFileName(path) + "?", "Profiles", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            File.Delete(path);
            RefreshProfileLibrary();
        }

        private void LoadProfileFromPath(string path)
        {
            var js = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 * 8 };
            var pf = js.Deserialize<ExportedProfile>(File.ReadAllText(path, Encoding.UTF8));
            if (pf == null || pf.Format != "NeutronM693ProfileV1") throw new InvalidDataException("Невідомий формат профілю.");
            byte[] raw = ParseHex(pf.ProfileHex);
            if (raw.Length != M693Profile.ProfileLength) throw new InvalidDataException("ProfileHex має неправильну довжину.");
            _pendingProfile = raw;
            _pendingMacroSlots.Clear();
            _parsedMacros.Clear();
            if (pf.Macros != null)
            {
                foreach (var kv in pf.Macros)
                {
                    int slot;
                    if (!int.TryParse(kv.Key, out slot) || slot < 0 || slot >= 16) continue;
                    byte[] m = ParseHex(kv.Value);
                    if (m.Length == M693Profile.MacroSlotSize)
                    {
                        _pendingMacroSlots[slot] = m;
                        _parsedMacros[slot] = ParsedMacro.Parse(slot, m);
                    }
                }
            }
            _loadedProfilePath = path;
            LoadUiFromPending();
            SetEditorsEnabled(true);
            lblStatus.Text = "Профіль завантажено з файла — ще НЕ записано в мишку.";
            RefreshDashboard();
        }

        private void LoadWindowsSettings()
        {
            try
            {
                WindowsPointerConfig c = WindowsMouseSettings.Read();
                if (numWinSpeed != null) numWinSpeed.Value = ClampDecimal(c.Speed, numWinSpeed.Minimum, numWinSpeed.Maximum);
                if (numWinDoubleClick != null) numWinDoubleClick.Value = ClampDecimal(c.DoubleClickMs, numWinDoubleClick.Minimum, numWinDoubleClick.Maximum);
                if (numWinWheel != null) numWinWheel.Value = ClampDecimal(c.WheelLines, numWinWheel.Minimum, numWinWheel.Maximum);
                if (chkWinEnhance != null) chkWinEnhance.Checked = c.EnhancePrecision;
            }
            catch (Exception ex) { Log("Windows settings read warning: " + ex.Message); }
        }

        private void ApplyWindowsSettings()
        {
            try
            {
                var c = new WindowsPointerConfig
                {
                    Speed = (int)numWinSpeed.Value,
                    DoubleClickMs = (int)numWinDoubleClick.Value,
                    WheelLines = (int)numWinWheel.Value,
                    EnhancePrecision = chkWinEnhance.Checked
                };
                WindowsMouseSettings.Apply(c);
                MessageBox.Show(this, "Параметри Windows застосовано.", "Windows", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Windows", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private Button MakeButton(string text, int x, int y, int w, int h, EventHandler click)
        {
            var b = new Button { Text = text, Left = x, Top = y, Width = w, Height = h };
            b.Click += click;
            return b;
        }

        private void BuildButtonsTab()
        {
            var page = new TabPage("Кнопки / Smooth Macro");
            tabs.TabPages.Add(page);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 650 };
            page.Controls.Add(split);

            var left = split.Panel1;
            var right = split.Panel2;

            var lbl = new Label
            {
                Text = "Фізичні кнопки M693 читаються з onboard-профілю. Нічого в Redragon Software змінювати не треба.",
                Left = 10, Top = 10, Width = 620, Height = 38
            };
            left.Controls.Add(lbl);

            gridButtons = new DataGridView
            {
                Left = 10, Top = 52, Width = 620, Height = 260,
                ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowHeadersVisible = false
            };
            gridButtons.Columns.Add("No", "№");
            gridButtons.Columns.Add("Name", "Фізична кнопка");
            gridButtons.Columns.Add("Slot", "EEPROM slot");
            gridButtons.Columns.Add("Current", "Зараз призначено");
            gridButtons.Columns.Add("Raw", "RAW");
            gridButtons.Columns[0].FillWeight = 35;
            gridButtons.Columns[2].FillWeight = 55;
            gridButtons.Columns[4].FillWeight = 85;
            left.Controls.Add(gridButtons);

            var grpCapture = new GroupBox { Text = "Вибір кнопки натисканням", Left = 10, Top = 320, Width = 620, Height = 112 };
            txtCapture = new TextBox { Left = 15, Top = 30, Width = 370, ReadOnly = true, Text = "Клікни сюди або натисни «Слухати», потім натисни кнопку миші" };
            txtCapture.Click += delegate { StartCapture(); };
            btnCapture = MakeButton("Слухати 2 с", 395, 28, 110, 28, delegate { StartCapture(); });
            lblCapture = new Label { Left = 15, Top = 66, Width = 585, Height = 36, Text = "Для кнопок, де вже стоїть onboard-макрос, програма намагається визначити фізичну кнопку за характером поточного макросу." };
            grpCapture.Controls.AddRange(new Control[] { txtCapture, btnCapture, lblCapture });
            left.Controls.Add(grpCapture);

            var grpSimple = new GroupBox { Text = "Призначення для вибраної кнопки", Left = 10, Top = 438, Width = 620, Height = 112 };
            cmbSimpleAction = new ComboBox { Left = 15, Top = 29, Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbSimpleAction.Items.AddRange(new object[] {
                "Ліва кнопка", "Права кнопка", "Середня кнопка", "Назад", "Вперед",
                "DPI Loop", "DPI +", "DPI -", "Polling rate switch", "Вимкнути",
                "Колесо вліво", "Колесо вправо", "Колесо вгору", "Колесо вниз", "Fire key"
            });
            cmbSimpleAction.SelectedIndex = 0;
            btnAssignSimple = MakeButton("Призначити (ще не Apply)", 350, 27, 245, 30, delegate { AssignSimpleAction(); });
            grpSimple.Controls.AddRange(new Control[] { cmbSimpleAction, btnAssignSimple });
            grpSimple.Controls.Add(new Label { Left = 15, Top = 69, Width = 88, Text = "Fire speed:" });
            numFireInterval = new NumericUpDown { Left = 100, Top = 65, Width = 70, Minimum = 3, Maximum = 255, Value = 10 };
            grpSimple.Controls.Add(numFireInterval);
            grpSimple.Controls.Add(new Label { Left = 185, Top = 69, Width = 92, Text = "Times 0–3:" });
            numFireRepeat = new NumericUpDown { Left = 272, Top = 65, Width = 60, Minimum = 0, Maximum = 3, Value = 0 };
            grpSimple.Controls.Add(numFireRepeat);
            grpSimple.Controls.Add(new Label { Left = 350, Top = 67, Width = 245, Height = 35, Text = "0 = повторювати, поки кнопку утримують" });
            left.Controls.Add(grpSimple);

            var adv = new GroupBox { Text = "Повна матриця 16 слотів (діагностика / інші функції)", Left = 10, Top = 557, Width = 620, Height = 168 };
            gridMatrix = new DataGridView
            {
                Left = 10, Top = 25, Width = 595, Height = 128,
                ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false
            };
            gridMatrix.Columns.Add("Slot", "Slot");
            gridMatrix.Columns.Add("Physical", "Пов'язано");
            gridMatrix.Columns.Add("Action", "Призначення");
            gridMatrix.Columns.Add("Raw", "RAW 4 байти");
            adv.Controls.Add(gridMatrix);
            left.Controls.Add(adv);

            var grpMacro = new GroupBox { Text = "Onboard Smooth Click Macro — працює в самій мишці", Dock = DockStyle.Top, Height = 465, Padding = new Padding(12) };
            right.Controls.Add(grpMacro);

            int y = 28;
            grpMacro.Controls.Add(new Label { Left = 15, Top = y + 4, Width = 150, Text = "Тип кліку:" });
            cmbMacroClick = new ComboBox { Left = 170, Top = y, Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbMacroClick.Items.AddRange(new object[] { "Лівий клік", "Правий клік" });
            cmbMacroClick.SelectedIndex = 0;
            grpMacro.Controls.Add(cmbMacroClick);

            y += 42;
            grpMacro.Controls.Add(new Label { Left = 15, Top = y + 4, Width = 150, Text = "Мінімум, мс:" });
            numMin = MakeNum(170, y, 10, 1, 1000);
            grpMacro.Controls.Add(numMin);
            y += 36;
            grpMacro.Controls.Add(new Label { Left = 15, Top = y + 4, Width = 150, Text = "Типово, мс:" });
            numTypical = MakeNum(170, y, 45, 1, 1000);
            grpMacro.Controls.Add(numTypical);
            y += 36;
            grpMacro.Controls.Add(new Label { Left = 15, Top = y + 4, Width = 150, Text = "Максимум, мс:" });
            numMax = MakeNum(170, y, 150, 1, 2000);
            grpMacro.Controls.Add(numMax);
            y += 42;
            grpMacro.Controls.Add(new Label { Left = 15, Top = y + 2, Width = 110, Text = "Плавність:" });
            trkSmooth = new TrackBar { Left = 120, Top = y - 8, Width = 225, Minimum = 0, Maximum = 100, Value = 88, TickFrequency = 10 };
            lblSmooth = new Label { Left = 350, Top = y + 2, Width = 55, Text = "88%" };
            trkSmooth.ValueChanged += delegate { lblSmooth.Text = trkSmooth.Value + "%"; };
            grpMacro.Controls.Add(trkSmooth);
            grpMacro.Controls.Add(lblSmooth);

            y += 54;
            btnGenerateMacro = MakeButton("Створити й прив'язати до вибраної кнопки", 15, y, 390, 36, delegate { GenerateAndBindMacro(); });
            grpMacro.Controls.Add(btnGenerateMacro);
            y += 48;
            grpMacro.Controls.Add(new Label { Left = 15, Top = y, Width = 390, Height = 55, Text = "Режим: HOLD (код 254) — прошивка повторює патерн, поки фізична кнопка затиснута; після відпускання зупиняється. Windows-фоновий клікер не потрібен." });
            y += 62;
            txtPreview = new TextBox { Left = 15, Top = y, Width = 390, Height = 105, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
            grpMacro.Controls.Add(txtPreview);

            var warn = new Label
            {
                Dock = DockStyle.Top, Height = 120, Padding = new Padding(12),
                Text = "Безпека запису:\r\n• перед кожним Apply автоматично створюється backup;\r\n• пишуться лише байти, які реально змінені;\r\n• після запису виконується read-back перевірка;\r\n• офіційну OemDrv.exe треба закрити, щоб вона не перехоплювала відповіді HID."
            };
            right.Controls.Add(warn);
            warn.BringToFront();
        }

        private NumericUpDown MakeNum(int x, int y, decimal value, decimal min, decimal max)
        {
            return new NumericUpDown { Left = x, Top = y, Width = 120, Minimum = min, Maximum = max, Value = value };
        }

        private void BuildDpiTab()
        {
            var page = new TabPage("DPI");
            tabs.TabPages.Add(page);
            page.Controls.Add(new Label { Left = 15, Top = 15, Width = 900, Height = 38, Text = "M693 Sensor=0x3104. Значення/коди взяті з вашого Cfg.ini; профіль EEPROM читається прямо з миші." });

            page.Controls.Add(new Label { Left = 15, Top = 64, Width = 160, Text = "Кількість ступенів:" });
            numDpiCount = new NumericUpDown { Left = 180, Top = 60, Width = 80, Minimum = 1, Maximum = 8, Value = 5 };
            page.Controls.Add(numDpiCount);
            page.Controls.Add(new Label { Left = 290, Top = 64, Width = 160, Text = "Активна ступінь:" });
            cmbDpiActive = new ComboBox { Left = 450, Top = 60, Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
            for (int i = 1; i <= 8; i++) cmbDpiActive.Items.Add(i.ToString());
            cmbDpiActive.SelectedIndex = 0;
            page.Controls.Add(cmbDpiActive);

            var values = M693DpiCodec.AllowedDpiValues.Select(x => (object)x.ToString()).ToArray();
            int y = 115;
            for (int i = 0; i < 8; i++)
            {
                page.Controls.Add(new Label { Left = 35, Top = y + 5, Width = 100, Text = "DPI " + (i + 1) + ":" });
                cmbDpi[i] = new ComboBox { Left = 140, Top = y, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
                cmbDpi[i].Items.AddRange(values);
                page.Controls.Add(cmbDpi[i]);
                btnDpiColor[i] = new Button { Left = 310, Top = y, Width = 130, Height = 26, Text = "Колір" };
                int idx = i;
                btnDpiColor[i].Click += delegate { PickDpiColor(idx); };
                page.Controls.Add(btnDpiColor[i]);
                page.Controls.Add(new Label { Left = 460, Top = y + 5, Width = 360, Text = "RAW EEPROM: 4 байти + checksum" });
                y += 42;
            }
        }

        private void BuildLightingTab()
        {
            var page = new TabPage("Підсвітка");
            tabs.TabPages.Add(page);
            page.Controls.Add(new Label { Left = 15, Top = 15, Width = 900, Height = 40, Text = "Onboard RGB блок EEPROM 0xA0..0xA6. Зміни зберігаються в мишці." });

            page.Controls.Add(new Label { Left = 30, Top = 75, Width = 150, Text = "Режим:" });
            cmbLedMode = new ComboBox { Left = 190, Top = 70, Width = 240, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbLedMode.Items.AddRange(new object[] { "Steady", "Breathing", "Streaming", "Neon", "Scrolling", "Color Breathing", "Off" });
            cmbLedMode.SelectedIndex = 5;
            page.Controls.Add(cmbLedMode);

            page.Controls.Add(new Label { Left = 30, Top = 120, Width = 150, Text = "Колір:" });
            btnLedColor = new Button { Left = 190, Top = 112, Width = 240, Height = 30, Text = "Вибрати колір" };
            btnLedColor.Click += delegate { PickLedColor(); };
            page.Controls.Add(btnLedColor);

            page.Controls.Add(new Label { Left = 30, Top = 165, Width = 150, Text = "Швидкість 1–10:" });
            numLedSpeed = new NumericUpDown { Left = 190, Top = 160, Width = 100, Minimum = 1, Maximum = 10, Value = 6 };
            page.Controls.Add(numLedSpeed);
            page.Controls.Add(new Label { Left = 30, Top = 205, Width = 150, Text = "Яскравість 1–10:" });
            numLedBrightness = new NumericUpDown { Left = 190, Top = 200, Width = 100, Minimum = 1, Maximum = 10, Value = 6 };
            page.Controls.Add(numLedBrightness);
        }

        private void BuildSensorTab()
        {
            var page = new TabPage("Сенсор / Polling / Debounce");
            tabs.TabPages.Add(page);
            page.Controls.Add(new Label { Left = 15, Top = 15, Width = 900, Height = 45, Text = "Відомі параметри JM03-профілю. Невідомі байти не переписуються." });

            int y = 75;
            page.Controls.Add(new Label { Left = 30, Top = y + 4, Width = 180, Text = "Polling rate:" });
            cmbPolling = new ComboBox { Left = 220, Top = y, Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbPolling.Items.AddRange(new object[] { "125 Hz", "250 Hz", "500 Hz", "1000 Hz" });
            page.Controls.Add(cmbPolling);
            y += 42;

            page.Controls.Add(new Label { Left = 30, Top = y + 4, Width = 180, Text = "Debounce, ms:" });
            numDebounce = new NumericUpDown { Left = 220, Top = y, Width = 100, Minimum = 0, Maximum = 30, Value = 8 };
            page.Controls.Add(numDebounce);
            y += 42;

            page.Controls.Add(new Label { Left = 30, Top = y + 4, Width = 180, Text = "LOD:" });
            cmbLod = new ComboBox { Left = 220, Top = y, Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbLod.Items.AddRange(new object[] { "1 mm", "2 mm" });
            page.Controls.Add(cmbLod);
            y += 42;

            page.Controls.Add(new Label { Left = 30, Top = y + 4, Width = 180, Text = "Sleep, сек:" });
            numSleep = new NumericUpDown { Left = 220, Top = y, Width = 100, Minimum = 10, Maximum = 2550, Increment = 10, Value = 600 };
            page.Controls.Add(numSleep);
            y += 44;

            chkMotionSync = new CheckBox { Left = 30, Top = y, Width = 250, Text = "Motion Sync (0xAB)" };
            page.Controls.Add(chkMotionSync); y += 34;
            chkAngleSnap = new CheckBox { Left = 30, Top = y, Width = 250, Text = "Angle Snapping (0xAF)" };
            page.Controls.Add(chkAngleSnap); y += 34;
            chkRipple = new CheckBox { Left = 30, Top = y, Width = 250, Text = "Ripple Control (0xB1)" };
            page.Controls.Add(chkRipple); y += 34;
            chkLedOffMoving = new CheckBox { Left = 30, Top = y, Width = 300, Text = "Гасити RGB при русі (0xB3)" };
            page.Controls.Add(chkLedOffMoving);
        }

        private void BuildLogTab()
        {
            var page = new TabPage("HID лог / Діагностика");
            tabs.TabPages.Add(page);
            txtLog = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9F), WordWrap = false };
            page.Controls.Add(txtLog);
        }

        private void Log(string s)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s;
            txtLog.AppendText(line + Environment.NewLine);
            try
            {
                string dir = Path.Combine(Application.StartupPath, "Logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "M693_HID.log"), DateTime.Now.ToString("yyyy-MM-dd ") + line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private void UpdateOfficialStatus()
        {
            bool running = Process.GetProcessesByName("OemDrv").Length > 0;
            lblOfficial.Text = running
                ? "УВАГА: OemDrv.exe зараз запущена. Для прямого HID читання/запису закрийте її через Exit у tray."
                : "OemDrv.exe не запущена — добре для прямого HID доступу.";
            lblOfficial.ForeColor = running ? Color.DarkRed : Color.DarkGreen;
        }

        private void ConnectDevice()
        {
            UpdateOfficialStatus();
            SafeDisposeDevice();
            Cursor = Cursors.WaitCursor;
            try
            {
                Log("Сканування HID VID=25A7 PID=FA7B/FA7C...");
                _device = M693Device.FindAndOpen(Log);
                if (_device == null)
                {
                    lblStatus.Text = "M693 command HID не знайдено";
                    lblStatus.ForeColor = Color.OrangeRed;
                    lblDevice.Text = "Перевірте USB/2.4G і закрийте OemDrv.exe. Програма шукає vendor command interface, а не звичайний mouse interface.";
                    return;
                }
                lblStatus.Text = "Підключено";
                lblStatus.ForeColor = Color.LightGreen;
                lblDevice.Text = _device.Description;
                Log("Вибрано: " + _device.Description);
                ReadFromMouse();
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Помилка підключення";
                lblStatus.ForeColor = Color.OrangeRed;
                Log("ERROR connect: " + ex);
                MessageBox.Show(this, ex.Message, "M693 HID", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { Cursor = Cursors.Default; }
        }

        private void ReadFromMouse()
        {
            if (!EnsureDevice()) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                UpdateOfficialStatus();
                Log("Читаю EEPROM profile 0x0000..0x00B4...");
                _originalProfile = _device.ReadBytes(0, M693Profile.ProfileLength);
                _pendingProfile = (byte[])_originalProfile.Clone();
                _macroSlots.Clear();
                _pendingMacroSlots.Clear();
                _parsedMacros.Clear();

                var refs = M693Profile.ReferencedMacroSlots(_pendingProfile).Distinct().Where(x => x >= 0 && x < 16).ToArray();
                foreach (int slot in refs)
                {
                    try
                    {
                        Log("Читаю macro slot " + slot + " @ 0x" + (M693Profile.MacroBase + slot * M693Profile.MacroSlotSize).ToString("X4"));
                        byte[] raw = _device.ReadBytes((ushort)(M693Profile.MacroBase + slot * M693Profile.MacroSlotSize), M693Profile.MacroSlotSize);
                        _macroSlots[slot] = raw;
                        _parsedMacros[slot] = ParsedMacro.Parse(slot, raw);
                    }
                    catch (Exception mex)
                    {
                        Log("Macro slot " + slot + " read warning: " + mex.Message);
                    }
                }

                LoadUiFromPending();
                SetEditorsEnabled(true);
                lblStatus.Text = "Профіль прочитано з миші";
                lblStatus.ForeColor = Color.LightGreen;
                _loadedProfilePath = null;
                RefreshDashboard();
                RefreshBatterySafe();
            }
            catch (Exception ex)
            {
                Log("ERROR read: " + ex);
                MessageBox.Show(this, "Не вдалося прочитати профіль:\r\n" + ex.Message + "\r\n\r\nЗакрийте OemDrv.exe повністю (включно з tray) і спробуйте ще раз.", "M693", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { Cursor = Cursors.Default; }
        }

        private bool EnsureDevice()
        {
            if (_device != null && !_device.IsClosed) return true;
            MessageBox.Show(this, "Спочатку натисніть «Знайти M693».", "M693", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        private void SafeDisposeDevice()
        {
            try { if (_device != null) _device.Dispose(); } catch { }
            _device = null;
        }

        private void SetEditorsEnabled(bool enabled)
        {
            btnRead.Enabled = enabled;
            btnExport.Enabled = enabled;
            btnRawBackup.Enabled = enabled;
            btnApply.Enabled = enabled;
            gridButtons.Enabled = enabled;
            gridMatrix.Enabled = enabled;
            btnAssignSimple.Enabled = enabled;
            btnCapture.Enabled = enabled;
            btnGenerateMacro.Enabled = enabled;
            if (numFireInterval != null) numFireInterval.Enabled = enabled;
            if (numFireRepeat != null) numFireRepeat.Enabled = enabled;
            cmbPolling.Enabled = enabled;
            numDebounce.Enabled = enabled;
            cmbLod.Enabled = enabled;
            numSleep.Enabled = enabled;
            chkMotionSync.Enabled = enabled;
            chkAngleSnap.Enabled = enabled;
            chkRipple.Enabled = enabled;
            chkLedOffMoving.Enabled = enabled;
            cmbLedMode.Enabled = enabled;
            btnLedColor.Enabled = enabled;
            numLedSpeed.Enabled = enabled;
            numLedBrightness.Enabled = enabled;
            numDpiCount.Enabled = enabled;
            cmbDpiActive.Enabled = enabled;
            for (int i = 0; i < 8; i++) { cmbDpi[i].Enabled = enabled; btnDpiColor[i].Enabled = enabled; }
        }

        private void LoadUiFromPending()
        {
            if (_pendingProfile == null || _pendingProfile.Length < M693Profile.ProfileLength) return;
            _loadingUi = true;
            try
            {
            RefreshButtonGrids();

            int polling = M693Profile.GetPair(_pendingProfile, M693Profile.AddrPolling, 1);
            int pollIndex = polling == 8 ? 0 : polling == 4 ? 1 : polling == 2 ? 2 : 3;
            cmbPolling.SelectedIndex = pollIndex;

            numDebounce.Value = ClampDecimal(M693Profile.GetPair(_pendingProfile, M693Profile.AddrDebounce, 8), numDebounce.Minimum, numDebounce.Maximum);
            int lod = M693Profile.GetPair(_pendingProfile, M693Profile.AddrLod, 1);
            cmbLod.SelectedIndex = lod == 2 ? 1 : 0;
            int sleepx10 = M693Profile.GetPair(_pendingProfile, M693Profile.AddrSleep, 60);
            numSleep.Value = ClampDecimal(sleepx10 * 10, numSleep.Minimum, numSleep.Maximum);
            chkMotionSync.Checked = M693Profile.GetPair(_pendingProfile, M693Profile.AddrMotionSync, 0) != 0;
            chkAngleSnap.Checked = M693Profile.GetPair(_pendingProfile, M693Profile.AddrAngleSnap, 0) != 0;
            chkRipple.Checked = M693Profile.GetPair(_pendingProfile, M693Profile.AddrRipple, 0) != 0;
            chkLedOffMoving.Checked = M693Profile.GetPair(_pendingProfile, M693Profile.AddrLedOffMoving, 0) != 0;

            LoadLedUi();
            LoadDpiUi();
            }
            finally
            {
                _loadingUi = false;
                _dirtySensor = false;
                _dirtyLed = false;
                _dirtyDpi = false;
                RefreshDashboard();
                RefreshAdvancedUi();
            }
        }

        private decimal ClampDecimal(int v, decimal min, decimal max)
        {
            decimal d = v;
            if (d < min) d = min;
            if (d > max) d = max;
            return d;
        }

        private void RefreshButtonGrids()
        {
            int selectedNo = SelectedPhysicalButtonNo();
            gridButtons.Rows.Clear();
            foreach (var b in _physicalButtons)
            {
                byte[] rec = M693Profile.GetButtonRecord(_pendingProfile, b.Slot);
                string action = DescribeAction(rec, true);
                int row = gridButtons.Rows.Add(b.Number, b.Name, b.Slot, action, Hex(rec));
                if (b.Number == selectedNo) gridButtons.Rows[row].Selected = true;
            }
            if (gridButtons.Rows.Count > 0 && gridButtons.SelectedRows.Count == 0) gridButtons.Rows[0].Selected = true;

            gridMatrix.Rows.Clear();
            for (int slot = 0; slot < 16; slot++)
            {
                byte[] rec = M693Profile.GetButtonRecord(_pendingProfile, slot);
                string phys = string.Join(", ", _physicalButtons.Where(x => x.Slot == slot).Select(x => "#" + x.Number).ToArray());
                gridMatrix.Rows.Add(slot, phys, DescribeAction(rec, true), Hex(rec));
            }
        }

        private string DescribeAction(byte[] rec, bool withMacroName)
        {
            if (rec == null || rec.Length < 4) return "?";
            if (rec[3] != M693Profile.Checksum55(rec.Take(3).ToArray())) return "BAD CHECKSUM " + Hex(rec);
            byte t = rec[0], a = rec[1], b = rec[2];
            if (t == 0) return "Вимкнено";
            if (t == 1)
            {
                if (a == 1) return "Ліва кнопка";
                if (a == 2) return "Права кнопка";
                if (a == 4) return "Середня кнопка";
                if (a == 8) return "Назад";
                if (a == 16) return "Вперед";
                return "Mouse mask 0x" + a.ToString("X2");
            }
            if (t == 2)
            {
                if (a == 1) return "DPI Loop";
                if (a == 2) return "DPI +";
                if (a == 3) return "DPI -";
                return "DPI action " + a;
            }
            if (t == 3) return a == 1 ? "Wheel Left" : a == 2 ? "Wheel Right" : "Wheel action";
            if (t == 4) return "Fire: interval=" + a + ", repeat=" + b;
            if (t == 5) return "Combo";
            if (t == 6)
            {
                string mode = b == 254 ? "HOLD до відпускання" : b == 255 ? "до іншої клавіші" : b == 253 ? "Toggle" : "Repeat " + b;
                string name = "";
                ParsedMacro pm;
                if (withMacroName && _parsedMacros.TryGetValue(a, out pm)) name = " «" + pm.Name + "»";
                return "Macro #" + a + name + " — " + mode;
            }
            if (t == 7) return "Polling rate switch";
            if (t == 8) return "Спец/клавіша 08 " + a.ToString("X2") + " " + b.ToString("X2");
            if (t == 10) return "DPI Lock raw=" + a;
            if (t == 11) return a == 1 ? "Wheel Up" : a == 2 ? "Wheel Down" : "Wheel vertical";
            return "Невідома функція " + t.ToString("X2") + " " + a.ToString("X2") + " " + b.ToString("X2");
        }

        private int SelectedPhysicalButtonNo()
        {
            if (gridButtons == null || gridButtons.SelectedRows.Count == 0) return 0;
            object v = gridButtons.SelectedRows[0].Cells[0].Value;
            int n;
            return v != null && int.TryParse(v.ToString(), out n) ? n : 0;
        }

        private PhysicalButtonSpec SelectedPhysicalButton()
        {
            int n = SelectedPhysicalButtonNo();
            return _physicalButtons.FirstOrDefault(x => x.Number == n);
        }

        private void AssignSimpleAction()
        {
            if (_pendingProfile == null) return;
            var pb = SelectedPhysicalButton();
            if (pb == null) { MessageBox.Show(this, "Виберіть фізичну кнопку у таблиці."); return; }
            byte[] action;
            switch (cmbSimpleAction.SelectedIndex)
            {
                case 0: action = new byte[] { 1, 1, 0 }; break;
                case 1: action = new byte[] { 1, 2, 0 }; break;
                case 2: action = new byte[] { 1, 4, 0 }; break;
                case 3: action = new byte[] { 1, 8, 0 }; break;
                case 4: action = new byte[] { 1, 16, 0 }; break;
                case 5: action = new byte[] { 2, 1, 0 }; break;
                case 6: action = new byte[] { 2, 2, 0 }; break;
                case 7: action = new byte[] { 2, 3, 0 }; break;
                case 8: action = new byte[] { 7, 0, 0 }; break;
                case 9: action = new byte[] { 0, 0, 0 }; break;
                case 10: action = new byte[] { 3, 1, 0 }; break;
                case 11: action = new byte[] { 3, 2, 0 }; break;
                case 12: action = new byte[] { 11, 1, 0 }; break;
                case 13: action = new byte[] { 11, 2, 0 }; break;
                case 14: action = new byte[] { 4, (byte)numFireInterval.Value, (byte)numFireRepeat.Value }; break;
                default: action = new byte[] { 0, 0, 0 }; break;
            }
            M693Profile.SetButtonAction(_pendingProfile, pb.Slot, action);
            RefreshButtonGrids();
            RefreshDashboard();
            lblStatus.Text = "Зміна підготовлена. Натисніть «ЗАСТОСУВАТИ В МИШКУ».";
        }

        private void StartCapture()
        {
            if (_pendingProfile == null) return;
            if (_capture != null) _capture.Dispose();
            txtCapture.Text = "СЛУХАЮ... натисніть потрібну фізичну кнопку";
            txtCapture.BackColor = Color.LightYellow;
            lblCapture.Text = "Збираю глобальні події 2 секунди. Нічого не блокую і не змінюю в мишці.";
            _capture = new CaptureEngine();
            _capture.Completed += CaptureCompleted;
            _capture.Start(2000);
        }

        private void CaptureCompleted(object sender, CaptureResult result)
        {
            if (InvokeRequired) { BeginInvoke(new Action<object, CaptureResult>(CaptureCompleted), sender, result); return; }
            txtCapture.BackColor = SystemColors.Window;
            List<PhysicalButtonSpec> candidates = InferCaptureCandidates(result);
            if (candidates.Count == 1)
            {
                var pb = candidates[0];
                SelectPhysicalButton(pb.Number);
                txtCapture.Text = "Визначено: кнопка #" + pb.Number + " — " + pb.Name;
                lblCapture.Text = result.Summary + " → matched onboard assignment";
            }
            else if (candidates.Count > 1)
            {
                txtCapture.Text = "Неоднозначно: " + string.Join(", ", candidates.Select(x => "#" + x.Number).ToArray());
                lblCapture.Text = result.Summary + ". Виберіть потрібну з кандидатів у таблиці.";
            }
            else
            {
                txtCapture.Text = "Автоматично не визначено. Виберіть кнопку в таблиці.";
                lblCapture.Text = result.Summary + ". Якщо onboard-макрос приховує фізичну кнопку, Windows бачить лише результат макросу.";
            }
            if (_capture != null) { _capture.Dispose(); _capture = null; }
        }

        private List<PhysicalButtonSpec> InferCaptureCandidates(CaptureResult r)
        {
            var list = new List<PhysicalButtonSpec>();
            foreach (var pb in _physicalButtons)
            {
                byte[] rec = M693Profile.GetButtonRecord(_pendingProfile, pb.Slot);
                string cls = ClassifyActionOutput(rec);
                if (r.Dominant == cls) list.Add(pb);
            }
            if (r.IsBurst && list.Count > 1)
            {
                var macros = list.Where(pb => M693Profile.GetButtonRecord(_pendingProfile, pb.Slot)[0] == 6).ToList();
                if (macros.Count == 1) return macros;
            }
            return list;
        }

        private string ClassifyActionOutput(byte[] rec)
        {
            if (rec[0] == 1)
            {
                if (rec[1] == 1) return "left";
                if (rec[1] == 2) return "right";
                if (rec[1] == 4) return "middle";
                if (rec[1] == 8) return "back";
                if (rec[1] == 16) return "forward";
            }
            if (rec[0] == 6)
            {
                ParsedMacro pm;
                if (_parsedMacros.TryGetValue(rec[1], out pm)) return pm.DominantOutput;
            }
            return "other";
        }

        private void SelectPhysicalButton(int number)
        {
            foreach (DataGridViewRow row in gridButtons.Rows)
            {
                if (Convert.ToInt32(row.Cells[0].Value) == number)
                {
                    row.Selected = true;
                    gridButtons.CurrentCell = row.Cells[0];
                    break;
                }
            }
        }

        private void GenerateAndBindMacro()
        {
            if (_pendingProfile == null) return;
            var pb = SelectedPhysicalButton();
            if (pb == null) { MessageBox.Show(this, "Спочатку виберіть фізичну кнопку."); return; }
            int min = (int)numMin.Value;
            int typ = (int)numTypical.Value;
            int max = (int)numMax.Value;
            if (!(min <= typ && typ <= max))
            {
                MessageBox.Show(this, "Має бути: Мінімум ≤ Типово ≤ Максимум.");
                return;
            }

            int macroSlot = ChooseMacroSlot(pb);
            if (macroSlot < 0)
            {
                MessageBox.Show(this, "Немає вільного macro slot 0..15.");
                return;
            }

            int clickMask = cmbMacroClick.SelectedIndex == 1 ? 2 : 1;
            int[] intervals = SmoothPatternGenerator.Generate(29, min, typ, max, trkSmooth.Value, Environment.TickCount ^ (pb.Number << 12));
            byte[] macro = MacroBuilder.BuildFullSlot(macroSlot, "NEUTRON_B" + pb.Number + (clickMask == 1 ? "_L" : "_R"), clickMask, intervals);
            _pendingMacroSlots[macroSlot] = macro;
            _parsedMacros[macroSlot] = ParsedMacro.Parse(macroSlot, macro);

            M693Profile.SetButtonAction(_pendingProfile, pb.Slot, new byte[] { 6, (byte)macroSlot, 254 });
            RefreshButtonGrids();
            RefreshDashboard();
            txtPreview.Text = "Macro slot: " + macroSlot + Environment.NewLine +
                "Подій: " + (intervals.Length * 2) + " (" + intervals.Length + " кліків у патерні)" + Environment.NewLine +
                "Інтервали, мс: " + string.Join(", ", intervals.Select(x => x.ToString()).ToArray()) + Environment.NewLine +
                "Після останнього значення прошивка плавно переходить до початку патерна і повторює, поки кнопка затиснута.";
            lblStatus.Text = "Smooth Macro підготовлено. Ще НЕ записано — натисніть Apply.";
        }

        private int ChooseMacroSlot(PhysicalButtonSpec pb)
        {
            byte[] cur = M693Profile.GetButtonRecord(_pendingProfile, pb.Slot);
            if (cur[0] == 6 && cur[1] < 16) return cur[1];
            var used = new HashSet<int>(M693Profile.ReferencedMacroSlots(_pendingProfile));
            int preferred = pb.Slot;
            if (preferred >= 0 && preferred < 16 && !used.Contains(preferred)) return preferred;
            for (int i = 0; i < 16; i++) if (!used.Contains(i)) return i;
            return -1;
        }

        private void LoadLedUi()
        {
            byte[] b = new byte[7];
            Array.Copy(_pendingProfile, M693Profile.AddrLed, b, 0, 7);
            if (b[6] != M693Profile.Checksum55(b.Take(6).ToArray()))
            {
                Log("LED block checksum invalid; залишаю UI defaults.");
                return;
            }
            int modeIndex = LedCodec.IndexFromHw(b[0]);
            if (modeIndex >= 0) cmbLedMode.SelectedIndex = modeIndex;
            _ledColor = Color.FromArgb(b[1], b[2], b[3]);
            UpdateLedColorButton();
            numLedSpeed.Value = LedCodec.DecodeSpeed(cmbLedMode.SelectedIndex, b[4]);
            numLedBrightness.Value = LedCodec.DecodeBrightness(cmbLedMode.SelectedIndex, b[5]);
        }

        private void PickLedColor()
        {
            using (var d = new ColorDialog())
            {
                d.Color = _ledColor;
                if (d.ShowDialog(this) == DialogResult.OK) { _ledColor = d.Color; UpdateLedColorButton(); if (!_loadingUi) _dirtyLed = true; }
            }
        }

        private void UpdateLedColorButton()
        {
            btnLedColor.BackColor = _ledColor;
            btnLedColor.ForeColor = _ledColor.GetBrightness() < 0.5 ? Color.White : Color.Black;
            btnLedColor.Text = string.Format("#{0:X2}{1:X2}{2:X2}", _ledColor.R, _ledColor.G, _ledColor.B);
        }

        private void LoadDpiUi()
        {
            int count = M693Profile.GetPair(_pendingProfile, M693Profile.AddrDpiCount, 5);
            if (count < 1 || count > 8) count = 5;
            numDpiCount.Value = count;
            int active = M693Profile.GetPair(_pendingProfile, M693Profile.AddrDpiActive, 0);
            if (active < 0 || active >= 8) active = 0;
            cmbDpiActive.SelectedIndex = active;
            for (int i = 0; i < 8; i++)
            {
                int off = M693Profile.AddrDpiTable + i * 4;
                byte[] rec = new byte[4]; Array.Copy(_pendingProfile, off, rec, 0, 4);
                int dpi = M693DpiCodec.Decode(rec);
                int idx = Array.IndexOf(M693DpiCodec.AllowedDpiValues, dpi);
                cmbDpi[i].SelectedIndex = idx >= 0 ? idx : 0;
                int coff = M693Profile.AddrDpiColors + i * 4;
                byte[] c = new byte[4]; Array.Copy(_pendingProfile, coff, c, 0, 4);
                dpiColors[i] = Color.FromArgb(c[0], c[1], c[2]);
                UpdateDpiColorButton(i);
            }
        }

        private void PickDpiColor(int i)
        {
            using (var d = new ColorDialog())
            {
                d.Color = dpiColors[i];
                if (d.ShowDialog(this) == DialogResult.OK) { dpiColors[i] = d.Color; UpdateDpiColorButton(i); if (!_loadingUi) _dirtyDpi = true; }
            }
        }

        private void UpdateDpiColorButton(int i)
        {
            btnDpiColor[i].BackColor = dpiColors[i];
            btnDpiColor[i].ForeColor = dpiColors[i].GetBrightness() < 0.5 ? Color.White : Color.Black;
            btnDpiColor[i].Text = string.Format("#{0:X2}{1:X2}{2:X2}", dpiColors[i].R, dpiColors[i].G, dpiColors[i].B);
        }

        private void SyncEditorsToPending()
        {
            if (_pendingProfile == null) return;

            if (_dirtySensor)
            {
                int poll = cmbPolling.SelectedIndex == 0 ? 8 : cmbPolling.SelectedIndex == 1 ? 4 : cmbPolling.SelectedIndex == 2 ? 2 : 1;
                M693Profile.SetPair(_pendingProfile, M693Profile.AddrPolling, (byte)poll);
                M693Profile.SetPair(_pendingProfile, M693Profile.AddrDebounce, (byte)numDebounce.Value);
                M693Profile.SetPair(_pendingProfile, M693Profile.AddrLod, (byte)(cmbLod.SelectedIndex == 1 ? 2 : 1));
                M693Profile.SetPair(_pendingProfile, M693Profile.AddrSleep, (byte)Math.Max(1, Math.Min(255, (int)numSleep.Value / 10)));
                M693Profile.SetPair(_pendingProfile, M693Profile.AddrMotionSync, (byte)(chkMotionSync.Checked ? 1 : 0));
                M693Profile.SetPair(_pendingProfile, M693Profile.AddrAngleSnap, (byte)(chkAngleSnap.Checked ? 1 : 0));
                M693Profile.SetPair(_pendingProfile, M693Profile.AddrRipple, (byte)(chkRipple.Checked ? 1 : 0));
                M693Profile.SetPair(_pendingProfile, M693Profile.AddrLedOffMoving, (byte)(chkLedOffMoving.Checked ? 1 : 0));
            }

            if (_dirtyLed)
            {
                byte[] led = LedCodec.Encode(cmbLedMode.SelectedIndex, _ledColor, (int)numLedSpeed.Value, (int)numLedBrightness.Value);
                Array.Copy(led, 0, _pendingProfile, M693Profile.AddrLed, led.Length);
            }

            if (_dirtyDpi)
            {
                int count = (int)numDpiCount.Value;
                if (M693Profile.GetPair(_pendingProfile, M693Profile.AddrDpiCount, 5) != count)
                    M693Profile.SetPair(_pendingProfile, M693Profile.AddrDpiCount, (byte)count);
                int active = cmbDpiActive.SelectedIndex;
                if (active >= count) active = count - 1;
                if (active < 0) active = 0;
                if (M693Profile.GetPair(_pendingProfile, M693Profile.AddrDpiActive, 0) != active)
                    M693Profile.SetPair(_pendingProfile, M693Profile.AddrDpiActive, (byte)active);

                for (int i = 0; i < 8; i++)
                {
                    int off = M693Profile.AddrDpiTable + i * 4;
                    byte[] oldRec = new byte[4]; Array.Copy(_pendingProfile, off, oldRec, 0, 4);
                    int oldDpi = M693DpiCodec.Decode(oldRec);
                    int dpi = int.Parse(cmbDpi[i].SelectedItem == null ? oldDpi.ToString() : cmbDpi[i].SelectedItem.ToString());
                    if (dpi != oldDpi)
                    {
                        byte[] rec = M693DpiCodec.Encode(dpi);
                        Array.Copy(rec, 0, _pendingProfile, off, 4);
                    }

                    int coff = M693Profile.AddrDpiColors + i * 4;
                    Color c = dpiColors[i];
                    if (_pendingProfile[coff] != c.R || _pendingProfile[coff + 1] != c.G || _pendingProfile[coff + 2] != c.B)
                    {
                        byte[] cr = new byte[] { c.R, c.G, c.B, M693Profile.Checksum55(new byte[] { c.R, c.G, c.B }) };
                        Array.Copy(cr, 0, _pendingProfile, coff, 4);
                    }
                }
            }
        }

        private void ExportProfile()
        {
            if (_pendingProfile == null) return;
            SyncEditorsToPending();
            using (var d = new SaveFileDialog())
            {
                d.Filter = "Neutron M693 profile (*.m693profile.json)|*.m693profile.json|JSON (*.json)|*.json";
                d.FileName = "M693_Profile_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".m693profile.json";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                SaveProfileFile(d.FileName, true);
                MessageBox.Show(this, "Профіль збережено:\r\n" + d.FileName, "M693", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void SaveProfileFile(string path, bool includeCurrentReferencedMacros)
        {
            var pf = new ExportedProfile();
            pf.Format = "NeutronM693ProfileV1";
            pf.ExportedUtc = DateTime.UtcNow.ToString("o");
            pf.Device = _device == null ? "VID_25A7 PID_FA7B/FA7C" : _device.Description;
            pf.ProfileHex = HexCompact(_pendingProfile);
            pf.Macros = new Dictionary<string, string>();

            var slots = new HashSet<int>(M693Profile.ReferencedMacroSlots(_pendingProfile));
            foreach (int s in _pendingMacroSlots.Keys) slots.Add(s);
            if (includeCurrentReferencedMacros && _device != null)
            {
                foreach (int s in slots.ToArray())
                {
                    if (!_macroSlots.ContainsKey(s) && !_pendingMacroSlots.ContainsKey(s) && s >= 0 && s < 16)
                    {
                        try { _macroSlots[s] = _device.ReadBytes((ushort)(M693Profile.MacroBase + s * M693Profile.MacroSlotSize), M693Profile.MacroSlotSize); }
                        catch (Exception ex) { Log("Export macro slot " + s + " warning: " + ex.Message); }
                    }
                }
            }
            foreach (int s in slots)
            {
                byte[] raw;
                if (_pendingMacroSlots.TryGetValue(s, out raw) || _macroSlots.TryGetValue(s, out raw)) pf.Macros[s.ToString()] = HexCompact(raw);
            }
            var js = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 * 8 };
            File.WriteAllText(path, js.Serialize(pf), new UTF8Encoding(false));
        }

        private void ImportProfile()
        {
            using (var d = new OpenFileDialog())
            {
                d.Filter = "Neutron M693 profile (*.m693profile.json)|*.m693profile.json|JSON (*.json)|*.json";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    LoadProfileFromPath(d.FileName);
                    MessageBox.Show(this, "Профіль імпортовано у редактор. Перевірте параметри і лише потім натискайте Apply.", "M693", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void RawBackup()
        {
            if (!EnsureDevice()) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                string dir = Path.Combine(Application.StartupPath, "Backups");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "M693_RAW_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".m693profile.json");
                byte[] profile = _device.ReadBytes(0, M693Profile.ProfileLength);
                SaveLiveBackup(path, profile, _pendingMacroSlots.Keys);
                MessageBox.Show(this, "Backup створено:\r\n" + path, "M693", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Backup", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { Cursor = Cursors.Default; }
        }

        private void SaveLiveBackup(string path, byte[] liveProfile, IEnumerable<int> extraMacroSlots)
        {
            byte[] savedPending = _pendingProfile;
            var savedPendingMacros = new Dictionary<int, byte[]>(_pendingMacroSlots);
            try
            {
                _pendingProfile = liveProfile;
                _pendingMacroSlots.Clear();
                var slots = new HashSet<int>(M693Profile.ReferencedMacroSlots(liveProfile));
                if (extraMacroSlots != null) foreach (int s in extraMacroSlots) if (s >= 0 && s < 16) slots.Add(s);
                foreach (int slot in slots)
                {
                    byte[] raw = _device.ReadBytes((ushort)(M693Profile.MacroBase + slot * M693Profile.MacroSlotSize), M693Profile.MacroSlotSize);
                    _macroSlots[slot] = raw;
                }
                SaveProfileFile(path, true);
            }
            finally
            {
                _pendingProfile = savedPending;
                _pendingMacroSlots.Clear();
                foreach (var kv in savedPendingMacros) _pendingMacroSlots[kv.Key] = kv.Value;
            }
        }

        private void ApplyToMouse()
        {
            if (!EnsureDevice() || _pendingProfile == null) return;
            SyncEditorsToPending();
            UpdateOfficialStatus();
            if (Process.GetProcessesByName("OemDrv").Length > 0)
            {
                MessageBox.Show(this, "Закрийте офіційну Redragon/OemDrv.exe повністю через tray і повторіть Apply.\r\n\r\nДві програми одночасно не повинні читати command HID interface.", "OemDrv.exe запущена", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!M693Profile.HasLeftClickOnPhysicalButton(_pendingProfile, _physicalButtons))
            {
                MessageBox.Show(this, "Заблоковано: серед фізичних кнопок не залишилось звичайного лівого кліку. Це захист від невдалого профілю.", "Safety", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int diff = CountDiff(_originalProfile, _pendingProfile);
            string summary = "Буде змінено байтів профілю: " + diff + "\r\nMacro slots до запису: " + _pendingMacroSlots.Count + "\r\n\r\nПеред записом буде створено автоматичний backup і після запису — read-back перевірка.";
            if (_loadedProfilePath != null) summary += "\r\n\r\nІмпортований файл: " + _loadedProfilePath;
            if (MessageBox.Show(this, summary + "\r\n\r\nПродовжити?", "Apply в onboard memory", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            Cursor = Cursors.WaitCursor;
            try
            {
                Log("=== APPLY START ===");
                byte[] live = _device.ReadBytes(0, M693Profile.ProfileLength);
                string bdir = Path.Combine(Application.StartupPath, "Backups");
                Directory.CreateDirectory(bdir);
                string backup = Path.Combine(bdir, "AUTO_BEFORE_APPLY_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".m693profile.json");
                SaveLiveBackup(backup, live, _pendingMacroSlots.Keys);
                Log("Backup: " + backup);

                foreach (var kv in _pendingMacroSlots.OrderBy(x => x.Key))
                {
                    int slot = kv.Key;
                    byte[] target = kv.Value;
                    byte[] before;
                    if (!_macroSlots.TryGetValue(slot, out before))
                    {
                        before = _device.ReadBytes((ushort)(M693Profile.MacroBase + slot * M693Profile.MacroSlotSize), M693Profile.MacroSlotSize);
                        _macroSlots[slot] = before;
                    }
                    WriteDiff((ushort)(M693Profile.MacroBase + slot * M693Profile.MacroSlotSize), before, target, "macro #" + slot);
                }

                WriteDiff(0, live, _pendingProfile, "profile");

                byte[] verify = _device.ReadBytes(0, M693Profile.ProfileLength);
                if (!verify.SequenceEqual(_pendingProfile))
                {
                    throw new IOException("Read-back profile не збігається з тим, що записували. Backup збережено: " + backup);
                }
                foreach (var kv in _pendingMacroSlots)
                {
                    byte[] vr = _device.ReadBytes((ushort)(M693Profile.MacroBase + kv.Key * M693Profile.MacroSlotSize), M693Profile.MacroSlotSize);
                    if (!vr.SequenceEqual(kv.Value)) throw new IOException("Read-back macro slot " + kv.Key + " не збігся.");
                    _macroSlots[kv.Key] = vr;
                }

                _originalProfile = verify;
                _pendingProfile = (byte[])verify.Clone();
                _pendingMacroSlots.Clear();
                lblStatus.Text = "УСПІХ: профіль записано і перевірено read-back.";
                lblStatus.ForeColor = Color.LightGreen;
                Log("=== APPLY OK ===");
                ReadFromMouse();
                MessageBox.Show(this, "Готово. Налаштування записані прямо в onboard memory M693 і перевірені повторним читанням.", "M693", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("APPLY ERROR: " + ex);
                MessageBox.Show(this, "Запис зупинено:\r\n" + ex.Message + "\r\n\r\nАвтоматичний backup лежить у папці Backups.", "M693 Apply", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { Cursor = Cursors.Default; }
        }

        private void WriteDiff(ushort baseAddr, byte[] before, byte[] after, string name)
        {
            if (before.Length != after.Length) throw new ArgumentException("WriteDiff length mismatch");
            int i = 0;
            while (i < before.Length)
            {
                while (i < before.Length && before[i] == after[i]) i++;
                if (i >= before.Length) break;
                int start = i;
                while (i < before.Length && before[i] != after[i]) i++;
                int end = i;
                int p = start;
                while (p < end)
                {
                    int len = Math.Min(10, end - p);
                    byte[] chunk = new byte[len]; Array.Copy(after, p, chunk, 0, len);
                    ushort addr = (ushort)(baseAddr + p);
                    Log("WRITE " + name + " 0x" + addr.ToString("X4") + " len=" + len + "  " + Hex(chunk));
                    _device.WriteBytes(addr, chunk);
                    p += len;
                }
            }
        }

        private int CountDiff(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return b == null ? 0 : b.Length;
            int n = 0; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++; return n;
        }

        private static string Hex(byte[] b)
        {
            return string.Join(" ", b.Select(x => x.ToString("X2")).ToArray());
        }

        private static string HexCompact(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("X2"));
            return sb.ToString();
        }

        private static byte[] ParseHex(string s)
        {
            if (s == null) return new byte[0];
            s = new string(s.Where(Uri.IsHexDigit).ToArray());
            if ((s.Length & 1) != 0) throw new InvalidDataException("HEX length odd");
            byte[] b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }
    }

    internal sealed class ExportedProfile
    {
        public string Format { get; set; }
        public string ExportedUtc { get; set; }
        public string Device { get; set; }
        public string ProfileHex { get; set; }
        public Dictionary<string, string> Macros { get; set; }
    }

    internal sealed class PhysicalButtonSpec
    {
        public int Number;
        public int Slot;
        public string Name;

        public static List<PhysicalButtonSpec> CreateM693()
        {
            // Derived from the supplied M693 Cfg.ini K1_1..K7_1. The final field is a one-based matrix position.
            return new List<PhysicalButtonSpec>
            {
                new PhysicalButtonSpec { Number = 1, Slot = 0, Name = "Ліва кнопка" },
                new PhysicalButtonSpec { Number = 2, Slot = 1, Name = "Права кнопка" },
                new PhysicalButtonSpec { Number = 3, Slot = 2, Name = "Колесо (натискання)" },
                new PhysicalButtonSpec { Number = 4, Slot = 4, Name = "Бокова кнопка 4" },
                new PhysicalButtonSpec { Number = 5, Slot = 3, Name = "Бокова кнопка 5" },
                new PhysicalButtonSpec { Number = 6, Slot = 9, Name = "Верхня кнопка 6" },
                new PhysicalButtonSpec { Number = 7, Slot = 10, Name = "Верхня кнопка 7" }
            };
        }
    }

    internal static class M693Profile
    {
        public const int ProfileLength = 0xB5;
        public const int AddrPolling = 0x00;
        public const int AddrDpiCount = 0x02;
        public const int AddrDpiActive = 0x04;
        public const int AddrLod = 0x0A;
        public const int AddrDpiTable = 0x0C;
        public const int AddrDpiColors = 0x2C;
        public const int AddrButtons = 0x60;
        public const int AddrLed = 0xA0;
        public const int AddrDebounce = 0xA9;
        public const int AddrMotionSync = 0xAB;
        public const int AddrSleep = 0xAD;
        public const int AddrAngleSnap = 0xAF;
        public const int AddrRipple = 0xB1;
        public const int AddrLedOffMoving = 0xB3;
        public const int MacroBase = 0x300;
        public const int MacroSlotSize = 0x180;

        public static byte Checksum55(byte[] data)
        {
            byte v = 0x55;
            foreach (byte b in data) unchecked { v -= b; }
            return v;
        }

        public static int GetPair(byte[] p, int addr, int fallback)
        {
            if (p == null || addr < 0 || addr + 1 >= p.Length) return fallback;
            return p[addr + 1] == unchecked((byte)(0x55 - p[addr])) ? p[addr] : fallback;
        }

        public static void SetPair(byte[] p, int addr, byte v)
        {
            p[addr] = v;
            p[addr + 1] = unchecked((byte)(0x55 - v));
        }

        public static byte[] GetButtonRecord(byte[] p, int slot)
        {
            var b = new byte[4];
            Array.Copy(p, AddrButtons + slot * 4, b, 0, 4);
            return b;
        }

        public static void SetButtonAction(byte[] p, int slot, byte[] action3)
        {
            if (action3 == null || action3.Length != 3) throw new ArgumentException("action3");
            int a = AddrButtons + slot * 4;
            p[a] = action3[0]; p[a + 1] = action3[1]; p[a + 2] = action3[2];
            p[a + 3] = Checksum55(action3);
        }

        public static IEnumerable<int> ReferencedMacroSlots(byte[] p)
        {
            if (p == null) yield break;
            for (int i = 0; i < 16; i++)
            {
                byte[] r = GetButtonRecord(p, i);
                if (r[0] == 6) yield return r[1];
            }
        }

        public static bool HasLeftClickOnPhysicalButton(byte[] p, List<PhysicalButtonSpec> buttons)
        {
            foreach (var pb in buttons)
            {
                byte[] r = GetButtonRecord(p, pb.Slot);
                if (r[0] == 1 && r[1] == 1 && r[2] == 0) return true;
            }
            return false;
        }
    }

    internal sealed class ParsedMacro
    {
        public int Slot;
        public string Name = "";
        public int EventCount;
        public List<MacroEventRaw> Events = new List<MacroEventRaw>();
        public bool ChecksumOk;
        public string DominantOutput = "other";

        public static ParsedMacro Parse(int slot, byte[] raw)
        {
            var m = new ParsedMacro { Slot = slot };
            if (raw == null || raw.Length < M693Profile.MacroSlotSize) return m;
            int declaredNameLen = (int)raw[0];
            if (declaredNameLen < 0 || declaredNameLen > 30) return m;
            int n = Math.Min(declaredNameLen, 30);
            m.Name = Encoding.ASCII.GetString(raw, 1, n).TrimEnd('\0');
            int count = raw[31];
            if (count < 0 || count > 70 || 32 + count * 5 >= raw.Length) return m;
            m.EventCount = count;
            for (int i = 0; i < count; i++)
            {
                int o = 32 + i * 5;
                m.Events.Add(new MacroEventRaw
                {
                    B0 = raw[o], B1 = raw[o + 1], B2 = raw[o + 2],
                    Delay = (ushort)((raw[o + 3] << 8) | raw[o + 4])
                });
            }
            int checkAt = 32 + count * 5;
            byte[] block = new byte[1 + count * 5];
            Array.Copy(raw, 31, block, 0, block.Length);
            m.ChecksumOk = raw[checkAt] == M693Profile.Checksum55(block);
            m.DominantOutput = Classify(m.Events);
            return m;
        }

        private static string Classify(List<MacroEventRaw> e)
        {
            int left = 0, right = 0, middle = 0, back = 0, forward = 0, keyboard = 0;
            foreach (var x in e)
            {
                int kind = x.B0 & 0x07;
                if (kind == 0x04)
                {
                    if (x.B1 == 1) left++; else if (x.B1 == 2) right++; else if (x.B1 == 4) middle++; else if (x.B1 == 8) back++; else if (x.B1 == 16) forward++;
                }
                else if (kind == 0x01 || kind == 0x00 || kind == 0x02) keyboard++;
            }
            int max = Math.Max(Math.Max(Math.Max(left, right), Math.Max(middle, back)), Math.Max(forward, keyboard));
            if (max == 0) return "other";
            if (max == left) return "left";
            if (max == right) return "right";
            if (max == middle) return "middle";
            if (max == back) return "back";
            if (max == forward) return "forward";
            return "keyboard";
        }
    }

    internal sealed class MacroEventRaw
    {
        public byte B0, B1, B2;
        public ushort Delay;
    }

    internal static class MacroBuilder
    {
        public static byte[] BuildFullSlot(int slot, string name, int mouseMask, int[] intervals)
        {
            if (intervals == null || intervals.Length == 0) throw new ArgumentException("intervals");
            if (intervals.Length * 2 > 70) throw new ArgumentException("Забагато macro events");
            byte[] raw = new byte[M693Profile.MacroSlotSize];
            byte[] nb = Encoding.ASCII.GetBytes(name ?? "NEUTRON");
            if (nb.Length > 30) Array.Resize(ref nb, 30);
            raw[0] = (byte)nb.Length;
            Array.Copy(nb, 0, raw, 1, nb.Length);

            int count = intervals.Length * 2;
            raw[31] = (byte)count;
            int o = 32;
            var rnd = new Random(Environment.TickCount ^ slot);
            foreach (int interval in intervals)
            {
                int down = Math.Min(Math.Max(3, 5 + rnd.Next(-1, 3)), Math.Max(3, interval - 1));
                int up = Math.Max(1, interval - down);
                WriteEvent(raw, ref o, 0x84, (byte)mouseMask, 0x00, (ushort)down);   // press + mouse/direction flag
                WriteEvent(raw, ref o, 0x44, (byte)mouseMask, 0x00, (ushort)up);     // release + mouse/direction flag
            }
            byte[] block = new byte[1 + count * 5];
            Array.Copy(raw, 31, block, 0, block.Length);
            raw[o] = M693Profile.Checksum55(block);
            return raw;
        }

        private static void WriteEvent(byte[] raw, ref int o, byte b0, byte b1, byte b2, ushort delay)
        {
            raw[o++] = b0; raw[o++] = b1; raw[o++] = b2;
            raw[o++] = (byte)(delay >> 8); raw[o++] = (byte)(delay & 0xFF);
        }
    }

    internal static class SmoothPatternGenerator
    {
        public static int[] Generate(int count, int min, int typical, int max, int smooth, int seed)
        {
            var rnd = new Random(seed);
            int controls = Math.Max(4, Math.Min(10, 10 - smooth / 15));
            double[] c = new double[controls];
            c[0] = typical;
            for (int i = 1; i < controls; i++)
            {
                double u = (rnd.NextDouble() + rnd.NextDouble() + rnd.NextDouble()) / 3.0;
                if (rnd.NextDouble() < 0.12) u = rnd.NextDouble() < 0.5 ? rnd.NextDouble() * 0.18 : 0.82 + rnd.NextDouble() * 0.18;
                c[i] = u < 0.5
                    ? min + (typical - min) * (u / 0.5)
                    : typical + (max - typical) * ((u - 0.5) / 0.5);
            }
            int[] outv = new int[count];
            for (int i = 0; i < count; i++)
            {
                double pos = (double)i / count * controls;
                int k0 = ((int)Math.Floor(pos)) % controls;
                int k1 = (k0 + 1) % controls;
                double t = pos - Math.Floor(pos);
                double s = t * t * (3.0 - 2.0 * t); // smoothstep
                double v = c[k0] * (1.0 - s) + c[k1] * s;
                v += (rnd.NextDouble() - 0.5) * (1.0 - smooth / 100.0) * 8.0;
                int iv = (int)Math.Round(v);
                if (iv < min) iv = min; if (iv > max) iv = max;
                outv[i] = iv;
            }
            return outv;
        }
    }

    internal static class LedCodec
    {
        private static readonly byte[] ModeHw = { 2, 1, 0, 3, 5, 7, 4 };
        private static readonly byte[] SpeedStd = { 0xFF, 0xE6, 0xD2, 0xBE, 0xAA, 0x96, 0x82, 0x6E, 0x46, 0x28 };
        private static readonly byte[] BriStd = { 0x2D, 0x32, 0x4B, 0x64, 0x7D, 0x96, 0xAF, 0xC8, 0xE1, 0xFF };
        private static readonly byte[] SpeedStream = { 0x0A, 0x09, 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 };
        private static readonly byte[] BriStream = { 0x48, 0x5C, 0x70, 0x84, 0x98, 0xAC, 0xC0, 0xD4, 0xE8, 0xFC };

        public static int IndexFromHw(byte hw) { return Array.IndexOf(ModeHw, hw); }
        public static int DecodeSpeed(int modeIndex, byte raw) { return FindLevel(modeIndex == 2 ? SpeedStream : SpeedStd, raw); }
        public static int DecodeBrightness(int modeIndex, byte raw) { return FindLevel(modeIndex == 2 ? BriStream : BriStd, raw); }
        private static int FindLevel(byte[] a, byte raw) { int i = Array.IndexOf(a, raw); return i >= 0 ? i + 1 : 6; }

        public static byte[] Encode(int modeIndex, Color color, int speed, int bri)
        {
            if (modeIndex < 0 || modeIndex >= ModeHw.Length) modeIndex = 5;
            int si = Math.Max(1, Math.Min(10, speed)) - 1;
            int bi = Math.Max(1, Math.Min(10, bri)) - 1;
            byte[] st = modeIndex == 2 ? SpeedStream : SpeedStd;
            byte[] bt = modeIndex == 2 ? BriStream : BriStd;
            byte[] b = { ModeHw[modeIndex], color.R, color.G, color.B, st[si], bt[bi], 0 };
            b[6] = M693Profile.Checksum55(b.Take(6).ToArray());
            return b;
        }
    }

    internal static class M693DpiCodec
    {
        public static readonly int[] AllowedDpiValues = {
            500,600,700,800,900,1000,1100,1200,1300,1400,1500,1600,1700,1800,1900,2000,
            2100,2200,2300,2400,2500,2600,2700,2800,2900,3000,3100,3200,3300,3400,3500,3600,
            3700,3800,3900,4000,4200,4400,4600,4800,5000,5200,5400,5600,5800,6000,6200,6400,
            6600,6800,7000,7200,7400,7600,7800,8000
        };
        private static readonly byte[] Codes = {
            0x0e,0x10,0x12,0x15,0x17,0x1a,0x1d,0x1f,0x22,0x25,0x27,0x2a,0x2d,0x2f,0x32,0x34,
            0x37,0x3a,0x3c,0x3f,0x42,0x44,0x47,0x4a,0x4c,0x4f,0x52,0x54,0x57,0x59,0x5c,0x5f,
            0x61,0x64,0x67,0x69,0x37,0x3a,0x3c,0x3f,0x42,0x44,0x47,0x4a,0x4c,0x4f,0x52,0x54,
            0x57,0x59,0x5c,0x5f,0x61,0x64,0x67,0x69
        };

        public static byte[] Encode(int dpi)
        {
            int idx = Array.IndexOf(AllowedDpiValues, dpi);
            if (idx < 0) idx = 0;
            byte code = Codes[idx];
            byte flags = (byte)(idx >= 36 ? 0x11 : 0x00);
            byte[] r = { code, code, flags, 0 };
            r[3] = M693Profile.Checksum55(r.Take(3).ToArray());
            return r;
        }

        public static int Decode(byte[] rec)
        {
            if (rec == null || rec.Length < 4 || rec[3] != M693Profile.Checksum55(rec.Take(3).ToArray())) return AllowedDpiValues[0];
            bool high = (rec[2] & 0x0F) != 0;
            for (int i = 0; i < Codes.Length; i++)
                if (Codes[i] == rec[0] && (i >= 36) == high) return AllowedDpiValues[i];
            for (int i = 0; i < Codes.Length; i++) if (Codes[i] == rec[0]) return AllowedDpiValues[i];
            return AllowedDpiValues[0];
        }
    }

    internal sealed class BatteryInfo
    {
        public int Percent;
        public bool Charging;
    }

    internal sealed class M693Device : IDisposable
    {
        private SafeFileHandle _handle;
        private FileStream _stream;
        private readonly int _inputReportLen;
        private readonly Action<string> _log;
        public string PathName { get; private set; }
        public ushort ProductId { get; private set; }
        public string Description { get; private set; }
        public bool IsClosed { get { return _handle == null || _handle.IsClosed || _handle.IsInvalid; } }

        private M693Device(string path, SafeFileHandle handle, HidInterop.HIDP_CAPS caps, ushort pid, Action<string> log)
        {
            PathName = path; _handle = handle; ProductId = pid; _inputReportLen = Math.Max(17, (int)caps.InputReportByteLength); _log = log;
            _stream = new FileStream(_handle, FileAccess.ReadWrite, _inputReportLen, true);
            Description = string.Format("VID=25A7 PID={0:X4} | UsagePage=0x{1:X4} Usage=0x{2:X4} | Input={3} Feature={4} | {5}", pid, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.FeatureReportByteLength, path);
        }

        public static M693Device FindAndOpen(Action<string> log)
        {
            var paths = HidInterop.EnumerateHidPaths();
            var candidates = new List<Tuple<string, ushort, HidInterop.HIDP_CAPS>>();
            foreach (string path in paths)
            {
                SafeFileHandle h = null;
                try
                {
                    h = HidInterop.Open(path, true);
                    if (h == null || h.IsInvalid) { if (h != null) h.Dispose(); continue; }
                    HidInterop.HIDD_ATTRIBUTES attr;
                    if (!HidInterop.GetAttributes(h, out attr)) { h.Dispose(); continue; }
                    if (attr.VendorID != 0x25A7 || (attr.ProductID != 0xFA7B && attr.ProductID != 0xFA7C)) { h.Dispose(); continue; }
                    HidInterop.HIDP_CAPS caps;
                    if (!HidInterop.GetCaps(h, out caps)) { h.Dispose(); continue; }
                    log(string.Format("Candidate VID={0:X4} PID={1:X4} UsagePage={2:X4} Usage={3:X4} In={4} Feat={5} path={6}", attr.VendorID, attr.ProductID, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.FeatureReportByteLength, path));
                    if (caps.FeatureReportByteLength == 17 && caps.InputReportByteLength >= 17) candidates.Add(Tuple.Create(path, attr.ProductID, caps));
                    h.Dispose(); h = null;
                }
                catch (Exception ex) { if (h != null) h.Dispose(); log("Candidate error: " + ex.Message); }
            }

            candidates = candidates.OrderByDescending(x => x.Item1.ToLowerInvariant().Contains("mi_01") ? 1 : 0).ToList();
            foreach (var c in candidates)
            {
                M693Device d = null;
                try
                {
                    var h = HidInterop.Open(c.Item1, true);
                    if (h == null || h.IsInvalid) { if (h != null) h.Dispose(); continue; }
                    d = new M693Device(c.Item1, h, c.Item3, c.Item2, log);
                    byte[] probe = d.ReadBytes(0, 2);
                    if (probe.Length == 2 && probe[1] == unchecked((byte)(0x55 - probe[0])))
                    {
                        log("Command interface підтверджено EEPROM probe: " + probe[0].ToString("X2") + " " + probe[1].ToString("X2"));
                        return d;
                    }
                    log("Не схоже на command interface: EEPROM pair invalid.");
                    d.Dispose();
                }
                catch (Exception ex) { log("Probe failed: " + ex.Message); if (d != null) d.Dispose(); }
            }
            return null;
        }

        public byte[] QuerySimple(byte cmd, int timeoutMs)
        {
            byte[] req = Protocol.BuildSimple(cmd);
            SendFeature(req);
            return WaitResponse(cmd, 0, timeoutMs);
        }

        public BatteryInfo GetBatteryInfo()
        {
            byte[] r = QuerySimple(Protocol.CmdBattery, 900);
            int percent = r.Length > 6 ? r[6] : 0;
            bool charging = r.Length > 7 && r[7] != 0;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            return new BatteryInfo { Percent = percent, Charging = charging };
        }

        public bool GetLinkStatus()
        {
            byte[] r = QuerySimple(Protocol.CmdConnectStatus, 900);
            return r.Length > 6 && r[6] != 0;
        }

        public int GetCurrentProfileIndex()
        {
            byte[] r = QuerySimple(Protocol.CmdCurrentProfile, 900);
            return r.Length > 6 ? r[6] : 0;
        }

        public void FactoryReset()
        {
            byte[] req = Protocol.BuildSimple(Protocol.CmdFactoryReset);
            SendFeature(req);
            WaitResponse(Protocol.CmdFactoryReset, 0, 1800);
        }

        public byte[] ReadBytes(ushort addr, int length)
        {
            var outb = new byte[length];
            int pos = 0;
            while (pos < length)
            {
                int len = Math.Min(10, length - pos);
                byte[] req = Protocol.BuildRead((ushort)(addr + pos), (byte)len);
                SendFeature(req);
                byte[] resp = WaitResponse(Protocol.CmdRead, (ushort)(addr + pos), 1200);
                if (resp[2] != 0) throw new IOException("READ status=" + resp[2]);
                int got = Math.Min(len, resp[5]);
                if (got < len) throw new IOException("READ short response " + got + "/" + len);
                Array.Copy(resp, 6, outb, pos, len);
                pos += len;
            }
            return outb;
        }

        public void WriteBytes(ushort addr, byte[] data)
        {
            int pos = 0;
            while (pos < data.Length)
            {
                int len = Math.Min(10, data.Length - pos);
                byte[] chunk = new byte[len]; Array.Copy(data, pos, chunk, 0, len);
                byte[] req = Protocol.BuildWrite((ushort)(addr + pos), chunk);
                SendFeature(req);
                byte[] resp = WaitResponse(Protocol.CmdWrite, (ushort)(addr + pos), 1500);
                if (resp[2] != 0) throw new IOException("WRITE status=" + resp[2] + " at 0x" + (addr + pos).ToString("X4"));
                pos += len;
            }
        }

        private void SendFeature(byte[] p)
        {
            if (IsClosed) throw new ObjectDisposedException("M693Device");
            if (_log != null) _log("TX " + string.Join(" ", p.Select(x => x.ToString("X2")).ToArray()));
            if (!HidInterop.HidD_SetFeature(_handle, p, p.Length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_SetFeature");
        }

        private byte[] WaitResponse(byte cmd, ushort addr, int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                int remain = Math.Max(50, (int)(end - DateTime.UtcNow).TotalMilliseconds);
                byte[] r = ReadReport(remain);
                if (r == null) break;
                if (_log != null) _log("RX " + string.Join(" ", r.Take(17).Select(x => x.ToString("X2")).ToArray()));
                if (r.Length >= 17 && r[0] == Protocol.ResponseId && Protocol.Verify(r.Take(17).ToArray()) && r[1] == cmd)
                {
                    ushort ra = (ushort)((r[3] << 8) | r[4]);
                    if (ra == addr) return r;
                }
            }
            throw new TimeoutException("Немає response report 0x09 для cmd=0x" + cmd.ToString("X2") + " addr=0x" + addr.ToString("X4") + ". Перевірте, чи OemDrv.exe закрита.");
        }

        private byte[] ReadReport(int timeoutMs)
        {
            byte[] b = new byte[_inputReportLen];
            IAsyncResult ar = null;
            try
            {
                ar = _stream.BeginRead(b, 0, b.Length, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    HidInterop.CancelIoEx(_handle, IntPtr.Zero);
                    try { _stream.EndRead(ar); } catch { }
                    return null;
                }
                int n = _stream.EndRead(ar);
                if (n <= 0) return null;
                if (n == b.Length) return b;
                byte[] x = new byte[n]; Array.Copy(b, x, n); return x;
            }
            finally { if (ar != null) try { ar.AsyncWaitHandle.Close(); } catch { } }
        }

        public void Dispose()
        {
            try { if (_stream != null) _stream.Dispose(); } catch { }
            _stream = null;
            try { if (_handle != null && !_handle.IsClosed) _handle.Dispose(); } catch { }
            _handle = null;
        }
    }

    internal static class Protocol
    {
        public const byte ReportId = 0x08;
        public const byte ResponseId = 0x09;
        public const byte CmdConnectStatus = 0x03;
        public const byte CmdBattery = 0x04;
        public const byte CmdWrite = 0x07;
        public const byte CmdRead = 0x08;
        public const byte CmdFactoryReset = 0x09;
        public const byte CmdCurrentProfile = 0x0E;

        public static byte[] BuildSimple(byte cmd) { return NewPacket(cmd, 0, new byte[0]); }

        public static byte[] BuildRead(ushort addr, byte len)
        {
            byte[] p = NewPacket(CmdRead, addr, new byte[0]);
            p[5] = len;
            p[16] = M693Profile.Checksum55(p.Take(16).ToArray());
            return p;
        }

        public static byte[] BuildWrite(ushort addr, byte[] data) { return NewPacket(CmdWrite, addr, data); }

        private static byte[] NewPacket(byte cmd, ushort addr, byte[] data)
        {
            if (data.Length > 10) throw new ArgumentException("max 10 bytes");
            byte[] p = new byte[17];
            p[0] = ReportId; p[1] = cmd; p[2] = 0; p[3] = (byte)(addr >> 8); p[4] = (byte)addr; p[5] = (byte)data.Length;
            Array.Copy(data, 0, p, 6, data.Length);
            p[16] = M693Profile.Checksum55(p.Take(16).ToArray());
            return p;
        }

        public static bool Verify(byte[] p)
        {
            return p != null && p.Length >= 17 && p[16] == M693Profile.Checksum55(p.Take(16).ToArray());
        }
    }

    internal static class HidInterop
    {
        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid HidGuid);
        [DllImport("hid.dll", SetLastError = true)] internal static extern bool HidD_SetFeature(SafeFileHandle HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr PreparsedData, ref HIDP_CAPS Capabilities);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

        public static List<string> EnumerateHidPaths()
        {
            Guid g; HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var list = new List<string>();
            try
            {
                uint i = 0;
                while (true)
                {
                    var di = new SP_DEVICE_INTERFACE_DATA(); di.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref di))
                    {
                        int err = Marshal.GetLastWin32Error(); if (err == 259) break; throw new Win32Exception(err);
                    }
                    uint req;
                    SetupDiGetDeviceInterfaceDetail(set, ref di, IntPtr.Zero, 0, out req, IntPtr.Zero);
                    IntPtr detail = Marshal.AllocHGlobal((int)req);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref di, detail, req, out req, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (!string.IsNullOrEmpty(path)) list.Add(path);
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                    i++;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        public static SafeFileHandle Open(string path, bool overlapped)
        {
            return CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, overlapped ? FILE_FLAG_OVERLAPPED : 0, IntPtr.Zero);
        }

        public static bool GetAttributes(SafeFileHandle h, out HIDD_ATTRIBUTES a)
        {
            a = new HIDD_ATTRIBUTES(); a.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)); return HidD_GetAttributes(h, ref a);
        }

        public static bool GetCaps(SafeFileHandle h, out HIDP_CAPS caps)
        {
            caps = new HIDP_CAPS(); caps.Reserved = new ushort[17];
            IntPtr p;
            if (!HidD_GetPreparsedData(h, out p)) return false;
            try { return HidP_GetCaps(p, ref caps) >= 0; }
            finally { HidD_FreePreparsedData(p); }
        }
    }

    internal sealed class WindowsPointerConfig
    {
        public int Speed;
        public int DoubleClickMs;
        public int WheelLines;
        public bool EnhancePrecision;
    }

    internal static class WindowsMouseSettings
    {
        private const uint SPI_GETMOUSE = 0x0003;
        private const uint SPI_SETMOUSE = 0x0004;
        private const uint SPI_GETWHEELSCROLLLINES = 0x0068;
        private const uint SPI_SETWHEELSCROLLLINES = 0x0069;
        private const uint SPI_GETMOUSESPEED = 0x0070;
        private const uint SPI_SETMOUSESPEED = 0x0071;
        private const uint SPIF_UPDATEINIFILE = 0x01;
        private const uint SPIF_SENDCHANGE = 0x02;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();
        [DllImport("user32.dll")]
        private static extern bool SetDoubleClickTime(uint ms);

        public static WindowsPointerConfig Read()
        {
            int speed = 10;
            int wheel = 3;
            int[] mouse = new int[3];
            if (!SystemParametersInfo(SPI_GETMOUSESPEED, 0, ref speed, 0)) speed = 10;
            if (!SystemParametersInfo(SPI_GETWHEELSCROLLLINES, 0, ref wheel, 0)) wheel = 3;
            bool enhance = false;
            if (SystemParametersInfo(SPI_GETMOUSE, 0, mouse, 0)) enhance = mouse[2] != 0;
            return new WindowsPointerConfig
            {
                Speed = speed,
                DoubleClickMs = (int)GetDoubleClickTime(),
                WheelLines = wheel,
                EnhancePrecision = enhance
            };
        }

        public static void Apply(WindowsPointerConfig c)
        {
            int speed = Math.Max(1, Math.Min(20, c.Speed));
            uint flags = SPIF_UPDATEINIFILE | SPIF_SENDCHANGE;
            if (!SystemParametersInfo(SPI_SETMOUSESPEED, 0, ref speed, flags)) throw new Win32Exception(Marshal.GetLastWin32Error(), "SPI_SETMOUSESPEED");
            if (!SetDoubleClickTime((uint)Math.Max(100, Math.Min(1200, c.DoubleClickMs)))) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetDoubleClickTime");
            uint wheel = (uint)Math.Max(1, Math.Min(100, c.WheelLines));
            if (!SystemParametersInfo(SPI_SETWHEELSCROLLLINES, wheel, IntPtr.Zero, flags)) throw new Win32Exception(Marshal.GetLastWin32Error(), "SPI_SETWHEELSCROLLLINES");
            int[] mouse = c.EnhancePrecision ? new int[] { 6, 10, 1 } : new int[] { 0, 0, 0 };
            if (!SystemParametersInfo(SPI_SETMOUSE, 0, mouse, flags)) throw new Win32Exception(Marshal.GetLastWin32Error(), "SPI_SETMOUSE");
        }
    }

    internal static class UiTheme
    {
        private static readonly Color Back = Color.FromArgb(24, 24, 26);
        private static readonly Color Panel = Color.FromArgb(31, 31, 34);
        private static readonly Color Panel2 = Color.FromArgb(39, 39, 43);
        private static readonly Color Text = Color.FromArgb(235, 235, 238);
        private static readonly Color Muted = Color.FromArgb(185, 185, 190);
        private static readonly Color Accent = Color.FromArgb(218, 45, 45);

        public static void Apply(Control root)
        {
            root.BackColor = Back;
            root.ForeColor = Text;
            ApplyRecursive(root);
        }

        private static void ApplyRecursive(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is TabControl)
                {
                    c.BackColor = Back;
                    c.ForeColor = Text;
                }
                else if (c is TabPage || c is Panel || c is SplitContainer)
                {
                    c.BackColor = Panel;
                    c.ForeColor = Text;
                }
                else if (c is GroupBox)
                {
                    c.BackColor = Panel;
                    c.ForeColor = Text;
                }
                else if (c is Button)
                {
                    Button b = (Button)c;
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Accent;
                    b.FlatAppearance.BorderSize = 1;
                    b.BackColor = Panel2;
                    b.ForeColor = Color.White;
                }
                else if (c is TextBox)
                {
                    c.BackColor = Color.FromArgb(19, 19, 21);
                    c.ForeColor = Text;
                }
                else if (c is ComboBox || c is NumericUpDown || c is ListBox)
                {
                    c.BackColor = Color.FromArgb(26, 26, 29);
                    c.ForeColor = Text;
                }
                else if (c is DataGridView)
                {
                    DataGridView g = (DataGridView)c;
                    g.BackgroundColor = Color.FromArgb(20, 20, 22);
                    g.GridColor = Color.FromArgb(65, 65, 70);
                    g.DefaultCellStyle.BackColor = Color.FromArgb(28, 28, 31);
                    g.DefaultCellStyle.ForeColor = Text;
                    g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(115, 31, 31);
                    g.DefaultCellStyle.SelectionForeColor = Color.White;
                    g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(45, 45, 49);
                    g.ColumnHeadersDefaultCellStyle.ForeColor = Text;
                    g.EnableHeadersVisualStyles = false;
                }
                else if (c is Label || c is CheckBox)
                {
                    c.ForeColor = c.ForeColor == Color.FromArgb(238, 56, 56) ? c.ForeColor : Muted;
                    c.BackColor = Color.Transparent;
                }
                ApplyRecursive(c);
            }
        }
    }

    internal sealed class CaptureResult
    {
        public int Left, Right, Middle, Back, Forward, Keyboard;
        public string Dominant = "other";
        public bool IsBurst;
        public string Summary;
    }

    internal sealed class CaptureEngine : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private IntPtr _mh, _kh;
        private HookProc _mproc, _kproc;
        private System.Windows.Forms.Timer _timer;
        private int left, right, middle, back, forward, keyboard;
        public event EventHandler<CaptureResult> Completed;

        public void Start(int ms)
        {
            _mproc = MouseHook; _kproc = KeyHook;
            _mh = SetWindowsHookEx(WH_MOUSE_LL, _mproc, GetModuleHandle(null), 0);
            _kh = SetWindowsHookEx(WH_KEYBOARD_LL, _kproc, GetModuleHandle(null), 0);
            _timer = new System.Windows.Forms.Timer(); _timer.Interval = ms; _timer.Tick += delegate { Finish(); }; _timer.Start();
        }

        private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int m = wParam.ToInt32();
                if (m == WM_LBUTTONDOWN) left++;
                else if (m == WM_RBUTTONDOWN) right++;
                else if (m == WM_MBUTTONDOWN) middle++;
                else if (m == WM_XBUTTONDOWN)
                {
                    MSLLHOOKSTRUCT s = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    int x = (int)((s.mouseData >> 16) & 0xFFFF);
                    if (x == 1) back++; else if (x == 2) forward++;
                }
            }
            return CallNextHookEx(_mh, code, wParam, lParam);
        }

        private IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && (wParam.ToInt32() == WM_KEYDOWN || wParam.ToInt32() == WM_SYSKEYDOWN)) keyboard++;
            return CallNextHookEx(_kh, code, wParam, lParam);
        }

        private void Finish()
        {
            if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
            DisposeHooks();
            var r = new CaptureResult { Left = left, Right = right, Middle = middle, Back = back, Forward = forward, Keyboard = keyboard };
            int[] vals = { left, right, middle, back, forward, keyboard };
            string[] names = { "left", "right", "middle", "back", "forward", "keyboard" };
            int best = 0; for (int i = 1; i < vals.Length; i++) if (vals[i] > vals[best]) best = i;
            r.Dominant = vals[best] > 0 ? names[best] : "other";
            r.IsBurst = vals[best] >= 3;
            r.Summary = string.Format("Captured: L={0}, R={1}, M={2}, Back={3}, Fwd={4}, Key={5}; dominant={6}", left, right, middle, back, forward, keyboard, r.Dominant);
            var h = Completed; if (h != null) h(this, r);
        }

        public void Dispose() { if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; } DisposeHooks(); }
        private void DisposeHooks() { if (_mh != IntPtr.Zero) { UnhookWindowsHookEx(_mh); _mh = IntPtr.Zero; } if (_kh != IntPtr.Zero) { UnhookWindowsHookEx(_kh); _kh = IntPtr.Zero; } }

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
