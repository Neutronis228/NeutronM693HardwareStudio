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
        internal static string StartupPhase = "Program.Main";
        // CLI smoke test: --smoke-test initializes every WinForms page without touching HID.
        // A failure must result in non-zero exit so GitHub Actions can catch startup crashes.
        [STAThread]
        private static int Main(string[] args)
        {
            bool smoke = args != null && Array.Exists(args,
                delegate(string a) { return string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase); });
            bool self = args != null && Array.Exists(args,
                delegate(string a) { return string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase); });
            try
            {
                if (self) { RunProtocolSelfTest(); return 0; }
                StartupPhase = "WinForms initialization";
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                StartupPhase = "MainForm constructor";
                bool smokeFailed = false;
                using (MainForm form = new MainForm(smoke))
                {
                    if (smoke)
                    {
                        // Exercise all pages after handles are created. No HID access, no EEPROM writes.
                        form.Shown += delegate
                        {
                            form.BeginInvoke((MethodInvoker)delegate
                            {
                                try
                                {
                                    StartupPhase = "Smoke test: exercise all pages";
                                    form.RunSmokeTest();
                                }
                                catch (Exception ex)
                                {
                                    smokeFailed = true;
                                    LogStartupFailure(ex);
                                }
                                finally { form.Close(); }
                            });
                        };
                    }
                    StartupPhase = "Application.Run";
                    Application.Run(form);
                }
                return smokeFailed ? 3 : 0;
            }
            catch (Exception ex)
            {
                LogStartupFailure(ex);
                if (!smoke)
                {
                    try { MessageBox.Show("Neutron M693 не зміг запуститись.\n" +
                        "Діагностика: %LOCALAPPDATA%\\NeutronM693HardwareStudio\\STARTUP_ERROR.log\n\n" +
                        ex.Message, "Neutron M693 — startup error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    catch { }
                }
                return 2;
            }
        }

        // Protocol/model tests need neither WinForms nor the physical mouse.
        private static void RunProtocolSelfTest()
        {
            int[] d = SmoothPatternGenerator.Generate(20, 10, 45, 150, 88, 2601);
            if (d.Length != 20 || d.Any(ms => ms < 10 || ms > 150))
                throw new InvalidOperationException("Smooth interval range test failed");
            byte[] slot = MacroBuilder.BuildFullSlot(4, "NEUTRON_B4_L", 1, d,
                new byte[] { 0x84, 0x01, 0x00 }, new byte[] { 0x44, 0x01, 0x00 });
            ParsedMacro parsed = ParsedMacro.Parse(4, slot);
            if (!parsed.ChecksumOk || parsed.EventCount != 40 || parsed.Name != "NEUTRON_B4_L")
                throw new InvalidOperationException("Onboard macro codec round-trip failed");
            if (2 + parsed.EventCount * 5 > 255)
                throw new InvalidOperationException("Safe macro payload exceeds 255 bytes");
            // Button #4 -> macro slot #4: first event must be at 0x0920.
            // The previous implementation wrote the first event BEFORE writing event count,
            // and firmware returned zero on read-back. Guard the record layout in CI.
            if (M693Profile.MacroBase + 4 * M693Profile.MacroSlotSize + 32 != 0x0920 ||
                slot[31] != 40 || slot[32] != 0x84 || slot[33] != 0x01)
                throw new InvalidOperationException("Macro slot 4 event header/layout regression");
            byte[] eventBlock = new byte[2 + slot[31] * 5];
            Array.Copy(slot, 31, eventBlock, 0, eventBlock.Length);
            byte expectedChecksum = M693Profile.Checksum55(eventBlock.Take(eventBlock.Length - 1).ToArray());
            if (expectedChecksum != eventBlock[eventBlock.Length - 1])
                throw new InvalidOperationException("Macro event checksum before write invalid");
            byte[] button = new byte[] { 0x06, 0x04, 0xFE, M693Profile.Checksum55(new byte[] { 0x06, 0x04, 0xFE }) };
            if (button.Length != 4 || button[3] != M693Profile.Checksum55(new byte[] { button[0], button[1], button[2] }))
                throw new InvalidOperationException("Button map checksum failed");
            Console.WriteLine("PROTOCOL SELF-TEST PASS; events=" + parsed.EventCount);
        }

        private static void LogStartupFailure(Exception ex)
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NeutronM693HardwareStudio");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "STARTUP_ERROR.log"),
                    DateTime.UtcNow.ToString("u") + " | phase=" + StartupPhase + Environment.NewLine +
                    "OS=" + Environment.OSVersion + " | CLR=" + Environment.Version + Environment.NewLine +
                    ex.ToString(), Encoding.UTF8);
            }
            catch { }
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
        private readonly HashSet<int> _explicitMacroWrites = new HashSet<int>();
        private readonly Dictionary<int, byte[]> _comboSlots = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, byte[]> _pendingComboSlots = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, ParsedCombo> _parsedCombos = new Dictionary<int, ParsedCombo>();
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
        private Button btnDiscard;
        private TabControl tabs;
        private TableLayoutPanel mainNav;
        private TableLayoutPanel appShell;
        private int _navColumns;
        private readonly List<Button> mainNavButtons = new List<Button>();
        private Label lblEditState;
        private StatusStrip appStatus;
        private ToolStripStatusLabel stDevice;
        private ToolStripStatusLabel stBattery;
        private ToolStripStatusLabel stProfile;
        private ToolStripStatusLabel stChanges;
        private ToolTip tips;

        private DataGridView gridButtons;
        private DataGridView gridMatrix;
        private ComboBox cmbSimpleAction;
        private ComboBox cmbExistingMacro;
        private ComboBox cmbExistingMacroMode;
        private Button btnAssignSimple;
        private TextBox txtCapture;
        private Button btnCapture;
        private Label lblCapture;

        private ComboBox cmbMacroClick;
        private NumericUpDown numMin;
        private NumericUpDown numTypical;
        private NumericUpDown numMax;
        private NumericUpDown numPatternClicks;
        private ComboBox cmbStopStrategy;
        private Label lblStopBound;
        private TrackBar trkSmooth;
        private Label lblSmooth;
        private Button btnGenerateMacro;
        private TextBox txtPreview;
        private Panel pnlMacroGraph;
        private int[] _lastMacroIntervals = new int[0];

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

        // RGB Palette Lab (saved with Neutron profile metadata; current M693 onboard block
        // exposes one base RGB color + fixed firmware effects, so arbitrary palettes are preview-only)
        private ListBox lstPaletteColors;
        private Panel pnlPalettePreview;
        private ComboBox cmbPaletteEffect;
        private NumericUpDown numPalettePeriod;
        private Button btnPaletteAdd;
        private Button btnPaletteRemove;
        private Button btnPaletteUseFirst;
        private readonly List<Color> _paletteColors = new List<Color>();
        private ComboBox cmbHardwareStyle;
        private TextBox txtHardwareStyleName;
        private readonly List<HardwareLightingStyle> _hardwareStyles = new List<HardwareLightingStyle>();
        private bool _loadingHardwareStyles;
        private static readonly string HardwareStylesFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NeutronM693HardwareStudio", "hardware_rgb_styles.json");

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
        private BatteryMeter prgBattery;
        private PictureBox picMouse;
        private TextBox txtDashSummary;
        private System.Windows.Forms.Timer batteryTimer;
        private System.Windows.Forms.Timer discoveryTimer;
        private CheckBox chkAutoConnect;
        private bool _connectBusy;
        private ListBox lstProfileLibrary;
        private TextBox txtProfileName;
        private Label lblProfileInfo;

        // Extended button actions
        private NumericUpDown numFireInterval;
        private NumericUpDown numFireRepeat;
        private ComboBox cmbKeyModifier1;
        private ComboBox cmbKeyModifier2;
        private ComboBox cmbKeyMain;
        private ComboBox cmbMediaAction;
        private ComboBox cmbDpiLockAction;

        // Full onboard Macro Studio
        private DataGridView gridMacroStudioSlots;
        private DataGridView gridMacroStudioEvents;
        private NumericUpDown numMacroStudioSlot;
        private TextBox txtMacroStudioName;
        private ComboBox cmbMacroStudioMode;
        private NumericUpDown numMacroStudioRepeat;
        private ComboBox cmbMacroEventType;
        private ComboBox cmbMacroEventValue;
        private ComboBox cmbMacroEventState;
        private NumericUpDown numMacroEventDelay;
        private int _macroStudioLoadedSlot = -1;
        private Label lblMacroStudioInfo;

        // Onboard profile switching (JM03 cmd 0x0F, paired with existing read cmd 0x0E)
        private ComboBox cmbOnboardProfile;
        private Label lblOnboardProfileState;

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

        public MainForm(bool smokeTest = false)
        {
            Text = "Neutron M693 Hardware Studio 2.6.7 QUICK STOP RC1";
            Width = 1430;
            Height = 880;
            MinimumSize = new Size(930, 620);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
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
            if (!smokeTest) batteryTimer.Start();

            discoveryTimer = new System.Windows.Forms.Timer();
            discoveryTimer.Interval = 2500;
            discoveryTimer.Tick += delegate { AutoDiscoveryTick(false); };
            if (!smokeTest)
            {
                discoveryTimer.Start();
                Shown += delegate { BeginInvoke((MethodInvoker)delegate { AutoDiscoveryTick(true); }); };
            }

            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (HasPendingChanges())
                {
                    DialogResult q = MessageBox.Show(this,
                        "Є незаписані зміни в редакторі. Закрити програму й втратити їх?",
                        "Neutron M693", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (q != DialogResult.Yes) { e.Cancel = true; return; }
                }
                if (batteryTimer != null) { batteryTimer.Stop(); batteryTimer.Dispose(); batteryTimer = null; }
                if (discoveryTimer != null) { discoveryTimer.Stop(); discoveryTimer.Dispose(); discoveryTimer = null; }
                SafeDisposeDevice();
                if (_capture != null) _capture.Dispose();
            };
            KeyDown += MainForm_KeyDown;
        }

        // Avoid setting SplitterDistance and PanelXMinSize while a SplitContainer
        // still has its small design-time default width (startup regression in 2.3–2.5).
        private static void ConfigureSplitAfterLayout(SplitContainer split, int preferredPixels, double ratio)
        {
            EventHandler fit = delegate
            {
                try
                {
                    if (split.IsDisposed || split.Orientation != Orientation.Vertical) return;
                    int usable = split.ClientSize.Width - split.SplitterWidth;
                    if (usable < 180) return;
                    int lower = Math.Max(split.Panel1MinSize, 70);
                    int upper = Math.Min(usable - Math.Max(split.Panel2MinSize, 70), usable - 70);
                    if (upper < lower) return;
                    int ideal = Math.Min(preferredPixels, (int)Math.Round(usable * ratio));
                    int target = Math.Max(lower, Math.Min(upper, ideal));
                    if (target != split.SplitterDistance) split.SplitterDistance = target;
                }
                catch (ArgumentException) { /* layout can briefly be too small */ }
                catch (InvalidOperationException) { /* layout can briefly be too small */ }
            };
            split.SizeChanged += fit;
            split.HandleCreated += fit;
        }

        public void RunSmokeTest()
        {
            if (tabs == null || tabs.TabPages.Count < 10)
                throw new InvalidOperationException("Tabs were not initialized.");
            if (btnConnect == null || btnApply == null || mainNav == null)
                throw new InvalidOperationException("Main controls were not initialized.");
            int oldIndex = tabs.SelectedIndex;
            Size oldSize = Size;
            foreach (Size viewport in new Size[] { new Size(980, 640), new Size(1280, 720), new Size(1600, 900) })
            {
                Program.StartupPhase = "Smoke viewport: " + viewport.Width + "x" + viewport.Height;
                Size = viewport;
                PerformLayout();
                Application.DoEvents();
                if (appShell == null || appShell.GetControlFromPosition(0, 2) != tabs)
                    throw new InvalidOperationException("Tab content not anchored below the header");
                if (appShell.GetControlFromPosition(0, 0).Bottom > tabs.Top - 30)
                    throw new InvalidOperationException("Header overlaps page viewport");
                foreach (Button b in mainNavButtons)
                    if (b.Right > mainNav.ClientSize.Width + 3 || b.Bottom > mainNav.ClientSize.Height + 3)
                        throw new InvalidOperationException("Navigation clipped: " + b.Text);
                for (int i = 0; i < tabs.TabPages.Count; i++)
                {
                    Program.StartupPhase = "Smoke page " + i + " at " + viewport.Width + "x" + viewport.Height;
                    tabs.SelectedIndex = i;
                    tabs.TabPages[i].PerformLayout();
                    Application.DoEvents();
                    foreach (Control c in tabs.TabPages[i].Controls)
                        if (c is SplitContainer && c.Width > 300)
                        {
                            SplitContainer sc = (SplitContainer)c;
                            int range = sc.Orientation == Orientation.Vertical ? sc.ClientSize.Width : sc.ClientSize.Height;
                            if (sc.SplitterDistance <= 0 || sc.SplitterDistance >= range)
                                throw new InvalidOperationException("Invalid split sizing: " + tabs.TabPages[i].Text);
                        }
                }
            }
            tabs.SelectedIndex = oldIndex >= 0 ? oldIndex : 0;
            Size = oldSize;
            Program.StartupPhase = "Smoke test OK";
        }

        private void BuildUi()
        {
            DoubleBuffered = true;
            tips = new ToolTip { AutoPopDelay = 9000, InitialDelay = 500, ReshowDelay = 150, ShowAlways = true };

            Program.StartupPhase = "BuildUi: header";
            var top = new Panel { Dock = DockStyle.Top, Height = 157, Padding = new Padding(18, 10, 18, 8) };
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52F));
            top.Controls.Add(header);

            var identity = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            header.Controls.Add(identity, 0, 0);
            var brand = new Label
            {
                Left = 4, Top = 4, Width = 470, Height = 30,
                Text = "NEUTRON M693 HARDWARE STUDIO",
                Font = new Font("Segoe UI Semibold", 14F, FontStyle.Bold),
                ForeColor = Color.White
            };
            identity.Controls.Add(brand);
            lblStatus = new Label { Left = 4, Top = 38, Width = 520, Height = 23, Text = "●  Очікування M693...", Font = new Font("Segoe UI Semibold", 9.5F) };
            lblOfficial = new Label { Left = 4, Top = 63, Width = 560, Height = 21 };
            lblDevice = new Label { Left = 4, Top = 85, Width = 610, Height = 20, AutoEllipsis = true };
            lblEditState = new Label { Left = 4, Top = 106, Width = 610, Height = 24, Text = "●  Змін немає", Font = new Font("Segoe UI Semibold", 9F) };
            identity.Controls.AddRange(new Control[] { lblStatus, lblOfficial, lblDevice, lblEditState });
            identity.Resize += delegate {
                int max = Math.Max(180, identity.ClientSize.Width - 12);
                brand.Width = max; lblStatus.Width = max; lblOfficial.Width = max;
                lblDevice.Width = max; lblEditState.Width = max;
            };
            brand.AutoEllipsis = true; lblStatus.AutoEllipsis = true;
            lblOfficial.AutoEllipsis = true; lblEditState.AutoEllipsis = true;

            var commandArea = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            header.Controls.Add(commandArea, 1, 0);
            chkAutoConnect = new CheckBox
            {
                Text = "Автопошук M693", Checked = true, AutoSize = true,
                Left = 8, Top = 6, Height = 24, Font = new Font("Segoe UI Semibold", 9F)
            };
            commandArea.Controls.Add(chkAutoConnect);

            var actions = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom, Height = 96, ColumnCount = 4, RowCount = 2, BackColor = Color.Transparent, Padding = new Padding(0), Margin = new Padding(0)
            };
            for (int i = 0; i < 4; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            commandArea.Controls.Add(actions);

            btnConnect = MakeButton("↻  Пересканувати", 0, 0, 160, 36, delegate { ConnectDevice(); });
            btnRead = MakeButton("↧  Прочитати", 0, 0, 160, 36, delegate { ReadFromMouse(); });
            btnApply = MakeButton("ЗАСТОСУВАТИ", 0, 0, 160, 36, delegate { ApplyToMouse(); });
            btnApply.Tag = "primary";
            btnDiscard = MakeButton("↶  Скасувати зміни", 0, 0, 160, 36, delegate { DiscardPendingChanges(); });
            btnExport = MakeButton("Експорт", 0, 0, 160, 34, delegate { ExportProfile(); });
            btnImport = MakeButton("Імпорт", 0, 0, 160, 34, delegate { ImportProfile(); });
            btnRawBackup = MakeButton("RAW backup", 0, 0, 160, 34, delegate { RawBackup(); });
            var btnProfilesQuick = MakeButton("Бібліотека профілів", 0, 0, 160, 34, delegate { if (tabs != null && tabs.TabPages.ContainsKey("ProfilesPage")) tabs.SelectedTab = tabs.TabPages["ProfilesPage"]; });
            foreach (Control c in new Control[] { btnConnect, btnRead, btnApply, btnDiscard, btnExport, btnImport, btnRawBackup, btnProfilesQuick })
            {
                c.Dock = DockStyle.Fill; c.Margin = new Padding(5);
            }
            actions.Controls.Add(btnConnect, 0, 0);
            actions.Controls.Add(btnRead, 1, 0);
            actions.Controls.Add(btnApply, 2, 0);
            actions.Controls.Add(btnDiscard, 3, 0);
            actions.Controls.Add(btnExport, 0, 1);
            actions.Controls.Add(btnImport, 1, 1);
            actions.Controls.Add(btnRawBackup, 2, 1);
            actions.Controls.Add(btnProfilesQuick, 3, 1);

            tips.SetToolTip(btnConnect, "Ручний пошук M693. Автопошук працює паралельно у фоні.");
            tips.SetToolTip(btnRead, "Повторно прочитати onboard-профіль з миші. Незбережені зміни редактора буде скасовано.");
            tips.SetToolTip(btnApply, "Записати тільки підготовлені зміни в onboard memory, зробити backup і read-back перевірку.");
            tips.SetToolTip(btnDiscard, "Відкинути локальні зміни й повернути редактор до останнього прочитаного стану.");

            mainNav = new TableLayoutPanel
            {
                Dock = DockStyle.Top, Height = 48, ColumnCount = 11, RowCount = 1,
                Padding = new Padding(8, 5, 8, 5), Margin = new Padding(0),
                BackColor = Color.FromArgb(20, 20, 23)
            };
            for (int navCol = 0; navCol < 11; navCol++)
                ((TableLayoutPanel)mainNav).ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 11F));
            ((TableLayoutPanel)mainNav).RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            tabs = new TabControl
            {
                Dock = DockStyle.Fill, Appearance = TabAppearance.FlatButtons, SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(1, 1), Padding = new Point(0, 0), Font = new Font("Segoe UI Semibold", 9F)
            };
            tabs.SelectedIndexChanged += delegate { RefreshMainNavState(); };

            appStatus = new StatusStrip
            {
                Dock = DockStyle.Bottom, SizingGrip = false, BackColor = Color.FromArgb(19, 19, 21), ForeColor = Color.FromArgb(205, 205, 210)
            };
            stDevice = new ToolStripStatusLabel("M693: очікування") { Spring = false };
            stBattery = new ToolStripStatusLabel("Battery: —") { Spring = false };
            stProfile = new ToolStripStatusLabel("Profile: —") { Spring = false };
            stChanges = new ToolStripStatusLabel("Змін немає") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
            appStatus.Items.Add(stDevice);
            appStatus.Items.Add(new ToolStripSeparator());
            appStatus.Items.Add(stBattery);
            appStatus.Items.Add(new ToolStripSeparator());
            appStatus.Items.Add(stProfile);
            appStatus.Items.Add(stChanges);

            // Explicit four-row shell: the tab viewport NEVER occupies the header/navigation rows.
            // WinForms Dock=Fill + BringToFront used to cover the first 150px of every page.
            appShell = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
                Margin = Padding.Empty, Padding = Padding.Empty,
                BackColor = Color.FromArgb(24, 24, 26)
            };
            appShell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            appShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 157F));
            appShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            appShell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            appShell.RowStyles.Add(new RowStyle(SizeType.Absolute, 25F));
            top.Dock = DockStyle.Fill;
            mainNav.Dock = DockStyle.Fill;
            tabs.Dock = DockStyle.Fill;
            appStatus.Dock = DockStyle.Fill;
            top.Margin = mainNav.Margin = tabs.Margin = appStatus.Margin = Padding.Empty;
            appShell.Controls.Add(top, 0, 0);
            appShell.Controls.Add(mainNav, 0, 1);
            appShell.Controls.Add(tabs, 0, 2);
            appShell.Controls.Add(appStatus, 0, 3);
            Controls.Add(appShell);
            SizeChanged += delegate { ArrangeMainNavigation(); };

            Program.StartupPhase = "BuildDashboardTab";
            BuildDashboardTab();
            Program.StartupPhase = "BuildButtonsTab";
            BuildButtonsTab();
            Program.StartupPhase = "BuildMacroStudioTab";
            BuildMacroStudioTab();
            Program.StartupPhase = "BuildDpiTab";
            BuildDpiTab();
            Program.StartupPhase = "BuildLightingTab";
            BuildLightingTab();
            Program.StartupPhase = "BuildSensorTab";
            BuildSensorTab();
            Program.StartupPhase = "BuildProfilesTab";
            BuildProfilesTab();
            Program.StartupPhase = "BuildWindowsTab";
            BuildWindowsTab();
            Program.StartupPhase = "BuildAdvancedTab";
            BuildAdvancedTab();
            Program.StartupPhase = "BuildCapabilitiesTab";
            BuildCapabilitiesTab();
            Program.StartupPhase = "BuildLogTab";
            BuildLogTab();
            Program.StartupPhase = "BuildMainNavigation";
            BuildMainNavigation();

            Program.StartupPhase = "Finish UI / theme";
            SetEditorsEnabled(false);
            UiTheme.Apply(this);
            btnApply.BackColor = Color.FromArgb(191, 38, 38);
            btnApply.FlatAppearance.BorderColor = Color.FromArgb(244, 73, 73);
            btnApply.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
            btnDiscard.FlatAppearance.BorderColor = Color.FromArgb(105, 105, 112);
            RefreshProfileLibrary();
            RefreshMainNavState();

            cmbPolling.SelectedIndexChanged += delegate { if (!_loadingUi) { _dirtySensor = true; RefreshEditState(); } };
            numDebounce.ValueChanged += delegate { if (!_loadingUi) { _dirtySensor = true; RefreshEditState(); } };
            cmbLod.SelectedIndexChanged += delegate { if (!_loadingUi) { _dirtySensor = true; RefreshEditState(); } };
            numSleep.ValueChanged += delegate { if (!_loadingUi) { _dirtySensor = true; RefreshEditState(); } };
            chkMotionSync.CheckedChanged += delegate { if (!_loadingUi) { _dirtySensor = true; RefreshEditState(); } };
            chkAngleSnap.CheckedChanged += delegate { if (!_loadingUi) { _dirtySensor = true; RefreshEditState(); } };
            chkRipple.CheckedChanged += delegate { if (!_loadingUi) { _dirtySensor = true; RefreshEditState(); } };
            chkLedOffMoving.CheckedChanged += delegate { if (!_loadingUi) { _dirtySensor = true; RefreshEditState(); } };
            cmbLedMode.SelectedIndexChanged += delegate { if (!_loadingUi) { _dirtyLed = true; RefreshEditState(); } };
            numLedSpeed.ValueChanged += delegate { if (!_loadingUi) { _dirtyLed = true; RefreshEditState(); } };
            numLedBrightness.ValueChanged += delegate { if (!_loadingUi) { _dirtyLed = true; RefreshEditState(); } };
            numDpiCount.ValueChanged += delegate { if (!_loadingUi) { _dirtyDpi = true; RefreshEditState(); } };
            cmbDpiActive.SelectedIndexChanged += delegate { if (!_loadingUi) { _dirtyDpi = true; RefreshEditState(); } };
            for (int di = 0; di < 8; di++)
                cmbDpi[di].SelectedIndexChanged += delegate { if (!_loadingUi) { _dirtyDpi = true; RefreshEditState(); } };

            RefreshEditState();
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.S) { if (btnApply.Enabled) ApplyToMouse(); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.R) { if (btnRead.Enabled) ReadFromMouse(); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.E) { if (btnExport.Enabled) ExportProfile(); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.Z) { DiscardPendingChanges(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.F5) { ConnectDevice(); e.SuppressKeyPress = true; }
        }

        private void BuildMainNavigation()
        {
            if (mainNav == null || tabs == null) return;
            mainNav.Controls.Clear();
            mainNavButtons.Clear();
            for (int i = 0; i < tabs.TabPages.Count; i++)
            {
                int index = i;
                var b = new Button
                {
                    Text = tabs.TabPages[i].Text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
                    Margin = new Padding(2, 0, 2, 0), Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 8.6F),
                    BackColor = Color.FromArgb(29, 29, 33), ForeColor = Color.FromArgb(185, 185, 192), UseVisualStyleBackColor = false
                };
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(47, 47, 53);
                string fullName = tabs.TabPages[i].Text;
                tips.SetToolTip(b, fullName);
                b.Click += delegate { tabs.SelectedIndex = index; RefreshMainNavState(); };
                mainNavButtons.Add(b);
                mainNav.Controls.Add(b, i, 0);
            }
            ArrangeMainNavigation();
            RefreshMainNavState();
        }

        // At normal window widths, put the navigation in two rows rather than
        // clipping the tab names or creating a horizontal Windows scrollbar.
        private void ArrangeMainNavigation()
        {
            if (appShell == null || mainNav == null || mainNavButtons.Count == 0) return;
            int cols = appShell.ClientSize.Width < 1220 ? 6 : 11;
            if (_navColumns == cols) return;
            _navColumns = cols;
            int rows = (mainNavButtons.Count + cols - 1) / cols;
            appShell.SuspendLayout();
            mainNav.SuspendLayout();
            mainNav.Controls.Clear();
            mainNav.ColumnStyles.Clear();
            mainNav.RowStyles.Clear();
            mainNav.ColumnCount = cols;
            mainNav.RowCount = rows;
            for (int i = 0; i < cols; i++)
                mainNav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / cols));
            for (int i = 0; i < rows; i++)
                mainNav.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / rows));
            for (int i = 0; i < mainNavButtons.Count; i++)
                mainNav.Controls.Add(mainNavButtons[i], i % cols, i / cols);
            appShell.RowStyles[1].Height = rows == 1 ? 48 : 78;
            mainNav.ResumeLayout(true);
            appShell.ResumeLayout(true);
            RefreshMainNavState();
        }

        private void RefreshMainNavState()
        {
            for (int i = 0; i < mainNavButtons.Count; i++)
            {
                bool active = tabs != null && i == tabs.SelectedIndex;
                Button b = mainNavButtons[i];
                b.BackColor = active ? Color.FromArgb(84, 29, 31) : Color.FromArgb(29, 29, 33);
                b.ForeColor = active ? Color.White : Color.FromArgb(188, 188, 195);
                b.FlatAppearance.BorderSize = active ? 1 : 0;
                b.FlatAppearance.BorderColor = Color.FromArgb(230, 55, 55);
            }
        }

        private void DrawMainTab(object sender, DrawItemEventArgs e)
        {
            bool selected = e.Index == tabs.SelectedIndex;
            Rectangle r = e.Bounds;
            Color back = selected ? Color.FromArgb(46, 46, 50) : Color.FromArgb(28, 28, 31);
            Color fore = selected ? Color.White : Color.FromArgb(175, 175, 182);
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, r);
            if (selected)
            {
                using (var a = new SolidBrush(Color.FromArgb(224, 48, 48)))
                    e.Graphics.FillRectangle(a, r.Left + 8, r.Bottom - 3, r.Width - 16, 3);
            }
            using (var f = new Font("Segoe UI Semibold", 8.5F))
                TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, f, r, fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void UpdateTabItemWidth()
        {
            if (tabs == null || tabs.TabPages.Count == 0) return;
            int usable = Math.Max(760, tabs.ClientSize.Width - 8);
            int w = Math.Max(82, Math.Min(132, usable / Math.Max(1, tabs.TabPages.Count)));
            if (tabs.ItemSize.Width != w) tabs.ItemSize = new Size(w, 34);
        }

        private static void EnablePageScroll(TabPage page, int minWidth, int minHeight)
        {
            page.AutoScroll = true;
            page.AutoScrollMinSize = new Size(minWidth, minHeight);
        }

        private void BuildDashboardTab()
        {
            var page = new TabPage("Огляд") { Name = "DashboardPage" };
            tabs.TabPages.Add(page);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Vertical
            };
            page.Controls.Add(split);
            ConfigureSplitAfterLayout(split, 350, 0.37);
            split.Panel1.Padding = new Padding(18);
            split.Panel2.Padding = new Padding(18);
            split.Panel1.AutoScroll = true;
            split.Panel2.AutoScroll = true;
            split.Panel2.AutoScrollMinSize = Size.Empty;

            var left = split.Panel1;
            var right = split.Panel2;
            var title = new Label
            {
                Dock = DockStyle.Top, Height = 54, Text = "REDRAGON TRIDENT M693",
                Font = new Font("Segoe UI Semibold", 17F, FontStyle.Bold), ForeColor = Color.FromArgb(238, 56, 56), TextAlign = ContentAlignment.MiddleLeft
            };
            picMouse = new PictureBox
            {
                Dock = DockStyle.Fill, MinimumSize = Size.Empty, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent, Padding = new Padding(8)
            };
            try
            {
                string pth = Path.Combine(Application.StartupPath, "Assets", "redragon_original_mouse_nr.png");
                if (!File.Exists(pth)) pth = Path.Combine(Application.StartupPath, "Assets", "mouse_nr.png");
                if (File.Exists(pth)) { using (var img = Image.FromFile(pth)) picMouse.Image = new Bitmap(img); }
            }
            catch { }
            left.Controls.Add(picMouse);
            left.Controls.Add(title);
            title.BringToFront();

            // Layout containers follow the viewport: no large fixed AutoScrollMinSize or clipped cards.
            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4,
                Margin = new Padding(0), Padding = new Padding(4), BackColor = Color.Transparent
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 134F));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 112F));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
            right.Controls.Add(content);

            var card = new GroupBox { Dock = DockStyle.Fill, Text = "Підключення / onboard state", Margin = new Padding(4) };
            content.Controls.Add(card, 0, 0);
            lblDashDevice = new Label { Left = 16, Top = 28, Width = 560, Height = 28, Text = "Пристрій: не підключено", AutoEllipsis = true, Font = new Font("Segoe UI Semibold", 9.5F) };
            lblDashMode = new Label { Left = 16, Top = 60, Width = 560, Height = 24, Text = "Режим: —" };
            lblDashProfile = new Label { Left = 16, Top = 92, Width = 560, Height = 24, Text = "Onboard profile: —" };
            card.Controls.AddRange(new Control[] { lblDashDevice, lblDashMode, lblDashProfile });
            card.Resize += delegate {
                int w = Math.Max(100, card.ClientSize.Width - 32);
                lblDashDevice.Width = w; lblDashMode.Width = w; lblDashProfile.Width = w;
            };

            var batGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Акумулятор", Margin = new Padding(4) };
            content.Controls.Add(batGroup, 0, 1);
            lblDashBattery = new Label { Left = 16, Top = 25, Width = 160, Height = 25, Text = "Заряд: —", Font = new Font("Segoe UI Semibold", 9.5F) };
            prgBattery = new BatteryMeter { Left = 16, Top = 57, Width = 280, Height = 23, Minimum = 0, Maximum = 100, Value = 0 };
            var btnBat = MakeButton("Оновити заряд", 302, 51, 142, 36, delegate { RefreshBatterySafe(); });
            batGroup.Controls.AddRange(new Control[] { lblDashBattery, prgBattery, btnBat });
            batGroup.Resize += delegate {
                btnBat.Left = Math.Max(140, batGroup.ClientSize.Width - 158);
                prgBattery.Width = Math.Max(80, btnBat.Left - 30);
            };

            var reportGroup = new GroupBox { Dock = DockStyle.Fill, Text = "Поточний onboard-профіль · технічний звіт", Margin = new Padding(4), Padding = new Padding(10, 22, 10, 10) };
            content.Controls.Add(reportGroup, 0, 2);
            txtDashSummary = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.None, WordWrap = true,
                Font = new Font("Consolas", 9F), BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(20, 20, 23), ForeColor = Color.Gainsboro
            };
            reportGroup.Controls.Add(txtDashSummary);
            // Dark page arrows instead of bright native scrollbars. The mouse wheel
            // still scrolls all of the report text, and the full report is copyable.
            var reportScrollButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, Width = 34, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0)
            };
            var reportUp = MakeButton("▲", 0, 0, 30, 33,
                delegate { ScrollReportLines(txtDashSummary, -7); });
            var reportDown = MakeButton("▼", 0, 0, 30, 33,
                delegate { ScrollReportLines(txtDashSummary, 7); });
            reportScrollButtons.Controls.Add(reportUp);
            reportScrollButtons.Controls.Add(reportDown);
            reportGroup.Controls.Add(reportScrollButtons);
            reportScrollButtons.BringToFront();
            txtDashSummary.MouseWheel += delegate(object sender, MouseEventArgs e) {
                ScrollReportLines(txtDashSummary, e.Delta > 0 ? -5 : 5);
            };

            var resetRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(4) };
            resetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 215F));
            resetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            content.Controls.Add(resetRow, 0, 3);
            var btnReset = MakeButton("Factory Reset миші", 0, 0, 210, 36, delegate { FactoryResetMouse(); });
            btnReset.Dock = DockStyle.Fill;
            var resetInfo = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                Text = "Небезпечна дія. Спочатку збережіть RAW backup. Скидання потребує окремого підтвердження." };
            resetRow.Controls.Add(btnReset, 0, 0);
            resetRow.Controls.Add(resetInfo, 1, 0);
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static void ScrollReportLines(TextBox report, int lines)
        {
            if (report == null || report.IsDisposed || !report.IsHandleCreated) return;
            // EM_LINESCROLL, positive = down; no visible native horizontal scrollbar.
            SendMessage(report.Handle, 0x00B6, IntPtr.Zero, new IntPtr(lines));
        }

        private void BuildProfilesTab()
        {
            var page = new TabPage("Профілі") { Name = "ProfilesPage" };
            tabs.TabPages.Add(page);
            EnablePageScroll(page, 980, 760);
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

            var onboard = new GroupBox { Left = 560, Top = 545, Width = 385, Height = 145, Text = "Onboard profile switch — JM03" };
            page.Controls.Add(onboard);
            onboard.Controls.Add(new Label { Left = 15, Top = 30, Width = 130, Text = "Активний профіль:" });
            cmbOnboardProfile = new ComboBox { Left = 145, Top = 26, Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbOnboardProfile.Items.AddRange(new object[] { "1", "2", "3", "4" });
            cmbOnboardProfile.SelectedIndex = 0;
            onboard.Controls.Add(cmbOnboardProfile);
            onboard.Controls.Add(MakeButton("Перемкнути", 245, 25, 120, 31, delegate { SwitchOnboardProfile(); }));
            lblOnboardProfileState = new Label
            {
                Left = 15, Top = 68, Width = 350, Height = 56,
                Text = "Команда 0x0F — парна до вже підтвердженого читання current profile 0x0E. Після перемикання профіль автоматично перечитується."
            };
            onboard.Controls.Add(lblOnboardProfileState);
        }

        private void BuildWindowsTab()
        {
            var page = new TabPage("Windows");
            tabs.TabPages.Add(page);
            EnablePageScroll(page, 900, 560);
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
            var page = new TabPage("RAW");
            tabs.TabPages.Add(page);
            EnablePageScroll(page, 1160, 760);
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
                RefreshEditState();
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
                RefreshMacroStudioSlots();
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
            int debounce = M693Profile.GetPair(_pendingProfile, M693Profile.AddrDebounce, 8);
            int lod = M693Profile.GetPair(_pendingProfile, M693Profile.AddrLod, 1);
            int sleep = M693Profile.GetPair(_pendingProfile, M693Profile.AddrSleep, 60) * 10;

            sb.AppendLine("=== DEVICE / SENSOR ===");
            sb.AppendLine("Polling: " + hz + " Hz");
            sb.AppendLine("Debounce: " + debounce + " ms");
            sb.AppendLine("LOD: " + lod + " mm");
            sb.AppendLine("Sleep: " + sleep + " s");
            sb.AppendLine("Motion Sync: " + (M693Profile.GetPair(_pendingProfile, M693Profile.AddrMotionSync, 0) != 0 ? "ON" : "OFF"));
            sb.AppendLine("Angle Snapping: " + (M693Profile.GetPair(_pendingProfile, M693Profile.AddrAngleSnap, 0) != 0 ? "ON" : "OFF"));
            sb.AppendLine("Ripple Control: " + (M693Profile.GetPair(_pendingProfile, M693Profile.AddrRipple, 0) != 0 ? "ON" : "OFF"));
            sb.AppendLine("LED off while moving: " + (M693Profile.GetPair(_pendingProfile, M693Profile.AddrLedOffMoving, 0) != 0 ? "ON" : "OFF"));
            sb.AppendLine();

            sb.AppendLine("=== DPI ===");
            sb.AppendLine("Stages: " + dpiCount + "   Active: " + (active + 1));
            for (int i = 0; i < 8; i++)
            {
                int off = M693Profile.AddrDpiTable + i * 4;
                byte[] rec = new byte[4];
                Array.Copy(_pendingProfile, off, rec, 0, 4);
                int dpi = M693DpiCodec.Decode(rec);
                int co = M693Profile.AddrDpiColors + i * 4;
                string c = "#" + _pendingProfile[co].ToString("X2") + _pendingProfile[co + 1].ToString("X2") + _pendingProfile[co + 2].ToString("X2");
                sb.AppendLine("DPI " + (i + 1) + ": " + dpi + "   color " + c + (i == active ? "   <ACTIVE>" : ""));
            }
            sb.AppendLine();

            sb.AppendLine("=== BUTTONS ===");
            foreach (var pb in _physicalButtons)
            {
                byte[] br = M693Profile.GetButtonRecord(_pendingProfile, pb.Slot);
                string ba = DescribeAction(br, true);
                ParsedCombo pc;
                if (br[0] == 5 && _parsedCombos.TryGetValue(pb.Slot, out pc)) ba += " «" + pc.Summary + "»";
                sb.AppendLine("#" + pb.Number + "  " + pb.Name.PadRight(22) + " -> " + ba);
            }
            sb.AppendLine();

            sb.AppendLine("=== RGB ===");
            byte[] led = new byte[7];
            Array.Copy(_pendingProfile, M693Profile.AddrLed, led, 0, 7);
            int ledIdx = LedCodec.IndexFromHw(led[0]);
            string ledName = ledIdx >= 0 && ledIdx < 7 ? new[] { "Steady", "Breathing", "Streaming", "Neon", "Scrolling", "Color Breathing", "Off" }[ledIdx] : "Unknown(" + led[0].ToString("X2") + ")";
            sb.AppendLine("Mode: " + ledName);
            sb.AppendLine("Base color: #" + led[1].ToString("X2") + led[2].ToString("X2") + led[3].ToString("X2"));
            sb.AppendLine("Palette Lab: " + (_paletteColors.Count > 0 ? string.Join(" -> ", _paletteColors.Select(c => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2")).ToArray()) : "—"));
            sb.AppendLine();

            sb.AppendLine("=== MACROS ===");
            var refs = M693Profile.ReferencedMacroSlots(_pendingProfile).Distinct().OrderBy(x => x).ToArray();
            sb.AppendLine("Referenced slots: " + (refs.Length == 0 ? "none" : string.Join(", ", refs)));
            foreach (int slot in refs)
            {
                ParsedMacro pm;
                if (_parsedMacros.TryGetValue(slot, out pm))
                    sb.AppendLine("slot " + slot + ": \"" + pm.Name + "\"  events=" + pm.EventCount + "  checksum=" + (pm.ChecksumOk ? "OK" : "BAD") + "  output=" + pm.DominantOutput);
                else
                    sb.AppendLine("slot " + slot + ": not loaded");
            }
            if (_pendingMacroSlots.Count > 0)
                sb.AppendLine("Pending macro changes: " + string.Join(", ", _pendingMacroSlots.Keys.OrderBy(x => x).Select(x => x.ToString()).ToArray()));
            sb.AppendLine();

            sb.AppendLine("=== KEYBOARD / MEDIA COMBOS ===");
            var comboRefs = M693Profile.ReferencedComboSlots(_pendingProfile).Distinct().OrderBy(x => x).ToArray();
            sb.AppendLine("Referenced combo slots: " + (comboRefs.Length == 0 ? "none" : string.Join(", ", comboRefs)));
            foreach (int slot in comboRefs)
            {
                ParsedCombo pc;
                if (_parsedCombos.TryGetValue(slot, out pc))
                    sb.AppendLine("slot " + slot + ": " + pc.Summary + "  checksum=" + (pc.ChecksumOk ? "OK" : "BAD"));
                else
                    sb.AppendLine("slot " + slot + ": not loaded");
            }
            if (_pendingComboSlots.Count > 0)
                sb.AppendLine("Pending combo changes: " + string.Join(", ", _pendingComboSlots.Keys.OrderBy(x => x).Select(x => x.ToString()).ToArray()));
            sb.AppendLine();

            int diff = _originalProfile == null ? 0 : CountDiff(_originalProfile, _pendingProfile);
            sb.AppendLine("=== EDIT STATE ===");
            sb.AppendLine("Changed profile bytes: " + diff);
            sb.AppendLine("Pending macros: " + _pendingMacroSlots.Count);
            sb.AppendLine("Pending combos: " + _pendingComboSlots.Count);
            if (!string.IsNullOrEmpty(_loadedProfilePath)) sb.AppendLine("Loaded file: " + _loadedProfilePath);

            txtDashSummary.Text = sb.ToString();
            if (stDevice != null) stDevice.Text = _device == null ? "M693: offline" : "M693: connected " + (_device.ProductId == 0xFA7C ? "2.4G" : "USB");
            if (stProfile != null) stProfile.Text = "Profile bytes: " + (_pendingProfile == null ? "—" : M693Profile.ProfileLength.ToString());
            RefreshEditState();
        }

        private void RefreshBatterySafe()
        {
            if (lblDashBattery == null) return;
            if (_device == null || _device.IsClosed)
            {
                lblDashBattery.Text = "Заряд: —";
                prgBattery.Value = 0;
                lblDashProfile.Text = "Onboard profile: —";
                if (stBattery != null) stBattery.Text = "Battery: —";
                return;
            }
            try
            {
                BatteryInfo b = _device.GetBatteryInfo();
                prgBattery.Value = Math.Max(0, Math.Min(100, b.Percent));
                lblDashBattery.Text = "Заряд: " + b.Percent + "%" + (b.Charging ? "  •  заряджається" : "");
                if (stBattery != null) stBattery.Text = "Battery: " + b.Percent + "%" + (b.Charging ? " ⚡" : "");
                try
                {
                    int pi = _device.GetCurrentProfileIndex();
                    lblDashProfile.Text = "Onboard profile: " + (pi + 1) + "  (index " + pi + ")";
                    if (cmbOnboardProfile != null && pi >= 0 && pi < cmbOnboardProfile.Items.Count)
                        cmbOnboardProfile.SelectedIndex = pi;
                    if (lblOnboardProfileState != null)
                        lblOnboardProfileState.Text = "Активний onboard profile: " + (pi + 1) + ". Перемикання використовує JM03 command 0x0F.";
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
            if (pf == null || (pf.Format != "NeutronM693ProfileV1" && pf.Format != "NeutronM693ProfileV2")) throw new InvalidDataException("Невідомий формат профілю.");
            byte[] raw = ParseHex(pf.ProfileHex);
            if (raw.Length != M693Profile.ProfileLength) throw new InvalidDataException("ProfileHex має неправильну довжину.");
            _pendingProfile = raw;
            _macroSlots.Clear();
            _pendingMacroSlots.Clear();
            _parsedMacros.Clear();
            _explicitMacroWrites.Clear();
            _comboSlots.Clear();
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
                        _explicitMacroWrites.Add(slot);
                    }
                }
            }
            _pendingComboSlots.Clear();
            _parsedCombos.Clear();
            if (pf.Combos != null)
            {
                foreach (var kv in pf.Combos)
                {
                    int slot;
                    if (!int.TryParse(kv.Key, out slot) || slot < 0 || slot >= 16) continue;
                    byte[] c = ParseHex(kv.Value);
                    if (c.Length == M693Profile.ComboSlotSize)
                    {
                        _pendingComboSlots[slot] = c;
                        _parsedCombos[slot] = ParsedCombo.Parse(slot, c);
                    }
                }
            }
            _loadedProfilePath = path;
            if (pf.PaletteColors != null && pf.PaletteColors.Count > 0)
            {
                _paletteColors.Clear();
                foreach (string hx in pf.PaletteColors.Take(8))
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(hx) && hx.StartsWith("#") && hx.Length == 7)
                        {
                            int v = Convert.ToInt32(hx.Substring(1), 16);
                            _paletteColors.Add(Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255));
                        }
                    }
                    catch { }
                }
                if (_paletteColors.Count == 0) { _paletteColors.Add(Color.Magenta); _paletteColors.Add(Color.Cyan); }
                if (cmbPaletteEffect != null && !string.IsNullOrEmpty(pf.PaletteEffect))
                {
                    int pi = cmbPaletteEffect.Items.IndexOf(pf.PaletteEffect);
                    if (pi >= 0) cmbPaletteEffect.SelectedIndex = pi;
                }
                if (numPalettePeriod != null && pf.PalettePeriodMs >= (int)numPalettePeriod.Minimum && pf.PalettePeriodMs <= (int)numPalettePeriod.Maximum)
                    numPalettePeriod.Value = pf.PalettePeriodMs;
                RefreshPaletteList();
            }

            LoadUiFromPending();
            SetEditorsEnabled(true);
            lblStatus.Text = "Профіль завантажено з файла — ще НЕ записано в мишку.";
            RefreshEditState();
            RefreshDashboard();
        }


        private void SwitchOnboardProfile()
        {
            if (!EnsureDevice()) return;
            if (HasPendingChanges())
            {
                MessageBox.Show(this, "Спочатку застосуйте або скасуйте локальні зміни. Перемикання onboard profile перезавантажує активну область EEPROM.", "Profiles", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            int index = cmbOnboardProfile == null ? 0 : Math.Max(0, cmbOnboardProfile.SelectedIndex);
            if (MessageBox.Show(this, "Перемкнути M693 на onboard profile " + (index + 1) + "?", "Profiles", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                _device.SetCurrentProfileIndex(index);
                Thread.Sleep(120);
                int got = _device.GetCurrentProfileIndex();
                if (got != index) throw new IOException("Firmware повернула profile index " + got + " замість " + index + ".");
                if (lblOnboardProfileState != null) lblOnboardProfileState.Text = "Активний onboard profile: " + (got + 1) + ". Профіль перечитується...";
                ReadFromMouse(true);
                RefreshBatterySafe();
                lblStatus.Text = "Onboard profile " + (got + 1) + " активовано.";
                lblStatus.ForeColor = Color.LightGreen;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не вдалося перемкнути onboard profile:\r\n" + ex.Message + "\r\n\r\nКоманда не змінює firmware; якщо ця ревізія M693 не підтримує 0x0F, поточний профіль лишається активним.", "Profiles", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log("Profile switch warning: " + ex);
            }
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
            var b = new Button
            {
                Text = text, Left = x, Top = y, Width = w, Height = h,
                Cursor = Cursors.Hand, FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 9F),
                Padding = new Padding(3)
            };
            b.Click += click;
            return b;
        }

        private void BuildButtonsTab()
        {
            var page = new TabPage("Кнопки");
            tabs.TabPages.Add(page);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                // Min sizes are set by scrollable child content, not on the splitter itself.
                // WinForms has not laid out this split yet; fixed huge minimums crash startup.
                SplitterWidth = 6
            };
            split.Panel1.AutoScroll = true;
            split.Panel1.AutoScrollMinSize = new Size(0, 1090);
            split.Panel2.AutoScroll = true;
            split.Panel2.AutoScrollMinSize = new Size(0, 780);
            page.Controls.Add(split);
            ConfigureSplitAfterLayout(split, 650, 0.52);
            // At ordinary window sizes, stack editor panes instead of pushing them offscreen.
            EventHandler resizeEditor = delegate {
                if (split.IsDisposed || !split.IsHandleCreated) return;
                bool stacked = page.ClientSize.Width < 1250;
                Orientation orientation = stacked ? Orientation.Horizontal : Orientation.Vertical;
                if (split.Orientation != orientation) {
                    try { split.Orientation = orientation; }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
                try {
                    int available = stacked ? split.ClientSize.Height - split.SplitterWidth : split.ClientSize.Width - split.SplitterWidth;
                    if (available > 150) {
                        int desired = stacked ? (int)(available * 0.52) : (int)(available * 0.51);
                        int bound = Math.Max(70, Math.Min(available - 70, desired));
                        if (split.SplitterDistance != bound) split.SplitterDistance = bound;
                    }
                } catch (ArgumentException) { }
            };
            page.SizeChanged += resizeEditor;
            split.HandleCreated += resizeEditor;

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
                "Колесо вліво", "Колесо вправо", "Колесо вгору", "Колесо вниз", "Fire key",
                "Подвійний лівий клік", "Подвійний правий клік", "Потрійний лівий клік"
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

            var grpKeys = new GroupBox { Text = "Keyboard / Multimedia / DPI Lock — onboard", Left = 10, Top = 557, Width = 620, Height = 185 };
            left.Controls.Add(grpKeys);
            grpKeys.Controls.Add(new Label { Left = 15, Top = 30, Width = 85, Text = "Modifier 1:" });
            cmbKeyModifier1 = new ComboBox { Left = 95, Top = 26, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbKeyModifier2 = new ComboBox { Left = 225, Top = 26, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbKeyMain = new ComboBox { Left = 355, Top = 26, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbKeyModifier1.Items.Add("—"); cmbKeyModifier2.Items.Add("—");
            foreach (var m in HidKeyCatalog.Modifiers) { cmbKeyModifier1.Items.Add(m); cmbKeyModifier2.Items.Add(m); }
            foreach (var k in HidKeyCatalog.KeyboardKeys) cmbKeyMain.Items.Add(k);
            cmbKeyModifier1.SelectedIndex = 0; cmbKeyModifier2.SelectedIndex = 0; if (cmbKeyMain.Items.Count > 0) cmbKeyMain.SelectedIndex = 0;
            grpKeys.Controls.AddRange(new Control[] { cmbKeyModifier1, cmbKeyModifier2, cmbKeyMain });
            var bKey = MakeButton("Призначити combo", 485, 25, 120, 31, delegate { AssignKeyboardCombo(); });
            grpKeys.Controls.Add(bKey);

            grpKeys.Controls.Add(new Label { Left = 15, Top = 72, Width = 80, Text = "Multimedia:" });
            cmbMediaAction = new ComboBox { Left = 95, Top = 68, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var k in HidKeyCatalog.ConsumerKeys) cmbMediaAction.Items.Add(k);
            if (cmbMediaAction.Items.Count > 0) cmbMediaAction.SelectedIndex = 0;
            grpKeys.Controls.Add(cmbMediaAction);
            grpKeys.Controls.Add(MakeButton("Призначити media", 355, 67, 160, 31, delegate { AssignMediaCombo(); }));

            grpKeys.Controls.Add(new Label { Left = 15, Top = 116, Width = 80, Text = "DPI Lock:" });
            cmbDpiLockAction = new ComboBox { Left = 95, Top = 112, Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (int dpi in M693DpiCodec.AllowedDpiValues.Where(x => x <= 4000)) cmbDpiLockAction.Items.Add(dpi.ToString());
            if (cmbDpiLockAction.Items.Count > 0) cmbDpiLockAction.SelectedIndex = 0;
            grpKeys.Controls.Add(cmbDpiLockAction);
            grpKeys.Controls.Add(MakeButton("Призначити DPI Lock", 245, 111, 180, 31, delegate { AssignDpiLock(); }));
            grpKeys.Controls.Add(new Label { Left = 435, Top = 111, Width = 170, Height = 47, Text = "DPI Lock friendly editor обмежено підтвердженим діапазоном ≤ 4000 DPI." });

            // Selecting a validated macro already present in the mouse writes ONLY the
            // 4-byte button binding; it does NOT touch the macro storage region.
            var grpExisting = new GroupBox { Text = "Існуючі макроси в мишці — без переписування подій", Left = 10, Top = 750, Width = 620, Height = 112 };
            grpExisting.Controls.Add(new Label { Left = 14, Top = 24, Width = 580, Height = 20,
                Text = "Спершу виберіть фізичну кнопку вище, потім макрос із пам'яті M693:" });
            cmbExistingMacro = new ComboBox { Left = 14, Top = 48, Width = 270, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbExistingMacroMode = new ComboBox { Left = 294, Top = 48, Width = 115, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbExistingMacroMode.Items.AddRange(new object[] { "HOLD", "Repeat 1", "Toggle" });
            cmbExistingMacroMode.SelectedIndex = 0;
            grpExisting.Controls.Add(cmbExistingMacro);
            grpExisting.Controls.Add(cmbExistingMacroMode);
            grpExisting.Controls.Add(MakeButton("Прив'язати", 420, 46, 170, 31, delegate { AssignExistingMacro(); }));
            grpExisting.Controls.Add(new Label { Left = 14, Top = 84, Width = 590, Height = 24,
                Text = "Тільки призначення кнопки (4 байти). Слоти з пошкодженим checksum виключені." });
            left.Controls.Add(grpExisting);

            var adv = new GroupBox { Text = "Повна матриця 16 слотів (діагностика / інші функції)", Left = 10, Top = 868, Width = 620, Height = 168 };
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

            var grpMacro = new GroupBox { Text = "Onboard Smooth Click Macro — працює в самій мишці", Dock = DockStyle.Top, Height = 620, Padding = new Padding(12) };
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
            y += 40;
            grpMacro.Controls.Add(new Label { Left = 15, Top = y + 4, Width = 150, Text = "Зупинка кліків:" });
            cmbStopStrategy = new ComboBox { Left = 170, Top = y, Width = 220,
                DropDownStyle = ComboBoxStyle.DropDownList };
            cmbStopStrategy.Items.AddRange(new object[] {
                "Нативний FIRE (на утримання)",
                "HOLD — 1 клік / цикл",
                "HOLD — 2 кліки / цикл",
                "HOLD — 3 кліки / цикл",
                "HOLD — вручну N кліків"
            });
            cmbStopStrategy.SelectedIndex = 1; // Shortest verified-format macro.
            grpMacro.Controls.Add(cmbStopStrategy);
            y += 38;
            grpMacro.Controls.Add(new Label { Left = 15, Top = y + 4, Width = 150, Text = "Кліків / цикл:" });
            numPatternClicks = new NumericUpDown { Left = 170, Top = y, Width = 120, Minimum = 1, Maximum = 24, Value = 1 };
            grpMacro.Controls.Add(numPatternClicks);
            cmbStopStrategy.SelectedIndexChanged += delegate { RefreshStopStrategyControls(); };
            numPatternClicks.ValueChanged += delegate { RefreshStopStrategyControls(); };
            numTypical.ValueChanged += delegate { RefreshStopStrategyControls(); };
            numMax.ValueChanged += delegate { RefreshStopStrategyControls(); };
            y += 34;
            lblStopBound = new Label { Left = 15, Top = y, Width = 390, Height = 58,
                Text = "Швидка зупинка: 1 клік / цикл." };
            grpMacro.Controls.Add(lblStopBound);
            RefreshStopStrategyControls();
            y += 65;
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
            grpMacro.Controls.Add(new Label { Left = 15, Top = y, Width = 390, Height = 57,
                Text = "M693 може завершувати поточний цикл після відпускання. Для швидкої зупинки обирайте FIRE або HOLD із 1 кліком. Точно нульова затримка прошивкою не гарантована." });
            y += 62;
            txtPreview = new TextBox { Left = 15, Top = y, Width = 390, Height = 105, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
            grpMacro.Controls.Add(txtPreview);
            y += 115;
            grpMacro.Controls.Add(new Label { Left = 15, Top = y, Width = 250, Height = 22, Text = "Крива інтервалів макросу:" });
            y += 24;
            pnlMacroGraph = new Panel { Left = 15, Top = y, Width = 390, Height = 82, BorderStyle = BorderStyle.FixedSingle };
            pnlMacroGraph.Paint += delegate(object sender, PaintEventArgs e) { DrawMacroGraph(e.Graphics, pnlMacroGraph.ClientRectangle); };
            grpMacro.Controls.Add(pnlMacroGraph);
            grpMacro.Height += 112;
            grpMacro.Resize += delegate
            {
                int inner = Math.Max(300, grpMacro.ClientSize.Width - 30);
                cmbMacroClick.Width = Math.Min(220, Math.Max(150, inner - 170));
                cmbStopStrategy.Width = Math.Min(270, Math.Max(140, inner - 170));
                lblStopBound.Width = Math.Max(260, inner);
                trkSmooth.Width = Math.Max(130, inner - 170);
                lblSmooth.Left = Math.Min(grpMacro.ClientSize.Width - 60, 120 + trkSmooth.Width + 5);
                btnGenerateMacro.Width = inner;
                txtPreview.Width = inner;
                if (pnlMacroGraph != null) pnlMacroGraph.Width = inner;
            };

            var warn = new Label
            {
                Dock = DockStyle.Top, Height = 120, Padding = new Padding(12),
                Text = "Безпека запису:\r\n• перед кожним Apply автоматично створюється backup;\r\n• пишуться лише байти, які реально змінені;\r\n• після запису виконується read-back перевірка;\r\n• офіційну OemDrv.exe треба закрити, щоб вона не перехоплювала відповіді HID."
            };
            right.Controls.Add(warn);
            warn.BringToFront();
            // Fit group cards and tables to the available panel width. Left-side controls
            // use proportional X/width reflow; vertical wheel scroll remains inside the panel.
            var leftBlocks = new Control[] { lbl, gridButtons, grpCapture, grpSimple, grpKeys, adv };
            foreach (Control block in leftBlocks) {
                if (block is GroupBox) EnableResponsiveChildren(block, 620);
            }
            left.Resize += delegate {
                int width = Math.Max(250, left.ClientSize.Width - 30);
                foreach (Control block in leftBlocks) block.Width = width;
            };
        }

        private static void EnableResponsiveChildren(Control parent, int designWidth)
        {
            var positions = parent.Controls.Cast<Control>()
                .Select(c => new { C = c, X = c.Left, W = c.Width, Dock = c.Dock }).ToArray();
            EventHandler fit = delegate {
                if (parent.IsDisposed) return;
                double factor = Math.Min(1.0, Math.Max(.48, (double)Math.Max(100, parent.ClientSize.Width - 22) / designWidth));
                foreach (var item in positions) {
                    if (item.Dock != DockStyle.None) continue;
                    int x = (int)Math.Round(item.X * factor);
                    int w = Math.Max(22, (int)Math.Round(item.W * factor));
                    item.C.SetBounds(x, item.C.Top, w, item.C.Height);
                }
            };
            parent.SizeChanged += fit;
        }

        private NumericUpDown MakeNum(int x, int y, decimal value, decimal min, decimal max)
        {
            return new NumericUpDown { Left = x, Top = y, Width = 120, Minimum = min, Maximum = max, Value = value };
        }


        private void BuildMacroStudioTab()
        {
            var page = new TabPage("Макроси PRO") { Name = "MacroStudioPage" };
            tabs.TabPages.Add(page);
            EnablePageScroll(page, 0, 830);

            page.Controls.Add(new Label
            {
                Left = 20, Top = 16, Width = 1120, Height = 42,
                Text = "Повний редактор onboard-макросів JM03. Тут можна читати/редагувати slot 0..15, додавати mouse/keyboard/media події, затримки, вибирати режим повтору і прив'язувати макрос до будь-якої фізичної кнопки.",
                Font = new Font("Segoe UI Semibold", 9.5F)
            });

            var slotsBox = new GroupBox { Left = 20, Top = 68, Width = 505, Height = 725, Text = "Macro slots 0..15" };
            page.Controls.Add(slotsBox);
            gridMacroStudioSlots = new DataGridView
            {
                Left = 12, Top = 28, Width = 478, Height = 595,
                ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            gridMacroStudioSlots.Columns.Add("Slot", "Slot");
            gridMacroStudioSlots.Columns.Add("Ref", "Прив'язано");
            gridMacroStudioSlots.Columns.Add("Name", "Назва");
            gridMacroStudioSlots.Columns.Add("Events", "Подій");
            gridMacroStudioSlots.Columns.Add("State", "Стан");
            gridMacroStudioSlots.Columns[0].FillWeight = 35;
            gridMacroStudioSlots.Columns[1].FillWeight = 65;
            gridMacroStudioSlots.Columns[3].FillWeight = 45;
            gridMacroStudioSlots.Columns[4].FillWeight = 55;
            gridMacroStudioSlots.DoubleClick += delegate
            {
                if (gridMacroStudioSlots.SelectedRows.Count == 0) return;
                int slot;
                if (int.TryParse(Convert.ToString(gridMacroStudioSlots.SelectedRows[0].Cells[0].Value), out slot))
                    LoadMacroStudioSlot(slot);
            };
            slotsBox.Controls.Add(gridMacroStudioSlots);

            var readAll = MakeButton("↧  Прочитати всі 16 слотів", 12, 638, 225, 34, delegate
            {
                if (!EnsureDevice()) return;
                ReadAllMacroSlots();
                RefreshMacroStudioSlots();
            });
            var loadSelected = MakeButton("Відкрити вибраний", 250, 638, 240, 34, delegate
            {
                if (gridMacroStudioSlots.SelectedRows.Count == 0) return;
                int slot;
                if (int.TryParse(Convert.ToString(gridMacroStudioSlots.SelectedRows[0].Cells[0].Value), out slot))
                    LoadMacroStudioSlot(slot);
            });
            slotsBox.Controls.AddRange(new Control[] { readAll, loadSelected });
            slotsBox.Controls.Add(new Label
            {
                Left = 12, Top = 682, Width = 465, Height = 30,
                Text = "Подвійний клік по slot — відкрити. Pending-позначка означає: зміни ще тільки в редакторі."
            });

            var edit = new GroupBox { Left = 545, Top = 68, Width = 595, Height = 725, Text = "Редактор макросу" };
            page.Controls.Add(edit);

            edit.Controls.Add(new Label { Left = 16, Top = 31, Width = 70, Text = "Slot:" });
            numMacroStudioSlot = new NumericUpDown { Left = 86, Top = 27, Width = 70, Minimum = 0, Maximum = 15, Value = 0 };
            edit.Controls.Add(numMacroStudioSlot);
            edit.Controls.Add(new Label { Left = 175, Top = 31, Width = 70, Text = "Назва:" });
            txtMacroStudioName = new TextBox { Left = 235, Top = 27, Width = 330, MaxLength = 30, Text = "NEUTRON_MACRO" };
            edit.Controls.Add(txtMacroStudioName);

            edit.Controls.Add(new Label { Left = 16, Top = 70, Width = 70, Text = "Режим:" });
            cmbMacroStudioMode = new ComboBox { Left = 86, Top = 65, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbMacroStudioMode.Items.AddRange(new object[] { "Repeat N разів", "HOLD — поки кнопку тримають", "Toggle", "Until any key pressed" });
            cmbMacroStudioMode.SelectedIndex = 1;
            edit.Controls.Add(cmbMacroStudioMode);
            edit.Controls.Add(new Label { Left = 325, Top = 70, Width = 80, Text = "Repeat N:" });
            numMacroStudioRepeat = new NumericUpDown { Left = 405, Top = 65, Width = 80, Minimum = 1, Maximum = 252, Value = 1 };
            edit.Controls.Add(numMacroStudioRepeat);
            numMacroStudioRepeat.Enabled = false;
            cmbMacroStudioMode.SelectedIndexChanged += delegate { if (numMacroStudioRepeat != null) numMacroStudioRepeat.Enabled = cmbMacroStudioMode.SelectedIndex == 0; };

            lblMacroStudioInfo = new Label
            {
                Left = 16, Top = 101, Width = 548, Height = 36,
                Text = "Відкрийте існуючий slot або створіть новий. Максимум — 70 подій."
            };
            edit.Controls.Add(lblMacroStudioInfo);

            gridMacroStudioEvents = new DataGridView
            {
                Left = 16, Top = 142, Width = 550, Height = 330,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            gridMacroStudioEvents.Columns.Add("No", "№");
            gridMacroStudioEvents.Columns.Add("Event", "Подія");
            gridMacroStudioEvents.Columns.Add("Raw", "RAW");
            gridMacroStudioEvents.Columns.Add("Delay", "Delay, ms");
            gridMacroStudioEvents.Columns[0].ReadOnly = true;
            gridMacroStudioEvents.Columns[1].ReadOnly = true;
            gridMacroStudioEvents.Columns[2].ReadOnly = true;
            gridMacroStudioEvents.Columns[0].FillWeight = 35;
            gridMacroStudioEvents.Columns[2].FillWeight = 70;
            gridMacroStudioEvents.Columns[3].FillWeight = 60;
            edit.Controls.Add(gridMacroStudioEvents);

            edit.Controls.Add(new Label { Left = 16, Top = 492, Width = 80, Text = "Тип події:" });
            cmbMacroEventType = new ComboBox { Left = 94, Top = 487, Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbMacroEventType.Items.AddRange(new object[] { "Mouse", "Keyboard", "Modifier", "Multimedia" });
            cmbMacroEventType.SelectedIndex = 0;
            edit.Controls.Add(cmbMacroEventType);

            cmbMacroEventValue = new ComboBox { Left = 224, Top = 487, Width = 185, DropDownStyle = ComboBoxStyle.DropDownList };
            edit.Controls.Add(cmbMacroEventValue);
            cmbMacroEventState = new ComboBox { Left = 419, Top = 487, Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbMacroEventState.Items.AddRange(new object[] { "Down", "Up" });
            cmbMacroEventState.SelectedIndex = 0;
            edit.Controls.Add(cmbMacroEventState);

            edit.Controls.Add(new Label { Left = 16, Top = 529, Width = 80, Text = "Delay, ms:" });
            numMacroEventDelay = new NumericUpDown { Left = 94, Top = 524, Width = 120, Minimum = 1, Maximum = 65535, Value = 20 };
            edit.Controls.Add(numMacroEventDelay);
            var addEv = MakeButton("＋ Додати подію", 224, 522, 165, 32, delegate { MacroStudioAddEvent(); });
            var delEv = MakeButton("− Видалити", 399, 522, 110, 32, delegate { MacroStudioRemoveEvent(); });
            edit.Controls.AddRange(new Control[] { addEv, delEv });

            var upEv = MakeButton("↑", 519, 522, 47, 32, delegate { MacroStudioMoveEvent(-1); });
            edit.Controls.Add(upEv);
            var downEv = MakeButton("↓", 519, 560, 47, 32, delegate { MacroStudioMoveEvent(1); });
            edit.Controls.Add(downEv);
            var clearEv = MakeButton("Очистити", 399, 560, 110, 32, delegate { if (gridMacroStudioEvents != null) gridMacroStudioEvents.Rows.Clear(); });
            edit.Controls.Add(clearEv);

            var savePending = MakeButton("Зберегти slot у pending", 16, 610, 220, 38, delegate { MacroStudioSavePending(false); });
            var bindSelected = MakeButton("Зберегти + прив'язати до вибраної кнопки", 246, 610, 320, 38, delegate { MacroStudioSavePending(true); });
            bindSelected.Tag = "primary";
            edit.Controls.AddRange(new Control[] { savePending, bindSelected });
            edit.Controls.Add(new Label
            {
                Left = 16, Top = 660, Width = 550, Height = 48,
                Text = "Запис у мишку все одно відбудеться лише через головну кнопку «ЗАСТОСУВАТИ». Macro Studio підтримує mouse, keyboard HID, modifiers і consumer/multimedia HID."
            });

            EnableResponsiveChildren(slotsBox, 505);
            EnableResponsiveChildren(edit, 595);
            EventHandler layoutStudio = delegate {
                int w = Math.Max(200, page.ClientSize.Width - 50);
                if (page.ClientSize.Width < 1160) {
                    slotsBox.Left = 20;
                    slotsBox.Top = 68;
                    slotsBox.Width = Math.Max(505, w);
                    edit.Left = 20;
                    edit.Top = slotsBox.Bottom + 18;
                    edit.Width = Math.Max(595, w);
                    page.AutoScrollMinSize = new Size(0, edit.Bottom + 35);
                } else {
                    slotsBox.Left = 20;
                    slotsBox.Top = 68;
                    int leftW = Math.Max(505, (int)(w * .43));
                    slotsBox.Width = leftW;
                    edit.Left = slotsBox.Right + 16;
                    edit.Top = 68;
                    edit.Width = Math.Max(595, w - leftW - 16);
                    page.AutoScrollMinSize = new Size(0, 830);
                }
            };
            page.SizeChanged += layoutStudio;
            page.HandleCreated += layoutStudio;
            slotsBox.SizeChanged += delegate {
                gridMacroStudioSlots.Width = Math.Max(110, slotsBox.ClientSize.Width - 26);
                readAll.Width = Math.Max(100, (slotsBox.ClientSize.Width - 36) / 2);
                loadSelected.Left = readAll.Right + 8;
                loadSelected.Width = Math.Max(100, slotsBox.ClientSize.Width - loadSelected.Left - 12);
            };

            cmbMacroEventType.SelectedIndexChanged += delegate { RefreshMacroEventValues(); };
            RefreshMacroEventValues();
            RefreshMacroStudioSlots();
        }

        private void RefreshMacroEventValues()
        {
            if (cmbMacroEventValue == null || cmbMacroEventType == null) return;
            cmbMacroEventValue.Items.Clear();
            if (cmbMacroEventType.SelectedIndex == 0)
            {
                cmbMacroEventValue.Items.AddRange(new object[] { "Left", "Right", "Middle", "Back", "Forward" });
            }
            else if (cmbMacroEventType.SelectedIndex == 1)
            {
                foreach (var k in HidKeyCatalog.KeyboardKeys) cmbMacroEventValue.Items.Add(k);
            }
            else if (cmbMacroEventType.SelectedIndex == 2)
            {
                foreach (var k in HidKeyCatalog.Modifiers) cmbMacroEventValue.Items.Add(k);
            }
            else
            {
                foreach (var k in HidKeyCatalog.ConsumerKeys) cmbMacroEventValue.Items.Add(k);
            }
            if (cmbMacroEventValue.Items.Count > 0) cmbMacroEventValue.SelectedIndex = 0;
        }

        private void RefreshMacroStudioSlots()
        {
            if (gridMacroStudioSlots == null) return;
            gridMacroStudioSlots.Rows.Clear();
            for (int slot = 0; slot < 16; slot++)
            {
                ParsedMacro pm = null;
                byte[] raw;
                if (_pendingMacroSlots.TryGetValue(slot, out raw))
                {
                    pm = ParsedMacro.Parse(slot, raw);
                    _parsedMacros[slot] = pm;
                }
                else
                {
                    _parsedMacros.TryGetValue(slot, out pm);
                }
                string refs = ReferencedBy(slot);
                string name = pm == null || string.IsNullOrEmpty(pm.Name) ? "—" : pm.Name;
                string events = pm == null ? "—" : pm.EventCount.ToString();
                string state = _pendingMacroSlots.ContainsKey(slot) ? "PENDING" : (pm == null ? "не прочитано" : (pm.ChecksumOk ? "OK" : "RAW/BAD"));
                int row = gridMacroStudioSlots.Rows.Add(slot, refs, name, events, state);
                if (_pendingMacroSlots.ContainsKey(slot))
                    gridMacroStudioSlots.Rows[row].DefaultCellStyle.ForeColor = Color.FromArgb(255, 188, 78);
            }
        }

        private void LoadMacroStudioSlot(int slot)
        {
            if (slot < 0 || slot > 15) return;
            try
            {
                byte[] raw = null;
                if (!_pendingMacroSlots.TryGetValue(slot, out raw) && !_macroSlots.TryGetValue(slot, out raw))
                {
                    if (!EnsureDevice()) return;
                    raw = _device.ReadBytes((ushort)(M693Profile.MacroBase + slot * M693Profile.MacroSlotSize), M693Profile.MacroSlotSize);
                    _macroSlots[slot] = raw;
                }
                ParsedMacro pm = ParsedMacro.Parse(slot, raw);
                _parsedMacros[slot] = pm;
                _macroStudioLoadedSlot = slot;
                numMacroStudioSlot.Value = slot;
                txtMacroStudioName.Text = string.IsNullOrEmpty(pm.Name) ? ("MACRO_" + slot) : pm.Name;
                gridMacroStudioEvents.Rows.Clear();
                int n = 1;
                foreach (var ev in pm.Events)
                {
                    int row = gridMacroStudioEvents.Rows.Add(n++, MacroEventCodec.Describe(ev), string.Format("{0:X2} {1:X2} {2:X2}", ev.B0, ev.B1, ev.B2), ev.Delay);
                    gridMacroStudioEvents.Rows[row].Tag = ev.Clone();
                }
                byte[] rec = FindButtonRecordReferencingMacro(slot);
                byte mode = rec == null ? (byte)254 : rec[2];
                if (mode == 254) cmbMacroStudioMode.SelectedIndex = 1;
                else if (mode == 253) cmbMacroStudioMode.SelectedIndex = 2;
                else if (mode == 255) cmbMacroStudioMode.SelectedIndex = 3;
                else { cmbMacroStudioMode.SelectedIndex = 0; numMacroStudioRepeat.Value = Math.Max(1, Math.Min(252, (int)mode)); }
                lblMacroStudioInfo.Text = "Slot " + slot + " • checksum " + (pm.ChecksumOk ? "OK" : "BAD/unknown") + " • output: " + pm.DominantOutput + " • refs: " + ReferencedBy(slot);
                RefreshMacroStudioSlots();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Macro Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private byte[] FindButtonRecordReferencingMacro(int slot)
        {
            if (_pendingProfile == null) return null;
            for (int i = 0; i < 16; i++)
            {
                byte[] r = M693Profile.GetButtonRecord(_pendingProfile, i);
                if (r[0] == 6 && r[1] == slot) return r;
            }
            return null;
        }

        private void MacroStudioAddEvent()
        {
            if (gridMacroStudioEvents == null || gridMacroStudioEvents.Rows.Count >= 70) { MessageBox.Show(this, "Максимум 70 подій."); return; }
            if (cmbMacroEventValue.SelectedItem == null) return;
            bool down = cmbMacroEventState.SelectedIndex != 1;
            MacroEventRaw ev = null;
            if (cmbMacroEventType.SelectedIndex == 0)
            {
                string v = cmbMacroEventValue.SelectedItem.ToString();
                int mask = v == "Right" ? 2 : v == "Middle" ? 4 : v == "Back" ? 8 : v == "Forward" ? 16 : 1;
                byte[] d, u; string src;
                FindMouseMacroEventTemplate(mask, out d, out u, out src);
                byte[] t = down ? d : u;
                ev = new MacroEventRaw { B0 = t[0], B1 = t[1], B2 = t[2], Delay = (ushort)numMacroEventDelay.Value };
            }
            else if (cmbMacroEventType.SelectedIndex == 1)
            {
                HidKeyChoice k = cmbMacroEventValue.SelectedItem as HidKeyChoice;
                if (k == null) return;
                ev = MacroEventCodec.Keyboard(k.Usage, down, (ushort)numMacroEventDelay.Value);
            }
            else if (cmbMacroEventType.SelectedIndex == 2)
            {
                HidModifierChoice k = cmbMacroEventValue.SelectedItem as HidModifierChoice;
                if (k == null) return;
                ev = MacroEventCodec.Modifier(k.Mask, down, (ushort)numMacroEventDelay.Value);
            }
            else
            {
                HidKeyChoice k = cmbMacroEventValue.SelectedItem as HidKeyChoice;
                if (k == null) return;
                ev = MacroEventCodec.Consumer(k.Usage, down, (ushort)numMacroEventDelay.Value);
            }
            int row = gridMacroStudioEvents.Rows.Add(gridMacroStudioEvents.Rows.Count + 1, MacroEventCodec.Describe(ev), string.Format("{0:X2} {1:X2} {2:X2}", ev.B0, ev.B1, ev.B2), ev.Delay);
            gridMacroStudioEvents.Rows[row].Tag = ev;
        }

        private void MacroStudioRemoveEvent()
        {
            if (gridMacroStudioEvents == null || gridMacroStudioEvents.SelectedRows.Count == 0) return;
            gridMacroStudioEvents.Rows.Remove(gridMacroStudioEvents.SelectedRows[0]);
            RenumberMacroStudioRows();
        }

        private void MacroStudioMoveEvent(int delta)
        {
            if (gridMacroStudioEvents == null || gridMacroStudioEvents.SelectedRows.Count == 0) return;
            int i = gridMacroStudioEvents.SelectedRows[0].Index;
            int j = i + delta;
            if (j < 0 || j >= gridMacroStudioEvents.Rows.Count) return;
            DataGridViewRow r = gridMacroStudioEvents.Rows[i];
            MacroEventRaw ev = r.Tag as MacroEventRaw;
            string delay = Convert.ToString(r.Cells[3].Value);
            gridMacroStudioEvents.Rows.RemoveAt(i);
            gridMacroStudioEvents.Rows.Insert(j, j + 1, MacroEventCodec.Describe(ev),
                string.Format("{0:X2} {1:X2} {2:X2}", ev.B0, ev.B1, ev.B2), delay);
            DataGridViewRow x = gridMacroStudioEvents.Rows[j];
            x.Tag = ev;
            x.Selected = true;
            RenumberMacroStudioRows();
        }

        private void RenumberMacroStudioRows()
        {
            if (gridMacroStudioEvents == null) return;
            for (int i = 0; i < gridMacroStudioEvents.Rows.Count; i++) gridMacroStudioEvents.Rows[i].Cells[0].Value = i + 1;
        }

        private void MacroStudioSavePending(bool bindToSelectedButton)
        {
            if (_pendingProfile == null) return;
            int slot = (int)numMacroStudioSlot.Value;
            if (gridMacroStudioEvents.Rows.Count < 1) { MessageBox.Show(this, "Додайте хоча б одну подію."); return; }
            if (gridMacroStudioEvents.Rows.Count > 70) { MessageBox.Show(this, "Максимум 70 подій."); return; }
            var events = new List<MacroEventRaw>();
            foreach (DataGridViewRow row in gridMacroStudioEvents.Rows)
            {
                MacroEventRaw ev = row.Tag as MacroEventRaw;
                if (ev == null) continue;
                ushort delay;
                if (!ushort.TryParse(Convert.ToString(row.Cells[3].Value), out delay) || delay < 1) delay = ev.Delay < 1 ? (ushort)1 : ev.Delay;
                MacroEventRaw copy = ev.Clone();
                copy.Delay = delay;
                events.Add(copy);
            }
            byte[] raw = MacroBuilder.BuildCustomSlot(slot, txtMacroStudioName.Text, events);
            _pendingMacroSlots[slot] = raw;
            _parsedMacros[slot] = ParsedMacro.Parse(slot, raw);
            _explicitMacroWrites.Add(slot);
            _macroStudioLoadedSlot = slot;

            if (bindToSelectedButton)
            {
                PhysicalButtonSpec pb = SelectedPhysicalButton();
                if (pb == null) { MessageBox.Show(this, "На вкладці «Кнопки» спочатку виберіть фізичну кнопку."); return; }
                byte mode = MacroStudioSelectedMode();
                M693Profile.SetButtonAction(_pendingProfile, pb.Slot, new byte[] { 6, (byte)slot, mode });
                RefreshButtonGrids();
            }
            RefreshMacroStudioSlots();
            RefreshDashboard();
            RefreshEditState();
            lblStatus.Text = "Macro slot " + slot + " підготовлено. Ще НЕ записано в мишку.";
            lblStatus.ForeColor = Color.FromArgb(255, 188, 78);
        }

        private byte MacroStudioSelectedMode()
        {
            if (cmbMacroStudioMode.SelectedIndex == 1) return 254;
            if (cmbMacroStudioMode.SelectedIndex == 2) return 253;
            if (cmbMacroStudioMode.SelectedIndex == 3) return 255;
            return (byte)Math.Max(1, Math.Min(252, (int)numMacroStudioRepeat.Value));
        }

        private void BuildDpiTab()
        {
            var page = new TabPage("DPI");
            tabs.TabPages.Add(page);
            EnablePageScroll(page, 900, 560);
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
            var page = new TabPage("RGB");
            tabs.TabPages.Add(page);
            EnablePageScroll(page, 0, 730);
            var rgbIntro = new Label { Left = 15, Top = 15, Width = 900, Height = 40,
                Text = "M693: 7 апаратних режимів, один базовий RGB колір, швидкість і яскравість. Запис — тільки після ЗАСТОСУВАТИ." };
            page.Controls.Add(rgbIntro);

            page.Controls.Add(new Label { Left = 30, Top = 75, Width = 150, Text = "Режим:" });
            cmbLedMode = new ComboBox { Left = 190, Top = 70, Width = 240, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbLedMode.Items.AddRange(new object[] { "Steady", "Breathing", "Streaming", "Neon", "Single color flow", "Colorful breathing", "Off" });
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
            page.Controls.Add(new Label { Left = 30, Top = 260, Width = 420, Height = 25,
                Text = "АПАРАТНІ ПРЕСЕТИ: записують один вибраний колір" });
            Color[] rgbPreset = { Color.Red, Color.Orange, Color.Lime, Color.Cyan, Color.Blue, Color.Magenta, Color.White };
            for (int i = 0; i < rgbPreset.Length; i++)
            {
                Color chosen = rgbPreset[i];
                var swatch = new Button { Left = 30 + i * 58, Top = 293, Width = 46, Height = 38,
                    BackColor = chosen, FlatStyle = FlatStyle.Flat, Text = "" };
                swatch.FlatAppearance.BorderColor = Color.FromArgb(102, 102, 110);
                swatch.Click += delegate { SelectHardwareRgbColor(chosen, false); };
                page.Controls.Add(swatch);
            }
            var steady = MakeButton("Постійний колір", 30, 354, 196, 34, delegate { SelectHardwareRgbMode(0); });
            var breathing = MakeButton("Дихання кольором", 240, 354, 195, 34, delegate { SelectHardwareRgbMode(1); });
            page.Controls.Add(steady);
            page.Controls.Add(breathing);
            var rgbInfo = new Label { Left = 30, Top = 400, Width = 410, Height = 67,
                Text = "Після вибору кольору натисніть ЗАСТОСУВАТИ. Пресети не виконують фонових USB/EEPROM-записів. Ефект Colorful breathing вмикає вбудовану кольорову анімацію firmware." };
            page.Controls.Add(rgbInfo);

            // Device-persisted custom style = one base RGB colour + genuine OEM effect, speed, brightness.
            // Arbitrary user-chosen multi-colour transitions are NOT claimed to be supported by firmware.
            var styleGroup = new GroupBox { Left = 30, Top = 490, Width = 440, Height = 300,
                Text = "Мої апаратні RGB-стилі — реальний запис у M693" };
            page.Controls.Add(styleGroup);
            styleGroup.Controls.Add(new Label { Left = 16, Top = 27, Width = 400, Height = 30,
                Text = "Створіть власний стиль із кольору, ефекту, швидкості та яскравості." });
            cmbHardwareStyle = new ComboBox { Left = 16, Top = 65, Width = 402, DropDownStyle = ComboBoxStyle.DropDownList };
            styleGroup.Controls.Add(cmbHardwareStyle);
            cmbHardwareStyle.SelectedIndexChanged += delegate {
                if (!_loadingHardwareStyles && SelectedHardwareStyleIndex() >= 0 && txtHardwareStyleName != null)
                    txtHardwareStyleName.Text = _hardwareStyles[SelectedHardwareStyleIndex()].Name;
            };
            txtHardwareStyleName = new TextBox { Left = 16, Top = 104, Width = 402,
                Text = "Мій RGB стиль" };
            styleGroup.Controls.Add(txtHardwareStyleName);
            var styleSave = MakeButton("Зберегти стиль", 16, 145, 191, 35,
                delegate { SaveHardwareLightingStyle(); });
            var styleLoad = MakeButton("Обрати стиль", 226, 145, 192, 35,
                delegate { LoadHardwareLightingStyle(); });
            styleGroup.Controls.Add(styleSave); styleGroup.Controls.Add(styleLoad);
            var styleApply = MakeButton("ЗАСТОСУВАТИ СТИЛЬ У M693", 16, 192, 402, 39,
                delegate { ApplyHardwareLightingStyle(); });
            styleApply.Tag = "primary";
            styleGroup.Controls.Add(styleApply);
            var styleRemove = MakeButton("Видалити стиль", 16, 240, 164, 31,
                delegate { DeleteHardwareLightingStyle(); });
            styleGroup.Controls.Add(styleRemove);
            styleGroup.Controls.Add(new Label { Left = 193, Top = 241, Width = 226, Height = 48,
                Text = "Без циклічних EEPROM-записів. Вбудовані RGB-анімації виконує прошивка." });
            LoadHardwareLightingStyles();

            var palette = new GroupBox
            {
                Left = 500, Top = 58, Width = 510, Height = 515,
                Text = "Neutron Palette Lab — кастомні сцени"
            };
            page.Controls.Add(palette);
            palette.Controls.Add(new Label
            {
                Left = 15, Top = 28, Width = 485, Height = 54,
                Text = "Створюйте сцену із 2–8 кольорів. Будь-який вибраний колір можна ОКРЕМО записати в мишку. Довільне переливання всієї палітри — лише прев'ю, доки не підтверджено RAM-команду."
            });
            lstPaletteColors = new ListBox { Left = 15, Top = 88, Width = 175, Height = 175 };
            palette.Controls.Add(lstPaletteColors);
            btnPaletteAdd = MakeButton("＋ Додати", 205, 88, 100, 30, delegate { PaletteAddColor(); });
            btnPaletteRemove = MakeButton("− Видалити", 315, 88, 100, 30, delegate { PaletteRemoveColor(); });
            palette.Controls.Add(btnPaletteAdd);
            palette.Controls.Add(btnPaletteRemove);
            palette.Controls.Add(new Label { Left = 205, Top = 169, Width = 100, Text = "Сцена:" });
            cmbPaletteEffect = new ComboBox { Left = 205, Top = 187, Width = 235, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbPaletteEffect.Items.AddRange(new object[] { "Crossfade", "Ping-pong", "Step", "Breathing palette" });
            cmbPaletteEffect.SelectedIndex = 0;
            palette.Controls.Add(cmbPaletteEffect);
            palette.Controls.Add(new Label { Left = 205, Top = 229, Width = 120, Text = "Період, мс:" });
            numPalettePeriod = new NumericUpDown { Left = 335, Top = 225, Width = 105, Minimum = 100, Maximum = 10000, Increment = 100, Value = 1200 };
            palette.Controls.Add(numPalettePeriod);
            btnPaletteUseFirst = MakeButton("Вибраний колір → M693", 205, 261, 235, 36, delegate { PaletteUseFirstColor(); });
            var editColor = MakeButton("Редагувати колір", 205, 130, 150, 30, delegate { PaletteEditSelectedColor(); });
            var moveUp = MakeButton("↑", 363, 130, 35, 30, delegate { PaletteMoveColor(-1); });
            var moveDown = MakeButton("↓", 405, 130, 35, 30, delegate { PaletteMoveColor(1); });
            palette.Controls.Add(editColor); palette.Controls.Add(moveUp); palette.Controls.Add(moveDown);
            palette.Controls.Add(btnPaletteUseFirst);
            pnlPalettePreview = new Panel { Left = 15, Top = 319, Width = 475, Height = 110, BorderStyle = BorderStyle.FixedSingle };
            pnlPalettePreview.Paint += delegate(object sender, PaintEventArgs e) { DrawPalettePreview(e.Graphics, pnlPalettePreview.ClientRectangle); };
            palette.Controls.Add(pnlPalettePreview);
            palette.Controls.Add(new Label
            {
                Left = 15, Top = 441, Width = 475, Height = 61,
                Text = "Важливо: RGB палітра зберігається в JSON-профілі; апаратно з неї можна записати вибраний колір. Режими із прошивки працюють самостійно; довільні 2–5-кольорові переливання ще не підтримано."
            });
            _paletteColors.Add(Color.Magenta);
            _paletteColors.Add(Color.Cyan);
            RefreshPaletteList();
            lstPaletteColors.SelectedIndex = 0;
            lstPaletteColors.DoubleClick += delegate { PaletteEditSelectedColor(); };
            page.Resize += delegate {
                int available = Math.Max(360, page.ClientSize.Width - 36);
                bool stacked = available < 1010;
                palette.Left = stacked ? 15 : Math.Max(480, available / 2);
                styleGroup.Left = stacked ? 15 : 30;
                styleGroup.Top = 490;
                styleGroup.Width = stacked ? available : Math.Min(440, Math.Max(300, palette.Left - 48));
                cmbHardwareStyle.Width = Math.Max(260, styleGroup.Width - 32);
                txtHardwareStyleName.Width = Math.Max(260, styleGroup.Width - 32);
                styleApply.Width = Math.Max(260, styleGroup.Width - 32);
                styleSave.Width = Math.Max(108, (styleGroup.Width - 44) / 2);
                styleLoad.Left = styleSave.Right + 12;
                styleLoad.Width = Math.Max(108, styleGroup.Width - styleLoad.Left - 16);
                palette.Top = stacked ? styleGroup.Bottom + 20 : 58;
                palette.Width = stacked ? available : Math.Max(480, available - palette.Left);
                pnlPalettePreview.Width = Math.Max(430, palette.Width - 36);
                rgbIntro.Width = Math.Max(350, page.ClientSize.Width - 35);
                page.AutoScrollMinSize = new Size(0, stacked ? palette.Bottom + 25 : Math.Max(825, styleGroup.Bottom + 30));
            };
            page.PerformLayout();
        }

        private int SelectedHardwareStyleIndex()
        {
            if (cmbHardwareStyle == null) return -1;
            int selected = cmbHardwareStyle.SelectedIndex;
            return selected >= 0 && selected < _hardwareStyles.Count ? selected : -1;
        }

        private static string NormalizeRgbStyleName(string raw)
        {
            string s = (raw ?? "").Trim();
            if (s.Length > 50) s = s.Substring(0, 50);
            return s;
        }

        private void LoadHardwareLightingStyles()
        {
            _loadingHardwareStyles = true;
            try
            {
                _hardwareStyles.Clear();
                if (File.Exists(HardwareStylesFile))
                {
                    var json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
                    var loaded = json.Deserialize<List<HardwareLightingStyle>>(
                        File.ReadAllText(HardwareStylesFile, Encoding.UTF8));
                    if (loaded != null)
                    {
                        foreach (var h in loaded.Take(64))
                        {
                            if (h != null && !string.IsNullOrWhiteSpace(h.Name) &&
                                h.Mode >= 0 && h.Mode < 7 && h.Speed >= 1 && h.Speed <= 10 &&
                                h.Brightness >= 1 && h.Brightness <= 10 &&
                                h.Red >= 0 && h.Red <= 255 &&
                                h.Green >= 0 && h.Green <= 255 &&
                                h.Blue >= 0 && h.Blue <= 255)
                                _hardwareStyles.Add(h);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не вдалося прочитати бібліотеку RGB-стилів. Дані мишки не змінені.\r\n" +
                    ex.Message, "M693 RGB", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _loadingHardwareStyles = false;
                RefreshHardwareStylesList();
            }
        }

        private void PersistHardwareLightingStyles()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HardwareStylesFile));
            var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
            string json = serializer.Serialize(_hardwareStyles);
            string temp = HardwareStylesFile + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(HardwareStylesFile)) File.Delete(HardwareStylesFile);
            File.Move(temp, HardwareStylesFile);
        }

        private void RefreshHardwareStylesList()
        {
            if (cmbHardwareStyle == null) return;
            _loadingHardwareStyles = true;
            try
            {
                cmbHardwareStyle.Items.Clear();
                foreach (var style in _hardwareStyles) cmbHardwareStyle.Items.Add(style.Name);
                if (cmbHardwareStyle.Items.Count > 0) cmbHardwareStyle.SelectedIndex = 0;
            }
            finally { _loadingHardwareStyles = false; }
        }

        private void SaveHardwareLightingStyle()
        {
            string name = NormalizeRgbStyleName(txtHardwareStyleName.Text);
            if (name.Length == 0)
            {
                MessageBox.Show(this, "Вкажіть назву стилю.", "M693 RGB");
                return;
            }
            if (_hardwareStyles.Count >= 64 &&
                !_hardwareStyles.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "Максимум 64 збережені стилі.", "M693 RGB");
                return;
            }
            var style = new HardwareLightingStyle {
                Name = name, Mode = cmbLedMode.SelectedIndex,
                Red = _ledColor.R, Green = _ledColor.G, Blue = _ledColor.B,
                Speed = (int)numLedSpeed.Value, Brightness = (int)numLedBrightness.Value
            };
            int index = _hardwareStyles.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _hardwareStyles[index] = style;
            else { index = _hardwareStyles.Count; _hardwareStyles.Add(style); }
            try
            {
                PersistHardwareLightingStyles();
                RefreshHardwareStylesList();
                cmbHardwareStyle.SelectedIndex = index;
                lblStatus.Text = "RGB-стиль збережено локально. Натисніть 'ЗАСТОСУВАТИ СТИЛЬ У M693'.";
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "RGB library write", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private bool PrepareHardwareLightingStyle()
        {
            int i = SelectedHardwareStyleIndex();
            if (i < 0)
            {
                MessageBox.Show(this, "Спочатку збережіть або виберіть стиль.", "M693 RGB",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            var style = _hardwareStyles[i];
            cmbLedMode.SelectedIndex = style.Mode;
            _ledColor = Color.FromArgb(style.Red, style.Green, style.Blue);
            numLedSpeed.Value = style.Speed;
            numLedBrightness.Value = style.Brightness;
            UpdateLedColorButton();
            txtHardwareStyleName.Text = style.Name;
            _dirtyLed = true;
            RefreshEditState();
            return true;
        }

        private void LoadHardwareLightingStyle()
        {
            if (PrepareHardwareLightingStyle())
                lblStatus.Text = "Стиль підготовлено. Натисніть ЗАСТОСУВАТИ для запису RGB у мишку.";
        }

        private void DeleteHardwareLightingStyle()
        {
            int i = SelectedHardwareStyleIndex();
            if (i < 0) return;
            if (MessageBox.Show(this, "Видалити стиль '" + _hardwareStyles[i].Name + "'? Це не змінить мишку.",
                "M693 RGB", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _hardwareStyles.RemoveAt(i);
            try { PersistHardwareLightingStyles(); RefreshHardwareStylesList(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "RGB library delete", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void ApplyHardwareLightingStyle()
        {
            if (_pendingProfile == null || _originalProfile == null || !EnsureDevice()) return;
            if (_dirtyDpi || _dirtySensor || _pendingComboSlots.Count > 0 || _pendingMacroSlots.Count > 0)
            {
                MessageBox.Show(this,
                    "Є незбережені зміни не-RGB параметрів. Збережіть їх окремо або скасуйте. " +
                    "Застосування RGB-стилю не повинно змінювати кнопки й макроси.",
                    "M693 RGB", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            for (int i = 0; i < _originalProfile.Length; i++)
            {
                if (i >= M693Profile.AddrLed && i < M693Profile.AddrLed + 7) continue;
                if (_originalProfile[i] != _pendingProfile[i])
                {
                    MessageBox.Show(this, "Є інші незбережені зміни профілю. " +
                        "Спочатку застосуйте або скасуйте їх.",
                        "M693 RGB", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            if (!PrepareHardwareLightingStyle()) return;
            ApplyToMouse(); // Existing proven backup, diff-write, and read-back.
        }

        private void PaletteAddColor()
        {
            if (_paletteColors.Count >= 8)
            {
                MessageBox.Show(this, "У Palette Lab максимум 8 кольорів.", "RGB Palette", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var d = new ColorDialog())
            {
                d.Color = _paletteColors.Count > 0 ? _paletteColors[_paletteColors.Count - 1] : Color.Magenta;
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    _paletteColors.Add(d.Color);
                    RefreshPaletteList();
                }
            }
        }

        private void PaletteRemoveColor()
        {
            if (lstPaletteColors == null || lstPaletteColors.SelectedIndex < 0) return;
            if (_paletteColors.Count <= 1) return;
            _paletteColors.RemoveAt(lstPaletteColors.SelectedIndex);
            RefreshPaletteList();
        }

        // A palette is local metadata; the M693 hardware accepts one selected color at a time.
        private void PaletteUseFirstColor()
        {
            int i = lstPaletteColors == null ? -1 : lstPaletteColors.SelectedIndex;
            if (i < 0 || i >= _paletteColors.Count) {
                MessageBox.Show(this, "Виберіть колір у палітрі.", "M693 RGB", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SelectHardwareRgbColor(_paletteColors[i], true);
        }

        private void SelectHardwareRgbColor(Color color, bool forceSteady)
        {
            _ledColor = color;
            UpdateLedColorButton();
            // OFF never lights up: using a preset enables Steady if necessary.
            if (cmbLedMode.SelectedIndex == 6 || forceSteady) cmbLedMode.SelectedIndex = 0;
            _dirtyLed = true;
            RefreshEditState();
            lblStatus.Text = "Обраний RGB #" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2") +
                " підготовлено. Для запису у M693 натисніть ЗАСТОСУВАТИ.";
            lblStatus.ForeColor = Color.LightGreen;
        }

        private void SelectHardwareRgbMode(int mode)
        {
            cmbLedMode.SelectedIndex = mode;
            _dirtyLed = true;
            RefreshEditState();
            lblStatus.Text = "RGB режим підготовлено. Натисніть ЗАСТОСУВАТИ.";
        }

        private void PaletteEditSelectedColor()
        {
            int i = lstPaletteColors == null ? -1 : lstPaletteColors.SelectedIndex;
            if (i < 0 || i >= _paletteColors.Count) return;
            using (var dialog = new ColorDialog { FullOpen = true, Color = _paletteColors[i] })
                if (dialog.ShowDialog(this) == DialogResult.OK) {
                    _paletteColors[i] = dialog.Color;
                    RefreshPaletteList();
                    lstPaletteColors.SelectedIndex = i;
                }
        }

        private void PaletteMoveColor(int step)
        {
            int i = lstPaletteColors == null ? -1 : lstPaletteColors.SelectedIndex;
            int target = i + step;
            if (i < 0 || target < 0 || target >= _paletteColors.Count) return;
            Color temp = _paletteColors[i];
            _paletteColors[i] = _paletteColors[target];
            _paletteColors[target] = temp;
            RefreshPaletteList();
            lstPaletteColors.SelectedIndex = target;
        }

        private void RefreshPaletteList()
        {
            if (lstPaletteColors == null) return;
            lstPaletteColors.Items.Clear();
            for (int i = 0; i < _paletteColors.Count; i++)
            {
                Color c = _paletteColors[i];
                lstPaletteColors.Items.Add((i + 1) + ". #" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"));
            }
            if (pnlPalettePreview != null) pnlPalettePreview.Invalidate();
        }

        private void DrawPalettePreview(Graphics g, Rectangle r)
        {
            g.Clear(Color.FromArgb(20, 20, 22));
            if (_paletteColors.Count == 0 || r.Width <= 2 || r.Height <= 2) return;
            int segs = Math.Max(1, _paletteColors.Count - 1);
            for (int x = 0; x < r.Width; x++)
            {
                double t = r.Width <= 1 ? 0 : (double)x / (r.Width - 1);
                double p = t * segs;
                int i = Math.Min(_paletteColors.Count - 1, (int)Math.Floor(p));
                int j = Math.Min(_paletteColors.Count - 1, i + 1);
                double f = p - i;
                Color a = _paletteColors[i];
                Color b = _paletteColors[j];
                int rr = (int)Math.Round(a.R + (b.R - a.R) * f);
                int gg = (int)Math.Round(a.G + (b.G - a.G) * f);
                int bb = (int)Math.Round(a.B + (b.B - a.B) * f);
                using (var pen = new Pen(Color.FromArgb(rr, gg, bb)))
                    g.DrawLine(pen, x, 0, x, r.Height - 1);
            }
        }

        private void BuildSensorTab()
        {
            var page = new TabPage("Сенсор");
            tabs.TabPages.Add(page);
            EnablePageScroll(page, 900, 620);
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

        private void BuildCapabilitiesTab()
        {
            var page = new TabPage("Можливості") { Name = "CapabilitiesPage" };
            tabs.TabPages.Add(page);
            EnablePageScroll(page, 1040, 760);

            page.Controls.Add(new Label
            {
                Left = 22, Top = 18, Width = 960, Height = 48,
                Text = "Original replacement center: тут видно, що Hardware Studio вже замінює у штатній REDRAGON-програмі, що додає поверх неї і які рідкісні дії поки зберігаються безпечним RAW-preserve режимом.",
                Font = new Font("Segoe UI Semibold", 10F)
            });

            var grid = new DataGridView
            {
                Left = 22, Top = 78, Width = 970, Height = 475, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            grid.Columns.Add("Area", "Функція");
            grid.Columns.Add("Status", "Стан");
            grid.Columns.Add("Notes", "Що є в Neutron");
            grid.Columns[0].FillWeight = 90; grid.Columns[1].FillWeight = 55; grid.Columns[2].FillWeight = 200;
            grid.Rows.Add("Підключення 2.4G / USB", "ПІДТВЕРДЖЕНО", "Автопошук + ручне пересканування, split FF02/FF01 HID pair");
            grid.Rows.Add("Battery / charging", "ПІДТВЕРДЖЕНО", "Заряд, charging state, автооновлення");
            grid.Rows.Add("Key Settings", "РОЗШИРЕНО", "7 фізичних кнопок, 16-slot matrix, mouse/DPI/wheel/fire/disable + keyboard/media combo editor + RAW preserve");
            grid.Rows.Add("Macros", "РОЗШИРЕНО+", "Повний Macro Studio: 16 slots, mouse/keyboard/media/modifier events, delay editor, режими Repeat/HOLD/Toggle/Until-key, Smooth Macro");
            grid.Rows.Add("DPI", "ПІДТВЕРДЖЕНО", "До 8 ступенів, активна ступінь, кольори DPI");
            grid.Rows.Add("Lighting", "ПІДТВЕРДЖЕНО+", "7 stock firmware effects, base color, speed, brightness + Palette Lab metadata");
            grid.Rows.Add("Sensor / Advanced", "ПІДТВЕРДЖЕНО", "Polling, debounce, LOD, sleep, Motion Sync, Angle Snapping, Ripple, LED-off-moving");
            grid.Rows.Add("Profiles", "РОЗШИРЕНО+", "JSON V2 import/export incl. macros+combos, local library, RAW backup, automatic pre-Apply backup, onboard profile 1..4 switch");
            grid.Rows.Add("Windows mouse params", "ПІДТВЕРДЖЕНО", "Pointer speed, double-click, wheel, Enhance pointer precision");
            grid.Rows.Add("Factory Reset", "ПІДТВЕРДЖЕНО", "Двократне підтвердження + backup перед hardware reset");
            grid.Rows.Add("Keyboard / multimedia friendly editor", "РОЗШИРЕНО", "Single key, 0–2 modifiers + key, multimedia/consumer HID; combo slots 0x100..0x2FF з checksum/read-back");
            grid.Rows.Add("Onboard active profile switch", "РОЗШИРЕНО", "Read 0x0E + guarded Set 0x0F, автоматичне reread після перемикання");
            grid.Rows.Add("RGB button switch rare actions", "RAW PRESERVE", "RGB ON/OFF / RGB Switch button encodings ще не пишемо навмання; існуючі байти зберігаються");
            grid.Rows.Add("Firmware updater", "НЕ УВІМКНЕНО", "Навмисно не пишемо firmware без повністю підтвердженого update protocol");
            grid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                if (e.RowIndex < 0 || e.ColumnIndex != 1 || e.Value == null) return;
                string v = e.Value.ToString();
                if (v.Contains("ПІДТВЕРДЖЕНО") || v.Contains("РОЗШИРЕНО")) e.CellStyle.ForeColor = Color.FromArgb(108, 219, 132);
                else if (v.Contains("ЧАСТКОВО") || v.Contains("RAW")) e.CellStyle.ForeColor = Color.FromArgb(244, 182, 71);
                else e.CellStyle.ForeColor = Color.FromArgb(226, 92, 92);
            };
            page.Controls.Add(grid);

            var custom = new GroupBox { Left = 22, Top = 575, Width = 970, Height = 135, Text = "Neutron-exclusive" };
            custom.Controls.Add(new Label
            {
                Left = 18, Top = 30, Width = 925, Height = 88,
                Text = "• Smooth human-like onboard click curve з Min / Typical / Max / Smoothness та preview графіком.\r\n" +
                       "• Pending-change model: нічого не пишеться до Apply; Ctrl+Z скасовує локальні зміни.\r\n" +
                       "• Backup/read-back/semantic verification перед і після критичних записів.\r\n" +
                       "• Palette Lab, повний технічний dashboard, діагностика HID, профільна бібліотека.\r\n" +
                       "• Macro Studio + friendly keyboard/media combo editor + guarded onboard profile switching."
            });
            page.Controls.Add(custom);
        }

        private bool HasPendingChanges()
        {
            if (_pendingProfile == null) return false;
            if (_dirtySensor || _dirtyLed || _dirtyDpi) return true;
            if (_pendingMacroSlots.Count > 0 || _pendingComboSlots.Count > 0) return true;
            return _originalProfile != null && CountDiff(_originalProfile, _pendingProfile) > 0;
        }

        private void RefreshEditState()
        {
            bool pending = HasPendingChanges();
            int diff = (_originalProfile == null || _pendingProfile == null) ? 0 : CountDiff(_originalProfile, _pendingProfile);
            int uiGroups = (_dirtySensor ? 1 : 0) + (_dirtyLed ? 1 : 0) + (_dirtyDpi ? 1 : 0);
            int work = diff + _pendingMacroSlots.Count + _pendingComboSlots.Count + uiGroups;
            if (lblEditState != null)
            {
                lblEditState.Text = pending ? "●  Є незаписані зміни  •  profile bytes: " + diff + "  •  macros: " + _pendingMacroSlots.Count + "  •  combos: " + _pendingComboSlots.Count : "●  Профіль синхронізований — змін немає";
                lblEditState.ForeColor = pending ? Color.FromArgb(255, 188, 78) : Color.FromArgb(91, 208, 123);
            }
            if (btnApply != null)
            {
                btnApply.Text = pending ? "ЗАСТОСУВАТИ  • " + Math.Max(1, work) : "ЗАСТОСУВАТИ";
                btnApply.Enabled = pending && _pendingProfile != null && _device != null && !_device.IsClosed;
            }
            if (btnDiscard != null) btnDiscard.Enabled = pending && _pendingProfile != null;
            if (stChanges != null)
            {
                stChanges.Text = pending ? "Незаписані зміни: " + Math.Max(1, work) : "Змін немає";
                stChanges.ForeColor = pending ? Color.FromArgb(255, 188, 78) : Color.FromArgb(120, 215, 145);
            }
        }

        private void DiscardPendingChanges()
        {
            if (_originalProfile == null) return;
            if (HasPendingChanges() && MessageBox.Show(this, "Відкинути всі локальні зміни, які ще не записані в мишку?", "Скасувати зміни", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _pendingProfile = (byte[])_originalProfile.Clone();
            _pendingMacroSlots.Clear();
            _explicitMacroWrites.Clear();
            _pendingComboSlots.Clear();
            _dirtySensor = _dirtyLed = _dirtyDpi = false;
            _loadedProfilePath = null;
            LoadUiFromPending();
            lblStatus.Text = "Локальні зміни скасовано. Редактор повернуто до останнього прочитаного стану.";
            lblStatus.ForeColor = Color.FromArgb(120, 215, 145);
            RefreshEditState();
        }

        private void BuildLogTab()
        {
            var page = new TabPage("Діагностика");
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
            if (HasPendingChanges() && MessageBox.Show(this,
                "Є незаписані зміни. Пересканування може повторно прочитати мишку й замінити локальний стан. Продовжити?",
                "Пересканування M693", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            ConnectDeviceCore(true);
        }

        private void AutoDiscoveryTick(bool force)
        {
            if (chkAutoConnect == null || !chkAutoConnect.Checked) return;
            if (_connectBusy) return;
            if (_device != null && !_device.IsClosed) return;
            ConnectDeviceCore(false);
        }

        private void ConnectDeviceCore(bool manual)
        {
            if (_connectBusy) return;
            _connectBusy = true;
            UpdateOfficialStatus();
            SafeDisposeDevice();
            if (manual) Cursor = Cursors.WaitCursor;
            try
            {
                if (manual) Log("Ручне сканування M693 VID=25A7 PID=FA7B/FA7C...");
                Action<string> scanLog = manual ? (Action<string>)Log : null;
                _device = M693Device.FindAndOpen(scanLog);
                if (_device == null)
                {
                    lblStatus.Text = "●  M693 очікується — автопошук активний";
                    lblStatus.ForeColor = Color.FromArgb(239, 146, 54);
                    lblDevice.Text = "M693 видно у Windows, але command/response HID pair ще не відкрито. Автопошук повториться автоматично.";
                    RefreshDashboard();
                    return;
                }
                lblStatus.Text = "●  M693 підключено";
                lblStatus.ForeColor = Color.FromArgb(94, 214, 122);
                lblDevice.Text = _device.Description;
                Log("M693 AUTO HID pair знайдено: " + _device.Description);
                if (HasPendingChanges())
                {
                    lblStatus.Text = "●  M693 підключено • локальні зміни збережено в редакторі, auto-read пропущено";
                    lblStatus.ForeColor = Color.FromArgb(255, 188, 78);
                    RefreshDashboard();
                }
                else
                {
                    ReadFromMouse(!manual);
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = "●  Помилка HID";
                lblStatus.ForeColor = Color.OrangeRed;
                if (manual)
                {
                    Log("ERROR connect: " + ex);
                    MessageBox.Show(this, ex.Message, "M693 HID", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                _connectBusy = false;
                if (manual) Cursor = Cursors.Default;
            }
        }

        private void ReadFromMouse()
        {
            if (HasPendingChanges() && MessageBox.Show(this,
                "Є незаписані зміни. Повторне читання з миші відкине локальні правки. Продовжити?",
                "Прочитати з M693", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            ReadFromMouse(false);
        }

        private bool ReadFromMouse(bool silent)
        {
            if (_device == null || _device.IsClosed)
            {
                if (!silent) EnsureDevice();
                return false;
            }
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
                _explicitMacroWrites.Clear();
                _comboSlots.Clear();
                _pendingComboSlots.Clear();
                _parsedCombos.Clear();

                var comboRefs = M693Profile.ReferencedComboSlots(_pendingProfile).Distinct().Where(x => x >= 0 && x < 16).ToArray();
                foreach (int slot in comboRefs)
                {
                    try
                    {
                        Log("Читаю combo slot " + slot + " @ 0x" + (M693Profile.ComboBase + slot * M693Profile.ComboSlotSize).ToString("X4"));
                        byte[] raw = _device.ReadBytes((ushort)(M693Profile.ComboBase + slot * M693Profile.ComboSlotSize), M693Profile.ComboSlotSize);
                        _comboSlots[slot] = raw;
                        _parsedCombos[slot] = ParsedCombo.Parse(slot, raw);
                    }
                    catch (Exception cex)
                    {
                        Log("Combo slot " + slot + " read warning: " + cex.Message);
                    }
                }

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
                lblStatus.Text = "Профіль M693 прочитано: кнопки, combo, DPI, RGB, сенсор, macro refs";
                lblStatus.ForeColor = Color.LightGreen;
                _loadedProfilePath = null;
                RefreshDashboard();
                RefreshEditState();
                RefreshBatterySafe();
                return true;
            }
            catch (Exception ex)
            {
                Log("ERROR read: " + ex.Message);
                if (!silent)
                    MessageBox.Show(this, "Не вдалося прочитати профіль:\r\n" + ex.Message + "\r\n\r\nЗакрийте OemDrv.exe повністю (включно з tray) і спробуйте ще раз.", "M693", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SafeDisposeDevice();
                SetEditorsEnabled(false);
                RefreshDashboard();
                return false;
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
            if (stDevice != null) stDevice.Text = "M693: offline";
            RefreshEditState();
        }

        private void SetEditorsEnabled(bool enabled)
        {
            btnRead.Enabled = enabled;
            btnExport.Enabled = enabled;
            btnRawBackup.Enabled = enabled;
            btnApply.Enabled = enabled;
            if (btnDiscard != null) btnDiscard.Enabled = enabled;
            gridButtons.Enabled = enabled;
            gridMatrix.Enabled = enabled;
            btnAssignSimple.Enabled = enabled;
            btnCapture.Enabled = enabled;
            btnGenerateMacro.Enabled = enabled;
            if (numFireInterval != null) numFireInterval.Enabled = enabled;
            if (numFireRepeat != null) numFireRepeat.Enabled = enabled;
            if (cmbKeyModifier1 != null) cmbKeyModifier1.Enabled = enabled;
            if (cmbKeyModifier2 != null) cmbKeyModifier2.Enabled = enabled;
            if (cmbKeyMain != null) cmbKeyMain.Enabled = enabled;
            if (cmbMediaAction != null) cmbMediaAction.Enabled = enabled;
            if (cmbDpiLockAction != null) cmbDpiLockAction.Enabled = enabled;
            if (cmbExistingMacro != null) cmbExistingMacro.Enabled = enabled;
            if (cmbExistingMacroMode != null) cmbExistingMacroMode.Enabled = enabled;
            if (gridMacroStudioSlots != null) gridMacroStudioSlots.Enabled = enabled;
            if (gridMacroStudioEvents != null) gridMacroStudioEvents.Enabled = enabled;
            if (numMacroStudioSlot != null) numMacroStudioSlot.Enabled = enabled;
            if (txtMacroStudioName != null) txtMacroStudioName.Enabled = enabled;
            if (cmbMacroStudioMode != null) cmbMacroStudioMode.Enabled = enabled;
            if (cmbOnboardProfile != null) cmbOnboardProfile.Enabled = enabled;
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
            RefreshEditState();
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
                ParsedCombo pc;
                if (rec[0] == 5 && _parsedCombos.TryGetValue(b.Slot, out pc))
                    action += " «" + pc.Summary + "»" + (pc.ChecksumOk ? "" : " [checksum?]");
                int row = gridButtons.Rows.Add(b.Number, b.Name, b.Slot, action, Hex(rec));
                if (b.Number == selectedNo) gridButtons.Rows[row].Selected = true;
            }
            if (gridButtons.Rows.Count > 0 && gridButtons.SelectedRows.Count == 0) gridButtons.Rows[0].Selected = true;

            gridMatrix.Rows.Clear();
            for (int slot = 0; slot < 16; slot++)
            {
                byte[] rec = M693Profile.GetButtonRecord(_pendingProfile, slot);
                string phys = string.Join(", ", _physicalButtons.Where(x => x.Slot == slot).Select(x => "#" + x.Number).ToArray());
                string action = DescribeAction(rec, true);
                ParsedCombo pc;
                if (rec[0] == 5 && _parsedCombos.TryGetValue(slot, out pc))
                    action += " «" + pc.Summary + "»";
                gridMatrix.Rows.Add(slot, phys, action, Hex(rec));
            }
            RefreshMacroStudioSlots();
            RefreshExistingMacroChoices();
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
            if (t == 5) return "Keyboard / Media Combo";
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
            if (t == 10) return "DPI Lock raw=" + a + " (~" + M693DpiCodec.DpiFromCodeLowRange(a) + " DPI)";
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

        private void RefreshExistingMacroChoices()
        {
            if (cmbExistingMacro == null) return;
            string before = cmbExistingMacro.SelectedItem as string;
            string prevKey = before != null && before.Length >= 2 ? before.Substring(0, 2) : "";
            cmbExistingMacro.BeginUpdate();
            try
            {
                cmbExistingMacro.Items.Clear();
                foreach (var kv in _macroSlots.OrderBy(kv => kv.Key))
                {
                    ParsedMacro pm = ParsedMacro.Parse(kv.Key, kv.Value);
                    // Existing only: never offer an uncommitted/pending or broken macro.
                    if (!pm.ChecksumOk || pm.EventCount < 1 || pm.EventCount > 70) continue;
                    string name = string.IsNullOrWhiteSpace(pm.Name) ? "(без назви)" : pm.Name;
                    cmbExistingMacro.Items.Add(kv.Key.ToString("D2") + " | " + name + " (" + pm.EventCount + " подій)");
                }
                for (int i = 0; i < cmbExistingMacro.Items.Count; i++)
                {
                    if (((string)cmbExistingMacro.Items[i]).StartsWith(prevKey + " | "))
                    {
                        cmbExistingMacro.SelectedIndex = i;
                        break;
                    }
                }
                if (cmbExistingMacro.SelectedIndex < 0 && cmbExistingMacro.Items.Count > 0)
                    cmbExistingMacro.SelectedIndex = 0;
            }
            finally { cmbExistingMacro.EndUpdate(); }
        }

        private void AssignExistingMacro()
        {
            if (_pendingProfile == null || cmbExistingMacro == null || cmbExistingMacro.SelectedItem == null) return;
            PhysicalButtonSpec pb = SelectedPhysicalButton();
            if (pb == null) { MessageBox.Show(this, "Виберіть фізичну кнопку у верхній таблиці."); return; }
            string label = cmbExistingMacro.SelectedItem.ToString();
            int slot;
            if (label.Length < 2 || !int.TryParse(label.Substring(0, 2), out slot)) return;
            byte[] raw;
            if (!_macroSlots.TryGetValue(slot, out raw)) { MessageBox.Show(this, "Спочатку перечитайте макроси з мишки."); return; }
            ParsedMacro parsed = ParsedMacro.Parse(slot, raw);
            if (!parsed.ChecksumOk || parsed.EventCount < 1)
            {
                MessageBox.Show(this, "Вибраний макрос не пройшов read-only перевірку. Запис скасовано.", "Safety", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            byte repeat = cmbExistingMacroMode.SelectedIndex == 1 ? (byte)1 : (cmbExistingMacroMode.SelectedIndex == 2 ? (byte)253 : (byte)254);
            byte[] former = M693Profile.GetButtonRecord(_pendingProfile, pb.Slot);
            // Previously failed pending macro for this button must not become a write side effect.
            if (former[0] == 6 && former[1] != slot)
            {
                _pendingMacroSlots.Remove(former[1]);
                _explicitMacroWrites.Remove(former[1]);
            }
            M693Profile.SetButtonAction(_pendingProfile, pb.Slot, new byte[] { 6, (byte)slot, repeat });
            Log("ASSIGN EXISTING macro #" + slot + " (" + parsed.Name + ") to physical #" + pb.Number + "; profile-only pending write.");
            RefreshButtonGrids();
            RefreshDashboard();
            RefreshEditState();
            lblStatus.Text = "Призначення готове: кнопка #" + pb.Number + " → macro #" + slot + ". Дані макросу НЕ перезаписуються.";
            lblStatus.ForeColor = Color.FromArgb(255, 188, 78);
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
                case 15: PrepareFixedClickMacro(pb, 1, 2, "NEUTRON_DBL_L"); return;
                case 16: PrepareFixedClickMacro(pb, 2, 2, "NEUTRON_DBL_R"); return;
                case 17: PrepareFixedClickMacro(pb, 1, 3, "NEUTRON_TRIPLE_L"); return;
                default: action = new byte[] { 0, 0, 0 }; break;
            }
            M693Profile.SetButtonAction(_pendingProfile, pb.Slot, action);
            RefreshButtonGrids();
            RefreshDashboard();
            lblStatus.Text = "Зміна підготовлена. Натисніть «ЗАСТОСУВАТИ В МИШКУ».";
            RefreshEditState();
        }

        private void PrepareFixedClickMacro(PhysicalButtonSpec pb, int mouseMask, int clicks, string name)
        {
            int macroSlot = ChooseMacroSlot(pb);
            if (macroSlot < 0)
            {
                MessageBox.Show(this, "Немає вільного macro slot. Звільніть один slot або перепризначте існуючий макрос.", "M693", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            byte[] down, up; string source;
            FindMouseMacroEventTemplate(mouseMask, out down, out up, out source);
            int[] intervals = Enumerable.Repeat(82, Math.Max(1, clicks)).ToArray();
            byte[] raw = MacroBuilder.BuildFullSlot(macroSlot, name, mouseMask, intervals, down, up);
            _pendingMacroSlots[macroSlot] = raw;
            _parsedMacros[macroSlot] = ParsedMacro.Parse(macroSlot, raw);
            M693Profile.SetButtonAction(_pendingProfile, pb.Slot, new byte[] { 6, (byte)macroSlot, 1 });
            RefreshButtonGrids();
            RefreshDashboard();
            lblStatus.Text = name + " підготовлено для " + pb.Name + ". Event template: " + source + ". Ще НЕ записано.";
            lblStatus.ForeColor = Color.FromArgb(255, 188, 78);
            RefreshEditState();
        }


        private void AssignKeyboardCombo()
        {
            if (_pendingProfile == null) return;
            PhysicalButtonSpec pb = SelectedPhysicalButton();
            if (pb == null) { MessageBox.Show(this, "Виберіть фізичну кнопку у таблиці."); return; }
            HidKeyChoice main = cmbKeyMain == null ? null : cmbKeyMain.SelectedItem as HidKeyChoice;
            if (main == null) { MessageBox.Show(this, "Виберіть клавішу."); return; }

            var mods = new List<HidModifierChoice>();
            HidModifierChoice m1 = cmbKeyModifier1 == null ? null : cmbKeyModifier1.SelectedItem as HidModifierChoice;
            HidModifierChoice m2 = cmbKeyModifier2 == null ? null : cmbKeyModifier2.SelectedItem as HidModifierChoice;
            if (m1 != null) mods.Add(m1);
            if (m2 != null && (m1 == null || m2.Mask != m1.Mask)) mods.Add(m2);

            var events = new List<ComboEventRaw>();
            foreach (var m in mods) events.Add(ComboEventCodec.Modifier(m.Mask, true));
            events.Add(ComboEventCodec.Keyboard(main.Usage, true));
            events.Add(ComboEventCodec.Keyboard(main.Usage, false));
            for (int i = mods.Count - 1; i >= 0; i--) events.Add(ComboEventCodec.Modifier(mods[i].Mask, false));
            if (events.Count > 6) { MessageBox.Show(this, "JM03 combo має максимум 6 подій."); return; }

            byte[] raw = ComboBuilder.Build(events);
            _pendingComboSlots[pb.Slot] = raw;
            _parsedCombos[pb.Slot] = ParsedCombo.Parse(pb.Slot, raw);
            M693Profile.SetButtonAction(_pendingProfile, pb.Slot, new byte[] { 5, 0, 0 });
            RefreshButtonGrids();
            RefreshDashboard();
            RefreshEditState();
            lblStatus.Text = "Keyboard combo підготовлено для " + pb.Name + ". Ще НЕ записано.";
            lblStatus.ForeColor = Color.FromArgb(255, 188, 78);
        }

        private void AssignMediaCombo()
        {
            if (_pendingProfile == null) return;
            PhysicalButtonSpec pb = SelectedPhysicalButton();
            if (pb == null) { MessageBox.Show(this, "Виберіть фізичну кнопку у таблиці."); return; }
            HidKeyChoice media = cmbMediaAction == null ? null : cmbMediaAction.SelectedItem as HidKeyChoice;
            if (media == null) return;
            var events = new List<ComboEventRaw>
            {
                ComboEventCodec.Consumer(media.Usage, true),
                ComboEventCodec.Consumer(media.Usage, false)
            };
            byte[] raw = ComboBuilder.Build(events);
            _pendingComboSlots[pb.Slot] = raw;
            _parsedCombos[pb.Slot] = ParsedCombo.Parse(pb.Slot, raw);
            M693Profile.SetButtonAction(_pendingProfile, pb.Slot, new byte[] { 5, 0, 0 });
            RefreshButtonGrids();
            RefreshDashboard();
            RefreshEditState();
            lblStatus.Text = "Multimedia action підготовлено для " + pb.Name + ". Ще НЕ записано.";
            lblStatus.ForeColor = Color.FromArgb(255, 188, 78);
        }

        private void AssignDpiLock()
        {
            if (_pendingProfile == null) return;
            PhysicalButtonSpec pb = SelectedPhysicalButton();
            if (pb == null) { MessageBox.Show(this, "Виберіть фізичну кнопку у таблиці."); return; }
            int dpi;
            if (cmbDpiLockAction == null || cmbDpiLockAction.SelectedItem == null || !int.TryParse(cmbDpiLockAction.SelectedItem.ToString(), out dpi)) return;
            byte code = M693DpiCodec.CodeForDpi(dpi);
            M693Profile.SetButtonAction(_pendingProfile, pb.Slot, new byte[] { 10, code, 0 });
            RefreshButtonGrids();
            RefreshDashboard();
            RefreshEditState();
            lblStatus.Text = "DPI Lock " + dpi + " підготовлено для " + pb.Name + ". Ще НЕ записано.";
            lblStatus.ForeColor = Color.FromArgb(255, 188, 78);
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


        private void RefreshStopStrategyControls()
        {
            if (cmbStopStrategy == null || numPatternClicks == null || numTypical == null) return;
            int mode = cmbStopStrategy.SelectedIndex;
            numPatternClicks.Enabled = mode == 4;
            decimal count = mode == 0 || mode == 1 ? 1 : mode == 2 ? 2 : mode == 3 ? 3 : numPatternClicks.Value;
            int bound = (int)count * (int)numMax.Value;
            if (lblStopBound != null)
                lblStopBound.Text = mode == 0
                    ? "FIRE: функція швидкого автокліку прошивки, без випадкових інтервалів. Припинення після відпускання потрібно перевірити на M693."
                    : "Орієнтовна верхня межа залишку циклу: до " + bound +
                      " мс (" + count + " кліків × Max). Реальна реакція залежить від firmware.";
        }

        private void GenerateAndBindMacro()
        {
            if (_pendingProfile == null) return;
            PhysicalButtonSpec pb = SelectedPhysicalButton();
            if (pb == null) { MessageBox.Show(this, "Спочатку виберіть фізичну кнопку."); return; }

            int min = (int)numMin.Value;
            int typ = (int)numTypical.Value;
            int max = (int)numMax.Value;
            if (!(min <= typ && typ <= max))
            {
                MessageBox.Show(this, "Має бути: Мінімум ≤ Типово ≤ Максимум.");
                return;
            }

            if (cmbStopStrategy.SelectedIndex == 0)
            {
                // The documented onboard FIRE action has repeat=0 to fire while held.
                // Unlike a macro it has no long scripted cycle to finish.
                // It uses fixed frequency, NOT smoothly varying timing.
                int rate = Math.Max(3, Math.Min(255, typ));
                byte[] former = M693Profile.GetButtonRecord(_pendingProfile, pb.Slot);
                if (former[0] == 6 && _pendingMacroSlots.ContainsKey(former[1]))
                {
                    int pending = former[1];
                    _pendingMacroSlots.Remove(pending);
                    _explicitMacroWrites.Remove(pending);
                    byte[] existing;
                    if (_macroSlots.TryGetValue(pending, out existing))
                        _parsedMacros[pending] = ParsedMacro.Parse(pending, existing);
                    else
                        _parsedMacros.Remove(pending);
                }
                M693Profile.SetButtonAction(_pendingProfile, pb.Slot, new byte[] { 4, (byte)rate, 0 });
                _lastMacroIntervals = new int[0];
                if (pnlMacroGraph != null) pnlMacroGraph.Invalidate();
                txtPreview.Text =
                    "Режим: апаратний FIRE, кнопка #" + pb.Number + Environment.NewLine +
                    "Швидкість: " + rate + " (параметр OEM Fire speed)" + Environment.NewLine +
                    "Повтор: 0 — поки утримують." + Environment.NewLine +
                    "Жоден Macro slot не перезаписується. Інтервал фіксований." + Environment.NewLine +
                    "Це гіпотеза про найшвидшу реакцію на відпускання; перевірте на фізичній M693.";
                RefreshButtonGrids(); RefreshDashboard(); RefreshEditState();
                lblStatus.Text = "FIRE підготовлено. Натисніть ЗАСТОСУВАТИ, потім перевірте зупинку.";
                return;
            }

            int macroSlot = ChooseMacroSlot(pb);
            if (macroSlot < 0)
            {
                MessageBox.Show(this, "Немає вільного macro slot 0..15.");
                return;
            }
            int clickMask = cmbMacroClick.SelectedIndex == 1 ? 2 : 1;
            int patternClicks = cmbStopStrategy.SelectedIndex == 1 ? 1 :
                cmbStopStrategy.SelectedIndex == 2 ? 2 :
                cmbStopStrategy.SelectedIndex == 3 ? 3 : (int)numPatternClicks.Value;
            int[] intervals = patternClicks == 1
                ? new int[] { typ }
                : SmoothPatternGenerator.Generate(patternClicks, min, typ, max, trkSmooth.Value,
                    Environment.TickCount ^ (pb.Number << 12));
            byte[] downTemplate, upTemplate;
            string templateSource;
            FindMouseMacroEventTemplate(clickMask, out downTemplate, out upTemplate, out templateSource);
            byte[] macro = MacroBuilder.BuildFullSlot(macroSlot,
                "NEUTRON_B" + pb.Number + (clickMask == 1 ? "_L" : "_R"),
                clickMask, intervals, downTemplate, upTemplate);
            _pendingMacroSlots[macroSlot] = macro;
            _parsedMacros[macroSlot] = ParsedMacro.Parse(macroSlot, macro);
            _lastMacroIntervals = intervals.ToArray();
            if (pnlMacroGraph != null) pnlMacroGraph.Invalidate();
            M693Profile.SetButtonAction(_pendingProfile, pb.Slot, new byte[] { 6, (byte)macroSlot, 254 });
            RefreshButtonGrids(); RefreshDashboard(); RefreshEditState();
            txtPreview.Text = "Macro slot: " + macroSlot + Environment.NewLine +
                "HOLD / фізична кнопка #" + pb.Number + Environment.NewLine +
                "Подій: " + (intervals.Length * 2) + " (" + intervals.Length + " кліків / цикл)" + Environment.NewLine +
                "Шаблон подій: " + templateSource + Environment.NewLine +
                "Інтервали, мс: " + string.Join(", ", intervals.Select(x => x.ToString()).ToArray()) + Environment.NewLine +
                "Час циклу: " + intervals.Sum() + " мс. Після відпускання можливе завершення циклу до його межі." +
                Environment.NewLine + "Для найкоротшого залишку оберіть HOLD 1 клік.";
            lblStatus.Text = "QuickStop HOLD підготовлено. Натисніть ЗАСТОСУВАТИ.";
        }

        private void DrawMacroGraph(Graphics g, Rectangle r)
        {
            g.Clear(Color.FromArgb(18, 18, 20));
            if (_lastMacroIntervals == null || _lastMacroIntervals.Length < 2 || r.Width < 10 || r.Height < 10) return;
            int min = _lastMacroIntervals.Min();
            int max = _lastMacroIntervals.Max();
            if (max <= min) max = min + 1;
            using (var grid = new Pen(Color.FromArgb(55, 55, 60)))
            {
                for (int y = 1; y < 4; y++)
                {
                    int yy = r.Top + y * r.Height / 4;
                    g.DrawLine(grid, r.Left, yy, r.Right, yy);
                }
            }
            PointF[] pts = new PointF[_lastMacroIntervals.Length];
            for (int i = 0; i < _lastMacroIntervals.Length; i++)
            {
                float x = r.Left + 4 + (r.Width - 9) * (float)i / Math.Max(1, _lastMacroIntervals.Length - 1);
                float norm = (_lastMacroIntervals[i] - min) / (float)(max - min);
                float y = r.Bottom - 5 - (r.Height - 10) * norm;
                pts[i] = new PointF(x, y);
            }
            using (var pen = new Pen(Color.FromArgb(238, 65, 65), 2F))
                g.DrawLines(pen, pts);
            using (var f = new Font("Segoe UI", 7.5F))
            using (var b = new SolidBrush(Color.Silver))
                g.DrawString(min + "–" + max + " ms", f, b, new PointF(r.Left + 6, r.Top + 4));
        }

        private void FindMouseMacroEventTemplate(int mouseMask, out byte[] down, out byte[] up, out string source)
        {
            down = new byte[] { 0x84, (byte)mouseMask, 0x00 };
            up = new byte[] { 0x44, (byte)mouseMask, 0x00 };
            source = "JM03 default 84/44";

            string wanted = mouseMask == 2 ? "right" : mouseMask == 4 ? "middle" : mouseMask == 8 ? "back" : mouseMask == 16 ? "forward" : "left";
            foreach (var pm in _parsedMacros.Values.OrderBy(x => x.Slot))
            {
                if (pm == null || pm.Events == null || pm.Events.Count < 2 || pm.DominantOutput != wanted) continue;
                MacroEventRaw press = pm.Events.FirstOrDefault(e => (e.B0 & 0xC0) == 0x80 && e.B1 == (byte)mouseMask);
                MacroEventRaw release = pm.Events.FirstOrDefault(e => (e.B0 & 0xC0) == 0x40 && e.B1 == (byte)mouseMask);
                if (press != null && release != null)
                {
                    down = new byte[] { press.B0, press.B1, press.B2 };
                    up = new byte[] { release.B0, release.B1, release.B2 };
                    source = "copied from existing macro #" + pm.Slot + " \"" + pm.Name + "\"";
                    Log("Smooth macro uses mouse event template from slot " + pm.Slot + ": DOWN=" + Hex(down) + " UP=" + Hex(up));
                    return;
                }
            }
            Log("Smooth macro uses fallback JM03 event template: DOWN=" + Hex(down) + " UP=" + Hex(up));
        }

        private int ChooseMacroSlot(PhysicalButtonSpec pb)
        {
            byte[] cur = M693Profile.GetButtonRecord(_pendingProfile, pb.Slot);
            var used = new HashSet<int>(M693Profile.ReferencedMacroSlots(_pendingProfile));
            // Never silently overwrite an existing macro, especially a damaged slot 4.
            // A new macro must be successfully verified before the profile is remapped.
            foreach (int slot in Enumerable.Range(0, 16))
            {
                if (used.Contains(slot) || _pendingMacroSlots.ContainsKey(slot)) continue;
                byte[] raw;
                if (!_macroSlots.TryGetValue(slot, out raw))
                {
                    // Examine the FULL slot before choosing it. An unreferenced slot can
                    // still contain a real, user-created macro.
                    if (_device == null || _device.IsClosed) return -1;
                    try
                    {
                        raw = _device.ReadBytes((ushort)(M693Profile.MacroBase + slot * M693Profile.MacroSlotSize), M693Profile.MacroSlotSize);
                        _macroSlots[slot] = raw;
                        _parsedMacros[slot] = ParsedMacro.Parse(slot, raw);
                    }
                    catch (Exception ex)
                    {
                        Log("EMPTY_SLOT_PROBE_FAIL slot=" + slot + ": " + ex.Message);
                        continue;
                    }
                }
                bool blank = raw.All(x => x == 0) || raw.All(x => x == 0xFF);
                if (blank)
                {
                    Log("SELECT_VERIFIED_EMPTY_MACRO_SLOT=" + slot + " (no existing macro overwritten)");
                    return slot;
                }
            }
            Log("NO_VERIFIED_EMPTY_MACRO_SLOT: refusing to overwrite an unreferenced user macro.");
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
                if (d.ShowDialog(this) == DialogResult.OK) { _ledColor = d.Color; UpdateLedColorButton(); if (!_loadingUi) { _dirtyLed = true; RefreshEditState(); } }
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
                if (d.ShowDialog(this) == DialogResult.OK) { dpiColors[i] = d.Color; UpdateDpiColorButton(i); if (!_loadingUi) { _dirtyDpi = true; RefreshEditState(); } }
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
            pf.Format = "NeutronM693ProfileV2";
            pf.ExportedUtc = DateTime.UtcNow.ToString("o");
            pf.Device = _device == null ? "VID_25A7 PID_FA7B/FA7C" : _device.Description;
            pf.ProfileHex = HexCompact(_pendingProfile);
            pf.Macros = new Dictionary<string, string>();
            pf.Combos = new Dictionary<string, string>();
            try { pf.ActiveOnboardProfile = _device == null ? 0 : _device.GetCurrentProfileIndex() + 1; } catch { pf.ActiveOnboardProfile = 0; }
            pf.PaletteColors = _paletteColors.Select(c => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2")).ToList();
            pf.PaletteEffect = cmbPaletteEffect == null || cmbPaletteEffect.SelectedItem == null ? "Crossfade" : cmbPaletteEffect.SelectedItem.ToString();
            pf.PalettePeriodMs = numPalettePeriod == null ? 1200 : (int)numPalettePeriod.Value;

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

            var comboSlots = new HashSet<int>(M693Profile.ReferencedComboSlots(_pendingProfile));
            foreach (int s in _pendingComboSlots.Keys) comboSlots.Add(s);
            if (includeCurrentReferencedMacros && _device != null)
            {
                foreach (int s in comboSlots.ToArray())
                {
                    if (!_comboSlots.ContainsKey(s) && !_pendingComboSlots.ContainsKey(s) && s >= 0 && s < 16)
                    {
                        try { _comboSlots[s] = _device.ReadBytes((ushort)(M693Profile.ComboBase + s * M693Profile.ComboSlotSize), M693Profile.ComboSlotSize); }
                        catch (Exception ex) { Log("Export combo slot " + s + " warning: " + ex.Message); }
                    }
                }
            }
            foreach (int s in comboSlots)
            {
                byte[] raw;
                if (_pendingComboSlots.TryGetValue(s, out raw) || _comboSlots.TryGetValue(s, out raw)) pf.Combos[s.ToString()] = HexCompact(raw);
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
            var savedExplicitMacros = new HashSet<int>(_explicitMacroWrites);
            var savedPendingCombos = new Dictionary<int, byte[]>(_pendingComboSlots);
            int[] extraSlotsSnapshot = extraMacroSlots == null ? new int[0] : extraMacroSlots.ToArray();
            try
            {
                _pendingProfile = liveProfile;
                _pendingMacroSlots.Clear();
                _explicitMacroWrites.Clear();
                _pendingComboSlots.Clear();

                var slots = new HashSet<int>(M693Profile.ReferencedMacroSlots(liveProfile));
                foreach (int s in extraSlotsSnapshot) if (s >= 0 && s < 16) slots.Add(s);
                foreach (int slot in slots)
                {
                    byte[] raw = _device.ReadBytes((ushort)(M693Profile.MacroBase + slot * M693Profile.MacroSlotSize), M693Profile.MacroSlotSize);
                    _macroSlots[slot] = raw;
                }

                var comboSlots = new HashSet<int>(M693Profile.ReferencedComboSlots(liveProfile));
                foreach (int slot in comboSlots)
                {
                    byte[] raw = _device.ReadBytes((ushort)(M693Profile.ComboBase + slot * M693Profile.ComboSlotSize), M693Profile.ComboSlotSize);
                    _comboSlots[slot] = raw;
                }

                SaveProfileFile(path, true);
            }
            finally
            {
                _pendingProfile = savedPending;
                _pendingMacroSlots.Clear();
                foreach (var kv in savedPendingMacros) _pendingMacroSlots[kv.Key] = kv.Value;
                _explicitMacroWrites.Clear();
                foreach (int x in savedExplicitMacros) _explicitMacroWrites.Add(x);
                _pendingComboSlots.Clear();
                foreach (var kv in savedPendingCombos) _pendingComboSlots[kv.Key] = kv.Value;
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
            var refMacroSetForSummary = new HashSet<int>(M693Profile.ReferencedMacroSlots(_pendingProfile));
            int referencedPendingCount = _pendingMacroSlots.Keys.Count(k => refMacroSetForSummary.Contains(k) || _explicitMacroWrites.Contains(k));
            int referencedComboCount = _pendingComboSlots.Keys.Count(k => M693Profile.ReferencedComboSlots(_pendingProfile).Contains(k));
            string summary = "Буде змінено байтів профілю: " + diff +
                "\r\nMacro slots до запису: " + referencedPendingCount +
                "\r\nKeyboard/Media combo slots: " + referencedComboCount +
                "\r\n\r\nПеред записом буде створено автоматичний backup і після запису — семантична read-back перевірка.";
            if (_loadedProfilePath != null) summary += "\r\n\r\nІмпортований файл: " + _loadedProfilePath;
            if (MessageBox.Show(this, summary + "\r\n\r\nПродовжити?", "Apply в onboard memory", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            Cursor = Cursors.WaitCursor;
            try
            {
                Log("=== APPLY START ===");
                Log("Pending macros: " + (_pendingMacroSlots.Count == 0 ? "none" : string.Join(", ", _pendingMacroSlots.Keys.OrderBy(x => x).Select(x => x.ToString()).ToArray())));
                Log("Pending combos: " + (_pendingComboSlots.Count == 0 ? "none" : string.Join(", ", _pendingComboSlots.Keys.OrderBy(x => x).Select(x => x.ToString()).ToArray())));
                byte[] live = _device.ReadBytes(0, M693Profile.ProfileLength);
                string bdir = Path.Combine(Application.StartupPath, "Backups");
                Directory.CreateDirectory(bdir);
                string backup = Path.Combine(bdir, "AUTO_BEFORE_APPLY_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".m693profile.json");
                SaveLiveBackup(backup, live, _pendingMacroSlots.Keys);
                Log("Backup: " + backup);

                var referencedAfterApply = new HashSet<int>(M693Profile.ReferencedMacroSlots(_pendingProfile));
                var macrosToWrite = _pendingMacroSlots
                    .Where(kv => kv.Key >= 0 && kv.Key < 16 && (referencedAfterApply.Contains(kv.Key) || _explicitMacroWrites.Contains(kv.Key)))
                    .OrderBy(kv => kv.Key)
                    .ToArray();

                foreach (var stale in _pendingMacroSlots.Keys.Where(k => !referencedAfterApply.Contains(k) && !_explicitMacroWrites.Contains(k)).ToArray())
                    Log("SKIP stale pending macro #" + stale + " — profile no longer references this slot.");

                // The user's firmware ACKs EEPROM cmd 0x07 but fails to persist
                // events on different attempts. Reconfirm explicitly before every
                // experimental new macro write. Existing macro bindings skip this.
                if (macrosToWrite.Length > 0 && MessageBox.Show(this,
                    "УВАГА: запис НОВИХ подій макросів ще НЕ підтверджено на цій M693.\r\n" +
                    "Попередні спроби повертали ACK, але read-back не збігався.\r\n" +
                    "Перед продовженням створено резервну копію.\r\n" +
                    "Якщо потрібен готовий старий макрос — використовуйте 'Існуючі макроси'.\r\n\r\n" +
                    "Спробувати експериментальний запис?", "Неперевірений запис macro-slot",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    throw new OperationCanceledException("Нові макроси не записувалися: користувач відмовився від експериментального запису.");

                var referencedCombosAfterApply = new HashSet<int>(M693Profile.ReferencedComboSlots(_pendingProfile));
                var combosToWrite = _pendingComboSlots
                    .Where(kv => kv.Key >= 0 && kv.Key < 16 && referencedCombosAfterApply.Contains(kv.Key))
                    .OrderBy(kv => kv.Key)
                    .ToArray();

                foreach (var stale in _pendingComboSlots.Keys.Where(k => !referencedCombosAfterApply.Contains(k)).ToArray())
                    Log("SKIP stale pending combo #" + stale + " — profile no longer references this slot.");

                foreach (var kv in combosToWrite)
                    WriteComboSlotSmart(kv.Key, kv.Value);

                foreach (var kv in macrosToWrite)
                {
                    int slot = kv.Key;
                    WriteMacroSlotSmart(slot, kv.Value);
                }

                WriteDiff(0, live, _pendingProfile, "profile");

                byte[] verify = _device.ReadBytes(0, M693Profile.ProfileLength);
                if (!verify.SequenceEqual(_pendingProfile))
                {
                    throw new IOException("Read-back profile не збігається з тим, що записували. Backup збережено: " + backup);
                }
                foreach (var kv in combosToWrite)
                {
                    VerifyComboSlotSmart(kv.Key, kv.Value);
                    _comboSlots[kv.Key] = _device.ReadBytes((ushort)(M693Profile.ComboBase + kv.Key * M693Profile.ComboSlotSize), M693Profile.ComboSlotSize);
                    _parsedCombos[kv.Key] = ParsedCombo.Parse(kv.Key, _comboSlots[kv.Key]);
                }
                foreach (var kv in macrosToWrite)
                {
                    VerifyMacroSlotSmart(kv.Key, kv.Value);
                    _macroSlots[kv.Key] = _device.ReadBytes((ushort)(M693Profile.MacroBase + kv.Key * M693Profile.MacroSlotSize), M693Profile.MacroSlotSize);
                    _parsedMacros[kv.Key] = ParsedMacro.Parse(kv.Key, _macroSlots[kv.Key]);
                }

                _originalProfile = verify;
                _pendingProfile = (byte[])verify.Clone();
                _pendingMacroSlots.Clear();
                _explicitMacroWrites.Clear();
                _pendingComboSlots.Clear();
                _dirtySensor = _dirtyLed = _dirtyDpi = false;
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


        private static int ComboSemanticLength(byte[] raw)
        {
            if (raw == null || raw.Length < M693Profile.ComboSlotSize) throw new InvalidDataException("Combo slot image invalid.");
            int count = raw[0];
            if (count < 1 || count > 6) throw new InvalidDataException("Combo event count invalid: " + count);
            int len = 2 + count * 3; // count + events + checksum
            if (len > raw.Length) throw new InvalidDataException("Combo block exceeds slot.");
            return len;
        }

        private void WriteComboSlotSmart(int slot, byte[] target)
        {
            ushort baseAddr = (ushort)(M693Profile.ComboBase + slot * M693Profile.ComboSlotSize);
            int len = ComboSemanticLength(target);
            byte[] block = new byte[len];
            Array.Copy(target, 0, block, 0, len);
            Log("WRITE combo #" + slot + " semantic len=" + len + " @0x" + baseAddr.ToString("X4"));
            _device.WriteBytes(baseAddr, block);
            VerifyComboSlotSmart(slot, target);
        }

        private void VerifyComboSlotSmart(int slot, byte[] target)
        {
            ushort baseAddr = (ushort)(M693Profile.ComboBase + slot * M693Profile.ComboSlotSize);
            int len = ComboSemanticLength(target);
            byte[] expected = new byte[len];
            Array.Copy(target, 0, expected, 0, len);
            byte[] got = _device.ReadBytes(baseAddr, len);
            if (!got.SequenceEqual(expected))
                throw new IOException("Read-back combo slot " + slot + " не збігся.");
            byte[] full = _device.ReadBytes(baseAddr, M693Profile.ComboSlotSize);
            ParsedCombo pc = ParsedCombo.Parse(slot, full);
            if (!pc.ChecksumOk)
                throw new IOException("Read-back combo slot " + slot + " має некоректний checksum.");
            Log("VERIFY combo #" + slot + " OK: " + pc.Summary);
        }

        private static int MacroNameBlockLength(byte[] raw)
        {
            if (raw == null || raw.Length < M693Profile.MacroSlotSize) throw new InvalidDataException("Macro slot image invalid.");
            int n = raw[0];
            if (n < 0 || n > 30) throw new InvalidDataException("Macro name length invalid: " + n);
            return 1 + n;
        }

        private static int MacroEventBlockLength(byte[] raw)
        {
            if (raw == null || raw.Length < M693Profile.MacroSlotSize) throw new InvalidDataException("Macro slot image invalid.");
            int count = raw[31];
            if (count < 0 || count > 70) throw new InvalidDataException("Macro event count invalid: " + count);
            int len = 2 + count * 5; // count + events + checksum
            if (31 + len > raw.Length) throw new InvalidDataException("Macro event block exceeds slot.");
            return len;
        }

        private static int FirstDifference(byte[] expected, byte[] actual)
        {
            int n = Math.Min(expected.Length, actual.Length);
            for (int i = 0; i < n; i++) if (expected[i] != actual[i]) return i;
            return expected.Length == actual.Length ? -1 : n;
        }

        // No false "success": compare exact meaningful macro bytes and show the
        // first incorrect address, expected and actual value for the firmware trace.
        private void VerifyMacroSegment(int slot, ushort addr, byte[] expected, string part)
        {
            byte[] got = _device.ReadBytes(addr, expected.Length);
            int off = FirstDifference(expected, got);
            if (off >= 0) {
                string want = off < expected.Length ? expected[off].ToString("X2") : "EOF";
                string actual = off < got.Length ? got[off].ToString("X2") : "EOF";
                Log("MACRO_VERIFY_FAIL slot=" + slot + " part=" + part + " addr=0x" + (addr + off).ToString("X4") +
                    " offset=" + off + " expected=" + want + " actual=" + actual + " len=" + expected.Length);
                int previewStart = Math.Max(0, off - 6);
                int previewLen = Math.Min(20, expected.Length - previewStart);
                Log("Expected region: " + BitConverter.ToString(expected, previewStart, previewLen));
                if (previewStart < got.Length)
                    Log("Actual region: " + BitConverter.ToString(got, previewStart, Math.Min(previewLen, got.Length - previewStart)));
                throw new IOException("Macro #" + slot + ": " + part + " не записався. Адреса 0x" +
                    (addr + off).ToString("X4") + ", очікувано " + want + ", реально " + actual +
                    ". Деталі у Logs/M693_HID.log; профіль НЕ вважається записаним.");
            }
        }

        private void WriteMacroSlotSmart(int slot, byte[] target)
        {
            ushort baseAddr = (ushort)(M693Profile.MacroBase + slot * M693Profile.MacroSlotSize);
            int semanticLen = 31 + MacroEventBlockLength(target);
            int eventCount = target[31];
            ParsedMacro parsed = ParsedMacro.Parse(slot, target);
            if (!parsed.ChecksumOk)
                throw new InvalidDataException("Macro #" + slot + " invalid local event checksum.");
            if (semanticLen > M693Profile.MacroSlotSize)
                throw new InvalidDataException("Macro #" + slot + " exceeds 0x180-byte slot.");

            // Static disassembly of the original user-provided OemDrv.exe:
            // FUN_00410960 serializes a 0x20-byte header + 5 bytes/event.
            // FUN_00410E..00410F writes ONE contiguous image from 0x300 +
            // (slot*0x180), including UTF-16LE name and event checksum.
            // Previous Neutron sent fragmented NAME and EVENTS writes, skipping
            // bytes in between, and encoded names as ASCII instead of UTF-16LE.
            byte[] contiguous = new byte[semanticLen];
            Array.Copy(target, 0, contiguous, 0, semanticLen);
            byte[] before = _device.ReadBytes(baseAddr, semanticLen);
            Log("MACRO_OEM_STREAM slot=" + slot + " addr=0x" + baseAddr.ToString("X4") +
                " len=" + semanticLen + " events=" + eventCount +
                " name=UTF16LE bytes=" + target[0] + " priorEquals=" + before.SequenceEqual(contiguous));
            if (!before.SequenceEqual(contiguous))
                _device.WriteBytes(baseAddr, contiguous);
            // Never treat an ACK as proof of persistence.
            VerifyMacroSegment(slot, baseAddr, contiguous, "OEM_CONTIGUOUS_READBACK");
            VerifyMacroSlotSmart(slot, target);
        }

        private void VerifyMacroSlotSmart(int slot, byte[] target)
        {
            ushort baseAddr = (ushort)(M693Profile.MacroBase + slot * M693Profile.MacroSlotSize);
            int nameLen = MacroNameBlockLength(target);
            int eventLen = MacroEventBlockLength(target);
            byte[] expectedName = new byte[nameLen];
            byte[] expectedEvents = new byte[eventLen];
            Array.Copy(target, 0, expectedName, 0, nameLen);
            Array.Copy(target, 31, expectedEvents, 0, eventLen);
            VerifyMacroSegment(slot, baseAddr, expectedName, "NAME_FINAL");
            VerifyMacroSegment(slot, (ushort)(baseAddr + 31), expectedEvents, "EVENTS_FINAL");
            byte[] full = _device.ReadBytes(baseAddr, M693Profile.MacroSlotSize);
            ParsedMacro pm = ParsedMacro.Parse(slot, full);
            if (!pm.ChecksumOk)
                throw new IOException("Read-back macro slot " + slot + " має некоректний checksum.");
            Log("VERIFY macro #" + slot + " OK: name=\"" + pm.Name + "\" events=" + pm.EventCount);
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

    // Local-only RGB scene definition. No arbitrary multi-color animation is stored onboard.
    internal sealed class HardwareLightingStyle
    {
        public string Name { get; set; }
        public int Mode { get; set; }
        public int Red { get; set; }
        public int Green { get; set; }
        public int Blue { get; set; }
        public int Speed { get; set; }
        public int Brightness { get; set; }
    }

    internal sealed class ExportedProfile
    {
        public string Format { get; set; }
        public string ExportedUtc { get; set; }
        public string Device { get; set; }
        public string ProfileHex { get; set; }
        public Dictionary<string, string> Macros { get; set; }
        public Dictionary<string, string> Combos { get; set; }
        public int ActiveOnboardProfile { get; set; }
        public List<string> PaletteColors { get; set; }
        public string PaletteEffect { get; set; }
        public int PalettePeriodMs { get; set; }
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
        public const int ComboBase = 0x100;
        public const int ComboSlotSize = 0x20;
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

        public static IEnumerable<int> ReferencedComboSlots(byte[] p)
        {
            if (p == null) yield break;
            for (int i = 0; i < 16; i++)
            {
                byte[] r = GetButtonRecord(p, i);
                if (r[0] == 5) yield return i;
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
            // Compatible with UTF-16LE OEM names and legacy ASCII Neutron names.
            bool isUnicode = n >= 2 && (n % 2 == 0);
            if (isUnicode)
            {
                int zeroHigh = 0;
                for (int k = 2; k <= n; k += 2) if (raw[k] == 0) zeroHigh++;
                isUnicode = zeroHigh >= Math.Max(1, n / 4);
            }
            m.Name = (isUnicode ? Encoding.Unicode.GetString(raw, 1, n) : Encoding.ASCII.GetString(raw, 1, n)).TrimEnd('\0');
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

        public MacroEventRaw Clone()
        {
            return new MacroEventRaw { B0 = B0, B1 = B1, B2 = B2, Delay = Delay };
        }
    }

    internal static class MacroBuilder
    {
        public static byte[] BuildFullSlot(int slot, string name, int mouseMask, int[] intervals, byte[] downTemplate, byte[] upTemplate)
        {
            if (intervals == null || intervals.Length == 0) throw new ArgumentException("intervals");
            if (intervals.Length * 2 > 70) throw new ArgumentException("Забагато macro events");
            byte[] raw = new byte[M693Profile.MacroSlotSize];
            // The OEM encodes macro names in UTF-16LE; 15 chars / 30 bytes.
            SetOfficialMacroName(raw, name ?? "NEUTRON");

            int count = intervals.Length * 2;
            raw[31] = (byte)count;
            int o = 32;
            var rnd = new Random(Environment.TickCount ^ slot);
            foreach (int interval in intervals)
            {
                int down = Math.Min(Math.Max(3, 5 + rnd.Next(-1, 3)), Math.Max(3, interval - 1));
                int up = Math.Max(1, interval - down);
                byte[] d = downTemplate != null && downTemplate.Length >= 3 ? downTemplate : new byte[] { 0x84, (byte)mouseMask, 0x00 };
                byte[] u = upTemplate != null && upTemplate.Length >= 3 ? upTemplate : new byte[] { 0x44, (byte)mouseMask, 0x00 };
                WriteEvent(raw, ref o, d[0], d[1], d[2], (ushort)down);
                WriteEvent(raw, ref o, u[0], u[1], u[2], (ushort)up);
            }
            byte[] block = new byte[1 + count * 5];
            Array.Copy(raw, 31, block, 0, block.Length);
            raw[o] = M693Profile.Checksum55(block);
            return raw;
        }

        public static byte[] BuildCustomSlot(int slot, string name, List<MacroEventRaw> events)
        {
            if (events == null || events.Count < 1) throw new ArgumentException("events");
            if (events.Count > 70) throw new ArgumentException("Максимум 70 macro events");
            byte[] raw = new byte[M693Profile.MacroSlotSize];
            SetOfficialMacroName(raw, string.IsNullOrWhiteSpace(name) ? ("MACRO_" + slot) : name);
            raw[31] = (byte)events.Count;
            int o = 32;
            foreach (MacroEventRaw ev in events)
            {
                WriteEvent(raw, ref o, ev.B0, ev.B1, ev.B2, ev.Delay < 1 ? (ushort)1 : ev.Delay);
            }
            byte[] block = new byte[1 + events.Count * 5];
            Array.Copy(raw, 31, block, 0, block.Length);
            raw[o] = M693Profile.Checksum55(block);
            return raw;
        }

        private static void SetOfficialMacroName(byte[] raw, string name)
        {
            if (name.Length > 15) name = name.Substring(0, 15);
            byte[] nb = Encoding.Unicode.GetBytes(name);
            if (nb.Length > 30) Array.Resize(ref nb, 30);
            raw[0] = (byte)nb.Length;
            Array.Copy(nb, 0, raw, 1, nb.Length);
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

        public static byte CodeForDpi(int dpi)
        {
            int idx = Array.IndexOf(AllowedDpiValues, dpi);
            if (idx < 0) idx = 0;
            return Codes[idx];
        }

        public static int DpiFromCodeLowRange(byte code)
        {
            for (int i = 0; i < Codes.Length && i < 36; i++)
                if (Codes[i] == code) return AllowedDpiValues[i];
            return AllowedDpiValues[0];
        }

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


    internal sealed class ComboEventRaw
    {
        public byte B0, B1, B2;
        public ComboEventRaw Clone() { return new ComboEventRaw { B0 = B0, B1 = B1, B2 = B2 }; }
    }

    internal sealed class ParsedCombo
    {
        public int Slot;
        public bool ChecksumOk;
        public List<ComboEventRaw> Events = new List<ComboEventRaw>();
        public string Summary = "—";

        public static ParsedCombo Parse(int slot, byte[] raw)
        {
            var c = new ParsedCombo { Slot = slot };
            if (raw == null || raw.Length < 2) return c;
            int count = raw[0];
            if (count < 1 || count > 6 || 1 + count * 3 >= raw.Length) return c;
            for (int i = 0; i < count; i++)
            {
                int o = 1 + i * 3;
                c.Events.Add(new ComboEventRaw { B0 = raw[o], B1 = raw[o + 1], B2 = raw[o + 2] });
            }
            int checkAt = 1 + count * 3;
            byte[] block = new byte[1 + count * 3];
            Array.Copy(raw, 0, block, 0, block.Length);
            c.ChecksumOk = raw[checkAt] == M693Profile.Checksum55(block);
            c.Summary = string.Join(" + ", c.Events.Where((x, i) => i % 2 == 0).Select(ComboEventCodec.Describe).Distinct().ToArray());
            if (string.IsNullOrWhiteSpace(c.Summary)) c.Summary = count + " events";
            return c;
        }
    }

    internal static class ComboBuilder
    {
        public static byte[] Build(List<ComboEventRaw> events)
        {
            if (events == null || events.Count < 1 || events.Count > 6) throw new ArgumentException("JM03 combo supports 1..6 events");
            byte[] raw = new byte[M693Profile.ComboSlotSize];
            raw[0] = (byte)events.Count;
            int o = 1;
            foreach (ComboEventRaw e in events)
            {
                raw[o++] = e.B0; raw[o++] = e.B1; raw[o++] = e.B2;
            }
            byte[] block = new byte[1 + events.Count * 3];
            Array.Copy(raw, 0, block, 0, block.Length);
            raw[o] = M693Profile.Checksum55(block);
            return raw;
        }
    }

    internal sealed class HidKeyChoice
    {
        public string Name;
        public ushort Usage;
        public override string ToString() { return Name; }
    }

    internal sealed class HidModifierChoice
    {
        public string Name;
        public byte Mask;
        public override string ToString() { return Name; }
    }

    internal static class HidKeyCatalog
    {
        public static readonly List<HidModifierChoice> Modifiers = new List<HidModifierChoice>
        {
            new HidModifierChoice { Name = "Left Ctrl", Mask = 0x01 },
            new HidModifierChoice { Name = "Left Shift", Mask = 0x02 },
            new HidModifierChoice { Name = "Left Alt", Mask = 0x04 },
            new HidModifierChoice { Name = "Left Win", Mask = 0x08 },
            new HidModifierChoice { Name = "Right Ctrl", Mask = 0x10 },
            new HidModifierChoice { Name = "Right Shift", Mask = 0x20 },
            new HidModifierChoice { Name = "Right Alt", Mask = 0x40 },
            new HidModifierChoice { Name = "Right Win", Mask = 0x80 }
        };

        public static readonly List<HidKeyChoice> KeyboardKeys = BuildKeyboardKeys();
        public static readonly List<HidKeyChoice> ConsumerKeys = new List<HidKeyChoice>
        {
            K("Play / Pause", 0x00CD), K("Next Track", 0x00B5), K("Previous Track", 0x00B6), K("Stop", 0x00B7),
            K("Mute", 0x00E2), K("Volume +", 0x00E9), K("Volume -", 0x00EA),
            K("Media Player", 0x0183), K("Email", 0x018A), K("Calculator", 0x0192), K("Explorer / My Computer", 0x0194),
            K("Browser Search", 0x0221), K("Browser Home", 0x0223), K("Browser Back", 0x0224), K("Browser Forward", 0x0225),
            K("Browser Stop", 0x0226), K("Browser Refresh", 0x0227), K("Browser Favorites", 0x022A)
        };

        private static HidKeyChoice K(string n, ushort u) { return new HidKeyChoice { Name = n, Usage = u }; }

        private static List<HidKeyChoice> BuildKeyboardKeys()
        {
            var x = new List<HidKeyChoice>();
            for (int i = 0; i < 26; i++) x.Add(K(((char)('A' + i)).ToString(), (ushort)(0x04 + i)));
            string[] nums = { "1","2","3","4","5","6","7","8","9","0" };
            for (int i = 0; i < nums.Length; i++) x.Add(K(nums[i], (ushort)(0x1E + i)));
            x.AddRange(new [] {
                K("Enter",0x28),K("Esc",0x29),K("Backspace",0x2A),K("Tab",0x2B),K("Space",0x2C),
                K("-",0x2D),K("=",0x2E),K("[",0x2F),K("]",0x30),K("\\",0x31),K(";",0x33),K("'",0x34),
                K("`",0x35),K(",",0x36),K(".",0x37),K("/",0x38),K("Caps Lock",0x39),
                K("F1",0x3A),K("F2",0x3B),K("F3",0x3C),K("F4",0x3D),K("F5",0x3E),K("F6",0x3F),
                K("F7",0x40),K("F8",0x41),K("F9",0x42),K("F10",0x43),K("F11",0x44),K("F12",0x45),
                K("Print Screen",0x46),K("Scroll Lock",0x47),K("Pause",0x48),K("Insert",0x49),K("Home",0x4A),
                K("Page Up",0x4B),K("Delete",0x4C),K("End",0x4D),K("Page Down",0x4E),K("Right Arrow",0x4F),
                K("Left Arrow",0x50),K("Down Arrow",0x51),K("Up Arrow",0x52),K("Num Lock",0x53)
            });
            return x;
        }
    }

    internal static class ComboEventCodec
    {
        public static ComboEventRaw Keyboard(ushort usage, bool down)
        {
            return new ComboEventRaw { B0 = (byte)(down ? 0x81 : 0x41), B1 = (byte)(usage & 0xFF), B2 = (byte)(usage >> 8) };
        }
        public static ComboEventRaw Consumer(ushort usage, bool down)
        {
            return new ComboEventRaw { B0 = (byte)(down ? 0x82 : 0x42), B1 = (byte)(usage & 0xFF), B2 = (byte)(usage >> 8) };
        }
        public static ComboEventRaw Modifier(byte mask, bool down)
        {
            return new ComboEventRaw { B0 = (byte)(down ? 0x80 : 0x40), B1 = mask, B2 = 0 };
        }
        public static string Describe(ComboEventRaw e)
        {
            if (e == null) return "?";
            bool down = (e.B0 & 0x80) != 0;
            if ((e.B0 & 0x01) != 0)
            {
                ushort u = (ushort)(e.B1 | (e.B2 << 8));
                HidKeyChoice k = HidKeyCatalog.KeyboardKeys.FirstOrDefault(x => x.Usage == u);
                return (down ? "↓ " : "↑ ") + (k == null ? ("Key 0x" + u.ToString("X4")) : k.Name);
            }
            if ((e.B0 & 0x02) != 0)
            {
                ushort u = (ushort)(e.B1 | (e.B2 << 8));
                HidKeyChoice k = HidKeyCatalog.ConsumerKeys.FirstOrDefault(x => x.Usage == u);
                return (down ? "↓ " : "↑ ") + (k == null ? ("Media 0x" + u.ToString("X4")) : k.Name);
            }
            if ((e.B0 & 0x07) == 0)
            {
                HidModifierChoice m = HidKeyCatalog.Modifiers.FirstOrDefault(x => x.Mask == e.B1);
                return (down ? "↓ " : "↑ ") + (m == null ? ("Modifier 0x" + e.B1.ToString("X2")) : m.Name);
            }
            return string.Format("{0:X2} {1:X2} {2:X2}", e.B0, e.B1, e.B2);
        }
    }

    internal static class MacroEventCodec
    {
        public static MacroEventRaw Keyboard(ushort usage, bool down, ushort delay)
        {
            return new MacroEventRaw { B0 = (byte)(down ? 0x81 : 0x41), B1 = (byte)(usage & 0xFF), B2 = (byte)(usage >> 8), Delay = delay };
        }
        public static MacroEventRaw Consumer(ushort usage, bool down, ushort delay)
        {
            return new MacroEventRaw { B0 = (byte)(down ? 0x82 : 0x42), B1 = (byte)(usage & 0xFF), B2 = (byte)(usage >> 8), Delay = delay };
        }
        public static MacroEventRaw Modifier(byte mask, bool down, ushort delay)
        {
            return new MacroEventRaw { B0 = (byte)(down ? 0x80 : 0x40), B1 = mask, B2 = 0, Delay = delay };
        }
        public static string Describe(MacroEventRaw e)
        {
            if (e == null) return "?";
            ComboEventRaw c = new ComboEventRaw { B0 = e.B0, B1 = e.B1, B2 = e.B2 };
            int kind = e.B0 & 0x07;
            if (kind == 0x04)
            {
                string n = e.B1 == 1 ? "Left" : e.B1 == 2 ? "Right" : e.B1 == 4 ? "Middle" : e.B1 == 8 ? "Back" : e.B1 == 16 ? "Forward" : ("Mouse 0x" + e.B1.ToString("X2"));
                bool down = (e.B0 & 0x80) != 0;
                return (down ? "↓ Mouse " : "↑ Mouse ") + n;
            }
            return ComboEventCodec.Describe(c);
        }
    }

    internal sealed class BatteryInfo
    {
        public int Percent;
        public bool Charging;
    }

    internal sealed class M693Device : IDisposable
    {
        private SafeFileHandle _featureHandle;
        private SafeFileHandle _inputHandle;
        private FileStream _stream;
        private readonly int _inputReportLen;
        private readonly Action<string> _log;
        public string PathName { get; private set; }
        public string InputPathName { get; private set; }
        public ushort ProductId { get; private set; }
        public string Description { get; private set; }
        public bool IsClosed
        {
            get
            {
                return _featureHandle == null || _featureHandle.IsClosed || _featureHandle.IsInvalid ||
                       _inputHandle == null || _inputHandle.IsClosed || _inputHandle.IsInvalid;
            }
        }

        private M693Device(string featurePath, SafeFileHandle featureHandle, HidInterop.HIDP_CAPS featureCaps,
                           string inputPath, SafeFileHandle inputHandle, HidInterop.HIDP_CAPS inputCaps,
                           ushort pid, Action<string> log)
        {
            PathName = featurePath;
            InputPathName = inputPath;
            _featureHandle = featureHandle;
            _inputHandle = inputHandle;
            ProductId = pid;
            _inputReportLen = Math.Max(17, (int)inputCaps.InputReportByteLength);
            _log = log;
            _stream = new FileStream(_inputHandle, FileAccess.Read, _inputReportLen, true);
            Description = string.Format(
                "VID=25A7 PID={0:X4} | CMD FF02 Feat={1} | RESP FF01 In={2} | {3}",
                pid, featureCaps.FeatureReportByteLength, inputCaps.InputReportByteLength,
                pid == 0xFA7C ? "2.4 GHz" : "USB");
        }

        private sealed class HidCandidate
        {
            public string Path;
            public ushort Pid;
            public HidInterop.HIDP_CAPS Caps;
            public string Group;
        }

        private static string CollectionGroup(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string x = path.ToLowerInvariant();
            int p = x.IndexOf("&col");
            return p >= 0 ? x.Substring(0, p) : x;
        }

        public static M693Device FindAndOpen(Action<string> log)
        {
            Action<string> writeLog = log ?? delegate { };
            var paths = HidInterop.EnumerateHidPaths();
            var all = new List<HidCandidate>();
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
                    writeLog(string.Format("Candidate VID={0:X4} PID={1:X4} UsagePage={2:X4} Usage={3:X4} In={4} Feat={5} path={6}", attr.VendorID, attr.ProductID, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.FeatureReportByteLength, path));
                    all.Add(new HidCandidate { Path = path, Pid = attr.ProductID, Caps = caps, Group = CollectionGroup(path) });
                    h.Dispose(); h = null;
                }
                catch (Exception ex)
                {
                    if (h != null) h.Dispose();
                    writeLog("Candidate error: " + ex.Message);
                }
            }

            // JM03 exposes the vendor protocol as TWO top-level HID collections on Windows:
            // FF02/Feature=17 sends report 0x08, while FF01/Input=17 receives report 0x09.
            // 2.1 incorrectly required both capabilities on one collection, so the exact mouse
            // visible in the user's log (COL07 + COL05) could never become a candidate.
            var command = all.Where(x => x.Caps.FeatureReportByteLength == 17 && x.Caps.UsagePage == 0xFF02).ToList();
            var response = all.Where(x => x.Caps.InputReportByteLength >= 17 && x.Caps.UsagePage == 0xFF01).ToList();

            // Fallbacks for firmware revisions that expose the same lengths on different vendor pages.
            if (command.Count == 0) command = all.Where(x => x.Caps.FeatureReportByteLength == 17).ToList();
            if (response.Count == 0) response = all.Where(x => x.Caps.InputReportByteLength >= 17).ToList();

            foreach (var cmd in command)
            {
                var matches = response
                    .Where(r => r.Pid == cmd.Pid)
                    .OrderByDescending(r => r.Group == cmd.Group ? 2 : 0)
                    .ThenByDescending(r => r.Caps.UsagePage == 0xFF01 ? 1 : 0)
                    .ToList();

                foreach (var inp in matches)
                {
                    SafeFileHandle hf = null;
                    SafeFileHandle hi = null;
                    M693Device d = null;
                    try
                    {
                        writeLog("Pair probe CMD=" + cmd.Caps.UsagePage.ToString("X4") + " / RESP=" + inp.Caps.UsagePage.ToString("X4") + " groupMatch=" + (cmd.Group == inp.Group));
                        hf = HidInterop.Open(cmd.Path, false);
                        if (hf == null || hf.IsInvalid) { if (hf != null) hf.Dispose(); continue; }
                        hi = HidInterop.Open(inp.Path, true);
                        if (hi == null || hi.IsInvalid) { if (hi != null) hi.Dispose(); hf.Dispose(); continue; }

                        d = new M693Device(cmd.Path, hf, cmd.Caps, inp.Path, hi, inp.Caps, cmd.Pid, log);
                        hf = null; hi = null;
                        byte[] probe = d.ReadBytes(0, 2);
                        if (probe.Length == 2 && probe[1] == unchecked((byte)(0x55 - probe[0])))
                        {
                            writeLog("Command/response HID pair підтверджено EEPROM probe: " + probe[0].ToString("X2") + " " + probe[1].ToString("X2"));
                            return d;
                        }
                        writeLog("Pair відповів, але EEPROM checksum pair invalid.");
                        d.Dispose(); d = null;
                    }
                    catch (Exception ex)
                    {
                        writeLog("Pair probe failed: " + ex.Message);
                        if (d != null) d.Dispose();
                    }
                    finally
                    {
                        if (hf != null) hf.Dispose();
                        if (hi != null) hi.Dispose();
                    }
                }
            }

            writeLog("M693 vendor HID знайдено, але робочу FF02/FF01 пару відкрити не вдалося.");
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

        public void SetCurrentProfileIndex(int index)
        {
            if (index < 0 || index > 3) throw new ArgumentOutOfRangeException("index");
            byte[] req = Protocol.BuildData(Protocol.CmdSetCurrentProfile, new byte[] { (byte)index });
            SendFeature(req);
            byte[] resp = WaitResponse(Protocol.CmdSetCurrentProfile, 0, 1200);
            if (resp.Length < 3 || resp[2] != 0) throw new IOException("SET PROFILE status=" + (resp.Length > 2 ? resp[2].ToString() : "short"));
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
                int got = Math.Min(len, (int)resp[5]);
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
            if (!HidInterop.HidD_SetFeature(_featureHandle, p, p.Length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_SetFeature");
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
                    HidInterop.CancelIoEx(_inputHandle, IntPtr.Zero);
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
            _inputHandle = null; // FileStream owns and closes the response handle.
            try { if (_featureHandle != null && !_featureHandle.IsClosed) _featureHandle.Dispose(); } catch { }
            _featureHandle = null;
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
        public const byte CmdSetCurrentProfile = 0x0F;

        public static byte[] BuildSimple(byte cmd) { return NewPacket(cmd, 0, new byte[0]); }
        public static byte[] BuildData(byte cmd, byte[] data) { return NewPacket(cmd, 0, data ?? new byte[0]); }

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
        // Best-effort native dark scrollbar theme on supported Windows 10/11 builds.
        // No requirement on older Windows: any unsupported theme call is ignored.
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        private static void ApplyNativeDarkTheme(Control c)
        {
            if (c == null || c.IsDisposed) return;
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (InvalidOperationException) { }
        }

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

        private static void DrawDarkComboItem(object sender, DrawItemEventArgs e)
        {
            ComboBox cb = sender as ComboBox;
            if (cb == null) return;
            e.DrawBackground();
            Color bg = (e.State & DrawItemState.Selected) != 0 ? Color.FromArgb(103, 31, 34) : Color.FromArgb(26, 26, 29);
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
            string text = e.Index >= 0 && e.Index < cb.Items.Count ? Convert.ToString(cb.Items[e.Index]) : cb.Text;
            TextRenderer.DrawText(e.Graphics, text ?? "", cb.Font, e.Bounds, Color.FromArgb(238, 238, 241),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        }

        private static void ApplyRecursive(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is ScrollableControl || c is DataGridView || c is TextBox || c is ListBox)
                {
                    c.HandleCreated += delegate { ApplyNativeDarkTheme(c); };
                    if (c.IsHandleCreated) ApplyNativeDarkTheme(c);
                }
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
                    b.BackColor = (b.Tag as string) == "primary" ? Color.FromArgb(184, 34, 34) : Panel2;
                    b.ForeColor = Color.White;
                    b.FlatAppearance.MouseOverBackColor = (b.Tag as string) == "primary" ? Color.FromArgb(205, 42, 42) : Color.FromArgb(52, 52, 57);
                    b.FlatAppearance.MouseDownBackColor = Color.FromArgb(128, 28, 28);
                }
                else if (c is TextBox)
                {
                    c.BackColor = Color.FromArgb(19, 19, 21);
                    c.ForeColor = Text;
                }
                else if (c is ComboBox)
                {
                    ComboBox cb = (ComboBox)c;
                    cb.BackColor = Color.FromArgb(26, 26, 29);
                    cb.ForeColor = Text;
                    cb.FlatStyle = FlatStyle.Flat;
                    cb.DrawMode = DrawMode.OwnerDrawFixed;
                    cb.ItemHeight = Math.Max(20, cb.Font.Height + 8);
                    cb.DrawItem += DrawDarkComboItem;
                }
                else if (c is NumericUpDown || c is ListBox)
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
                    g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(32, 32, 36);
                    g.BorderStyle = BorderStyle.None;
                    g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
                    g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
                    g.RowTemplate.Height = 27;
                    g.ColumnHeadersHeight = 30;
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

    internal sealed class BatteryMeter : Control
    {
        private int _value;
        public int Minimum { get; set; }
        public int Maximum { get; set; }
        public int Value
        {
            get { return _value; }
            set { _value = Math.Max(Minimum, Math.Min(Maximum, value)); Invalidate(); }
        }

        public BatteryMeter()
        {
            Minimum = 0; Maximum = 100; DoubleBuffered = true;
            BackColor = Color.FromArgb(18, 18, 20); ForeColor = Color.White;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle r = ClientRectangle;
            if (r.Width <= 2 || r.Height <= 2) return;
            using (var bg = new SolidBrush(Color.FromArgb(17, 17, 19))) e.Graphics.FillRectangle(bg, r);
            int range = Math.Max(1, Maximum - Minimum);
            double f = (double)(Value - Minimum) / range;
            int fill = Math.Max(0, Math.Min(r.Width - 2, (int)Math.Round((r.Width - 2) * f)));
            Color c = Value <= 20 ? Color.FromArgb(220, 65, 65) : Value <= 45 ? Color.FromArgb(232, 165, 52) : Color.FromArgb(61, 190, 92);
            using (var b = new SolidBrush(c)) e.Graphics.FillRectangle(b, 1, 1, fill, r.Height - 2);
            using (var p = new Pen(Color.FromArgb(82, 82, 90))) e.Graphics.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
            string t = Value + "%";
            Size sz = TextRenderer.MeasureText(t, Font);
            TextRenderer.DrawText(e.Graphics, t, Font, new Point(Math.Max(3, (r.Width - sz.Width) / 2), Math.Max(1, (r.Height - sz.Height) / 2)), Color.White);
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
