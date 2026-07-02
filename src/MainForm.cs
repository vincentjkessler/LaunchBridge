using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DevMind.LaunchBridge
{
    public class MainForm : Form
    {
        private readonly Color Cream = Color.FromArgb(247, 243, 235);
        private readonly Color Ink = Color.FromArgb(28, 36, 42);
        private readonly Color Muted = Color.FromArgb(96, 105, 110);
        private readonly Color Accent = Color.FromArgb(18, 173, 193);
        private readonly Color Surface = Color.White;

        private string pendingPackage;
        private readonly string pendingPackageSource;
        private Label openStatus;
        private ListBox extensionList;
        private TextBox extensionInput;
        private TextBox extensionDisplayNameInput;
        private TextBox extensionDescriptionInput;
        private Label defaultExtensionLabel;
        private Label extensionProfileSummary;
        private DataGridView productsGrid;
        private TextBox workRootInput;
        private CheckBox autoLaunchCheck;
        private CheckBox rollbackCheck;
        private CheckBox dingerCheck;
        private CheckBox copyErrorsCheck;
        private CheckBox zipContextCheck;
        private RichTextBox logBox;
        private DataGridView errorGrid;
        private RichTextBox errorDetail;
        private Label errorSummary;
        private TabPage errorCockpitTab;
        private string lastPresentedRuntimeIssueId;
        private CheckBox runtimeMonitorCheck;
        private CheckBox openErrorTabCheck;
        private CheckBox turboLaunchCheck;

        private TextBox builderFolder;
        private TextBox builderProductId;
        private TextBox builderDisplayName;
        private TextBox builderVersion;
        private TextBox builderPublisher;
        private TextBox builderInstallFolder;
        private TextBox builderEntryPoint;
        private ComboBox builderEntryType;
        private TextBox builderArguments;
        private TextBox builderLaunchUrl;
        private NumericUpDown builderNodeMajor;
        private ComboBox builderExtension;
        private TextBox builderOutput;
        private Label builderStatus;

        private readonly Queue<string> packageQueue = new Queue<string>();
        private readonly HashSet<string> queuedPackagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ActivityRecord> activityRecords = new List<ActivityRecord>();
        private bool packageWorkerBusy;
        private DataGridView activityGrid;
        private Label activitySummary;
        private TabControl mainTabs;
        private System.Windows.Forms.Timer activityTimer;
        private int runtimeRefreshQueued;
        private int bulkStopInProgress;
        private Dictionary<string, bool> productRunningStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private string lastProductsSignature;
        private string lastActivitySignature;
        private string lastErrorSignature;
        private readonly bool startHidden;
        private bool allowClose;
        private NotifyIcon trayIcon;
        private readonly ToolTip openStatusToolTip = new ToolTip();
        private readonly ToolTip extensionHelpToolTip = new ToolTip();
        private TabPage extensionsTab;
        private readonly List<ExtensionTourStep> extensionTourSteps = new List<ExtensionTourStep>();
        private readonly List<Panel> extensionTourBorders = new List<Panel>();
        private Form extensionTourWindow;
        private Label extensionTourCountLabel;
        private Label extensionTourTitleLabel;
        private Label extensionTourTextLabel;
        private Button extensionTourNextButton;
        private int extensionTourIndex = -1;

        private sealed class ExtensionTourStep
        {
            public Control Target;
            public string Title;
            public string Text;

            public ExtensionTourStep(Control target, string title, string text)
            {
                Target = target;
                Title = title;
                Text = text;
            }
        }

        public MainForm(string packagePath, bool hiddenStart, string initialPackageSource)
        {
            pendingPackage = packagePath;
            pendingPackageSource = string.IsNullOrWhiteSpace(initialPackageSource) ? "Browser Open file" : initialPackageSource;
            startHidden = hiddenStart;
            Text = "LaunchBridge v0.3.1";
            Width = 1160;
            Height = 980;
            MinimumSize = new Size(1080, 880);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Cream;
            ForeColor = Ink;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            BuildInterface();
            ApplyCrispTextRendering(this);
            ConfigureTrayIcon();
            FormClosing += OnLaunchBridgeFormClosing;
            activityTimer = new System.Windows.Forms.Timer();
            activityTimer.Interval = 3000;
            activityTimer.Tick += delegate { SafeRefreshRuntimeViews(); };
            activityTimer.Start();
            Shown += OnShown;
            FormClosed += delegate
            {
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                }
            };
        }

        private void ConfigureTrayIcon()
        {
            trayIcon = new NotifyIcon();
            try
            {
                Icon applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                trayIcon.Icon = applicationIcon ?? SystemIcons.Application;
            }
            catch { trayIcon.Icon = SystemIcons.Application; }
            trayIcon.Text = "LaunchBridge — Turbo Launch ready";
            trayIcon.Visible = LaunchBridgeCore.TurboLaunchEnabled;
            trayIcon.DoubleClick += delegate { ActivatePrimaryWindow(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem openItem = new ToolStripMenuItem("Open LaunchBridge");
            openItem.Click += delegate { ActivatePrimaryWindow(); };
            menu.Items.Add(openItem);
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit LaunchBridge");
            exitItem.Click += delegate
            {
                allowClose = true;
                Application.Exit();
            };
            menu.Items.Add(exitItem);
            trayIcon.ContextMenuStrip = menu;
        }

        private void OnLaunchBridgeFormClosing(object sender, FormClosingEventArgs e)
        {
            if (allowClose || !LaunchBridgeCore.TurboLaunchEnabled || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            HideToTray();
        }

        private void HideToTray()
        {
            if (!LaunchBridgeCore.TurboLaunchEnabled) return;
            ShowInTaskbar = false;
            Hide();
            if (trayIcon != null) trayIcon.Visible = true;
        }

        private void SafeRefreshRuntimeViews()
        {
            if (IsDisposed || Interlocked.CompareExchange(ref bulkStopInProgress, 0, 0) != 0 || Interlocked.Exchange(ref runtimeRefreshQueued, 1) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Dictionary<string, bool> states = null;
                List<RuntimeIssue> issues = null;
                try
                {
                    states = LaunchBridgeCore.CaptureProductRunningStates();
                    issues = LaunchBridgeCore.RuntimeIssuesSnapshot();
                }
                catch (Exception ex)
                {
                    LaunchBridgeCore.Log("Background runtime refresh warning: " + ex.Message);
                }

                try
                {
                    if (IsDisposed || !IsHandleCreated)
                    {
                        Interlocked.Exchange(ref runtimeRefreshQueued, 0);
                        return;
                    }
                    BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            if (states != null && Interlocked.CompareExchange(ref bulkStopInProgress, 0, 0) == 0)
                            {
                                productRunningStates = states;
                                SynchronizeRecoveredActivities(states);
                            }
                            RefreshActivityGrid();
                            RefreshProducts();
                            RefreshErrorCockpit(issues);
                        }
                        catch (InvalidOperationException ex)
                        {
                            LaunchBridgeCore.Log("Runtime refresh recovered from a concurrent collection change: " + ex.Message);
                        }
                        catch (Exception ex)
                        {
                            LaunchBridgeCore.Log("Runtime refresh warning: " + ex.Message);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref runtimeRefreshQueued, 0);
                        }
                    }));
                }
                catch
                {
                    Interlocked.Exchange(ref runtimeRefreshQueued, 0);
                }
            });
        }

        private bool IsProductRunningFast(ProductRecord product)
        {
            if (product == null) return false;
            bool running;
            if (!string.IsNullOrWhiteSpace(product.ProductId) && productRunningStates.TryGetValue(product.ProductId, out running)) return running;
            return LaunchBridgeCore.IsTrackedProductProcessRunning(product);
        }

        private void SynchronizeRecoveredActivities(Dictionary<string, bool> states)
        {
            if (states == null) return;
            foreach (ProductRecord product in LaunchBridgeCore.InstalledProductsSnapshot())
            {
                bool running;
                if (product == null || string.IsNullOrWhiteSpace(product.ProductId) || !states.TryGetValue(product.ProductId, out running) || !running) continue;
                bool alreadyTracked = activityRecords.Any(x => string.Equals(x.ProductId, product.ProductId, StringComparison.OrdinalIgnoreCase) &&
                    (x.Status == "Running" || x.Status == "Launched" || x.Status == "Running without UI" || x.Status == "Installing" || x.Status == "Queued"));
                if (alreadyTracked) continue;

                ActivityRecord activity = new ActivityRecord();
                activity.ActivityId = Guid.NewGuid().ToString("N");
                activity.ProductId = product.ProductId;
                activity.Product = product.DisplayName;
                activity.Version = product.Version;
                activity.Status = "Running";
                activity.ProcessId = product.LastProcessId;
                activity.UiProcessId = product.LastUiProcessId;
                activity.UiTargetId = product.LastUiTargetId;
                activity.QueuedAtUtc = string.IsNullOrWhiteSpace(product.LastLaunchAtUtc) ? DateTime.UtcNow.ToString("o") : product.LastLaunchAtUtc;
                activity.StartedAtUtc = activity.QueuedAtUtc;
                activity.Source = "Recovered by background status scan";
                activity.InstallPath = product.InstallPath;
                activity.Message = "LaunchBridge found this product already running.";
                activityRecords.Add(activity);
            }
        }

        private void BuildInterface()
        {
            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 92;
            header.BackColor = Ink;
            Controls.Add(header);

            Label brand = new Label();
            brand.Text = "LAUNCHBRIDGE";
            brand.ForeColor = Color.White;
            brand.Font = new Font("Segoe UI Semibold", 19F, FontStyle.Bold);
            brand.AutoSize = true;
            brand.UseCompatibleTextRendering = false;
            brand.Location = new Point(28, 17);
            header.Controls.Add(brand);

            Label subtitle = new Label();
            subtitle.Text = "Turn an AI download into a Windows app with one click.";
            subtitle.ForeColor = Color.FromArgb(205, 224, 226);
            subtitle.Font = new Font("Segoe UI", 10.5F);
            subtitle.AutoSize = true;
            subtitle.UseCompatibleTextRendering = false;
            subtitle.Location = new Point(30, 57);
            header.Controls.Add(subtitle);

            mainTabs = new TabControl();
            mainTabs.Dock = DockStyle.Fill;
            mainTabs.Padding = new Point(18, 8);
            mainTabs.Font = new Font("Segoe UI Semibold", 10F);
            Controls.Add(mainTabs);
            mainTabs.BringToFront();

            mainTabs.TabPages.Add(BuildExtensionsTab());
            mainTabs.TabPages.Add(BuildActivityTab());
            errorCockpitTab = BuildErrorCockpitTab();
            mainTabs.TabPages.Add(errorCockpitTab);
            mainTabs.TabPages.Add(BuildInstalledTab());
            mainTabs.TabPages.Add(BuildSettingsTab());
            mainTabs.TabPages.Add(BuildLogsTab());
            mainTabs.SelectedIndex = 0;
        }

        private TabPage NewTab(string name)
        {
            TabPage page = new TabPage(name);
            page.BackColor = Cream;
            page.Padding = new Padding(24);
            return page;
        }

        private TabPage BuildActivityTab()
        {
            TabPage page = NewTab("Activity");
            page.AutoScroll = false;

            Label title = HeaderLabel("Open, install, and run apps");
            title.Location = new Point(28, 18);
            page.Controls.Add(title);

            Label text = BodyLabel("Open a downloaded app file here. LaunchBridge will check it, install it, start it, and show what is running.");
            text.Location = new Point(30, title.Bottom + 10);
            text.MaximumSize = new Size(900, 0);
            page.Controls.Add(text);

            Panel intake = new Panel();
            intake.BackColor = Surface;
            intake.BorderStyle = BorderStyle.FixedSingle;
            intake.Location = new Point(30, text.Bottom + 14);
            intake.Size = new Size(920, 62);
            intake.AllowDrop = true;
            intake.DragEnter += OnDragEnter;
            intake.DragDrop += OnDragDrop;
            page.Controls.Add(intake);

            Button choose = AccentButton("Open package");
            choose.Location = new Point(12, 13);
            choose.Width = 130;
            choose.Click += delegate { ChoosePackage(); };
            intake.Controls.Add(choose);

            Button downloads = SecondaryButton("Open Downloads");
            downloads.Location = new Point(152, 13);
            downloads.Width = 140;
            downloads.Click += delegate
            {
                string downloadsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Process.Start("explorer.exe", downloadsFolder);
            };
            intake.Controls.Add(downloads);

            openStatus = new Label();
            openStatus.Text = LaunchBridgeCore.TurboLaunchEnabled
                ? "TURBO READY. Browser Open file requests route here immediately."
                : "Ready. Drop a package here or use Open package.";
            openStatus.Font = new Font("Segoe UI Semibold", 9.5F);
            openStatus.ForeColor = Ink;
            openStatus.BackColor = Color.FromArgb(226, 244, 246);
            openStatus.Padding = new Padding(10, 0, 10, 0);
            openStatus.AutoEllipsis = true;
            openStatus.TextAlign = ContentAlignment.MiddleLeft;
            intake.Controls.Add(openStatus);
            openStatusToolTip.SetToolTip(openStatus, openStatus.Text);

            activitySummary = new Label();
            activitySummary.Text = "Queue empty. No tracked products are running.";
            activitySummary.Font = new Font("Segoe UI Semibold", 10F);
            activitySummary.ForeColor = Ink;
            activitySummary.BackColor = Color.FromArgb(226, 244, 246);
            activitySummary.Location = new Point(30, intake.Bottom + 12);
            activitySummary.Size = new Size(920, 48);
            activitySummary.Padding = new Padding(14);
            page.Controls.Add(activitySummary);

            Panel workspace = new Panel();
            workspace.Location = new Point(30, activitySummary.Bottom + 12);
            workspace.Size = new Size(920, 430);
            workspace.BackColor = Cream;
            page.Controls.Add(workspace);

            Panel actionBar = new Panel();
            actionBar.Location = new Point(0, 382);
            actionBar.Size = new Size(920, 48);
            actionBar.BackColor = Cream;
            workspace.Controls.Add(actionBar);

            activityGrid = new DataGridView();
            activityGrid.Location = new Point(0, 0);
            activityGrid.Size = new Size(920, 372);
            activityGrid.ReadOnly = true;
            activityGrid.AllowUserToAddRows = false;
            activityGrid.AllowUserToDeleteRows = false;
            activityGrid.AllowUserToResizeColumns = true;
            activityGrid.AllowUserToResizeRows = false;
            activityGrid.AutoGenerateColumns = false;
            activityGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            activityGrid.ScrollBars = ScrollBars.Both;
            activityGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            activityGrid.MultiSelect = false;
            activityGrid.BackgroundColor = Surface;
            activityGrid.BorderStyle = BorderStyle.FixedSingle;
            activityGrid.RowHeadersVisible = false;
            ApplyReadableGridLayout(activityGrid);

            activityGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Product", DataPropertyName = "Product", HeaderText = "Product", Width = 190, MinimumWidth = 120, Frozen = true });
            activityGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Version", DataPropertyName = "Version", HeaderText = "Version", Width = 76, MinimumWidth = 60 });
            activityGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", DataPropertyName = "Status", HeaderText = "Status", Width = 135, MinimumWidth = 90 });
            activityGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "AppPID", DataPropertyName = "AppPID", HeaderText = "App PID", Width = 78, MinimumWidth = 65 });
            activityGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "UI", DataPropertyName = "UI", HeaderText = "UI target", Width = 230, MinimumWidth = 120 });
            activityGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", DataPropertyName = "Source", HeaderText = "Source", Width = 190, MinimumWidth = 110 });
            activityGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Started", DataPropertyName = "Started", HeaderText = "Started", Width = 205, MinimumWidth = 130 });
            activityGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Message", DataPropertyName = "Message", HeaderText = "Message", Width = 420, MinimumWidth = 180 });
            activityGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ActivityId", DataPropertyName = "ActivityId", HeaderText = "ActivityId", Visible = false });
            workspace.Controls.Add(activityGrid);

            Button relaunch = AccentButton("Launch selected");
            relaunch.Location = new Point(0, 5);
            relaunch.Width = 128;
            relaunch.Click += delegate { RelaunchSelectedActivity(); };
            actionBar.Controls.Add(relaunch);

            Button closeUi = SecondaryButton("Close app tab");
            closeUi.Location = new Point(138, 5);
            closeUi.Width = 138;
            closeUi.Click += delegate { CloseSelectedActivityUi(); };
            actionBar.Controls.Add(closeUi);

            Button stop = SecondaryButton("Stop app");
            stop.Location = new Point(286, 5);
            stop.Width = 120;
            stop.Click += delegate { StopSelectedActivity(); };
            actionBar.Controls.Add(stop);

            Button kill = SecondaryButton("Force kill");
            kill.Location = new Point(416, 5);
            kill.Width = 105;
            kill.Click += delegate { ForceKillSelectedActivity(); };
            actionBar.Controls.Add(kill);

            Button openActivityFolderButton = SecondaryButton("Open folder");
            openActivityFolderButton.Location = new Point(531, 5);
            openActivityFolderButton.Width = 112;
            openActivityFolderButton.Click += delegate { OpenSelectedActivityFolder(); };
            actionBar.Controls.Add(openActivityFolderButton);

            Button clear = SecondaryButton("Clear finished");
            clear.Location = new Point(653, 5);
            clear.Width = 125;
            clear.Click += delegate
            {
                activityRecords.RemoveAll(x => x.Status == "Failed" || x.Status == "Exited" || x.Status == "Stopped" || x.Status == "Force killed" || x.Status == "Installed");
                RefreshActivityGrid();
            };
            actionBar.Controls.Add(clear);

            Button stopAllActivity = DangerButton("Stop all");
            stopAllActivity.Location = new Point(788, 5);
            stopAllActivity.Width = 132;
            stopAllActivity.Click += delegate { StopAllTrackedProducts(); };
            actionBar.Controls.Add(stopAllActivity);

            Action layoutActivity = delegate
            {
                int contentWidth = Math.Max(920, page.ClientSize.Width - 60);

                intake.Width = contentWidth;
                activitySummary.Width = contentWidth;
                workspace.Width = contentWidth;
                workspace.Height = Math.Max(290, page.ClientSize.Height - workspace.Top - 22);

                openStatus.Location = new Point(downloads.Right + 12, 8);
                openStatus.Size = new Size(Math.Max(170, intake.ClientSize.Width - openStatus.Left - 12), intake.ClientSize.Height - 16);

                actionBar.Location = new Point(0, workspace.ClientSize.Height - 48);
                actionBar.Size = new Size(workspace.ClientSize.Width, 48);
                activityGrid.Size = new Size(workspace.ClientSize.Width, Math.Max(180, actionBar.Top - 10));
            };
            layoutActivity();
            page.SizeChanged += delegate { layoutActivity(); };

            RefreshActivityGrid();
            return page;
        }

        private TabPage BuildErrorCockpitTab()
        {
            TabPage page = NewTab("Problems");
            page.AutoScroll = true;
            page.AutoScrollMinSize = new Size(1020, 680);
            Label title = HeaderLabel("App problems");
            title.Location = new Point(28, 22);
            page.Controls.Add(title);

            Label text = BodyLabel("LaunchBridge watches your apps for crashes, blank screens, missing files, and failed web requests. It saves the details so the problem can be fixed.");
            text.Location = new Point(30, title.Bottom + 10);
            text.MaximumSize = new Size(900, 0);
            page.Controls.Add(text);

            errorSummary = new Label();
            errorSummary.Text = "Runtime monitor ready. No issues captured this session.";
            errorSummary.Font = new Font("Segoe UI Semibold", 10F);
            errorSummary.ForeColor = Ink;
            errorSummary.BackColor = Color.FromArgb(226, 244, 246);
            errorSummary.Location = new Point(30, text.Bottom + 16);
            errorSummary.Size = new Size(920, 50);
            errorSummary.Padding = new Padding(14);
            errorSummary.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(errorSummary);

            errorGrid = new DataGridView();
            errorGrid.Location = new Point(30, errorSummary.Bottom + 16);
            errorGrid.Size = new Size(920, 220);
            errorGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            errorGrid.ReadOnly = true;
            errorGrid.AllowUserToAddRows = false;
            errorGrid.AllowUserToDeleteRows = false;
            errorGrid.AllowUserToResizeRows = false;
            errorGrid.AllowUserToResizeColumns = true;
            errorGrid.MultiSelect = false;
            errorGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            errorGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            errorGrid.ScrollBars = ScrollBars.Both;
            errorGrid.BackgroundColor = Color.White;
            errorGrid.BorderStyle = BorderStyle.FixedSingle;
            errorGrid.RowHeadersVisible = false;
            ApplyReadableGridLayout(errorGrid);
            errorGrid.Columns.Add("Time", "Time");
            errorGrid.Columns.Add("Product", "Product");
            errorGrid.Columns.Add("Severity", "Severity");
            errorGrid.Columns.Add("Type", "Type");
            errorGrid.Columns.Add("Message", "Message");
            errorGrid.Columns[0].Width = 95;
            errorGrid.Columns[0].MinimumWidth = 75;
            errorGrid.Columns[0].Frozen = true;
            errorGrid.Columns[1].Width = 190;
            errorGrid.Columns[1].MinimumWidth = 120;
            errorGrid.Columns[1].Frozen = true;
            errorGrid.Columns[2].Width = 90;
            errorGrid.Columns[2].MinimumWidth = 70;
            errorGrid.Columns[3].Width = 145;
            errorGrid.Columns[3].MinimumWidth = 95;
            errorGrid.Columns[4].Width = 560;
            errorGrid.Columns[4].MinimumWidth = 220;
            errorGrid.SelectionChanged += delegate { ShowSelectedRuntimeIssue(); };
            page.Controls.Add(errorGrid);

            errorDetail = new RichTextBox();
            errorDetail.Location = new Point(30, errorGrid.Bottom + 16);
            errorDetail.Size = new Size(920, 150);
            errorDetail.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            errorDetail.Multiline = true;
            errorDetail.ScrollBars = RichTextBoxScrollBars.ForcedBoth;
            errorDetail.WordWrap = false;
            errorDetail.ReadOnly = true;
            errorDetail.Font = new Font("Consolas", 9F);
            page.Controls.Add(errorDetail);

            Button copyPacket = AccentButton("Copy problem details");
            copyPacket.Location = new Point(30, errorDetail.Bottom + 16);
            copyPacket.Width = 150;
            copyPacket.Click += delegate { CopySelectedRuntimeIssue(true); };
            page.Controls.Add(copyPacket);

            Button openChat = SecondaryButton("Copy + ChatGPT");
            openChat.Location = new Point(192, errorDetail.Bottom + 16);
            openChat.Width = 145;
            openChat.Click += delegate { OpenSelectedIssueInChatGPT(); };
            page.Controls.Add(openChat);

            Button openFolder = SecondaryButton("Open product folder");
            openFolder.Location = new Point(349, errorDetail.Bottom + 16);
            openFolder.Width = 155;
            openFolder.Click += delegate { OpenSelectedIssueFolder(); };
            page.Controls.Add(openFolder);

            Button openLogs = SecondaryButton("Open problem logs");
            openLogs.Location = new Point(516, errorDetail.Bottom + 16);
            openLogs.Width = 135;
            openLogs.Click += delegate { Process.Start("explorer.exe", Path.Combine(LaunchBridgeCore.AppRoot, "runtime-issues")); };
            page.Controls.Add(openLogs);

            Button repairBundle = SecondaryButton("Open fix files");
            repairBundle.Location = new Point(663, errorDetail.Bottom + 16);
            repairBundle.Width = 145;
            repairBundle.Click += delegate { OpenSelectedRepairBundle(); };
            page.Controls.Add(repairBundle);

            Button clear = SecondaryButton("Clear list");
            clear.Location = new Point(820, errorDetail.Bottom + 16);
            clear.Width = 125;
            clear.Click += delegate
            {
                LaunchBridgeCore.ClearRuntimeIssues();
                lastPresentedRuntimeIssueId = null;
                RefreshErrorCockpit();
            };
            page.Controls.Add(clear);

            RefreshErrorCockpit();
            return page;
        }

        private TabPage BuildExtensionsTab()
        {
            TabPage page = NewTab("Extensions");
            extensionsTab = page;
            extensionTourSteps.Clear();
            page.AutoScroll = true;
            page.AutoScrollMinSize = new Size(1020, 900);

            Label title = HeaderLabel("Create a file ending for your app");
            title.Location = new Point(28, 24);
            page.Controls.Add(title);

            Button startTour = SecondaryButton("Show me around");
            startTour.Location = new Point(790, 24);
            startTour.Width = 160;
            startTour.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            startTour.Click += delegate { ShowExtensionTour(); };
            page.Controls.Add(startTour);

            Label text = BodyLabel("A file ending is the part after the last dot, like .badmoth. It tells Windows to send that file to LaunchBridge.");
            text.Location = new Point(30, title.Bottom + 10);
            text.MaximumSize = new Size(900, 0);
            page.Controls.Add(text);

            Panel meaningCard = new Panel();
            meaningCard.BackColor = Color.FromArgb(226, 244, 246);
            meaningCard.Location = new Point(30, text.Bottom + 16);
            meaningCard.Size = new Size(920, 128);
            meaningCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(meaningCard);

            Label meaningTitle = SmallLabel("WHAT THESE WORDS MEAN");
            meaningTitle.Location = new Point(20, 14);
            meaningCard.Controls.Add(meaningTitle);

            Label meaningLeft = BodyLabel(
                "File ending: The letters after the last dot, such as .badmoth.\r\n" +
                "Name: The label you will see inside LaunchBridge.\r\n" +
                "What it opens: A short note about the kind of app.");
            meaningLeft.Location = new Point(20, 42);
            meaningLeft.AutoSize = false;
            meaningLeft.Size = new Size(420, 76);
            meaningCard.Controls.Add(meaningLeft);

            Label meaningRight = BodyLabel(
                "Build prompt: Text you paste into your AI to get the app file.\r\n" +
                "Icon prompt: A separate request that asks the AI for a picture.\r\n" +
                "Default: The file ending LaunchBridge uses first.");
            meaningRight.Location = new Point(470, 42);
            meaningRight.AutoSize = false;
            meaningRight.Size = new Size(420, 76);
            meaningRight.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            meaningCard.Controls.Add(meaningRight);

            Panel createCard = new Panel();
            createCard.BackColor = Surface;
            createCard.Location = new Point(30, meaningCard.Bottom + 16);
            createCard.Size = new Size(920, 150);
            createCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(createCard);

            Label extLabel = SmallLabel("FILE ENDING");
            extLabel.Location = new Point(22, 18);
            createCard.Controls.Add(extLabel);
            extensionInput = new TextBox();
            extensionInput.Text = ".myapp";
            extensionInput.Location = new Point(24, 44);
            extensionInput.Size = new Size(150, 28);
            extensionInput.Font = new Font("Consolas", 11F);
            createCard.Controls.Add(extensionInput);

            Label nameLabel = SmallLabel("NAME");
            nameLabel.Location = new Point(194, 18);
            createCard.Controls.Add(nameLabel);
            extensionDisplayNameInput = new TextBox();
            extensionDisplayNameInput.Text = "My App File";
            extensionDisplayNameInput.Location = new Point(196, 44);
            extensionDisplayNameInput.Size = new Size(240, 28);
            createCard.Controls.Add(extensionDisplayNameInput);

            Label descriptionLabel = SmallLabel("WHAT IT OPENS");
            descriptionLabel.Location = new Point(456, 18);
            createCard.Controls.Add(descriptionLabel);
            extensionDescriptionInput = new TextBox();
            extensionDescriptionInput.Text = "A finished app made by an AI";
            extensionDescriptionInput.Location = new Point(458, 44);
            extensionDescriptionInput.Size = new Size(438, 28);
            extensionDescriptionInput.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            createCard.Controls.Add(extensionDescriptionInput);

            Button create = AccentButton("Create extension");
            create.Location = new Point(24, 94);
            create.Width = 150;
            create.Click += delegate { RegisterTypedExtension(); };
            createCard.Controls.Add(create);

            Label createHint = BodyLabel("LaunchBridge will copy the build prompt. The icon prompt stays separate so the AI can focus on one job at a time.");
            createHint.Location = new Point(190, 99);
            createHint.MaximumSize = new Size(690, 0);
            createCard.Controls.Add(createHint);

            Panel profilesCard = new Panel();
            profilesCard.BackColor = Surface;
            profilesCard.Location = new Point(30, createCard.Bottom + 16);
            profilesCard.Size = new Size(920, 430);
            profilesCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(profilesCard);

            Label listLabel = SmallLabel("YOUR FILE ENDINGS");
            listLabel.Location = new Point(24, 16);
            profilesCard.Controls.Add(listLabel);

            extensionList = new ListBox();
            extensionList.Location = new Point(24, 42);
            extensionList.Size = new Size(270, 312);
            extensionList.Font = new Font("Consolas", 11F);
            extensionList.SelectedIndexChanged += delegate { ShowSelectedExtensionProfile(); };
            profilesCard.Controls.Add(extensionList);

            Panel info = new Panel();
            info.Location = new Point(316, 24);
            info.Size = new Size(580, 330);
            info.BackColor = Color.FromArgb(241, 247, 247);
            info.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            profilesCard.Controls.Add(info);

            defaultExtensionLabel = new Label();
            defaultExtensionLabel.Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold);
            defaultExtensionLabel.ForeColor = Ink;
            defaultExtensionLabel.Location = new Point(20, 18);
            defaultExtensionLabel.Size = new Size(530, 32);
            defaultExtensionLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            info.Controls.Add(defaultExtensionLabel);

            extensionProfileSummary = BodyLabel("");
            extensionProfileSummary.Location = new Point(20, 58);
            extensionProfileSummary.AutoSize = false;
            extensionProfileSummary.Size = new Size(530, 112);
            extensionProfileSummary.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            info.Controls.Add(extensionProfileSummary);

            Button openPrompts = AccentButton("See prompts");
            openPrompts.Location = new Point(20, 184);
            openPrompts.Width = 120;
            openPrompts.Click += delegate
            {
                ExtensionProfile profile = SelectedExtensionProfile();
                if (profile != null) ShowExtensionPromptDialog(profile);
            };
            info.Controls.Add(openPrompts);

            Button copyBuild = SecondaryButton("Copy build prompt");
            copyBuild.Location = new Point(152, 184);
            copyBuild.Width = 144;
            copyBuild.Click += delegate
            {
                ExtensionProfile profile = SelectedExtensionProfile();
                if (profile == null) return;
                try { Clipboard.SetText(LaunchBridgeCore.BuildFullAiPrompt(profile)); } catch { }
            };
            info.Controls.Add(copyBuild);

            Button copyIcon = SecondaryButton("Copy icon prompt");
            copyIcon.Location = new Point(308, 184);
            copyIcon.Width = 140;
            copyIcon.Click += delegate
            {
                ExtensionProfile profile = SelectedExtensionProfile();
                if (profile == null) return;
                try { Clipboard.SetText(LaunchBridgeCore.BuildIconAiPrompt(profile)); } catch { }
            };
            info.Controls.Add(copyIcon);

            Button icon = SecondaryButton("Add icon");
            icon.Location = new Point(20, 236);
            icon.Width = 112;
            icon.Click += delegate { ImportSelectedExtensionIcon(); };
            info.Controls.Add(icon);

            Button setDefault = SecondaryButton("Use first");
            setDefault.Location = new Point(144, 236);
            setDefault.Width = 112;
            setDefault.Click += delegate { SetSelectedDefault(); };
            info.Controls.Add(setDefault);

            Button repair = SecondaryButton("Fix Windows link");
            repair.Location = new Point(268, 236);
            repair.Width = 142;
            repair.Click += delegate
            {
                ExtensionProfile profile = SelectedExtensionProfile();
                if (profile != null) LaunchBridgeCore.RegisterExtension(profile.Extension);
                RefreshExtensions();
            };
            info.Controls.Add(repair);

            Button remove = DangerButton("Delete");
            remove.Location = new Point(422, 236);
            remove.Width = 104;
            remove.Click += delegate { RemoveSelectedExtension(); };
            info.Controls.Add(remove);

            Label compatibility = BodyLabel("Old .devmind files still work. New files do not need an extra JSON file.");
            compatibility.Location = new Point(24, 374);
            compatibility.AutoSize = false;
            compatibility.Size = new Size(872, 32);
            compatibility.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            profilesCard.Controls.Add(compatibility);

            Action layoutProfiles = delegate
            {
                info.Size = new Size(Math.Max(420, profilesCard.ClientSize.Width - info.Left - 24), 330);
                defaultExtensionLabel.Width = info.ClientSize.Width - 40;
                extensionProfileSummary.Width = info.ClientSize.Width - 40;
            };
            layoutProfiles();
            profilesCard.SizeChanged += delegate { layoutProfiles(); UpdateExtensionTourHighlight(); };
            page.Scroll += delegate { UpdateExtensionTourHighlight(); };
            page.SizeChanged += delegate { UpdateExtensionTourHighlight(); };

            extensionHelpToolTip.SetToolTip(extensionInput, "Type the ending you want, such as .badmoth.");
            extensionHelpToolTip.SetToolTip(extensionDisplayNameInput, "Type the name you want to see in LaunchBridge.");
            extensionHelpToolTip.SetToolTip(extensionDescriptionInput, "Write one short line about the kind of app this file opens.");
            extensionHelpToolTip.SetToolTip(create, "Save this file ending and connect it to LaunchBridge.");
            extensionHelpToolTip.SetToolTip(copyBuild, "Copy the instructions for your AI to make the app file.");
            extensionHelpToolTip.SetToolTip(copyIcon, "Copy a separate request for your AI to make an icon.");

            extensionTourSteps.Add(new ExtensionTourStep(meaningCard, "Start here", "This box explains the main words on this page."));
            extensionTourSteps.Add(new ExtensionTourStep(extensionInput, "File ending", "Type the letters that come after the last dot. Example: .badmoth"));
            extensionTourSteps.Add(new ExtensionTourStep(extensionDisplayNameInput, "Name", "This is the name LaunchBridge will show for this file type."));
            extensionTourSteps.Add(new ExtensionTourStep(extensionDescriptionInput, "What it opens", "Write one short line that says what kind of app this file will hold."));
            extensionTourSteps.Add(new ExtensionTourStep(create, "Create extension", "Press this when the three boxes are ready. LaunchBridge will connect the file ending to itself."));
            extensionTourSteps.Add(new ExtensionTourStep(extensionList, "Your file endings", "Every file ending you create will appear in this list."));
            extensionTourSteps.Add(new ExtensionTourStep(openPrompts, "See prompts", "This opens both prompts. Use the build prompt first. Use the icon prompt in a new AI request."));
            extensionTourSteps.Add(new ExtensionTourStep(copyBuild, "Copy build prompt", "This copies the words that tell your AI how to return the finished app file."));
            extensionTourSteps.Add(new ExtensionTourStep(copyIcon, "Copy icon prompt", "This copies a separate request for the picture shown on the file."));
            extensionTourSteps.Add(new ExtensionTourStep(icon, "Add icon", "After the AI makes the picture, use this button to add it to the file ending."));
            extensionTourSteps.Add(new ExtensionTourStep(setDefault, "Use first", "This makes the chosen file ending the first one LaunchBridge uses."));
            extensionTourSteps.Add(new ExtensionTourStep(repair, "Fix Windows link", "Use this if double-click or Open file stops sending this file type to LaunchBridge."));

            RefreshExtensions();
            return page;
        }

        private void ShowExtensionTour()
        {
            if (extensionsTab == null || extensionTourSteps.Count == 0) return;
            if (mainTabs != null) mainTabs.SelectedTab = extensionsTab;

            if (extensionTourWindow != null && !extensionTourWindow.IsDisposed)
            {
                extensionTourWindow.BringToFront();
                return;
            }

            extensionTourWindow = new Form();
            extensionTourWindow.Text = "Extensions tour";
            extensionTourWindow.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            extensionTourWindow.StartPosition = FormStartPosition.Manual;
            extensionTourWindow.Size = new Size(440, 240);
            extensionTourWindow.BackColor = Surface;
            extensionTourWindow.ShowInTaskbar = false;
            extensionTourWindow.TopMost = true;
            extensionTourWindow.Font = Font;

            extensionTourCountLabel = SmallLabel("");
            extensionTourCountLabel.Location = new Point(22, 18);
            extensionTourWindow.Controls.Add(extensionTourCountLabel);

            extensionTourTitleLabel = new Label();
            extensionTourTitleLabel.Location = new Point(20, 44);
            extensionTourTitleLabel.Size = new Size(390, 34);
            extensionTourTitleLabel.Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold);
            extensionTourTitleLabel.ForeColor = Ink;
            extensionTourWindow.Controls.Add(extensionTourTitleLabel);

            extensionTourTextLabel = BodyLabel("");
            extensionTourTextLabel.Location = new Point(22, 84);
            extensionTourTextLabel.AutoSize = false;
            extensionTourTextLabel.Size = new Size(388, 82);
            extensionTourWindow.Controls.Add(extensionTourTextLabel);

            Button closeTour = SecondaryButton("Close tour");
            closeTour.Location = new Point(214, 178);
            closeTour.Width = 96;
            closeTour.Click += delegate { extensionTourWindow.Close(); };
            extensionTourWindow.Controls.Add(closeTour);

            extensionTourNextButton = AccentButton("Next");
            extensionTourNextButton.Location = new Point(320, 178);
            extensionTourNextButton.Width = 90;
            extensionTourNextButton.Click += delegate
            {
                if (extensionTourIndex >= extensionTourSteps.Count - 1)
                    extensionTourWindow.Close();
                else
                    ShowExtensionTourStep(extensionTourIndex + 1);
            };
            extensionTourWindow.Controls.Add(extensionTourNextButton);

            Form tour = extensionTourWindow;
            tour.FormClosed += delegate
            {
                if (extensionTourWindow == tour) extensionTourWindow = null;
                ClearExtensionTourHighlight();
                LaunchBridgeCore.Config.ExtensionTourCompleted = true;
                LaunchBridgeCore.SaveConfig();
            };

            extensionTourIndex = 0;
            extensionTourWindow.Show(this);
            ShowExtensionTourStep(0);
        }

        private void ShowExtensionTourStep(int index)
        {
            if (extensionTourWindow == null || extensionTourWindow.IsDisposed) return;
            if (index < 0 || index >= extensionTourSteps.Count) return;

            extensionTourIndex = index;
            ExtensionTourStep step = extensionTourSteps[index];
            extensionTourCountLabel.Text = "STEP " + (index + 1).ToString() + " OF " + extensionTourSteps.Count.ToString();
            extensionTourTitleLabel.Text = step.Title;
            extensionTourTextLabel.Text = step.Text;
            extensionTourNextButton.Text = index == extensionTourSteps.Count - 1 ? "Finish" : "Next";

            try { extensionsTab.ScrollControlIntoView(step.Target); } catch { }
            UpdateExtensionTourHighlight();
        }

        private void UpdateExtensionTourHighlight()
        {
            if (extensionTourWindow == null || extensionTourWindow.IsDisposed) return;
            if (extensionTourIndex < 0 || extensionTourIndex >= extensionTourSteps.Count) return;
            if (extensionsTab == null) return;

            ClearExtensionTourHighlight();
            Control target = extensionTourSteps[extensionTourIndex].Target;
            if (target == null || target.IsDisposed || !target.Visible) return;

            Rectangle targetScreen = target.RectangleToScreen(target.ClientRectangle);
            Rectangle targetOnPage = extensionsTab.RectangleToClient(targetScreen);
            targetOnPage.Inflate(6, 6);
            int thickness = 4;
            Color highlight = Color.FromArgb(255, 183, 55);

            AddExtensionTourBorder(new Rectangle(targetOnPage.Left, targetOnPage.Top, targetOnPage.Width, thickness), highlight);
            AddExtensionTourBorder(new Rectangle(targetOnPage.Left, targetOnPage.Bottom - thickness, targetOnPage.Width, thickness), highlight);
            AddExtensionTourBorder(new Rectangle(targetOnPage.Left, targetOnPage.Top, thickness, targetOnPage.Height), highlight);
            AddExtensionTourBorder(new Rectangle(targetOnPage.Right - thickness, targetOnPage.Top, thickness, targetOnPage.Height), highlight);

            PositionExtensionTourWindow(targetScreen);
        }

        private void AddExtensionTourBorder(Rectangle bounds, Color color)
        {
            Panel border = new Panel();
            border.BackColor = color;
            border.Bounds = bounds;
            border.Enabled = false;
            extensionsTab.Controls.Add(border);
            border.BringToFront();
            extensionTourBorders.Add(border);
        }

        private void ClearExtensionTourHighlight()
        {
            foreach (Panel border in extensionTourBorders.ToArray())
            {
                try
                {
                    if (border.Parent != null) border.Parent.Controls.Remove(border);
                    border.Dispose();
                }
                catch { }
            }
            extensionTourBorders.Clear();
        }

        private void PositionExtensionTourWindow(Rectangle targetScreen)
        {
            if (extensionTourWindow == null || extensionTourWindow.IsDisposed) return;
            Rectangle formScreen = RectangleToScreen(ClientRectangle);
            int gap = 14;
            int x = targetScreen.Right + gap;
            if (x + extensionTourWindow.Width > formScreen.Right)
                x = targetScreen.Left - extensionTourWindow.Width - gap;
            if (x < formScreen.Left) x = formScreen.Right - extensionTourWindow.Width - 24;

            int y = targetScreen.Top;
            if (y + extensionTourWindow.Height > formScreen.Bottom)
                y = formScreen.Bottom - extensionTourWindow.Height - 24;
            if (y < formScreen.Top + 96) y = formScreen.Top + 96;
            extensionTourWindow.Location = new Point(x, y);
        }

        private TabPage BuildBuilderTab()
        {
            TabPage page = NewTab("Package Builder");
            page.AutoScroll = true;
            page.AutoScrollMinSize = new Size(1020, 760);

            Label title = HeaderLabel("Create an installable .devmind package from a finished product folder");
            title.Location = new Point(28, 20);
            page.Controls.Add(title);

            Panel shell = new Panel();
            shell.BackColor = Surface;
            shell.BorderStyle = BorderStyle.None;
            shell.Location = new Point(30, title.Bottom + 18);
            shell.Size = new Size(920, 610);
            shell.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(shell);

            Panel left = new Panel();
            left.BackColor = Surface;
            left.BorderStyle = BorderStyle.None;
            shell.Controls.Add(left);

            Panel right = new Panel();
            right.BackColor = Surface;
            right.BorderStyle = BorderStyle.None;
            shell.Controls.Add(right);

            Action layoutBuilderColumns = delegate
            {
                int inner = 16;
                int gutter = 16;
                int available = shell.ClientSize.Width - (inner * 2) - gutter;
                int leftWidth = Math.Max(392, available / 2);
                int rightWidth = Math.Max(392, available - leftWidth);

                if (leftWidth + rightWidth > available)
                {
                    rightWidth = Math.Max(392, available - 392);
                    leftWidth = available - rightWidth;
                }

                left.Location = new Point(inner, inner);
                left.Size = new Size(leftWidth, shell.ClientSize.Height - (inner * 2));

                right.Location = new Point(left.Right + gutter, inner);
                right.Size = new Size(shell.ClientSize.Width - right.Left - inner, shell.ClientSize.Height - (inner * 2));
            };
            layoutBuilderColumns();
            shell.SizeChanged += delegate { layoutBuilderColumns(); };

            int y = 18;
            builderFolder = AddField(left, "SOURCE PRODUCT FOLDER", ref y, true, delegate { BrowseBuilderFolder(); });
            builderDisplayName = AddField(left, "DISPLAY NAME", ref y, false, null);
            builderProductId = AddField(left, "PRODUCT ID", ref y, false, null);
            builderVersion = AddField(left, "VERSION", ref y, false, null);
            builderVersion.Text = "0.1.0";
            builderPublisher = AddField(left, "PUBLISHER", ref y, false, null);
            builderPublisher.Text = "SphereShift Labs";
            builderInstallFolder = AddField(left, "INSTALL FOLDER NAME", ref y, false, null);

            y = 18;
            builderEntryPoint = AddField(right, "ENTRY POINT INSIDE PRODUCT", ref y, true, delegate { BrowseEntryPoint(); });

            Label typeLabel = SmallLabel("ENTRY TYPE");
            typeLabel.Location = new Point(20, y);
            right.Controls.Add(typeLabel);
            y += 25;
            builderEntryType = new ComboBox();
            builderEntryType.DropDownStyle = ComboBoxStyle.DropDownList;
            builderEntryType.Items.AddRange(new object[] { "auto", "bat", "cmd", "exe", "ps1", "html" });
            builderEntryType.SelectedIndex = 0;
            builderEntryType.Location = new Point(20, y);
            builderEntryType.Width = 190;
            builderEntryType.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            right.Controls.Add(builderEntryType);
            y += 54;

            builderArguments = AddField(right, "ARGUMENTS (OPTIONAL)", ref y, false, null);
            builderLaunchUrl = AddField(right, "LAUNCH URL (OPTIONAL)", ref y, false, null);

            Label nodeLabel = SmallLabel("MINIMUM NODE.JS MAJOR (0 = NONE)");
            nodeLabel.Location = new Point(20, y);
            right.Controls.Add(nodeLabel);
            y += 25;
            builderNodeMajor = new NumericUpDown();
            builderNodeMajor.Minimum = 0;
            builderNodeMajor.Maximum = 99;
            builderNodeMajor.Value = 0;
            builderNodeMajor.Location = new Point(20, y);
            builderNodeMajor.Width = 190;
            builderNodeMajor.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            right.Controls.Add(builderNodeMajor);
            y += 54;

            Label extLabel = SmallLabel("PACKAGE EXTENSION");
            extLabel.Location = new Point(20, y);
            right.Controls.Add(extLabel);
            y += 25;
            builderExtension = new ComboBox();
            builderExtension.DropDownStyle = ComboBoxStyle.DropDown;
            builderExtension.Location = new Point(20, y);
            builderExtension.Width = 190;
            builderExtension.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            right.Controls.Add(builderExtension);
            y += 54;

            builderOutput = AddField(right, "OUTPUT FILE (OPTIONAL)", ref y, true, delegate { BrowseBuilderOutput(); });

            int actionY = y + 8;
            Button build = AccentButton("Build package");
            build.Location = new Point(20, actionY);
            build.Width = 145;
            build.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            build.Click += delegate { BuildPackageFromForm(); };
            right.Controls.Add(build);

            builderStatus = new Label();
            builderStatus.Text = "The builder adds a manifest and SHA-256 hash for every payload file.";
            builderStatus.ForeColor = Muted;
            builderStatus.Font = new Font("Segoe UI", 9F);
            builderStatus.Location = new Point(179, actionY - 2);
            builderStatus.Size = new Size(230, 56);
            builderStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            right.Controls.Add(builderStatus);

            Action layoutBuilderStatus = delegate
            {
                builderStatus.Location = new Point(build.Right + 14, build.Top - 2);
                builderStatus.Size = new Size(Math.Max(180, right.ClientSize.Width - builderStatus.Left - 18), 56);
            };
            layoutBuilderStatus();
            right.SizeChanged += delegate { layoutBuilderStatus(); };

            RefreshBuilderExtensions();
            return page;
        }

        private TabPage BuildInstalledTab()
        {
            TabPage page = NewTab("Installed Apps");
            page.AutoScroll = true;
            page.AutoScrollMinSize = new Size(1020, 660);
            Label title = HeaderLabel("Apps installed by LaunchBridge");
            title.Location = new Point(28, 22);
            page.Controls.Add(title);

            Panel card = new Panel();
            card.Location = new Point(30, title.Bottom + 18);
            card.Size = new Size(920, 560);
            card.BackColor = Surface;
            card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            page.Controls.Add(card);

            productsGrid = new DataGridView();
            productsGrid.Location = new Point(18, 18);
            productsGrid.Size = new Size(884, 360);
            productsGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            productsGrid.ReadOnly = true;
            productsGrid.AllowUserToAddRows = false;
            productsGrid.AllowUserToDeleteRows = false;
            productsGrid.AllowUserToResizeColumns = true;
            productsGrid.AllowUserToResizeRows = false;
            productsGrid.AutoGenerateColumns = false;
            productsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            productsGrid.ScrollBars = ScrollBars.Both;
            productsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            productsGrid.MultiSelect = false;
            productsGrid.BackgroundColor = Surface;
            productsGrid.BorderStyle = BorderStyle.FixedSingle;
            productsGrid.RowHeadersVisible = false;
            ApplyReadableGridLayout(productsGrid);
            productsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Product", DataPropertyName = "Product", HeaderText = "Product", Width = 190, MinimumWidth = 120, Frozen = true });
            productsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Version", DataPropertyName = "Version", HeaderText = "Version", Width = 80, MinimumWidth = 60 });
            productsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", DataPropertyName = "Status", HeaderText = "Status", Width = 135, MinimumWidth = 90 });
            productsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "AppPID", DataPropertyName = "AppPID", HeaderText = "App PID", Width = 80, MinimumWidth = 65 });
            productsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "UI", DataPropertyName = "UI", HeaderText = "UI target", Width = 210, MinimumWidth = 120 });
            productsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Publisher", DataPropertyName = "Publisher", HeaderText = "Publisher", Width = 160, MinimumWidth = 100 });
            productsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Installed", DataPropertyName = "Installed", HeaderText = "Installed", Width = 205, MinimumWidth = 130 });
            productsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", DataPropertyName = "Path", HeaderText = "Install path", Width = 430, MinimumWidth = 200 });
            productsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ProductId", DataPropertyName = "ProductId", HeaderText = "ProductId", Visible = false });
            card.Controls.Add(productsGrid);

            Button launch = AccentButton("Launch selected");
            launch.Location = new Point(18, 396);
            launch.Width = 130;
            launch.Click += delegate { LaunchSelected(); };
            card.Controls.Add(launch);

            Button closeUi = SecondaryButton("Close app tab");
            closeUi.Location = new Point(158, 396);
            closeUi.Width = 140;
            closeUi.Click += delegate { CloseSelectedProductUi(); };
            card.Controls.Add(closeUi);

            Button stop = SecondaryButton("Stop app");
            stop.Location = new Point(308, 396);
            stop.Width = 120;
            stop.Click += delegate { StopSelectedProduct(); };
            card.Controls.Add(stop);

            Button kill = SecondaryButton("Force kill");
            kill.Location = new Point(438, 396);
            kill.Width = 110;
            kill.Click += delegate { ForceKillSelectedProduct(); };
            card.Controls.Add(kill);

            Button folder = SecondaryButton("Open folder");
            folder.Location = new Point(558, 396);
            folder.Width = 115;
            folder.Click += delegate { OpenSelectedFolder(); };
            card.Controls.Add(folder);

            Button stopAllInstalled = DangerButton("Stop all");
            stopAllInstalled.Location = new Point(683, 396);
            stopAllInstalled.Width = 120;
            stopAllInstalled.Click += delegate { StopAllTrackedProducts(); };
            card.Controls.Add(stopAllInstalled);

            Button rollback = SecondaryButton("Roll back");
            rollback.Location = new Point(18, 444);
            rollback.Width = 115;
            rollback.Click += delegate { RollbackSelected(); };
            card.Controls.Add(rollback);

            Button uninstall = SecondaryButton("Uninstall");
            uninstall.Location = new Point(143, 444);
            uninstall.Width = 110;
            uninstall.Click += delegate { UninstallSelected(); };
            card.Controls.Add(uninstall);

            Button refresh = SecondaryButton("Refresh");
            refresh.Location = new Point(263, 444);
            refresh.Width = 105;
            refresh.Click += delegate { RefreshProducts(); RefreshActivityGrid(); };
            card.Controls.Add(refresh);

            Label hint = BodyLabel("Stop app closes one app. Stop all closes every app shown here. These buttons do not uninstall anything.");
            hint.Location = new Point(390, 440);
            hint.Size = new Size(500, 74);
            hint.AutoSize = false;
            card.Controls.Add(hint);

            RefreshProducts();
            return page;
        }

        private TabPage BuildSettingsTab()
        {
            TabPage page = NewTab("Settings");
            page.AutoScroll = true;
            page.AutoScrollMinSize = new Size(1020, 1060);

            Label title = HeaderLabel("How LaunchBridge should work");
            title.Location = new Point(28, 22);
            page.Controls.Add(title);

            Panel smartClickCard = new Panel();
            smartClickCard.Location = new Point(30, title.Bottom + 18);
            smartClickCard.Size = new Size(920, 174);
            smartClickCard.BackColor = Surface;
            page.Controls.Add(smartClickCard);

            Label smartClickTitle = SmallLabel("SMART CLICK — ONE CLICK FROM AI TO APP");
            smartClickTitle.Location = new Point(24, 20);
            smartClickCard.Controls.Add(smartClickTitle);

            Label smartClickDescription = BodyLabel("Click a ZIP app link in ChatGPT, Gemini, or Claude. The browser finishes the download and sends it straight to LaunchBridge. Normal pictures and documents are left alone.");
            smartClickDescription.Location = new Point(26, 48);
            smartClickDescription.AutoSize = false;
            smartClickDescription.Size = new Size(640, 62);
            smartClickCard.Controls.Add(smartClickDescription);

            Label smartClickStatus = BodyLabel(LaunchBridgeCore.GetSmartClickStatusText());
            smartClickStatus.Location = new Point(26, 116);
            smartClickStatus.AutoSize = false;
            smartClickStatus.Size = new Size(630, 38);
            smartClickStatus.Font = new Font("Segoe UI Semibold", 9.5F);
            smartClickCard.Controls.Add(smartClickStatus);

            Button smartClickSetup = AccentButton("Set up Smart Click");
            smartClickSetup.Location = new Point(694, 48);
            smartClickSetup.Size = new Size(194, 42);
            smartClickSetup.Click += delegate
            {
                try
                {
                    string folder = LaunchBridgeCore.PrepareSmartClickSetup();
                    smartClickStatus.Text = LaunchBridgeCore.GetSmartClickStatusText();
                    MessageBox.Show(
                        "One-time setup:\r\n\r\n" +
                        "1. Turn on Developer mode in the browser Extensions page.\r\n" +
                        "2. Click Load unpacked.\r\n" +
                        "3. Choose the folder LaunchBridge just opened.\r\n\r\n" +
                        "The folder path is also copied to your clipboard:\r\n" + folder,
                        "Set up Smart Click",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch (Exception ex) { ShowError(ex); }
            };
            smartClickCard.Controls.Add(smartClickSetup);

            Button smartClickFolder = SecondaryButton("Open helper folder");
            smartClickFolder.Location = new Point(694, 102);
            smartClickFolder.Size = new Size(194, 38);
            smartClickFolder.Click += delegate
            {
                try { Process.Start("explorer.exe", LaunchBridgeCore.SmartClickExtensionFolder); }
                catch (Exception ex) { ShowError(ex); }
            };
            smartClickCard.Controls.Add(smartClickFolder);

            Panel card = new Panel();
            card.Location = new Point(30, smartClickCard.Bottom + 20);
            card.Size = new Size(920, 550);
            card.BackColor = Surface;
            page.Controls.Add(card);

            Label workLabel = SmallLabel("PRODUCT INSTALL ROOT");
            workLabel.Location = new Point(24, 24);
            card.Controls.Add(workLabel);

            workRootInput = new TextBox();
            workRootInput.Text = LaunchBridgeCore.Config.WorkRoot;
            workRootInput.Location = new Point(26, 52);
            workRootInput.Width = 650;
            card.Controls.Add(workRootInput);

            Button browse = SecondaryButton("Browse");
            browse.Location = new Point(690, 49);
            browse.Click += delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    dialog.SelectedPath = workRootInput.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) workRootInput.Text = dialog.SelectedPath;
                }
            };
            card.Controls.Add(browse);

            autoLaunchCheck = AddCheck(card, "Start the app after it is installed", 112, LaunchBridgeCore.Config.AutoLaunch);
            rollbackCheck = AddCheck(card, "Keep a backup before replacing an app", 154, LaunchBridgeCore.Config.KeepRollback);
            dingerCheck = AddCheck(card, "Play a sound when a job works or fails", 196, LaunchBridgeCore.Config.Dinger);
            copyErrorsCheck = AddCheck(card, "Copy error details so they are ready to paste", 238, LaunchBridgeCore.Config.AutoCopyErrors);
            zipContextCheck = AddCheck(card, "Add LaunchBridge to the ZIP right-click menu", 280, LaunchBridgeCore.Config.AddZipContextMenu);
            runtimeMonitorCheck = AddCheck(card, "Watch apps for crashes, blank screens, and failed web requests", 322, LaunchBridgeCore.Config.RuntimeMonitorEnabled.GetValueOrDefault(true));
            openErrorTabCheck = AddCheck(card, "Open Problems and copy the details when an app fails", 364, LaunchBridgeCore.Config.OpenErrorCockpitOnIssue.GetValueOrDefault(true));
            turboLaunchCheck = AddCheck(card, "Fast start: keep LaunchBridge ready in the background", 406, LaunchBridgeCore.Config.TurboLaunchEnabled.GetValueOrDefault(true));

            Button save = AccentButton("Save settings");
            save.Location = new Point(26, 477);
            save.Click += delegate { SaveSettings(); };
            card.Controls.Add(save);

            Button test = SecondaryButton("Run self-test");
            test.Location = new Point(168, 477);
            test.Click += delegate
            {
                MessageBox.Show(LaunchBridgeCore.RunSelfTest(), "LaunchBridge self-test", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            card.Controls.Add(test);

            Panel uninstallCard = new Panel();
            uninstallCard.Location = new Point(30, card.Bottom + 20);
            uninstallCard.Size = new Size(920, 128);
            uninstallCard.BackColor = Surface;
            page.Controls.Add(uninstallCard);

            Label uninstallTitle = SmallLabel("REMOVE LAUNCHBRIDGE");
            uninstallTitle.Location = new Point(24, 20);
            uninstallTitle.ForeColor = Color.FromArgb(162, 45, 45);
            uninstallCard.Controls.Add(uninstallTitle);

            Label uninstallDescription = BodyLabel(
                "Uninstall LaunchBridge, its shortcuts, file associations, settings, logs, rollback snapshots, and managed-browser profiles. Installed products and their project data are not deleted.");
            uninstallDescription.Location = new Point(26, 49);
            uninstallDescription.Size = new Size(650, 58);
            uninstallDescription.AutoSize = false;
            uninstallCard.Controls.Add(uninstallDescription);

            Button uninstallLaunchBridgeButton = DangerButton("Uninstall LaunchBridge");
            uninstallLaunchBridgeButton.Location = new Point(700, 44);
            uninstallLaunchBridgeButton.Size = new Size(190, 42);
            uninstallLaunchBridgeButton.Click += delegate { StartLaunchBridgeUninstall(); };
            uninstallCard.Controls.Add(uninstallLaunchBridgeButton);

            return page;
        }

        private void StartLaunchBridgeUninstall()
        {
            ProductRecord protectedProduct = FindProductInsideLaunchBridgeFolder();
            if (protectedProduct != null)
            {
                MessageBox.Show(
                    "LaunchBridge cannot uninstall safely because this installed product is inside the LaunchBridge application folder:\r\n\r\n" +
                    protectedProduct.DisplayName + "\r\n" + protectedProduct.InstallPath +
                    "\r\n\r\nMove that product outside LaunchBridge's application folder, then run the uninstall again.",
                    "Uninstall blocked to protect an installed product",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            DialogResult answer = MessageBox.Show(
                "This will remove LaunchBridge from this computer.\r\n\r\n" +
                "Removed:\r\n" +
                "- LaunchBridge application files\r\n" +
                "- Desktop and Start menu shortcuts\r\n" +
                "- .devmind and other LaunchBridge file associations\r\n- Direct Extract All commands for those custom package types\r\n" +
                "- LaunchBridge settings, logs, repair packets, rollback snapshots, and managed-browser profiles\r\n" +
                "- The Smart Click native helper connection\r\n\r\n" +
                "Not removed:\r\n" +
                "- Products already installed through LaunchBridge\r\n" +
                "- Product project files and product data\r\n\r\n" +
                "Continue with the uninstall?",
                "Uninstall LaunchBridge",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes) return;

            try
            {
                if (activityTimer != null) activityTimer.Stop();

                try
                {
                    LaunchBridgeCore.RemoveAllAssociations();
                    LaunchBridgeCore.RemoveTurboLaunchStartup();
                    LaunchBridgeCore.RemoveSmartClickNativeHost();
                }
                catch (Exception associationError)
                {
                    LaunchBridgeCore.Log("Association cleanup warning during uninstall: " + associationError.Message);
                }

                string uninstallStage = Path.Combine(
                    Path.GetTempPath(),
                    "LaunchBridgeUninstall",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(uninstallStage);

                string uninstallScriptPath = Path.Combine(uninstallStage, "uninstall-launchbridge.ps1");
                string uninstallLogPath = Path.Combine(
                    Path.GetTempPath(),
                    "LaunchBridge-Uninstall-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");

                File.WriteAllText(
                    uninstallScriptPath,
                    BuildLaunchBridgeUninstallScript(),
                    new UTF8Encoding(false));

                ProcessStartInfo uninstallStartInfo = new ProcessStartInfo();
                uninstallStartInfo.FileName = "powershell.exe";
                uninstallStartInfo.Arguments =
                    "-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " +
                    QuoteProcessArgument(uninstallScriptPath) +
                    " -InstallPath " + QuoteProcessArgument(LaunchBridgeCore.AppRoot) +
                    " -LaunchBridgeProcessId " + Process.GetCurrentProcess().Id.ToString() +
                    " -LogPath " + QuoteProcessArgument(uninstallLogPath) +
                    " -StagePath " + QuoteProcessArgument(uninstallStage);
                uninstallStartInfo.UseShellExecute = false;
                uninstallStartInfo.CreateNoWindow = true;
                uninstallStartInfo.WorkingDirectory = uninstallStage;

                Process uninstallProcess = Process.Start(uninstallStartInfo);
                if (uninstallProcess == null)
                    throw new InvalidOperationException("Windows did not start the LaunchBridge uninstaller.");

                LaunchBridgeCore.Log("Detached self-uninstaller started with PID " + uninstallProcess.Id + ".");
                allowClose = true;
                Application.Exit();
            }
            catch (Exception ex)
            {
                try
                {
                    LaunchBridgeCore.InstallDefaultAssociations();
                    LaunchBridgeCore.ApplyTurboLaunchSettings();
                }
                catch { }
                if (activityTimer != null) activityTimer.Start();
                ShowError(new InvalidOperationException(
                    "LaunchBridge could not start its uninstaller. The application remains installed.\r\n\r\n" + ex.Message,
                    ex));
            }
        }

        private ProductRecord FindProductInsideLaunchBridgeFolder()
        {
            string launchBridgeFolder = Path.GetFullPath(LaunchBridgeCore.AppRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string launchBridgePrefix = launchBridgeFolder + Path.DirectorySeparatorChar;

            foreach (ProductRecord installedProduct in LaunchBridgeCore.InstalledProductsSnapshot())
            {
                if (installedProduct == null || string.IsNullOrWhiteSpace(installedProduct.InstallPath)) continue;
                try
                {
                    string productFolder = Path.GetFullPath(installedProduct.InstallPath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(productFolder, launchBridgeFolder, StringComparison.OrdinalIgnoreCase) ||
                        productFolder.StartsWith(launchBridgePrefix, StringComparison.OrdinalIgnoreCase))
                        return installedProduct;
                }
                catch { }
            }
            return null;
        }

        private static string BuildLaunchBridgeUninstallScript()
        {
            return
@"param(
    [Parameter(Mandatory=$true)][string]$InstallPath,
    [Parameter(Mandatory=$true)][int]$LaunchBridgeProcessId,
    [Parameter(Mandatory=$true)][string]$LogPath,
    [Parameter(Mandatory=$true)][string]$StagePath
)

$ErrorActionPreference = 'Continue'

function Write-UninstallLog([string]$Message) {
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'
    Add-Content -LiteralPath $LogPath -Value (""[$stamp] $Message"") -Encoding UTF8
}

function Show-UninstallMessage([string]$Message, [string]$Title, [string]$Icon) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $messageIcon = [System.Windows.Forms.MessageBoxIcon]$Icon
        [System.Windows.Forms.MessageBox]::Show(
            $Message,
            $Title,
            [System.Windows.Forms.MessageBoxButtons]::OK,
            $messageIcon
        ) | Out-Null
    }
    catch { }
}

try {
    Set-Content -LiteralPath $LogPath -Value 'LaunchBridge detached self-uninstall' -Encoding UTF8
    Write-UninstallLog (""Install path: "" + $InstallPath)
    Write-UninstallLog (""LaunchBridge PID: "" + $LaunchBridgeProcessId)

    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Process -Id $LaunchBridgeProcessId -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
        Start-Sleep -Milliseconds 400
    }

    $remaining = Get-Process -Id $LaunchBridgeProcessId -ErrorAction SilentlyContinue
    if ($remaining) {
        Write-UninstallLog 'LaunchBridge did not exit normally; forcing it closed.'
        Stop-Process -Id $LaunchBridgeProcessId -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }

    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'LaunchBridge.lnk'
    $legacyDesktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'DevMind LaunchBridge.lnk'
    $startMenuRoot = [Environment]::GetFolderPath('StartMenu')
    $startMenuFolder = Join-Path $startMenuRoot 'Programs\LaunchBridge'
    $legacyStartMenuFolder = Join-Path $startMenuRoot 'Programs\DevMind'
    $startMenuShortcut = Join-Path $startMenuFolder 'LaunchBridge.lnk'
    $legacyStartMenuShortcut = Join-Path $legacyStartMenuFolder 'DevMind LaunchBridge.lnk'

    Remove-Item -LiteralPath $desktopShortcut -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $legacyDesktopShortcut -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $startMenuShortcut -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $legacyStartMenuShortcut -Force -ErrorAction SilentlyContinue
    Write-UninstallLog 'Removed LaunchBridge shortcuts.'

    try {
        Remove-Item -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\Classes\SystemFileAssociations\.zip\shell\LaunchBridge' -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\Classes\SystemFileAssociations\.zip\shell\DevMindLaunchBridge' -Recurse -Force -ErrorAction SilentlyContinue
    }
    catch { }

    $removed = $false
    for ($attempt = 1; $attempt -le 40; $attempt++) {
        try {
            if (Test-Path -LiteralPath $InstallPath) {
                Get-ChildItem -LiteralPath $InstallPath -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
                    try { $_.Attributes = 'Normal' } catch { }
                }
                Remove-Item -LiteralPath $InstallPath -Recurse -Force -ErrorAction Stop
            }
            if (-not (Test-Path -LiteralPath $InstallPath)) {
                $removed = $true
                break
            }
        }
        catch {
            Write-UninstallLog (""Removal attempt $attempt failed: "" + $_.Exception.Message)
        }
        Start-Sleep -Milliseconds 500
    }

    if ((Test-Path -LiteralPath $startMenuFolder) -and -not (Get-ChildItem -LiteralPath $startMenuFolder -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $startMenuFolder -Force -ErrorAction SilentlyContinue
    }
    if ((Test-Path -LiteralPath $legacyStartMenuFolder) -and -not (Get-ChildItem -LiteralPath $legacyStartMenuFolder -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $legacyStartMenuFolder -Force -ErrorAction SilentlyContinue
    }

    if (-not $removed) {
        Write-UninstallLog 'ERROR: LaunchBridge installation folder could not be completely removed.'
        Show-UninstallMessage (
            ""LaunchBridge could not be completely removed.`r`n`r`nLog: $LogPath"",
            'LaunchBridge uninstall incomplete',
            'Error'
        )
        try { Start-Process -FilePath 'notepad.exe' -ArgumentList ('""' + $LogPath + '""') | Out-Null } catch { }
        exit 1
    }

    Write-UninstallLog 'LaunchBridge removed successfully. Installed products were left untouched.'
    Show-UninstallMessage (
        'LaunchBridge was removed. Installed products and their project data were left untouched.',
        'LaunchBridge uninstalled',
        'Information'
    )
    Remove-Item -LiteralPath $LogPath -Force -ErrorAction SilentlyContinue

    $stageParent = Split-Path -Parent $StagePath
    $cleanupCommand = 'ping 127.0.0.1 -n 3 >nul & rmdir /s /q ""' + $StagePath + '"" & rmdir ""' + $stageParent + '"" 2>nul'
    Start-Process -FilePath 'cmd.exe' -ArgumentList ('/d /c ' + $cleanupCommand) -WindowStyle Hidden | Out-Null
    exit 0
}
catch {
    Write-UninstallLog (""ERROR: "" + $_.Exception.ToString())
    Show-UninstallMessage (
        ""LaunchBridge uninstall failed.`r`n`r`nLog: $LogPath"",
        'LaunchBridge uninstall failed',
        'Error'
    )
    try { Start-Process -FilePath 'notepad.exe' -ArgumentList ('""' + $LogPath + '""') | Out-Null } catch { }
    exit 1
}
";
        }

        private static string QuoteProcessArgument(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private TabPage BuildLogsTab()
        {
            TabPage page = NewTab("Logs");
            page.AutoScroll = true;
            page.AutoScrollMinSize = new Size(1020, 610);
            Label title = HeaderLabel("LaunchBridge activity log");
            title.Location = new Point(28, 22);
            page.Controls.Add(title);

            logBox = new RichTextBox();
            logBox.Location = new Point(30, title.Bottom + 18);
            logBox.Size = new Size(920, 430);
            logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            logBox.Multiline = true;
            logBox.ScrollBars = RichTextBoxScrollBars.ForcedBoth;
            logBox.ReadOnly = true;
            logBox.WordWrap = false;
            logBox.Font = new Font("Consolas", 9.5F);
            page.Controls.Add(logBox);

            Button refresh = SecondaryButton("Refresh logs");
            refresh.Location = new Point(30, logBox.Bottom + 20);
            refresh.Click += delegate { RefreshLogs(); };
            page.Controls.Add(refresh);

            Button folder = SecondaryButton("Open log folder");
            folder.Location = new Point(170, logBox.Bottom + 20);
            folder.Click += delegate { Process.Start("explorer.exe", LaunchBridgeCore.LogsRoot); };
            page.Controls.Add(folder);

            Button copy = SecondaryButton("Copy latest");
            copy.Location = new Point(310, logBox.Bottom + 20);
            copy.Click += delegate
            {
                try { if (!string.IsNullOrEmpty(logBox.Text)) Clipboard.SetText(logBox.Text); } catch { }
            };
            page.Controls.Add(copy);

            RefreshLogs();
            return page;
        }

        private void OnShown(object sender, EventArgs e)
        {
            SafeRefreshRuntimeViews();
            bool openedWithPackage = !string.IsNullOrWhiteSpace(pendingPackage);
            if (openedWithPackage)
            {
                string file = pendingPackage;
                pendingPackage = null;
                BeginInvoke(new Action(delegate { QueuePackage(file, pendingPackageSource); }));
            }
            if (startHidden && LaunchBridgeCore.TurboLaunchEnabled)
            {
                BeginInvoke(new Action(delegate { HideToTray(); }));
                return;
            }
            if (!openedWithPackage && !LaunchBridgeCore.Config.ExtensionTourCompleted.GetValueOrDefault(false))
                BeginInvoke(new Action(delegate { ShowExtensionTour(); }));
        }

        public void ReceiveExternalRequest(string[] args)
        {
            string package = args == null ? null : args.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("--", StringComparison.Ordinal) && File.Exists(x));
            if (string.IsNullOrWhiteSpace(package))
            {
                ActivatePrimaryWindow();
                SetOpenStatus("READY", "LaunchBridge is already open. New package requests will appear in Activity.", Color.FromArgb(226, 244, 246));
                return;
            }
            string source = args.Any(x => string.Equals(x, "--smart-click", StringComparison.OrdinalIgnoreCase)) ? "Smart Click" : "Browser Open file";
            QueuePackage(package, source);
        }

        private void ActivatePrimaryWindow()
        {
            ShowInTaskbar = true;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            BringToFront();
            Activate();
            TopMost = true;
            TopMost = false;
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) QueuePackage(files[0], "Drag and drop");
        }

        private void ChoosePackage()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Open a LaunchBridge package";
                dialog.Filter = "LaunchBridge packages|*.*";
                dialog.InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (dialog.ShowDialog(this) == DialogResult.OK) QueuePackage(dialog.FileName, "Choose package");
            }
        }

        private void HandlePackage(string path)
        {
            QueuePackage(path, "Manual");
        }

        private void QueuePackage(string path, string source)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                SetOpenStatus("ACTION NEEDED", "Package file not found: " + (path ?? ""), Color.FromArgb(255, 231, 224));
                return;
            }

            string fullPath = Path.GetFullPath(path);
            if (queuedPackagePaths.Contains(fullPath))
            {
                SetOpenStatus("ALREADY QUEUED", Path.GetFileName(fullPath), Color.FromArgb(226, 244, 246));
                return;
            }

            ActivityRecord activity = new ActivityRecord();
            activity.ActivityId = Guid.NewGuid().ToString("N");
            activity.Product = Path.GetFileNameWithoutExtension(fullPath);
            activity.Version = "";
            activity.Status = "Queued";
            activity.QueuedAtUtc = DateTime.UtcNow.ToString("o");
            activity.Source = source;
            activity.PackagePath = fullPath;
            activity.Message = "Waiting for LaunchBridge.";
            activityRecords.Insert(0, activity);

            packageQueue.Enqueue(fullPath);
            queuedPackagePaths.Add(fullPath);
            SetOpenStatus("QUEUED", Path.GetFileName(fullPath) + " — watch Activity for installation and launch status.", Color.FromArgb(226, 244, 246));
            RefreshActivityGrid();
            if (mainTabs != null)
            {
                foreach (TabPage tab in mainTabs.TabPages)
                {
                    if (string.Equals(tab.Text, "Activity", StringComparison.OrdinalIgnoreCase))
                    {
                        mainTabs.SelectedTab = tab;
                        break;
                    }
                }
            }
            StartNextPackage();
        }

        private void StartNextPackage()
        {
            if (packageWorkerBusy || packageQueue.Count == 0) return;
            packageWorkerBusy = true;
            string path = packageQueue.Dequeue();
            ActivityRecord activity = activityRecords.FirstOrDefault(x => string.Equals(x.PackagePath, path, StringComparison.OrdinalIgnoreCase) && x.Status == "Queued");
            if (activity != null)
            {
                activity.Status = "Installing";
                activity.StartedAtUtc = DateTime.UtcNow.ToString("o");
                activity.Message = LaunchBridgeCore.TurboLaunchEnabled ? "Checking Turbo Launch cache." : "Validating manifest and hashes.";
            }
            SetOpenStatus(LaunchBridgeCore.TurboLaunchEnabled ? "TURBO CHECK" : "VALIDATING AND INSTALLING",
                Path.GetFileName(path),
                Color.FromArgb(226, 244, 246));
            RefreshActivityGrid();

            ThreadPool.QueueUserWorkItem(delegate
            {
                InstallResult result;
                try
                {
                    if (!LaunchBridgeCore.TryTurboLaunchInstalledPackage(path, out result))
                        result = LaunchBridgeCore.InstallAndLaunchPackage(path);
                }
                catch (Exception ex)
                {
                    result = new InstallResult();
                    result.Success = false;
                    result.Message = ex.Message;
                    result.LogPath = LaunchBridgeCore.NewLogPath("install-unhandled");
                }
                try
                {
                    BeginInvoke(new Action(delegate { CompletePackage(path, activity, result); }));
                }
                catch { }
            });
        }

        private void CompletePackage(string path, ActivityRecord activity, InstallResult result)
        {
            queuedPackagePaths.Remove(path);
            packageWorkerBusy = false;

            if (activity != null)
            {
                activity.CompletedAtUtc = DateTime.UtcNow.ToString("o");
                activity.Message = result.Message;
                activity.InstallPath = result.InstallPath;
                activity.ProcessId = result.ProcessId;
                activity.UiProcessId = result.UiProcessId;
                activity.UiTargetId = result.UiTargetId;
                if (result.Product != null)
                {
                    activity.ProductId = result.Product.ProductId;
                    activity.Product = result.Product.DisplayName;
                    activity.Version = result.Product.Version;
                }
                if (!result.Success) activity.Status = "Failed";
                else if (result.Product != null && LaunchBridgeCore.IsProductRunning(result.Product)) activity.Status = "Running";
                else if (result.Launched) activity.Status = "Launched";
                else activity.Status = "Installed";
            }

            if (result.Success)
                SetOpenStatus("SUCCESS", result.Message + " — Installed at: " + result.InstallPath, Color.FromArgb(222, 246, 233));
            else
                SetOpenStatus("ACTION NEEDED", result.Message + " — Log: " + result.LogPath, Color.FromArgb(255, 231, 224));
            RefreshProducts();
            RefreshActivityGrid();
            RefreshLogs();
            if (!result.Success)
            {
                ActivatePrimaryWindow();
                MessageBox.Show(result.Message + "\r\n\r\nThe error and log path were copied to your clipboard.", "LaunchBridge could not open the package", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            StartNextPackage();
        }

        private void RegisterTypedExtension()
        {
            try
            {
                ExtensionProfile profile = LaunchBridgeCore.CreateOrUpdateExtensionProfile(
                    extensionInput.Text,
                    extensionDisplayNameInput == null ? null : extensionDisplayNameInput.Text,
                    extensionDescriptionInput == null ? null : extensionDescriptionInput.Text);
                RefreshExtensions();
                SelectExtension(profile.Extension);
                try { Clipboard.SetText(LaunchBridgeCore.BuildFullAiPrompt(profile)); } catch { }
                ShowExtensionPromptDialog(profile);
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private ExtensionProfile SelectedExtensionProfile()
        {
            string item = extensionList == null ? null : extensionList.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(item)) return null;
            int split = item.IndexOf("  ", StringComparison.Ordinal);
            string ext = split > 0 ? item.Substring(0, split).Trim() : item.Trim();
            return LaunchBridgeCore.FindExtensionProfile(ext);
        }

        private void SelectExtension(string extension)
        {
            if (extensionList == null || string.IsNullOrWhiteSpace(extension)) return;
            for (int i = 0; i < extensionList.Items.Count; i++)
            {
                string item = Convert.ToString(extensionList.Items[i]);
                if (item != null && item.StartsWith(extension + " ", StringComparison.OrdinalIgnoreCase))
                {
                    extensionList.SelectedIndex = i;
                    return;
                }
            }
        }

        private void ShowSelectedExtensionProfile()
        {
            ExtensionProfile profile = SelectedExtensionProfile();
            if (defaultExtensionLabel == null || extensionProfileSummary == null) return;
            if (profile == null)
            {
                defaultExtensionLabel.Text = "No profile selected";
                extensionProfileSummary.Text = "";
                return;
            }
            bool isDefault = string.Equals(LaunchBridgeCore.Config.DefaultExtension, profile.Extension, StringComparison.OrdinalIgnoreCase);
            defaultExtensionLabel.Text = profile.Extension + " — " + profile.DisplayName + (isDefault ? "  [DEFAULT]" : "");
            extensionProfileSummary.Text =
                (profile.Description ?? "A finished app made by an AI") + "\r\n\r\n" +
                "Extra JSON file: not needed\r\n" +
                "Windows link: ready\r\n" +
                "Icon: " + (!string.IsNullOrWhiteSpace(profile.IconIcoPath) && File.Exists(profile.IconIcoPath) ? "your icon is set" : "using the LaunchBridge icon");
        }

        private void ShowExtensionPromptDialog(ExtensionProfile profile)
        {
            if (profile == null) return;

            Form dialog = new Form();
            dialog.Text = profile.Extension + " prompts";
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(980, 850);
            dialog.MinimumSize = new Size(820, 700);
            dialog.BackColor = Cream;
            dialog.Font = Font;

            Label heading = HeaderLabel("Prompts for " + profile.Extension);
            heading.Location = new Point(24, 18);
            dialog.Controls.Add(heading);

            Label instruction = BodyLabel("Use the build prompt first. Use the icon prompt in a new AI chat. Keeping them separate helps the AI do one job at a time.");
            instruction.Location = new Point(26, heading.Bottom + 8);
            instruction.MaximumSize = new Size(900, 0);
            dialog.Controls.Add(instruction);

            Panel buildCard = new Panel();
            buildCard.BackColor = Surface;
            buildCard.Location = new Point(26, instruction.Bottom + 16);
            buildCard.Size = new Size(910, 400);
            buildCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            dialog.Controls.Add(buildCard);

            Label buildLabel = SmallLabel("1. BUILD PROMPT");
            buildLabel.Location = new Point(18, 16);
            buildCard.Controls.Add(buildLabel);

            Button copyBuild = AccentButton("Copy build prompt");
            copyBuild.Location = new Point(728, 10);
            copyBuild.Size = new Size(164, 38);
            copyBuild.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            buildCard.Controls.Add(copyBuild);

            Button copyShort = SecondaryButton("Copy short version");
            copyShort.Location = new Point(550, 10);
            copyShort.Size = new Size(166, 38);
            copyShort.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            buildCard.Controls.Add(copyShort);

            RichTextBox buildPrompt = new RichTextBox();
            buildPrompt.Location = new Point(18, 58);
            buildPrompt.Size = new Size(874, 324);
            buildPrompt.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            buildPrompt.Font = new Font("Segoe UI", 10F);
            buildPrompt.Text = LaunchBridgeCore.BuildFullAiPrompt(profile);
            buildPrompt.ReadOnly = true;
            buildPrompt.WordWrap = true;
            buildPrompt.ScrollBars = RichTextBoxScrollBars.ForcedVertical;
            buildCard.Controls.Add(buildPrompt);

            copyBuild.Click += delegate
            {
                string fullPrompt = LaunchBridgeCore.BuildFullAiPrompt(profile);
                buildPrompt.Text = fullPrompt;
                try { Clipboard.SetText(fullPrompt); } catch { }
            };
            copyShort.Click += delegate
            {
                string shortPrompt = LaunchBridgeCore.BuildShortAiPrompt(profile);
                try { Clipboard.SetText(shortPrompt); } catch { }
                buildPrompt.Text = shortPrompt;
            };

            Panel iconCard = new Panel();
            iconCard.BackColor = Surface;
            iconCard.Location = new Point(26, buildCard.Bottom + 16);
            iconCard.Size = new Size(910, 240);
            iconCard.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            dialog.Controls.Add(iconCard);

            Label iconLabel = SmallLabel("2. ICON PROMPT — USE IN A NEW AI CHAT");
            iconLabel.Location = new Point(18, 16);
            iconCard.Controls.Add(iconLabel);

            Button copyIcon = AccentButton("Copy icon prompt");
            copyIcon.Location = new Point(728, 10);
            copyIcon.Size = new Size(164, 38);
            copyIcon.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            iconCard.Controls.Add(copyIcon);

            Button import = SecondaryButton("Add finished icon");
            import.Location = new Point(550, 10);
            import.Size = new Size(166, 38);
            import.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            import.Click += delegate { ImportExtensionIcon(profile); };
            iconCard.Controls.Add(import);

            RichTextBox iconPrompt = new RichTextBox();
            iconPrompt.Location = new Point(18, 58);
            iconPrompt.Size = new Size(874, 164);
            iconPrompt.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            iconPrompt.Font = new Font("Segoe UI", 10F);
            iconPrompt.Text = LaunchBridgeCore.BuildIconAiPrompt(profile);
            iconPrompt.ReadOnly = true;
            iconPrompt.WordWrap = true;
            iconPrompt.ScrollBars = RichTextBoxScrollBars.ForcedVertical;
            iconCard.Controls.Add(iconPrompt);

            copyIcon.Click += delegate
            {
                try { Clipboard.SetText(iconPrompt.Text); } catch { }
            };

            Button done = SecondaryButton("Done");
            done.Location = new Point(846, 772);
            done.Size = new Size(90, 38);
            done.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            done.Click += delegate { dialog.Close(); };
            dialog.Controls.Add(done);

            dialog.ShowDialog(this);
        }

        private void ImportSelectedExtensionIcon()
        {
            ExtensionProfile profile = SelectedExtensionProfile();
            if (profile != null) ImportExtensionIcon(profile);
        }

        private void ImportExtensionIcon(ExtensionProfile profile)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Choose the icon generated for " + profile.Extension;
                dialog.Filter = "Icon and image files|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    LaunchBridgeCore.SetExtensionProfileIcon(profile.Extension, dialog.FileName);
                    RefreshExtensions();
                    SelectExtension(profile.Extension);
                    MessageBox.Show("The icon is now registered for " + profile.Extension + ". Windows Explorer may take a moment to refresh its icon cache.", "Icon installed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { ShowError(ex); }
            }
        }

        private void RemoveSelectedExtension()
        {
            ExtensionProfile profile = SelectedExtensionProfile();
            if (profile == null) return;
            if (MessageBox.Show("Delete the " + profile.Extension + " Extension Profile and remove its Windows association?", "Delete extension profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try
            {
                LaunchBridgeCore.UnregisterExtension(profile.Extension);
                RefreshExtensions();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void SetSelectedDefault()
        {
            ExtensionProfile profile = SelectedExtensionProfile();
            if (profile == null) return;
            LaunchBridgeCore.Config.DefaultExtension = profile.Extension;
            LaunchBridgeCore.SaveConfig();
            RefreshExtensions();
            SelectExtension(profile.Extension);
            RefreshBuilderExtensions();
        }

        private void RefreshExtensions()
        {
            if (extensionList == null) return;
            string selected = SelectedExtensionProfile() == null ? null : SelectedExtensionProfile().Extension;
            extensionList.Items.Clear();
            foreach (ExtensionProfile profile in LaunchBridgeCore.ExtensionProfilesSnapshot())
            {
                if (profile == null) continue;
                extensionList.Items.Add(profile.Extension + "  " + profile.DisplayName);
            }
            if (!string.IsNullOrWhiteSpace(selected)) SelectExtension(selected);
            if (extensionList.SelectedIndex < 0 && extensionList.Items.Count > 0) extensionList.SelectedIndex = 0;
            ShowSelectedExtensionProfile();
            if (openStatus != null) SetOpenStatus("READY", "AI-native package profiles: " + string.Join(", ", LaunchBridgeCore.RegisteredExtensionsSnapshot().ToArray()), Color.FromArgb(226, 244, 246));
            RefreshBuilderExtensions();
        }

        private void RefreshBuilderExtensions()
        {
            if (builderExtension == null) return;
            string current = builderExtension.Text;
            builderExtension.Items.Clear();
            foreach (string ext in LaunchBridgeCore.RegisteredExtensionsSnapshot()) builderExtension.Items.Add(ext);
            builderExtension.Text = string.IsNullOrWhiteSpace(current) ? LaunchBridgeCore.Config.DefaultExtension : current;
        }

        private void BrowseBuilderFolder()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                builderFolder.Text = dialog.SelectedPath;
                string name = new DirectoryInfo(dialog.SelectedPath).Name;
                builderDisplayName.Text = name.Replace('_', ' ');
                builderProductId.Text = name.ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
                builderInstallFolder.Text = name;
                string[] candidates = Directory.GetFiles(dialog.SelectedPath, "RUN*.bat", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(dialog.SelectedPath, "START*.cmd", SearchOption.TopDirectoryOnly))
                    .Concat(Directory.GetFiles(dialog.SelectedPath, "*.exe", SearchOption.TopDirectoryOnly)).ToArray();
                if (candidates.Length > 0) builderEntryPoint.Text = Path.GetFileName(candidates[0]);
            }
        }

        private void BrowseEntryPoint()
        {
            if (!Directory.Exists(builderFolder.Text))
            {
                MessageBox.Show("Choose the source product folder first.", "LaunchBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.InitialDirectory = builderFolder.Text;
                dialog.Filter = "Launch files|*.bat;*.cmd;*.exe;*.ps1;*.html|All files|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    builderEntryPoint.Text = MakeRelativePath(builderFolder.Text, dialog.FileName);
            }
        }

        private void BrowseBuilderOutput()
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                string ext = LaunchBridgeCore.NormalizeExtension(builderExtension.Text) ?? ".devmind";
                dialog.Filter = "LaunchBridge package|*" + ext + "|All files|*.*";
                dialog.DefaultExt = ext.TrimStart('.');
                dialog.InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                dialog.FileName = (string.IsNullOrWhiteSpace(builderDisplayName.Text) ? "Product" : builderDisplayName.Text.Replace(' ', '_')) + "_v" + builderVersion.Text.Replace('.', '_') + ext;
                if (dialog.ShowDialog(this) == DialogResult.OK) builderOutput.Text = dialog.FileName;
            }
        }

        private void BuildPackageFromForm()
        {
            try
            {
                PackageBuildRequest request = new PackageBuildRequest();
                request.SourceFolder = builderFolder.Text;
                request.OutputFile = builderOutput.Text;
                request.ProductId = builderProductId.Text;
                request.DisplayName = builderDisplayName.Text;
                request.Version = builderVersion.Text;
                request.Publisher = builderPublisher.Text;
                request.InstallDirectoryName = builderInstallFolder.Text;
                request.EntryPoint = builderEntryPoint.Text;
                request.EntryType = builderEntryType.Text;
                request.Arguments = builderArguments.Text;
                request.LaunchUrl = builderLaunchUrl.Text;
                request.MinimumNodeMajor = (int)builderNodeMajor.Value;
                request.Extension = builderExtension.Text;
                Cursor previous = Cursor;
                Cursor = Cursors.WaitCursor;
                builderStatus.Text = "Building package and hashing every file...";
                builderStatus.Refresh();
                string output = LaunchBridgeCore.BuildPackage(request);
                Cursor = previous;
                builderOutput.Text = output;
                builderStatus.Text = "Built successfully: " + output;
                if (MessageBox.Show("Package built successfully.\r\n\r\n" + output + "\r\n\r\nOpen its folder?", "LaunchBridge", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Process.Start("explorer.exe", "/select,\"" + output + "\"");
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                builderStatus.Text = "Build failed: " + ex.Message;
                ShowError(ex);
            }
        }

        private void RefreshProducts()
        {
            if (productsGrid == null) return;
            List<ProductRecord> products = LaunchBridgeCore.InstalledProductsSnapshot().Where(x => x != null).ToList();
            Dictionary<ProductRecord, bool> runningStates = new Dictionary<ProductRecord, bool>();
            foreach (ProductRecord product in products) runningStates[product] = IsProductRunningFast(product);
            string signature = string.Join("|", products.Select(x => (x.ProductId ?? "") + ":" + (x.Version ?? "") + ":" +
                (runningStates[x] ? "1" : "0") + ":" + x.LastProcessId + ":" + x.LastUiProcessId + ":" + (x.LastUiTargetId ?? "") + ":" + (x.LastKnownStatus ?? "")).ToArray());
            if (string.Equals(signature, lastProductsSignature, StringComparison.Ordinal)) return;
            lastProductsSignature = signature;
            string selectedProductId = SelectedProduct() == null ? null : SelectedProduct().ProductId;
            int firstVisibleRow = -1;
            int horizontalOffset = productsGrid.HorizontalScrollingOffset;
            try
            {
                if (productsGrid.Rows.Count > 0) firstVisibleRow = productsGrid.FirstDisplayedScrollingRowIndex;
            }
            catch { firstVisibleRow = -1; }
            productsGrid.DataSource = null;
            var view = products.Select(x => new
            {
                Product = x.DisplayName,
                Version = x.Version,
                Status = ProductDisplayStatus(x, runningStates[x]),
                AppPID = runningStates[x] && x.LastProcessId > 0 ? x.LastProcessId.ToString() : "",
                UI = runningStates[x] ? FormatUiIdentity(x.LastUiTargetId, x.LastUiMode, x.LastUiProcessId) : "",
                Publisher = x.Publisher,
                Installed = x.InstalledAtUtc,
                Path = x.InstallPath,
                ProductId = x.ProductId
            }).ToList();
            productsGrid.DataSource = view;
            if (productsGrid.Columns["ProductId"] != null) productsGrid.Columns["ProductId"].Visible = false;
            try
            {
                if (firstVisibleRow >= 0 && firstVisibleRow < productsGrid.Rows.Count)
                    productsGrid.FirstDisplayedScrollingRowIndex = firstVisibleRow;
                productsGrid.HorizontalScrollingOffset = horizontalOffset;
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(selectedProductId))
            {
                foreach (DataGridViewRow row in productsGrid.Rows)
                {
                    object value = row.Cells["ProductId"].Value;
                    if (value != null && string.Equals(value.ToString(), selectedProductId, StringComparison.OrdinalIgnoreCase))
                    {
                        row.Selected = true;
                        productsGrid.CurrentCell = row.Cells[0];
                        break;
                    }
                }
            }
        }


        private static string FormatUiIdentity(string targetId, string mode, int legacyPid)
        {
            if (!string.IsNullOrWhiteSpace(targetId))
            {
                string shortId = targetId.Length > 10 ? targetId.Substring(0, 10) : targetId;
                return "Tab " + shortId;
            }
            if (legacyPid > 0) return "Legacy PID " + legacyPid;
            if (string.Equals(mode, "UntrackedDefaultBrowser", StringComparison.OrdinalIgnoreCase)) return "Untracked browser";
            return "";
        }

        private ProductRecord SelectedProduct()
        {
            if (productsGrid == null || productsGrid.SelectedRows.Count == 0) return null;
            object id = productsGrid.SelectedRows[0].Cells["ProductId"].Value;
            if (id == null) return null;
            return LaunchBridgeCore.FindInstalledProduct(id.ToString());
        }

        private void LaunchSelected()
        {
            ProductRecord product = SelectedProduct();
            if (product == null) return;
            try
            {
                int processId = LaunchBridgeCore.LaunchProduct(product, LaunchBridgeCore.NewLogPath("manual-launch"));
                AddManualLaunchActivity(product, processId, "Installed Products");
                RefreshProducts();
                RefreshActivityGrid();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void OpenSelectedFolder()
        {
            ProductRecord product = SelectedProduct();
            if (product != null && Directory.Exists(product.InstallPath)) Process.Start("explorer.exe", product.InstallPath);
        }

        private void RollbackSelected()
        {
            ProductRecord product = SelectedProduct();
            if (product == null) return;
            if (MessageBox.Show("Replace the current installation with its saved rollback snapshot?", "Roll back product", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            string message;
            bool ok = LaunchBridgeCore.RollbackProduct(product, out message);
            MessageBox.Show(message, "LaunchBridge", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            RefreshProducts();
        }

        private void UninstallSelected()
        {
            ProductRecord product = SelectedProduct();
            if (product == null) return;
            if (MessageBox.Show("Remove " + product.DisplayName + " from " + product.InstallPath + "?", "Uninstall product", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            string message;
            bool ok = LaunchBridgeCore.UninstallProduct(product, out message);
            MessageBox.Show(message, "LaunchBridge", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            RefreshProducts();
        }

        private static string ProductDisplayStatus(ProductRecord product, bool running)
        {
            if (running) return "Running";
            string status = product == null ? null : product.LastKnownStatus;
            if (string.IsNullOrWhiteSpace(status)) return "Installed";
            if (string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Launched", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Running without UI", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "Installing", StringComparison.OrdinalIgnoreCase))
                return "Stopped";
            return status;
        }

        private void SeedRunningProducts()
        {
            foreach (ProductRecord product in LaunchBridgeCore.InstalledProductsSnapshot())
            {
                if (!IsProductRunningFast(product)) continue;
                ActivityRecord activity = new ActivityRecord();
                activity.ActivityId = Guid.NewGuid().ToString("N");
                activity.ProductId = product.ProductId;
                activity.Product = product.DisplayName;
                activity.Version = product.Version;
                activity.Status = "Running";
                activity.ProcessId = product.LastProcessId;
                activity.UiProcessId = product.LastUiProcessId;
                activity.UiTargetId = product.LastUiTargetId;
                activity.QueuedAtUtc = string.IsNullOrWhiteSpace(product.LastLaunchAtUtc) ? DateTime.UtcNow.ToString("o") : product.LastLaunchAtUtc;
                activity.StartedAtUtc = activity.QueuedAtUtc;
                activity.Source = "Recovered on startup";
                activity.InstallPath = product.InstallPath;
                activity.Message = "Product process or tracked window was already running when LaunchBridge opened.";
                activityRecords.Add(activity);
            }
        }

        private void RefreshActivityGrid()
        {
            if (activityGrid == null) return;
            ActivityRecord[] records = activityRecords.ToArray();
            foreach (ActivityRecord item in records)
            {
                if (item.Status == "Running" || item.Status == "Launched" || item.Status == "Running without UI")
                {
                    ProductRecord product = ProductForActivity(item);
                    bool productIsRunning = product != null ? IsProductRunningFast(product) :
                        LaunchBridgeCore.IsProcessRunning(item.ProcessId) || LaunchBridgeCore.IsProcessRunning(item.UiProcessId);
                    if (!productIsRunning)
                    {
                        if (product != null && string.Equals(product.LastKnownStatus, "Auto-stopped", StringComparison.OrdinalIgnoreCase))
                        {
                            item.Status = "Auto-stopped";
                            item.Message = string.IsNullOrWhiteSpace(product.LastAutoStopReason)
                                ? "LaunchBridge automatically cleaned up the remaining process tree after the product window or command launcher closed."
                                : product.LastAutoStopReason;
                        }
                        else item.Status = "Exited";
                    }
                    else if (product != null)
                    {
                        item.ProcessId = product.LastProcessId;
                        item.UiProcessId = product.LastUiProcessId;
                        item.UiTargetId = product.LastUiTargetId;
                        item.Status = string.IsNullOrWhiteSpace(product.LastUiTargetId) && product.LastUiProcessId <= 0 && LaunchBridgeCore.IsProcessRunning(product.LastProcessId) ? "Running without UI" : "Running";
                    }
                }
            }
            string signature = string.Join("|", records.Select(x => (x.ActivityId ?? "") + ":" + (x.Status ?? "") + ":" + x.ProcessId + ":" + x.UiProcessId + ":" + (x.UiTargetId ?? "") + ":" + (x.Message ?? "")).ToArray());
            if (string.Equals(signature, lastActivitySignature, StringComparison.Ordinal)) return;
            lastActivitySignature = signature;
            string selectedActivityId = SelectedActivity() == null ? null : SelectedActivity().ActivityId;
            int firstVisibleRow = -1;
            int horizontalOffset = activityGrid.HorizontalScrollingOffset;
            try
            {
                if (activityGrid.Rows.Count > 0) firstVisibleRow = activityGrid.FirstDisplayedScrollingRowIndex;
            }
            catch { firstVisibleRow = -1; }

            activityGrid.DataSource = null;
            var view = records.Select(x => new
            {
                Product = x.Product,
                Version = x.Version,
                Status = x.Status,
                AppPID = x.ProcessId > 0 ? x.ProcessId.ToString() : "",
                UI = FormatUiIdentity(x.UiTargetId, x.UiProcessId > 0 ? "Legacy window" : "", x.UiProcessId),
                Source = x.Source,
                Started = string.IsNullOrWhiteSpace(x.StartedAtUtc) ? x.QueuedAtUtc : x.StartedAtUtc,
                Message = x.Message,
                ActivityId = x.ActivityId
            }).ToList();
            activityGrid.DataSource = view;
            if (activityGrid.Columns["ActivityId"] != null) activityGrid.Columns["ActivityId"].Visible = false;
            try
            {
                if (firstVisibleRow >= 0 && firstVisibleRow < activityGrid.Rows.Count)
                    activityGrid.FirstDisplayedScrollingRowIndex = firstVisibleRow;
                activityGrid.HorizontalScrollingOffset = horizontalOffset;
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(selectedActivityId))
            {
                foreach (DataGridViewRow row in activityGrid.Rows)
                {
                    object value = row.Cells["ActivityId"].Value;
                    if (value != null && string.Equals(value.ToString(), selectedActivityId, StringComparison.OrdinalIgnoreCase))
                    {
                        row.Selected = true;
                        activityGrid.CurrentCell = row.Cells[0];
                        break;
                    }
                }
            }
            int activeCount = records.Count(x => x.Status == "Running" || x.Status == "Launched" || x.Status == "Running without UI");
            int queued = records.Count(x => x.Status == "Queued" || x.Status == "Installing");
            if (activitySummary != null)
                activitySummary.Text = activeCount + " active products  •  " + queued + " queued/installing  •  " + records.Length + " total activity records";
        }

        private ActivityRecord SelectedActivity()
        {
            if (activityGrid == null || activityGrid.SelectedRows.Count == 0) return null;
            object id = activityGrid.SelectedRows[0].Cells["ActivityId"].Value;
            if (id == null) return null;
            return activityRecords.FirstOrDefault(x => string.Equals(x.ActivityId, id.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        private ProductRecord ProductForActivity(ActivityRecord activity)
        {
            if (activity == null || string.IsNullOrWhiteSpace(activity.ProductId)) return null;
            return LaunchBridgeCore.FindInstalledProduct(activity.ProductId);
        }

        private void RelaunchSelectedActivity()
        {
            ProductRecord product = ProductForActivity(SelectedActivity());
            if (product == null) return;
            try
            {
                int processId = LaunchBridgeCore.LaunchProduct(product, LaunchBridgeCore.NewLogPath("activity-launch"));
                AddManualLaunchActivity(product, processId, "Activity tab");
                RefreshProducts();
                RefreshActivityGrid();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void AddManualLaunchActivity(ProductRecord product, int processId, string source)
        {
            ActivityRecord activity = new ActivityRecord();
            activity.ActivityId = Guid.NewGuid().ToString("N");
            activity.ProductId = product.ProductId;
            activity.Product = product.DisplayName;
            activity.Version = product.Version;
            activity.Status = IsProductRunningFast(product) ? "Running" : "Launched";
            activity.ProcessId = product.LastProcessId;
            activity.UiProcessId = product.LastUiProcessId;
            activity.UiTargetId = product.LastUiTargetId;
            activity.QueuedAtUtc = DateTime.UtcNow.ToString("o");
            activity.StartedAtUtc = activity.QueuedAtUtc;
            activity.Source = source;
            activity.InstallPath = product.InstallPath;
            activity.Message = "Product launched.";
            activityRecords.Insert(0, activity);
        }

        private void CloseSelectedActivityUi()
        {
            ProductRecord product = ProductForActivity(SelectedActivity());
            if (product == null) return;
            string message;
            bool ok = LaunchBridgeCore.CloseProductUi(product, out message);
            UpdateActivityForProduct(product, ok ? product.LastKnownStatus : null, message);
            MessageBox.Show(message, "LaunchBridge", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshProducts();
            RefreshActivityGrid();
        }

        private void StopSelectedActivity()
        {
            ProductRecord product = ProductForActivity(SelectedActivity());
            if (product == null) return;
            string message;
            bool ok = LaunchBridgeCore.StopProduct(product, out message);
            UpdateActivityForProduct(product, ok ? "Stopped" : null, message);
            MessageBox.Show(message, "LaunchBridge", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshProducts();
            RefreshActivityGrid();
        }

        private void ForceKillSelectedActivity()
        {
            ProductRecord product = ProductForActivity(SelectedActivity());
            if (product == null) return;
            if (MessageBox.Show("Force kill the selected product process tree and close its managed browser tab?", "Force kill product", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            string message;
            bool ok = LaunchBridgeCore.ForceKillProduct(product, out message);
            UpdateActivityForProduct(product, ok ? "Force killed" : null, message);
            MessageBox.Show(message, "LaunchBridge", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshProducts();
            RefreshActivityGrid();
        }

        private void UpdateActivityForProduct(ProductRecord product, string status, string message)
        {
            if (product == null) return;
            foreach (ActivityRecord item in activityRecords.Where(x => string.Equals(x.ProductId, product.ProductId, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                if (!string.IsNullOrWhiteSpace(status)) item.Status = status;
                item.ProcessId = product.LastProcessId;
                item.UiProcessId = product.LastUiProcessId;
                item.UiTargetId = product.LastUiTargetId;
                item.Message = message;
                item.CompletedAtUtc = DateTime.UtcNow.ToString("o");
            }
        }

        private void OpenSelectedActivityFolder()
        {
            ProductRecord product = ProductForActivity(SelectedActivity());
            if (product != null && Directory.Exists(product.InstallPath)) Process.Start("explorer.exe", product.InstallPath);
        }

        private void CloseSelectedProductUi()
        {
            ProductRecord product = SelectedProduct();
            if (product == null) return;
            string message;
            bool ok = LaunchBridgeCore.CloseProductUi(product, out message);
            UpdateActivityForProduct(product, ok ? product.LastKnownStatus : null, message);
            MessageBox.Show(message, "LaunchBridge", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshProducts();
            RefreshActivityGrid();
        }

        private void StopSelectedProduct()
        {
            ProductRecord product = SelectedProduct();
            if (product == null) return;
            string message;
            bool ok = LaunchBridgeCore.StopProduct(product, out message);
            UpdateActivityForProduct(product, ok ? "Stopped" : null, message);
            MessageBox.Show(message, "LaunchBridge", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshProducts();
            RefreshActivityGrid();
        }

        private void ForceKillSelectedProduct()
        {
            ProductRecord product = SelectedProduct();
            if (product == null) return;
            if (MessageBox.Show("Force kill " + product.DisplayName + ", its child processes, and its managed browser tab?", "Force kill product", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            string message;
            bool ok = LaunchBridgeCore.ForceKillProduct(product, out message);
            UpdateActivityForProduct(product, ok ? "Force killed" : null, message);
            MessageBox.Show(message, "LaunchBridge", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshProducts();
            RefreshActivityGrid();
        }

        private void StopAllTrackedProducts()
        {
            List<ProductRecord> products = LaunchBridgeCore.InstalledProductsSnapshot().Where(x => x != null).ToList();
            if (products.Count == 0)
            {
                MessageBox.Show("No installed products are tracked.", "Stop all", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult confirmation = MessageBox.Show(
                "Stop every tracked product and close every managed app tab?\r\n\r\n" +
                "This also clears stale Running indicators. It does not uninstall products or delete product data.",
                "Stop all products",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes) return;
            if (Interlocked.Exchange(ref bulkStopInProgress, 1) != 0) return;

            if (activityTimer != null) activityTimer.Stop();
            Enabled = false;
            UseWaitCursor = true;
            if (activitySummary != null) activitySummary.Text = "Stopping every tracked product and closing managed app tabs...";

            ThreadPool.QueueUserWorkItem(delegate
            {
                string message;
                bool ok = LaunchBridgeCore.StopAllProducts(out message);
                Dictionary<string, bool> states = null;
                try { states = LaunchBridgeCore.CaptureProductRunningStates(); } catch { }

                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            productRunningStates = states ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                            foreach (ActivityRecord activity in activityRecords)
                            {
                                if (activity.Status == "Running" || activity.Status == "Launched" || activity.Status == "Running without UI" || activity.Status == "Installing")
                                {
                                    activity.Status = "Stopped";
                                    activity.ProcessId = 0;
                                    activity.UiProcessId = 0;
                                    activity.UiTargetId = null;
                                    activity.CompletedAtUtc = DateTime.UtcNow.ToString("o");
                                    activity.Message = "Stopped by Stop all.";
                                }
                            }
                            lastProductsSignature = null;
                            lastActivitySignature = null;
                            RefreshProducts();
                            RefreshActivityGrid();
                            MessageBox.Show(message, "Stop all", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                        }
                        finally
                        {
                            UseWaitCursor = false;
                            Enabled = true;
                            Interlocked.Exchange(ref bulkStopInProgress, 0);
                            if (activityTimer != null) activityTimer.Start();
                        }
                    }));
                }
                catch
                {
                    Interlocked.Exchange(ref bulkStopInProgress, 0);
                }
            });
        }

        private RuntimeIssue SelectedRuntimeIssue()
        {
            if (errorGrid == null || errorGrid.SelectedRows.Count == 0) return null;
            return errorGrid.SelectedRows[0].Tag as RuntimeIssue;
        }

        private void RefreshErrorCockpit()
        {
            RefreshErrorCockpit(null);
        }

        private void RefreshErrorCockpit(List<RuntimeIssue> suppliedIssues)
        {
            if (errorGrid == null || errorSummary == null) return;
            List<RuntimeIssue> issues = suppliedIssues ?? LaunchBridgeCore.RuntimeIssuesSnapshot();
            string signature = (LaunchBridgeCore.RuntimeMonitorEnabled ? "1:" : "0:") + string.Join("|", issues.Select(x =>
                (x.IssueId ?? "") + ":" + (x.Severity ?? "") + ":" + (x.Type ?? "") + ":" + (x.Message ?? "")).ToArray());
            if (string.Equals(signature, lastErrorSignature, StringComparison.Ordinal)) return;
            lastErrorSignature = signature;
            string selectedId = SelectedRuntimeIssue() == null ? null : SelectedRuntimeIssue().IssueId;
            int firstVisibleRow = -1;
            int horizontalOffset = errorGrid.HorizontalScrollingOffset;
            try
            {
                if (errorGrid.Rows.Count > 0) firstVisibleRow = errorGrid.FirstDisplayedScrollingRowIndex;
            }
            catch { firstVisibleRow = -1; }
            errorGrid.Rows.Clear();
            foreach (RuntimeIssue issue in issues)
            {
                int rowIndex = errorGrid.Rows.Add(
                    FormatIssueTime(issue.ReceivedAtUtc),
                    issue.Product ?? issue.ProductId,
                    issue.Severity,
                    issue.Type,
                    issue.Message);
                DataGridViewRow row = errorGrid.Rows[rowIndex];
                row.Tag = issue;
                if (string.Equals(issue.IssueId, selectedId, StringComparison.OrdinalIgnoreCase)) row.Selected = true;
            }
            try
            {
                if (firstVisibleRow >= 0 && firstVisibleRow < errorGrid.Rows.Count)
                    errorGrid.FirstDisplayedScrollingRowIndex = firstVisibleRow;
                errorGrid.HorizontalScrollingOffset = horizontalOffset;
            }
            catch { }
            errorSummary.Text = LaunchBridgeCore.RuntimeMonitorEnabled
                ? (issues.Count == 0
                    ? "Runtime monitor active on 127.0.0.1:" + LaunchBridgeCore.RuntimeIssuePort + ". No issues captured this session."
                    : issues.Count + " runtime issue(s) captured this session. The newest issue is copied and surfaced automatically when enabled.")
                : "Runtime monitor is disabled in Settings.";

            RuntimeIssue newest = issues.FirstOrDefault();
            if (newest != null && !string.Equals(newest.IssueId, lastPresentedRuntimeIssueId, StringComparison.OrdinalIgnoreCase))
            {
                lastPresentedRuntimeIssueId = newest.IssueId;
                if (errorGrid.Rows.Count > 0)
                {
                    errorGrid.ClearSelection();
                    errorGrid.Rows[0].Selected = true;
                    errorGrid.CurrentCell = errorGrid.Rows[0].Cells[0];
                }
                if (LaunchBridgeCore.Config.AutoCopyErrors)
                {
                    try { Clipboard.SetText(LaunchBridgeCore.BuildRuntimeIssuePacket(newest)); } catch { }
                }
                if (LaunchBridgeCore.Config.OpenErrorCockpitOnIssue.GetValueOrDefault(true) && mainTabs != null && errorCockpitTab != null)
                {
                    mainTabs.SelectedTab = errorCockpitTab;
                    ActivatePrimaryWindow();
                }
            }
            ShowSelectedRuntimeIssue();
        }

        private static string FormatIssueTime(string raw)
        {
            DateTime value;
            return DateTime.TryParse(raw, out value) ? value.ToLocalTime().ToString("HH:mm:ss") : raw;
        }

        private void ShowSelectedRuntimeIssue()
        {
            if (errorDetail == null) return;
            RuntimeIssue issue = SelectedRuntimeIssue();
            errorDetail.Text = issue == null ? "Select an issue to inspect its complete repair packet." : LaunchBridgeCore.BuildRuntimeIssuePacket(issue);
        }

        private void CopySelectedRuntimeIssue(bool fullPacket)
        {
            RuntimeIssue issue = SelectedRuntimeIssue();
            if (issue == null) return;
            string text = fullPacket ? LaunchBridgeCore.BuildRuntimeIssuePacket(issue) : (issue.Message ?? "");
            try { Clipboard.SetText(text); } catch { }
        }

        private void OpenSelectedIssueInChatGPT()
        {
            RuntimeIssue issue = SelectedRuntimeIssue();
            if (issue == null) return;
            CopySelectedRuntimeIssue(true);
            try { Process.Start("https://chatgpt.com/"); }
            catch (Exception ex) { ShowError(ex); }
        }

        private void OpenSelectedRepairBundle()
        {
            RuntimeIssue issue = SelectedRuntimeIssue();
            if (issue == null) return;
            string bundle = issue.RepairBundlePath;
            if (string.IsNullOrWhiteSpace(bundle) || !File.Exists(bundle))
            {
                try
                {
                    bundle = LaunchBridgeCore.CreateRuntimeRepairBundle(issue);
                    issue.RepairBundlePath = bundle;
                    ShowSelectedRuntimeIssue();
                }
                catch (Exception ex)
                {
                    ShowError(ex);
                    return;
                }
            }
            Process.Start("explorer.exe", "/select,\"" + bundle + "\"");
        }

        private void OpenSelectedIssueFolder()
        {
            RuntimeIssue issue = SelectedRuntimeIssue();
            if (issue == null || string.IsNullOrWhiteSpace(issue.InstallPath) || !Directory.Exists(issue.InstallPath)) return;
            Process.Start("explorer.exe", issue.InstallPath);
        }

        private void SaveSettings()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(workRootInput.Text)) throw new InvalidOperationException("Choose a product install root.");
                Directory.CreateDirectory(workRootInput.Text);
                LaunchBridgeCore.Config.WorkRoot = Path.GetFullPath(workRootInput.Text);
                LaunchBridgeCore.Config.AutoLaunch = autoLaunchCheck.Checked;
                LaunchBridgeCore.Config.KeepRollback = rollbackCheck.Checked;
                LaunchBridgeCore.Config.Dinger = dingerCheck.Checked;
                LaunchBridgeCore.Config.AutoCopyErrors = copyErrorsCheck.Checked;
                LaunchBridgeCore.Config.AddZipContextMenu = zipContextCheck.Checked;
                LaunchBridgeCore.Config.RuntimeMonitorEnabled = runtimeMonitorCheck == null ? true : runtimeMonitorCheck.Checked;
                LaunchBridgeCore.Config.OpenErrorCockpitOnIssue = openErrorTabCheck == null ? true : openErrorTabCheck.Checked;
                LaunchBridgeCore.Config.TurboLaunchEnabled = turboLaunchCheck == null ? true : turboLaunchCheck.Checked;
                LaunchBridgeCore.SaveConfig();
                LaunchBridgeCore.ApplyTurboLaunchSettings();
                if (trayIcon != null) trayIcon.Visible = LaunchBridgeCore.TurboLaunchEnabled;
                LaunchBridgeCore.ApplyRuntimeMonitorSettings();
                LaunchBridgeCore.SetZipContextMenu(zipContextCheck.Checked);
                MessageBox.Show("Settings saved.", "LaunchBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void SetOpenStatus(string headline, string detail, Color backColor)
        {
            if (openStatus == null) return;

            string safeHeadline = string.IsNullOrWhiteSpace(headline) ? "STATUS" : headline.Trim();
            string safeDetail = string.IsNullOrWhiteSpace(detail) ? "" : detail.Replace("\r", " ").Replace("\n", " ").Trim();
            while (safeDetail.Contains("  ")) safeDetail = safeDetail.Replace("  ", " ");

            openStatus.Text = string.IsNullOrWhiteSpace(safeDetail)
                ? safeHeadline
                : safeHeadline + " — " + safeDetail;
            openStatus.BackColor = backColor;
            openStatusToolTip.SetToolTip(openStatus, openStatus.Text);
        }

        private void RefreshLogs()
        {
            if (logBox == null) return;
            try
            {
                string[] files = Directory.Exists(LaunchBridgeCore.LogsRoot) ? Directory.GetFiles(LaunchBridgeCore.LogsRoot, "*.log") : new string[0];
                string latest = files.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                logBox.Text = latest == null ? "No logs yet." : File.ReadAllText(latest);
            }
            catch (Exception ex) { logBox.Text = ex.Message; }
        }

        private TextBox AddField(Control parent, string labelText, ref int y, bool withButton, EventHandler buttonAction)
        {
            Label label = SmallLabel(labelText);
            label.Location = new Point(20, y);
            parent.Controls.Add(label);
            y += 24;

            TextBox box = new TextBox();
            box.Location = new Point(20, y);
            box.Width = withButton ? parent.ClientSize.Width - 128 : parent.ClientSize.Width - 42;
            box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            parent.Controls.Add(box);

            if (withButton)
            {
                Button button = SecondaryButton("Browse");
                button.Location = new Point(parent.ClientSize.Width - 98, y - 2);
                button.Width = 78;
                button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                if (buttonAction != null) button.Click += buttonAction;
                parent.Controls.Add(button);
            }

            y += 52;
            return box;
        }

        private CheckBox AddCheck(Control parent, string text, int y, bool value)
        {
            CheckBox check = new CheckBox();
            check.Text = text;
            check.Checked = value;
            check.Location = new Point(28, y);
            check.AutoSize = true;
            check.Font = new Font("Segoe UI", 10.5F);
            check.ForeColor = Ink;
            parent.Controls.Add(check);
            return check;
        }

        private void ApplyReadableGridLayout(DataGridView grid)
        {
            if (grid == null) return;

            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 38;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(7, 5, 7, 5);
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(238, 242, 243);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Ink;

            grid.DefaultCellStyle.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);
            grid.DefaultCellStyle.Padding = new Padding(5, 3, 5, 3);
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            grid.RowTemplate.Height = 30;
        }

        private Label HeaderLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold);
            label.ForeColor = Ink;
            Size measured = TextRenderer.MeasureText(text + "Ag", label.Font, new Size(2400, 0), TextFormatFlags.SingleLine);
            label.Size = new Size(Math.Max(920, measured.Width + 8), measured.Height + 8);
            return label;
        }

        private Label BodyLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Font = new Font("Segoe UI", 10F);
            label.ForeColor = Muted;
            return label;
        }

        private Label SmallLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold);
            label.ForeColor = Muted;
            return label;
        }

        private Button AccentButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            button.Size = new Size(126, 38);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = Accent;
            button.ForeColor = Color.White;
            button.Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
            return button;
        }

        private Button SecondaryButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            button.Size = new Size(126, 38);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(194, 205, 207);
            button.BackColor = Color.White;
            button.ForeColor = Ink;
            button.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
            return button;
        }

        private Button DangerButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            button.Size = new Size(170, 38);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = Color.FromArgb(176, 45, 45);
            button.ForeColor = Color.White;
            button.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
            button.Cursor = Cursors.Hand;
            return button;
        }

        private static void ApplyCrispTextRendering(Control root)
        {
            if (root == null) return;

            Label label = root as Label;
            if (label != null) label.UseCompatibleTextRendering = false;

            Button button = root as Button;
            if (button != null) button.UseCompatibleTextRendering = false;

            CheckBox checkBox = root as CheckBox;
            if (checkBox != null) checkBox.UseCompatibleTextRendering = false;

            RadioButton radioButton = root as RadioButton;
            if (radioButton != null) radioButton.UseCompatibleTextRendering = false;

            LinkLabel linkLabel = root as LinkLabel;
            if (linkLabel != null) linkLabel.UseCompatibleTextRendering = false;

            foreach (Control child in root.Controls) ApplyCrispTextRendering(child);
        }

        private void ShowError(Exception ex)
        {
            MessageBox.Show(ex.Message, "LaunchBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static string MakeRelativePath(string root, string file)
        {
            Uri rootUri = new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            Uri fileUri = new Uri(Path.GetFullPath(file));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
