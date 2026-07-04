using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Media;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DevMind.LaunchBridge
{
    public static class LaunchBridgeCore
    {
        private static readonly object Sync = new object();
        private static AppConfig config;
        private static string appRoot;
        private static string logsRoot;
        private static string rollbacksRoot;
        private static string browserProfilesRoot;
        private static string managedBrowserProfileRoot;
        private static string configPath;
        private static JavaScriptSerializer serializer = new JavaScriptSerializer();
        private static readonly List<RuntimeIssue> runtimeIssues = new List<RuntimeIssue>();
        private static TcpListener runtimeIssueListener;
        private static Thread runtimeIssueThread;
        private static int runtimeIssuePort;
        private static string runtimeIssueToken;
        private static string runtimeIssuesRoot;
        private static string runtimeIssueClearMarkerPath;
        private static string repairPacketsRoot;
        private static string extensionProfilesRoot;
        private static string extensionIconsRoot;
        private static readonly object LaunchProcessSync = new object();
        private static readonly Dictionary<int, LaunchProcessCapture> launchProcessCaptures = new Dictionary<int, LaunchProcessCapture>();
        private static readonly HashSet<int> suppressedExitIssuePids = new HashSet<int>();
        private static readonly object LifecycleSync = new object();
        private static readonly Dictionary<string, LifecycleWatch> lifecycleWatches = new Dictionary<string, LifecycleWatch>(StringComparer.OrdinalIgnoreCase);
        private static Thread lifecycleSupervisorThread;
        private static int lifecycleSupervisorStarted;
        private const string SmartClickHostName = "com.launchbridge.smartclick";
        private const string SmartClickExtensionId = "lkjjnhlhobcpmhpgkmkbgjioclgohjdk";

        private sealed class LifecycleWatch
        {
            public string ProductId;
            public string DisplayName;
            public string InstallPath;
            public int LauncherProcessId;
            public int ProductProcessId;
            public string LaunchAtUtc;
            public string EntryType;
            public DateTime RegisteredAtUtc;
            public bool ConsoleAnchorConfirmed;
            public bool ApplicationWindowSeen;
            public DateTime? LauncherMissingSinceUtc;
            public DateTime? ApplicationWindowMissingSinceUtc;
        }

        private sealed class LaunchProcessCapture
        {
            public Process Process;
            public int ProcessId;
            public string ProductId;
            public string DisplayName;
            public string Version;
            public string InstallPath;
            public string EntryPath;
            public string EntryType;
            public string CommandLine;
            public string LogPath;
            public DateTime StartedAtUtc;
            public bool CaptureStreams;
            public readonly object TextSync = new object();
            public readonly StringBuilder StandardOutput = new StringBuilder();
            public readonly StringBuilder StandardError = new StringBuilder();
            public int CompletionStarted;
        }

        private sealed class ProcessSnapshot
        {
            public int ProcessId;
            public int ParentProcessId;
            public string Name;
            public string ExecutablePath;
            public string CommandLine;
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        public static AppConfig Config { get { return config; } }
        public static string AppRoot { get { return appRoot; } }
        public static string LogsRoot { get { return logsRoot; } }
        public static string CurrentExePath { get { return Application.ExecutablePath; } }
        public static int RuntimeIssuePort { get { return runtimeIssuePort; } }
        public static bool RuntimeMonitorEnabled { get { return config != null && config.RuntimeMonitorEnabled.GetValueOrDefault(true); } }
        public static bool TurboLaunchEnabled { get { return config != null && config.TurboLaunchEnabled.GetValueOrDefault(true); } }
        public static bool SmartClickEnabled { get { return config != null && config.SmartClickEnabled.GetValueOrDefault(true); } }
        public static string SmartClickExtensionFolder { get { return Path.Combine(appRoot, "browser-extension"); } }

        public static List<ProductRecord> InstalledProductsSnapshot()
        {
            lock (Sync)
            {
                return config == null || config.InstalledProducts == null
                    ? new List<ProductRecord>()
                    : config.InstalledProducts.ToList();
            }
        }

        public static List<string> RegisteredExtensionsSnapshot()
        {
            lock (Sync)
            {
                return config == null || config.RegisteredExtensions == null
                    ? new List<string>()
                    : config.RegisteredExtensions.ToList();
            }
        }

        public static List<ExtensionProfile> ExtensionProfilesSnapshot()
        {
            lock (Sync)
            {
                return config == null || config.ExtensionProfiles == null
                    ? new List<ExtensionProfile>()
                    : config.ExtensionProfiles.OrderBy(x => x == null ? "" : x.Extension).ToList();
            }
        }

        public static ExtensionProfile FindExtensionProfile(string extension)
        {
            string ext = NormalizeExtension(extension);
            if (ext == null) return null;
            lock (Sync)
            {
                return config == null || config.ExtensionProfiles == null
                    ? null
                    : config.ExtensionProfiles.FirstOrDefault(x => x != null && string.Equals(x.Extension, ext, StringComparison.OrdinalIgnoreCase));
            }
        }

        public static ExtensionProfile CreateOrUpdateExtensionProfile(string extension, string displayName, string description)
        {
            string ext = NormalizeExtension(extension);
            if (ext == null) throw new InvalidOperationException("Enter a custom extension such as .badmoth, .council, or .myapp.");
            if (IsProtectedAssociation(ext)) throw new InvalidOperationException("LaunchBridge will not take over common executable, script, or archive extensions. Use a custom extension.");

            ExtensionProfile profile;
            lock (Sync)
            {
                if (config.ExtensionProfiles == null) config.ExtensionProfiles = new List<ExtensionProfile>();
                profile = config.ExtensionProfiles.FirstOrDefault(x => x != null && string.Equals(x.Extension, ext, StringComparison.OrdinalIgnoreCase));
                if (profile == null)
                {
                    profile = ExtensionProfile.CreateDefault(ext,
                        string.IsNullOrWhiteSpace(displayName) ? ext.TrimStart('.') + " Package" : displayName.Trim(),
                        string.IsNullOrWhiteSpace(description) ? "AI-built runnable application package" : description.Trim());
                    config.ExtensionProfiles.Add(profile);
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(displayName)) profile.DisplayName = displayName.Trim();
                    if (!string.IsNullOrWhiteSpace(description)) profile.Description = description.Trim();
                    profile.UpdatedAtUtc = DateTime.UtcNow.ToString("o");
                }
                profile.FullPrompt = BuildFullAiPrompt(profile);
                profile.ShortPrompt = BuildShortAiPrompt(profile);
                profile.IconPrompt = BuildIconAiPrompt(profile);
                if (!config.RegisteredExtensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase)))
                    config.RegisteredExtensions.Add(ext);
                config.RegisteredExtensions = config.RegisteredExtensions.OrderBy(x => x).ToList();
                SaveConfig();
                SaveExtensionProfileFile(profile);
            }
            RegisterExtension(ext);
            return profile;
        }

        public static ExtensionProfile SetExtensionProfileIcon(string extension, string sourcePath)
        {
            ExtensionProfile profile = FindExtensionProfile(extension);
            if (profile == null) throw new InvalidOperationException("Create the extension profile before assigning an icon.");
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) throw new FileNotFoundException("The icon image was not found.", sourcePath);

            Directory.CreateDirectory(extensionIconsRoot);
            string key = profile.Extension.TrimStart('.');
            string sourceExt = Path.GetExtension(sourcePath).ToLowerInvariant();
            string copiedSource = Path.Combine(extensionIconsRoot, key + "-master" + sourceExt);
            File.Copy(sourcePath, copiedSource, true);
            string icoPath = Path.Combine(extensionIconsRoot, key + ".ico");
            if (string.Equals(sourceExt, ".ico", StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePath, icoPath, true);
            else
                CreateIcoFromImage(sourcePath, icoPath);

            lock (Sync)
            {
                profile.IconSourcePath = copiedSource;
                profile.IconIcoPath = icoPath;
                profile.UpdatedAtUtc = DateTime.UtcNow.ToString("o");
                SaveConfig();
                SaveExtensionProfileFile(profile);
            }
            RegisterExtension(profile.Extension);
            return profile;
        }

        public static string BuildFullAiPrompt(ExtensionProfile profile)
        {
            if (profile == null) return "";
            string ext = profile.Extension ?? ".app";
            StringBuilder b = new StringBuilder();
            b.AppendLine("You are preparing a completed runnable product for LaunchBridge.");
            b.AppendLine();
            b.AppendLine("Export the finished build as a ZIP-compatible archive named:");
            b.AppendLine("<ProductName>_v<Version>" + ext);
            b.AppendLine();
            b.AppendLine("The final filename must end in " + ext + ", not .zip.");
            b.AppendLine();
            b.AppendLine("PACKAGE REQUIREMENTS");
            b.AppendLine();
            b.AppendLine("1. Include the complete runnable build and every runtime dependency needed to launch it.");
            b.AppendLine("2. Do not require npm install, compilation, dependency downloads, or additional setup after download.");
            b.AppendLine("3. Do not include an unnecessary outer folder around the product contents.");
            b.AppendLine("4. Do not include devmind.package.json or another LaunchBridge manifest unless I explicitly request an advanced package.");
            b.AppendLine("5. Include one clear launch target: a Windows .exe, packaged Electron application, start.cmd, start.bat, start.ps1, a complete Node application with its runtime dependencies, or index.html for a local web product.");
            b.AppendLine("6. Exclude development-only files such as .git, test output, coverage data, temporary files, and build caches unless the running product requires them.");
            b.AppendLine("7. Use a semantic version in the filename, such as 1.0.0 or 2.1.0-beta.1.");
            b.AppendLine();
            b.AppendLine("FINAL DELIVERY");
            b.AppendLine();
            b.AppendLine("Return the finished " + ext + " package and briefly identify the product name, version, and primary launch file.");
            b.AppendLine("Do not rename the final file to .zip.");
            b.AppendLine();
            b.AppendLine("Do not create or discuss an icon in this task. Icon generation is handled by a separate prompt.");
            return b.ToString().Trim();
        }

        public static string BuildShortAiPrompt(ExtensionProfile profile)
        {
            if (profile == null) return "";
            string ext = profile.Extension ?? ".app";
            return "Export this completed runnable product as a ZIP-compatible archive named <ProductName>_v<Version>" + ext + ". Use " + ext + ", not .zip. Include every runtime dependency, no unnecessary outer folder, no mandatory LaunchBridge JSON manifest, and one clear launch target. Do not generate or discuss an icon; icon generation uses a separate prompt.";
        }

        public static string BuildIconAiPrompt(ExtensionProfile profile)
        {
            if (profile == null) return "";
            string ext = profile.Extension ?? ".app";
            StringBuilder b = new StringBuilder();
            b.AppendLine("Create a distinctive square icon representing this product and the " + ext + " package type.");
            b.AppendLine();
            b.AppendLine("This is an icon-generation task only. Do not package, rebuild, or modify the application.");
            b.AppendLine();
            b.AppendLine("The icon will appear in browser downloads, Windows Explorer, the Downloads folder, LaunchBridge, and shortcuts.");
            b.AppendLine();
            b.AppendLine("Requirements:");
            b.AppendLine("- strong recognizable silhouette");
            b.AppendLine("- no tiny text");
            b.AppendLine("- transparent background when appropriate");
            b.AppendLine("- 1024 x 1024 PNG master");
            b.AppendLine("- simplified version recognizable at 16 x 16 and 32 x 32");
            b.AppendLine("- visually distinct from generic ZIP, folder, and executable icons");
            return b.ToString().Trim();
        }

        private static void SaveExtensionProfileFile(ExtensionProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Extension)) return;
            try
            {
                Directory.CreateDirectory(extensionProfilesRoot);
                string name = profile.Extension.TrimStart('.') + ".profile.json";
                File.WriteAllText(Path.Combine(extensionProfilesRoot, name), serializer.Serialize(profile), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log("Extension profile save warning: " + ex.Message); }
        }

        private static void CreateIcoFromImage(string sourcePath, string icoPath)
        {
            using (Image source = Image.FromFile(sourcePath))
            using (Bitmap bitmap = new Bitmap(256, 256, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (MemoryStream png = new MemoryStream())
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                float scale = Math.Min(256F / source.Width, 256F / source.Height);
                int width = Math.Max(1, (int)Math.Round(source.Width * scale));
                int height = Math.Max(1, (int)Math.Round(source.Height * scale));
                int x = (256 - width) / 2;
                int y = (256 - height) / 2;
                graphics.DrawImage(source, new Rectangle(x, y, width, height));
                bitmap.Save(png, ImageFormat.Png);
                byte[] data = png.ToArray();
                using (BinaryWriter writer = new BinaryWriter(File.Create(icoPath)))
                {
                    writer.Write((ushort)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)1);
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write(data.Length);
                    writer.Write(22);
                    writer.Write(data);
                }
            }
        }

        public static ProductRecord FindInstalledProduct(string productId)
        {
            if (string.IsNullOrWhiteSpace(productId)) return null;
            lock (Sync)
            {
                return config == null || config.InstalledProducts == null
                    ? null
                    : config.InstalledProducts.FirstOrDefault(x => x != null && string.Equals(x.ProductId, productId, StringComparison.OrdinalIgnoreCase));
            }
        }

        public static Dictionary<string, bool> CaptureProductRunningStates()
        {
            List<ProductRecord> products = InstalledProductsSnapshot();
            HashSet<string> openTargetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (products.Any(x => x != null && !string.IsNullOrWhiteSpace(x.LastUiTargetId)))
            {
                foreach (BrowserTarget target in GetManagedBrowserTargets())
                {
                    if (target != null && !string.IsNullOrWhiteSpace(target.id)) openTargetIds.Add(target.id);
                }
            }

            Dictionary<string, bool> states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (ProductRecord product in products)
            {
                if (product == null || string.IsNullOrWhiteSpace(product.ProductId)) continue;
                bool managedTabOpen = !string.IsNullOrWhiteSpace(product.LastUiTargetId) && openTargetIds.Contains(product.LastUiTargetId);
                bool running = IsTrackedProductProcessRunning(product) || managedTabOpen;
                states[product.ProductId] = running;
            }
            return states;
        }

        public static void Initialize()
        {
            serializer.MaxJsonLength = int.MaxValue;
            appRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevMind", "LaunchBridge");
            logsRoot = Path.Combine(appRoot, "logs");
            rollbacksRoot = Path.Combine(appRoot, "rollbacks");
            browserProfilesRoot = Path.Combine(appRoot, "browser-profiles");
            managedBrowserProfileRoot = Path.Combine(browserProfilesRoot, "managed-apps");
            configPath = Path.Combine(appRoot, "config.json");
            runtimeIssuesRoot = Path.Combine(appRoot, "runtime-issues");
            runtimeIssueClearMarkerPath = Path.Combine(runtimeIssuesRoot, ".cleared-at-utc.txt");
            repairPacketsRoot = Path.Combine(appRoot, "repair-packets");
            extensionProfilesRoot = Path.Combine(appRoot, "extension-profiles");
            extensionIconsRoot = Path.Combine(appRoot, "extension-icons");
            Directory.CreateDirectory(appRoot);
            Directory.CreateDirectory(logsRoot);
            Directory.CreateDirectory(rollbacksRoot);
            Directory.CreateDirectory(browserProfilesRoot);
            Directory.CreateDirectory(managedBrowserProfileRoot);
            Directory.CreateDirectory(runtimeIssuesRoot);
            Directory.CreateDirectory(repairPacketsRoot);
            Directory.CreateDirectory(extensionProfilesRoot);
            Directory.CreateDirectory(extensionIconsRoot);
            config = LoadConfig();
            NormalizeConfig();
            SaveConfig();
            LoadRecentRuntimeIssues();
            ApplyTurboLaunchSettings();
            StartRuntimeIssueServer();
            StartProductLifecycleSupervisor();
            RestoreProductLifecycleWatches();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(1200);
                try { InstrumentExistingWebProducts(); }
                catch (Exception ex) { Log("Deferred runtime instrumentation warning: " + ex.Message); }
            });
        }

        private static void NormalizeConfig()
        {
            if (config == null) config = AppConfig.CreateDefault();
            if (string.IsNullOrWhiteSpace(config.WorkRoot)) config.WorkRoot = AppConfig.CreateDefault().WorkRoot;
            if (config.RegisteredExtensions == null) config.RegisteredExtensions = new List<string>();
            if (config.ExtensionProfiles == null) config.ExtensionProfiles = new List<ExtensionProfile>();
            if (config.InstalledProducts == null) config.InstalledProducts = new List<ProductRecord>();
            if (!config.RuntimeMonitorEnabled.HasValue) config.RuntimeMonitorEnabled = true;
            if (!config.OpenErrorCockpitOnIssue.HasValue) config.OpenErrorCockpitOnIssue = true;
            if (!config.TurboLaunchEnabled.HasValue) config.TurboLaunchEnabled = true;
            if (!config.ExtensionTourCompleted.HasValue) config.ExtensionTourCompleted = false;
            if (!config.SmartClickEnabled.HasValue) config.SmartClickEnabled = true;
            if (string.IsNullOrWhiteSpace(config.DefaultExtension)) config.DefaultExtension = ".devmind";
            string normalized = NormalizeExtension(config.DefaultExtension);
            if (normalized == null) normalized = ".devmind";
            config.DefaultExtension = normalized;
            if (!config.RegisteredExtensions.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)))
                config.RegisteredExtensions.Add(normalized);
            config.RegisteredExtensions = config.RegisteredExtensions
                .Select(NormalizeExtension)
                .Where(x => x != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();
            foreach (string ext in config.RegisteredExtensions)
            {
                ExtensionProfile existingProfile = config.ExtensionProfiles.FirstOrDefault(x => x != null && string.Equals(x.Extension, ext, StringComparison.OrdinalIgnoreCase));
                if (existingProfile == null)
                {
                    existingProfile = ExtensionProfile.CreateDefault(ext, ext.TrimStart('.') + " Package", "AI-built runnable application package");
                    config.ExtensionProfiles.Add(existingProfile);
                }
                if (existingProfile.PreserveStatePaths == null) existingProfile.PreserveStatePaths = new List<string>();
                if (string.IsNullOrWhiteSpace(existingProfile.PackageMode)) existingProfile.PackageMode = "smart";
                existingProfile.ManifestAllowed = true;
                existingProfile.FullPrompt = BuildFullAiPrompt(existingProfile);
                existingProfile.ShortPrompt = BuildShortAiPrompt(existingProfile);
                existingProfile.IconPrompt = BuildIconAiPrompt(existingProfile);
                SaveExtensionProfileFile(existingProfile);
            }
            config.ExtensionProfiles = config.ExtensionProfiles
                .Where(x => x != null && NormalizeExtension(x.Extension) != null)
                .GroupBy(x => NormalizeExtension(x.Extension), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => x.Extension)
                .ToList();
            foreach (ProductRecord product in config.InstalledProducts)
            {
                if (product == null) continue;
                if (product.LaunchDelayMilliseconds <= 0) product.LaunchDelayMilliseconds = 350;
                if (string.Equals(product.LastUiMode, "DedicatedAppWindow", StringComparison.OrdinalIgnoreCase))
                {
                    product.LastUiProcessId = 0;
                    product.LastUiTargetId = null;
                    product.LastUiMode = "Legacy browser window";
                }
            }
        }

        private static AppConfig LoadConfig()
        {
            try
            {
                if (!File.Exists(configPath)) return AppConfig.CreateDefault();
                string json = File.ReadAllText(configPath, Encoding.UTF8);
                AppConfig loaded = serializer.Deserialize<AppConfig>(json);
                return loaded ?? AppConfig.CreateDefault();
            }
            catch
            {
                return AppConfig.CreateDefault();
            }
        }

        public static void SaveConfig()
        {
            lock (Sync)
            {
                Directory.CreateDirectory(appRoot);
                File.WriteAllText(configPath, serializer.Serialize(config), new UTF8Encoding(false));
            }
        }

        public static string NormalizeExtension(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string ext = raw.Trim().ToLowerInvariant();
            if (!ext.StartsWith(".")) ext = "." + ext;
            if (!Regex.IsMatch(ext, "^\\.[a-z0-9][a-z0-9_-]{1,19}$")) return null;
            return ext;
        }

        public static bool IsProtectedAssociation(string extension)
        {
            string ext = NormalizeExtension(extension);
            if (ext == null) return true;
            string[] protectedExts = new string[] { ".exe", ".com", ".bat", ".cmd", ".msi", ".ps1", ".vbs", ".js", ".lnk", ".zip", ".rar", ".7z" };
            return protectedExts.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }

        public static void RegisterExtension(string extension)
        {
            string ext = NormalizeExtension(extension);
            if (ext == null) throw new InvalidOperationException("Enter a custom extension such as .devmind, .vibeapp, or .sphere.");
            if (IsProtectedAssociation(ext)) throw new InvalidOperationException("LaunchBridge will not take over common executable or archive extensions. Use a custom extension instead.");

            string progId = "LaunchBridge.Package" + ext.Replace('.', '_');
            string legacyProgId = "DevMind.LaunchBridge" + ext.Replace('.', '_');
            using (RegistryKey extKey = Registry.CurrentUser.CreateSubKey("Software\\Classes\\" + ext))
            {
                extKey.SetValue("", progId);
                extKey.SetValue("Content Type", "application/zip");
                extKey.SetValue("PerceivedType", "compressed");
            }
            using (RegistryKey progKey = Registry.CurrentUser.CreateSubKey("Software\\Classes\\" + progId))
            {
                progKey.SetValue("", "LaunchBridge package (" + ext + ")");
                ExtensionProfile profile = FindExtensionProfile(ext);
                string iconValue = profile != null && !string.IsNullOrWhiteSpace(profile.IconIcoPath) && File.Exists(profile.IconIcoPath)
                    ? Quote(profile.IconIcoPath)
                    : Quote(CurrentExePath) + ",0";
                using (RegistryKey iconKey = progKey.CreateSubKey("DefaultIcon"))
                    iconKey.SetValue("", iconValue);
                using (RegistryKey shellKey = progKey.CreateSubKey("shell"))
                    shellKey.SetValue("", "open");
                using (RegistryKey openKey = progKey.CreateSubKey("shell\\open"))
                {
                    openKey.SetValue("", "Open with LaunchBridge");
                    openKey.SetValue("Icon", CurrentExePath);
                    using (RegistryKey commandKey = openKey.CreateSubKey("command"))
                        commandKey.SetValue("", Quote(CurrentExePath) + " \"%1\"");
                }
                using (RegistryKey extractKey = progKey.CreateSubKey("shell\\extract"))
                {
                    extractKey.SetValue("", "Extract All...");
                    extractKey.SetValue("Icon", "%SystemRoot%\\System32\\zipfldr.dll,0", RegistryValueKind.ExpandString);
                    using (RegistryKey extractCommand = extractKey.CreateSubKey("command"))
                        extractCommand.SetValue("", Quote(CurrentExePath) + " --extract \"%1\"");
                }
            }
            try { Registry.CurrentUser.DeleteSubKeyTree("Software\\Classes\\" + legacyProgId, false); } catch { }

            lock (Sync)
            {
                List<string> updated = config.RegisteredExtensions == null
                    ? new List<string>()
                    : config.RegisteredExtensions.ToList();
                if (!updated.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase)))
                    updated.Add(ext);
                config.RegisteredExtensions = updated.OrderBy(x => x).ToList();
                SaveConfig();
            }
            NotifyAssociationChanged();
            Log("Registered extension " + ext);
        }

        public static void UnregisterExtension(string extension)
        {
            string ext = NormalizeExtension(extension);
            if (ext == null) return;
            string progId = "LaunchBridge.Package" + ext.Replace('.', '_');
            string legacyProgId = "DevMind.LaunchBridge" + ext.Replace('.', '_');
            try { Registry.CurrentUser.DeleteSubKeyTree("Software\\Classes\\" + ext, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree("Software\\Classes\\" + progId, false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree("Software\\Classes\\" + legacyProgId, false); } catch { }
            lock (Sync)
            {
                List<string> updated = config.RegisteredExtensions == null
                    ? new List<string>()
                    : config.RegisteredExtensions.Where(x => !string.Equals(x, ext, StringComparison.OrdinalIgnoreCase)).ToList();
                config.RegisteredExtensions = updated;
                if (config.ExtensionProfiles != null)
                    config.ExtensionProfiles = config.ExtensionProfiles.Where(x => x == null || !string.Equals(x.Extension, ext, StringComparison.OrdinalIgnoreCase)).ToList();
                if (string.Equals(config.DefaultExtension, ext, StringComparison.OrdinalIgnoreCase))
                {
                    config.DefaultExtension = updated.Count > 0 ? updated[0] : ".devmind";
                }
                SaveConfig();
            }
            try { File.Delete(Path.Combine(extensionProfilesRoot, ext.TrimStart('.') + ".profile.json")); } catch { }
            NotifyAssociationChanged();
            Log("Unregistered extension " + ext);
        }

        public static void InstallDefaultAssociations()
        {
            foreach (string ext in RegisteredExtensionsSnapshot()) RegisterExtension(ext);
            if (config.AddZipContextMenu) SetZipContextMenu(true);
        }

        public static void RemoveAllAssociations()
        {
            foreach (string ext in RegisteredExtensionsSnapshot())
            {
                string progId = "LaunchBridge.Package" + ext.Replace('.', '_');
                string legacyProgId = "DevMind.LaunchBridge" + ext.Replace('.', '_');
                try { Registry.CurrentUser.DeleteSubKeyTree("Software\\Classes\\" + ext, false); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree("Software\\Classes\\" + progId, false); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree("Software\\Classes\\" + legacyProgId, false); } catch { }
            }
            try { Registry.CurrentUser.DeleteSubKeyTree("Software\\Classes\\SystemFileAssociations\\.zip\\shell\\LaunchBridge", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree("Software\\Classes\\SystemFileAssociations\\.zip\\shell\\DevMindLaunchBridge", false); } catch { }
            NotifyAssociationChanged();
            Log("Removed all LaunchBridge file associations.");
        }

        public static void SetZipContextMenu(bool enabled)
        {
            const string keyPath = "Software\\Classes\\SystemFileAssociations\\.zip\\shell\\LaunchBridge";
            const string legacyKeyPath = "Software\\Classes\\SystemFileAssociations\\.zip\\shell\\DevMindLaunchBridge";
            try { Registry.CurrentUser.DeleteSubKeyTree(legacyKeyPath, false); } catch { }
            if (enabled)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    key.SetValue("", "Open package with LaunchBridge");
                    key.SetValue("Icon", CurrentExePath);
                    using (RegistryKey cmd = key.CreateSubKey("command"))
                        cmd.SetValue("", Quote(CurrentExePath) + " \"%1\"");
                }
            }
            else
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); } catch { }
            }
            config.AddZipContextMenu = enabled;
            SaveConfig();
            NotifyAssociationChanged();
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void NotifyAssociationChanged()
        {
            try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch { }
        }


public static void InstallSmartClickNativeHost()
{
    string hostExe = Path.Combine(appRoot, "LaunchBridgeSmartClickHost.exe");
    string manifestPath = Path.Combine(appRoot, "smart-click-native-host.json");
    if (!File.Exists(hostExe)) throw new FileNotFoundException("The Smart Click helper is missing.", hostExe);
    if (!Directory.Exists(SmartClickExtensionFolder)) throw new DirectoryNotFoundException("The Smart Click browser extension folder is missing.");

    Dictionary<string, object> manifest = new Dictionary<string, object>();
    manifest["name"] = SmartClickHostName;
    manifest["description"] = "LaunchBridge Smart Click native messaging host";
    manifest["path"] = hostExe;
    manifest["type"] = "stdio";
    manifest["allowed_origins"] = new string[] { "chrome-extension://" + SmartClickExtensionId + "/" };
    File.WriteAllText(manifestPath, serializer.Serialize(manifest), new UTF8Encoding(false));

    string[] registryPaths = new string[]
    {
        "Software\\Google\\Chrome\\NativeMessagingHosts\\" + SmartClickHostName,
        "Software\\Microsoft\\Edge\\NativeMessagingHosts\\" + SmartClickHostName,
        "Software\\BraveSoftware\\Brave-Browser\\NativeMessagingHosts\\" + SmartClickHostName
    };
    foreach (string registryPath in registryPaths)
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(registryPath))
            key.SetValue(null, manifestPath, RegistryValueKind.String);
    }
    Log("Smart Click native host registered for Chrome, Edge, and Brave.");
}

public static void RemoveSmartClickNativeHost()
{
    string[] registryPaths = new string[]
    {
        "Software\\Google\\Chrome\\NativeMessagingHosts\\" + SmartClickHostName,
        "Software\\Microsoft\\Edge\\NativeMessagingHosts\\" + SmartClickHostName,
        "Software\\BraveSoftware\\Brave-Browser\\NativeMessagingHosts\\" + SmartClickHostName
    };
    foreach (string registryPath in registryPaths)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(registryPath, false); }
        catch { }
    }
    try { File.Delete(Path.Combine(appRoot, "smart-click-native-host.json")); }
    catch { }
}

public static string GetSmartClickStatusText()
{
    bool extensionFiles = Directory.Exists(SmartClickExtensionFolder) && File.Exists(Path.Combine(SmartClickExtensionFolder, "manifest.json"));
    bool hostExe = File.Exists(Path.Combine(appRoot, "LaunchBridgeSmartClickHost.exe"));
    bool registered = false;
    try
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Edge\\NativeMessagingHosts\\" + SmartClickHostName))
            registered = key != null && !string.IsNullOrWhiteSpace(Convert.ToString(key.GetValue(null)));
    }
    catch { }
    if (extensionFiles && hostExe && registered)
        return "LaunchBridge is ready. Load the browser companion once to turn on one-click AI downloads.";
    return "Smart Click needs setup. Click Set up Smart Click.";
}

public static string PrepareSmartClickSetup()
{
    InstallSmartClickNativeHost();
    if (!Directory.Exists(SmartClickExtensionFolder)) throw new DirectoryNotFoundException("The browser companion folder is missing.");
    try { Clipboard.SetText(SmartClickExtensionFolder); }
    catch { }
    try { Process.Start("explorer.exe", SmartClickExtensionFolder); }
    catch { }
    bool browserOpened = false;
    try
    {
        Process.Start(new ProcessStartInfo("msedge.exe", "edge://extensions/") { UseShellExecute = true });
        browserOpened = true;
    }
    catch { }
    if (!browserOpened)
    {
        try { Process.Start(new ProcessStartInfo("chrome.exe", "chrome://extensions/") { UseShellExecute = true }); }
        catch { }
    }
    return SmartClickExtensionFolder;
}

private static ExtensionProfile ResolveSmartPackageProfile(string packagePath)
{
    string extension = NormalizeExtension(Path.GetExtension(packagePath));
    ExtensionProfile profile = FindExtensionProfile(extension);
    if (profile != null) return profile;
    if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
    {
        ExtensionProfile zipProfile = ExtensionProfile.CreateDefault(".zip", "Smart Click ZIP", "Runnable app downloaded from an approved AI page");
        zipProfile.ManifestRequired = false;
        zipProfile.ManifestAllowed = true;
        return zipProfile;
    }
    return null;
}

        public static void ApplyTurboLaunchSettings()
        {
            const string runKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
            const string valueName = "LaunchBridge";
            const string legacyValueName = "DevMind LaunchBridge";
            try
            {
                using (RegistryKey runKey = Registry.CurrentUser.CreateSubKey(runKeyPath))
                {
                    runKey.DeleteValue(legacyValueName, false);
                    if (TurboLaunchEnabled)
                        runKey.SetValue(valueName, Quote(CurrentExePath) + " --resident");
                    else
                        runKey.DeleteValue(valueName, false);
                }
            }
            catch (Exception ex)
            {
                Log("Turbo Launch startup registration warning: " + ex.Message);
            }
        }

        public static void RemoveTurboLaunchStartup()
        {
            const string runKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
            try
            {
                using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(runKeyPath, true))
                {
                    if (runKey != null)
                    {
                        runKey.DeleteValue("LaunchBridge", false);
                        runKey.DeleteValue("DevMind LaunchBridge", false);
                    }
                }
            }
            catch { }
        }

        public static bool TryTurboLaunchInstalledPackage(string packagePath, out InstallResult result)
        {
            result = null;
            if (!TurboLaunchEnabled || config == null || !config.AutoLaunch) return false;
            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath)) return false;

            string logPath = NewLogPath("turbo-launch");
            try
            {
                string manifestJson;
                string manifestSignature;
                DevMindPackageManifest manifest;
                if (!TryReadPackageManifestFast(packagePath, logPath, out manifestJson, out manifestSignature, out manifest))
                    return TryTurboLaunchSmartPackage(packagePath, logPath, out result);
                ValidateManifest(manifest);
                if (string.Equals(manifest.ProductId, "devmind-launchbridge", StringComparison.OrdinalIgnoreCase)) return false;

                ProductRecord record = FindInstalledProduct(manifest.ProductId);
                if (record == null || !Directory.Exists(record.InstallPath)) return false;
                if (!string.Equals(record.Version, manifest.Version, StringComparison.OrdinalIgnoreCase)) return false;

                bool signatureMatches = !string.IsNullOrWhiteSpace(record.PackageManifestSignature) &&
                    string.Equals(record.PackageManifestSignature, manifestSignature, StringComparison.OrdinalIgnoreCase);

                // One-time migration for products installed before Turbo Launch stored a manifest signature.
                // The original package path is trusted only when it is the exact source recorded at install
                // time and Windows reports that the file has not been modified after that installation.
                if (!signatureMatches && string.IsNullOrWhiteSpace(record.PackageManifestSignature) &&
                    !string.IsNullOrWhiteSpace(record.SourcePackage) &&
                    string.Equals(Path.GetFullPath(record.SourcePackage), Path.GetFullPath(packagePath), StringComparison.OrdinalIgnoreCase))
                {
                    DateTime installedUtc;
                    DateTime sourceWriteUtc = File.GetLastWriteTimeUtc(packagePath);
                    if (DateTime.TryParse(record.InstalledAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out installedUtc) &&
                        sourceWriteUtc <= installedUtc.ToUniversalTime().AddSeconds(2))
                    {
                        record.PackageManifestSignature = manifestSignature;
                        UpsertProduct(record);
                        signatureMatches = true;
                        WriteLog(logPath, "Migrated verified source package into the Turbo Launch cache for " + record.DisplayName + ".");
                    }
                }

                if (!signatureMatches) return false;
                if (!RequiredInstalledFilesPresent(record, manifest)) return false;

                result = new InstallResult();
                result.LogPath = logPath;
                result.Product = record;
                result.InstallPath = record.InstallPath;
                result.Success = true;
                result.Launched = true;
                result.TurboLaunched = true;

                if (IsProductRunning(record))
                {
                    result.ProcessId = record.LastProcessId;
                    result.UiProcessId = record.LastUiProcessId;
                    result.UiTargetId = record.LastUiTargetId;
                    result.Message = record.DisplayName + " " + record.Version + " is already running. Turbo Launch reused the verified installation.";
                    WriteLog(logPath, result.Message);
                    return true;
                }

                int processId = LaunchProduct(record, logPath);
                result.ProcessId = processId;
                result.UiProcessId = record.LastUiProcessId;
                result.UiTargetId = record.LastUiTargetId;
                result.Message = record.DisplayName + " " + record.Version + " turbo-launched from its verified installation.";
                WriteLog(logPath, result.Message);
                SuccessSignal();
                return true;
            }
            catch (Exception ex)
            {
                WriteLog(logPath, "Turbo Launch bypassed; normal package validation will run. " + ex.Message);
                result = null;
                return false;
            }
        }

        private static bool TryTurboLaunchSmartPackage(string packagePath, string logPath, out InstallResult result)
        {
            result = null;
            string ext = NormalizeExtension(Path.GetExtension(packagePath));
            ExtensionProfile profile = ResolveSmartPackageProfile(packagePath);
            if (profile == null) return false;

            string productName;
            string version;
            ParseSmartPackageFileName(packagePath, out productName, out version);
            string extensionKey = ext.TrimStart('.').ToLowerInvariant();
            string slug = Regex.Replace(productName.ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-', '_', '.');
            if (slug.Length < 2) slug = "application";
            ProductRecord record = FindInstalledProduct(extensionKey + "." + slug);
            if (record == null || !Directory.Exists(record.InstallPath)) return false;
            if (!string.Equals(record.Version, version, StringComparison.OrdinalIgnoreCase)) return false;
            bool sourceMetadataMatches = false;
            try
            {
                FileInfo sourceInfo = new FileInfo(packagePath);
                DateTime recordedWrite;
                sourceMetadataMatches = !string.IsNullOrWhiteSpace(record.SourcePackage) &&
                    string.Equals(Path.GetFullPath(record.SourcePackage), Path.GetFullPath(packagePath), StringComparison.OrdinalIgnoreCase) &&
                    record.SourcePackageLength == sourceInfo.Length &&
                    DateTime.TryParse(record.SourcePackageWriteUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out recordedWrite) &&
                    Math.Abs((sourceInfo.LastWriteTimeUtc - recordedWrite.ToUniversalTime()).TotalSeconds) < 1.0;
            }
            catch { }
            if (!sourceMetadataMatches)
            {
                string fingerprint = ComputeSha256(packagePath);
                if (string.IsNullOrWhiteSpace(record.PackageManifestSignature) || !string.Equals(record.PackageManifestSignature, fingerprint, StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (!string.IsNullOrWhiteSpace(record.EntryPoint))
            {
                string entry = SafeCombine(record.InstallPath, record.EntryPoint);
                if (!File.Exists(entry)) return false;
            }

            result = new InstallResult();
            result.LogPath = logPath;
            result.Product = record;
            result.InstallPath = record.InstallPath;
            result.Success = true;
            result.Launched = true;
            result.TurboLaunched = true;
            if (IsProductRunning(record))
            {
                result.ProcessId = record.LastProcessId;
                result.UiProcessId = record.LastUiProcessId;
                result.UiTargetId = record.LastUiTargetId;
                result.Message = record.DisplayName + " " + record.Version + " is already running. Turbo Launch reused the verified smart package installation.";
                WriteLog(logPath, result.Message);
                return true;
            }
            int processId = LaunchProduct(record, logPath);
            result.ProcessId = processId;
            result.UiProcessId = record.LastUiProcessId;
            result.UiTargetId = record.LastUiTargetId;
            result.Message = record.DisplayName + " " + record.Version + " turbo-launched through its " + ext + " Extension Profile.";
            WriteLog(logPath, result.Message);
            SuccessSignal();
            return true;
        }

        private static bool TryReadPackageManifestFast(string packagePath, string logPath, out string manifestJson, out string manifestSignature, out DevMindPackageManifest manifest)
        {
            manifestJson = null;
            manifestSignature = null;
            manifest = null;
            using (FileStream stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
            {
                ZipArchiveEntry entry = archive.Entries.FirstOrDefault(x =>
                    x != null && string.Equals((x.FullName ?? "").Replace('\\', '/').TrimStart('/'), "devmind.package.json", StringComparison.OrdinalIgnoreCase));
                if (entry == null) return false;
                if (entry.Length < 1 || entry.Length > 4L * 1024L * 1024L)
                    throw new InvalidDataException("The package manifest size is invalid.");
                using (Stream entryStream = entry.Open())
                using (StreamReader reader = new StreamReader(entryStream, Encoding.UTF8, true))
                    manifestJson = reader.ReadToEnd();
            }

            manifest = DeserializeManifestCompatible(manifestJson, logPath);
            manifestSignature = ComputeManifestSignature(manifestJson);
            return true;
        }

        private static string ComputeManifestSignature(string manifestJson)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(manifestJson ?? "");
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder value = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) value.Append(b.ToString("x2"));
                return value.ToString();
            }
        }

        private static bool RequiredInstalledFilesPresent(ProductRecord record, DevMindPackageManifest manifest)
        {
            if (record == null || manifest == null || !Directory.Exists(record.InstallPath)) return false;
            foreach (string required in manifest.RequiredFiles ?? new List<string>())
            {
                string requiredPath;
                try { requiredPath = SafeCombine(record.InstallPath, required); }
                catch { return false; }
                if (!File.Exists(requiredPath) && !Directory.Exists(requiredPath)) return false;
            }
            if (!string.IsNullOrWhiteSpace(record.EntryPoint))
            {
                string entryPath;
                try { entryPath = SafeCombine(record.InstallPath, record.EntryPoint); }
                catch { return false; }
                if (!File.Exists(entryPath) && !Directory.Exists(entryPath)) return false;
            }
            return true;
        }

        private static DevMindPackageManifest BuildSmartManifest(string packagePath, string extractedRoot, ExtensionProfile profile, string logPath, out string payloadPath)
        {
            payloadPath = ResolveSmartPayloadRoot(extractedRoot);
            string productName;
            string version;
            ParseSmartPackageFileName(packagePath, out productName, out version);

            string extensionKey = (profile.Extension ?? Path.GetExtension(packagePath) ?? ".app").TrimStart('.').ToLowerInvariant();
            string slug = Regex.Replace(productName.ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-', '_', '.');
            if (slug.Length < 2) slug = "application";
            string productId = extensionKey + "." + slug;

            string entryType;
            string entryPoint = DetectSmartEntryPoint(payloadPath, productName, productId, logPath, out entryType);
            if (string.IsNullOrWhiteSpace(entryPoint))
                throw new InvalidDataException("LaunchBridge could not find a clear runnable entry point. Include a product .exe, packaged Electron app, start.cmd/start.bat/start.ps1, a complete Node app, or index.html.");

            DevMindPackageManifest manifest = new DevMindPackageManifest();
            manifest.SchemaVersion = 1;
            manifest.ProductId = productId;
            manifest.DisplayName = productName;
            manifest.Version = string.IsNullOrWhiteSpace(version) ? "unversioned" : version;
            manifest.Publisher = "AI-built package";
            manifest.Description = string.IsNullOrWhiteSpace(profile.Description) ? profile.DisplayName : profile.Description;
            manifest.PayloadRoot = ".";
            manifest.InstallDirectoryName = Regex.Replace(productName, "[^A-Za-z0-9._-]+", "_").Trim('_', '.');
            if (string.IsNullOrWhiteSpace(manifest.InstallDirectoryName)) manifest.InstallDirectoryName = slug;
            manifest.EntryPoint = entryPoint;
            manifest.EntryType = entryType;
            manifest.WorkingDirectory = ".";
            manifest.LaunchDelayMilliseconds = 350;
            manifest.RequiredFiles = new List<string>();
            manifest.RequiredFiles.Add(entryPoint);
            manifest.PreserveStatePaths = profile.PreserveState && profile.PreserveStatePaths != null
                ? profile.PreserveStatePaths.ToList()
                : new List<string>();
            manifest.FileHashes = new Dictionary<string, string>();
            manifest.ReleaseChannel = "smart";
            manifest.PackageNotes = "Manifest-free smart package interpreted through the " + profile.Extension + " Extension Profile.";
            WriteLog(logPath, "Smart package profile: " + profile.Extension + " | product: " + manifest.DisplayName + " | version: " + manifest.Version + " | entry: " + entryPoint);
            return manifest;
        }

        private static string ResolveSmartPayloadRoot(string extractedRoot)
        {
            string[] files = Directory.GetFiles(extractedRoot, "*", SearchOption.TopDirectoryOnly)
                .Where(x => !string.Equals(Path.GetFileName(x), ".DS_Store", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            string[] dirs = Directory.GetDirectories(extractedRoot, "*", SearchOption.TopDirectoryOnly)
                .Where(x => !string.Equals(Path.GetFileName(x), "__MACOSX", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (files.Length == 0 && dirs.Length == 1) return dirs[0];
            return extractedRoot;
        }

        private static void ParseSmartPackageFileName(string packagePath, out string productName, out string version)
        {
            string baseName = Path.GetFileNameWithoutExtension(packagePath) ?? "Application";
            Match match = Regex.Match(baseName, "^(?<name>.+?)[_-]v?(?<version>\\d+\\.\\d+\\.\\d+(?:[-+][A-Za-z0-9.-]+)?)$", RegexOptions.IgnoreCase);
            if (!match.Success)
                match = Regex.Match(baseName, "^(?<name>.+?)[_-](?<version>\\d+\\.\\d+(?:\\.\\d+)?(?:[-+][A-Za-z0-9.-]+)?)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                productName = match.Groups["name"].Value;
                version = match.Groups["version"].Value;
            }
            else
            {
                productName = baseName;
                version = "unversioned";
            }
            productName = Regex.Replace(productName.Replace('_', ' '), "\\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(productName)) productName = "Application";
        }

        private static string DetectSmartEntryPoint(string payloadPath, string productName, string productId, string logPath, out string entryType)
        {
            entryType = null;
            ProductRecord existing = FindInstalledProduct(productId);
            if (existing != null && !string.IsNullOrWhiteSpace(existing.EntryPoint))
            {
                try
                {
                    string remembered = SafeCombine(payloadPath, existing.EntryPoint);
                    if (File.Exists(remembered))
                    {
                        entryType = string.IsNullOrWhiteSpace(existing.EntryType) ? Path.GetExtension(remembered).TrimStart('.').ToLowerInvariant() : existing.EntryType;
                        WriteLog(logPath, "Reused the remembered launch target: " + existing.EntryPoint);
                        return existing.EntryPoint.Replace('\\', '/');
                    }
                }
                catch { }
            }

            List<string> allFiles = Directory.GetFiles(payloadPath, "*", SearchOption.AllDirectories).ToList();
            string productToken = Regex.Replace(productName.ToLowerInvariant(), "[^a-z0-9]+", "");
            string[] excludedExeTokens = new string[] { "unins", "uninstall", "update", "squirrel", "crashpad", "elevate", "helper", "notification" };
            List<string> executables = allFiles.Where(x => string.Equals(Path.GetExtension(x), ".exe", StringComparison.OrdinalIgnoreCase))
                .Where(x => !excludedExeTokens.Any(t => Path.GetFileNameWithoutExtension(x).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
            if (executables.Count > 0)
            {
                string best = executables
                    .OrderByDescending(x => SmartEntryScore(payloadPath, x, productToken))
                    .ThenByDescending(x => new FileInfo(x).Length)
                    .First();
                entryType = "exe";
                return MakeRelativePath(payloadPath, best).Replace('\\', '/');
            }

            string[] launcherNames = new string[] { "start.cmd", "launch.cmd", "run.cmd", "start.bat", "launch.bat", "run.bat", "start.ps1", "launch.ps1", "run.ps1" };
            foreach (string launcherName in launcherNames)
            {
                string launcher = allFiles
                    .Where(x => string.Equals(Path.GetFileName(x), launcherName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => RelativeDepth(payloadPath, x))
                    .FirstOrDefault();
                if (launcher != null)
                {
                    entryType = Path.GetExtension(launcher).TrimStart('.').ToLowerInvariant();
                    return MakeRelativePath(payloadPath, launcher).Replace('\\', '/');
                }
            }

            string packageJson = allFiles.Where(x => string.Equals(Path.GetFileName(x), "package.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => RelativeDepth(payloadPath, x)).FirstOrDefault();
            if (packageJson != null)
            {
                string packageFolder = Path.GetDirectoryName(packageJson);
                string nodeModules = Path.Combine(packageFolder, "node_modules");
                if (Directory.Exists(nodeModules))
                {
                    Dictionary<string, object> raw = null;
                    try { raw = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(packageJson, Encoding.UTF8)); } catch { }
                    string generated = Path.Combine(packageFolder, "launchbridge-start.cmd");
                    string electronCmd = Path.Combine(nodeModules, ".bin", "electron.cmd");
                    if (File.Exists(electronCmd))
                    {
                        File.WriteAllText(generated, "@echo off\r\ncd /d \"%~dp0\"\r\ncall \"node_modules\\.bin\\electron.cmd\" .\r\n", Encoding.ASCII);
                        entryType = "cmd";
                        return MakeRelativePath(payloadPath, generated).Replace('\\', '/');
                    }
                    object scriptsObject;
                    Dictionary<string, object> scripts = null;
                    if (raw != null && raw.TryGetValue("scripts", out scriptsObject)) scripts = scriptsObject as Dictionary<string, object>;
                    object startObject;
                    if (scripts != null && scripts.TryGetValue("start", out startObject) && startObject != null)
                    {
                        File.WriteAllText(generated, "@echo off\r\ncd /d \"%~dp0\"\r\ncall npm start\r\n", Encoding.ASCII);
                        entryType = "cmd";
                        return MakeRelativePath(payloadPath, generated).Replace('\\', '/');
                    }
                    object mainObject;
                    if (raw != null && raw.TryGetValue("main", out mainObject) && mainObject != null)
                    {
                        string main = Convert.ToString(mainObject, CultureInfo.InvariantCulture);
                        string mainPath = Path.Combine(packageFolder, main.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(mainPath))
                        {
                            File.WriteAllText(generated, "@echo off\r\ncd /d \"%~dp0\"\r\nnode \"" + main.Replace("\"", "") + "\"\r\n", Encoding.ASCII);
                            entryType = "cmd";
                            return MakeRelativePath(payloadPath, generated).Replace('\\', '/');
                        }
                    }
                }
            }

            string html = allFiles.Where(x => string.Equals(Path.GetFileName(x), "index.html", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => RelativeDepth(payloadPath, x)).FirstOrDefault();
            if (html != null)
            {
                entryType = "html";
                return MakeRelativePath(payloadPath, html).Replace('\\', '/');
            }
            return null;
        }

        private static int SmartEntryScore(string root, string path, string productToken)
        {
            int score = 0;
            int depth = RelativeDepth(root, path);
            score += Math.Max(0, 100 - (depth * 20));
            string token = Regex.Replace(Path.GetFileNameWithoutExtension(path).ToLowerInvariant(), "[^a-z0-9]+", "");
            if (!string.IsNullOrWhiteSpace(productToken) && string.Equals(token, productToken, StringComparison.OrdinalIgnoreCase)) score += 500;
            else if (!string.IsNullOrWhiteSpace(productToken) && (token.Contains(productToken) || productToken.Contains(token))) score += 220;
            if (depth == 0) score += 120;
            return score;
        }

        private static int RelativeDepth(string root, string path)
        {
            string relative = MakeRelativePath(root, path).Replace('\\', '/');
            return relative.Count(x => x == '/');
        }

        public static InstallResult InstallAndLaunchPackage(string packagePath)
        {
            InstallResult result = new InstallResult();
            string tempRoot = null;
            string stateBackup = null;
            string rollbackPath = null;
            string installPath = null;
            DevMindPackageManifest manifest = null;
            ProductRecord record = null;
            string logPath = NewLogPath("install");
            result.LogPath = logPath;
            try
            {
                if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
                    throw new FileNotFoundException("The package file was not found.", packagePath);

                WriteLog(logPath, "Opening package: " + packagePath);
                tempRoot = Path.Combine(Path.GetTempPath(), "LaunchBridge", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempRoot);
                SafeExtract(packagePath, tempRoot, logPath);

                string manifestPath = Path.Combine(tempRoot, "devmind.package.json");
                string packageManifestSignature;
                string payloadPath;
                bool smartPackage = !File.Exists(manifestPath);
                ExtensionProfile smartProfile = null;

                if (!smartPackage)
                {
                    string manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
                    packageManifestSignature = ComputeManifestSignature(manifestJson);
                    manifest = DeserializeManifestCompatible(manifestJson, logPath);
                    ValidateManifest(manifest);
                    string manifestFolder = Path.GetDirectoryName(manifestPath);
                    payloadPath = SafeCombine(manifestFolder, string.IsNullOrWhiteSpace(manifest.PayloadRoot) ? "payload" : manifest.PayloadRoot);
                    if (!Directory.Exists(payloadPath)) throw new InvalidDataException("The package payload folder is missing.");
                    VerifyPackageHashes(manifest, payloadPath, logPath);
                    VerifyPrerequisites(manifest, logPath);
                    WriteLog(logPath, "Using advanced manifest package mode.");
                }
                else
                {
                    string packageExtension = NormalizeExtension(Path.GetExtension(packagePath));
                    smartProfile = ResolveSmartPackageProfile(packagePath);
                    if (smartProfile == null)
                        throw new InvalidDataException("LaunchBridge does not know how to open this file type. Smart Click supports normal ZIP app packages and registered custom package types.");
                    packageManifestSignature = ComputeSha256(packagePath);
                    manifest = BuildSmartManifest(packagePath, tempRoot, smartProfile, logPath, out payloadPath);
                    ValidateManifest(manifest);
                    WriteLog(logPath, "No JSON manifest was required. LaunchBridge derived the package contract from the filename, extension profile, and archive contents.");
                }

                string dirName = string.IsNullOrWhiteSpace(manifest.InstallDirectoryName) ? manifest.ProductId : manifest.InstallDirectoryName;
                dirName = SanitizeFolderName(dirName);
                // LaunchBridge updates are bootstrap packages, not ordinary products.
                // Give every self-update a unique disposable staging folder so a stale
                // cmd.exe working directory or antivirus scan can never block the next update.
                if (string.Equals(manifest.ProductId, "devmind-launchbridge", StringComparison.OrdinalIgnoreCase))
                {
                    string safeUpdateVersion = Regex.Replace(manifest.Version ?? "update", "[^A-Za-z0-9_-]+", "_");
                    dirName = SanitizeFolderName("LaunchBridge_Update_" + safeUpdateVersion + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
                }
                Directory.CreateDirectory(config.WorkRoot);
                installPath = Path.Combine(config.WorkRoot, dirName);
                result.InstallPath = installPath;

                WriteLog(logPath, "Validated: " + manifest.DisplayName + " " + manifest.Version);
                WriteLog(logPath, "Install path: " + installPath);

                if (Directory.Exists(installPath))
                {
                    ProductRecord existing = InstalledProductsSnapshot().FirstOrDefault(x => x != null &&
                        (string.Equals(x.ProductId, manifest.ProductId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.InstallPath, installPath, StringComparison.OrdinalIgnoreCase)));
                    string stopMessage;
                    if (existing != null)
                    {
                        WriteLog(logPath, "Stopping the existing product before update: " + existing.DisplayName);
                        StopProduct(existing, out stopMessage);
                        WriteLog(logPath, stopMessage);
                    }
                    else
                    {
                        int killed = KillProcessesMatchingToken(installPath, true, logPath);
                        if (killed > 0) WriteLog(logPath, "Stopped " + killed + " untracked process(es) using the install folder.");
                    }
                    Thread.Sleep(650);
                    stateBackup = BackupState(installPath, manifest.PreserveStatePaths, logPath);
                    if (config.KeepRollback)
                    {
                        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                        rollbackPath = Path.Combine(rollbacksRoot, SanitizeFolderName(manifest.ProductId), stamp);
                        Directory.CreateDirectory(Path.GetDirectoryName(rollbackPath));
                        CopyDirectory(installPath, rollbackPath, true);
                        WriteLog(logPath, "Rollback snapshot: " + rollbackPath);
                    }
                    DeleteDirectoryRobust(installPath);
                }

                CopyDirectory(payloadPath, installPath, true);
                RestoreState(stateBackup, installPath, manifest.PreserveStatePaths, logPath);

                foreach (string req in manifest.RequiredFiles ?? new List<string>())
                {
                    string requiredPath = SafeCombine(installPath, req);
                    if (!File.Exists(requiredPath) && !Directory.Exists(requiredPath))
                        throw new InvalidDataException("Required file is missing after installation: " + req);
                }

                record = new ProductRecord();
                record.ProductId = manifest.ProductId;
                record.DisplayName = string.IsNullOrWhiteSpace(manifest.DisplayName) ? manifest.ProductId : manifest.DisplayName;
                record.Version = manifest.Version;
                record.Publisher = manifest.Publisher;
                record.InstallPath = installPath;
                record.EntryPoint = manifest.EntryPoint;
                record.EntryType = manifest.EntryType;
                record.Arguments = manifest.Arguments;
                record.WorkingDirectory = manifest.WorkingDirectory;
                record.LaunchUrl = manifest.LaunchUrl;
                record.LaunchDelayMilliseconds = manifest.LaunchDelayMilliseconds <= 0 ? 350 : manifest.LaunchDelayMilliseconds;
                record.InstalledAtUtc = DateTime.UtcNow.ToString("o");
                record.SourcePackage = packagePath;
                try
                {
                    FileInfo sourceInfo = new FileInfo(packagePath);
                    record.SourcePackageLength = sourceInfo.Length;
                    record.SourcePackageWriteUtc = sourceInfo.LastWriteTimeUtc.ToString("o");
                }
                catch { }
                record.PackageManifestSignature = packageManifestSignature;
                record.SourceExtension = NormalizeExtension(Path.GetExtension(packagePath));
                record.PackageMode = smartPackage ? "smart" : "manifest";
                record.RollbackPath = rollbackPath;
                record.LastKnownStatus = "Installed";

                if (RuntimeMonitorEnabled && !string.IsNullOrWhiteSpace(record.LaunchUrl))
                    InstrumentProductHtml(record, logPath);

                File.WriteAllText(Path.Combine(installPath, ".devmind-installed.json"), serializer.Serialize(record), new UTF8Encoding(false));
                UpsertProduct(record);

                result.Product = record;
                result.Success = true;
                result.Message = record.DisplayName + " " + record.Version + " installed successfully.";

                bool shouldAutoLaunch = config.AutoLaunch && (!smartPackage || smartProfile == null || smartProfile.AutoLaunch);
                if (shouldAutoLaunch)
                {
                    int processId = LaunchProduct(record, logPath);
                    result.ProcessId = processId;
                    result.UiProcessId = record.LastUiProcessId;
                    result.UiTargetId = record.LastUiTargetId;
                    result.Launched = true;
                    result.Message += " It has been launched.";
                }

                WriteLog(logPath, result.Message);
                SuccessSignal();
                return result;
            }
            catch (Exception ex)
            {
                WriteLog(logPath, "ERROR: " + ex.ToString());
                result.Success = false;
                result.Message = ex.Message;
                TryRollbackFailedInstall(installPath, rollbackPath, logPath);
                ReportPackageOperationFailure(packagePath, manifest, record, ex, logPath, installPath);
                ErrorSignal(ex, logPath);
                return result;
            }
            finally
            {
                TryDelete(tempRoot);
                TryDelete(stateBackup);
            }
        }

        public static int LaunchProduct(ProductRecord record, string logPath)
        {
            if (record == null) throw new ArgumentNullException("record");
            if (!Directory.Exists(record.InstallPath)) throw new DirectoryNotFoundException("Installed product folder is missing: " + record.InstallPath);

            if (IsProductRunning(record))
            {
                string priorMessage;
                StopProduct(record, out priorMessage);
                WriteLog(logPath, "Restart handoff: " + priorMessage);
                Thread.Sleep(450);
            }

            bool usesPortToken = !string.IsNullOrWhiteSpace(record.LaunchUrl) &&
                record.LaunchUrl.IndexOf("{port}", StringComparison.OrdinalIgnoreCase) >= 0;
            bool fixedLoopbackUrl = !usesPortToken && IsLoopbackLaunchUrl(record.LaunchUrl);
            bool triedConflictShutdown = false;
            bool forceDynamicPort = false;
            Exception lastConflict = null;
            int maxAttempts = (usesPortToken || fixedLoopbackUrl) ? 5 : 1;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                int assignedPort;
                string resolvedLaunchUrl = ResolveLaunchUrl(record.LaunchUrl, forceDynamicPort, out assignedPort);
                record.LastResolvedLaunchUrl = resolvedLaunchUrl;
                WriteLog(logPath, "Launch attempt " + attempt + " of " + maxAttempts + " using " + (string.IsNullOrWhiteSpace(resolvedLaunchUrl) ? "no launch URL" : resolvedLaunchUrl) + ".");

                try
                {
                    return LaunchProductAttempt(record, resolvedLaunchUrl, assignedPort, logPath);
                }
                catch (PortConflictException conflict)
                {
                    lastConflict = conflict;
                    WriteLog(logPath, "Port identity conflict: " + conflict.Message);

                    if (usesPortToken)
                    {
                        WriteLog(logPath, "The package uses {port}; assigning another free port and retrying without stopping the existing product.");
                        Thread.Sleep(180);
                        continue;
                    }

                    if (fixedLoopbackUrl && !triedConflictShutdown)
                    {
                        triedConflictShutdown = true;
                        if (TryReleaseConflictingProduct(conflict, logPath))
                        {
                            WriteLog(logPath, "The conflicting legacy fixed-port product was fully stopped and its port was released. Retrying the requested product on its declared port.");
                            Thread.Sleep(250);
                            continue;
                        }
                        WriteLog(logPath, "The conflicting runtime resisted graceful and trusted force-kill recovery; trying the package with a free DEVMIND_PORT as a final compatibility path.");
                    }

                    if (fixedLoopbackUrl && !forceDynamicPort)
                    {
                        forceDynamicPort = true;
                        WriteLog(logPath, "Legacy fixed-port recovery: assigning a free port through DEVMIND_PORT and retrying.");
                        Thread.Sleep(180);
                        continue;
                    }

                    throw;
                }
            }

            throw new InvalidOperationException(
                "LaunchBridge exhausted its automatic port recovery attempts for " + record.DisplayName + ". " +
                (lastConflict == null ? "No additional conflict detail was available." : lastConflict.Message),
                lastConflict);
        }

        private static int LaunchProductAttempt(ProductRecord record, string resolvedLaunchUrl, int assignedPort, string logPath)
        {
            string entry = string.IsNullOrWhiteSpace(record.EntryPoint) ? null : SafeCombine(record.InstallPath, record.EntryPoint);
            string working = record.InstallPath;
            if (!string.IsNullOrWhiteSpace(record.WorkingDirectory) && record.WorkingDirectory != ".")
                working = SafeCombine(record.InstallPath, record.WorkingDirectory);

            int initialPid = 0;
            int backendPid = 0;
            string launchEntryType = "";

            if (entry != null)
            {
                if (!File.Exists(entry)) throw new FileNotFoundException("The product entry point is missing.", entry);

                string type = (record.EntryType ?? "auto").Trim().ToLowerInvariant();
                if (type == "auto") type = Path.GetExtension(entry).TrimStart('.').ToLowerInvariant();
                launchEntryType = type;

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.WorkingDirectory = working;
                psi.UseShellExecute = type == "html";
                psi.WindowStyle = ProcessWindowStyle.Normal;
                psi.CreateNoWindow = false;

                if (type == "bat" || type == "cmd")
                {
                    psi.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
                    psi.Arguments = "/c \"\"" + entry + "\" " + (record.Arguments ?? "") + "\"";
                }
                else if (type == "ps1" || Path.GetExtension(entry).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                {
                    psi.FileName = "powershell.exe";
                    psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + entry + "\" " + (record.Arguments ?? "");
                }
                else
                {
                    psi.FileName = entry;
                    psi.Arguments = record.Arguments ?? "";
                }

                bool captureConsoleStreams = !psi.UseShellExecute && IsConsoleEntryType(type);
                if (captureConsoleStreams)
                {
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                }

                if (!psi.UseShellExecute)
                {
                    psi.EnvironmentVariables["DEVMIND_PRODUCT_ID"] = record.ProductId ?? "";
                    psi.EnvironmentVariables["DEVMIND_PRODUCT_VERSION"] = record.Version ?? "";
                    if (assignedPort > 0) psi.EnvironmentVariables["DEVMIND_PORT"] = assignedPort.ToString();
                    if (!string.IsNullOrWhiteSpace(resolvedLaunchUrl)) psi.EnvironmentVariables["DEVMIND_LAUNCH_URL"] = resolvedLaunchUrl;
                }

                WriteLog(logPath, "Launching product process: " + psi.FileName + " " + psi.Arguments);
                Process p = StartProcessWithRetry(psi, logPath);
                if (p == null) throw new InvalidOperationException("Windows did not start the product process.");
                initialPid = p.Id;
                backendPid = initialPid;
                if (!string.Equals(type, "html", StringComparison.OrdinalIgnoreCase))
                    AttachLaunchProcessDiagnostics(record, p, entry, type, psi.FileName + " " + psi.Arguments, logPath, captureConsoleStreams);
            }

            if (!string.IsNullOrWhiteSpace(resolvedLaunchUrl))
            {
                try
                {
                    LocalLaunchHealth health = WaitForLaunchHealth(record, resolvedLaunchUrl, initialPid, logPath);
                    if (health != null && health.ProcessId > 0) backendPid = health.ProcessId;
                    record.LastUiTargetId = LaunchUrlInManagedTab(record, resolvedLaunchUrl, logPath);
                    record.LastUiProcessId = 0;
                    record.LastUiUrl = resolvedLaunchUrl;
                }
                catch
                {
                    if (initialPid > 0) TryClosePid(initialPid, true);
                    KillProcessesMatchingToken(record.InstallPath, true, logPath);
                    throw;
                }
            }

            if (backendPid <= 0 && string.IsNullOrWhiteSpace(record.LastUiTargetId))
                throw new InvalidOperationException("The product has no entry point or launch URL.");

            record.LastProcessId = backendPid;
            record.LastLauncherProcessId = initialPid;
            record.LastLauncherEntryType = launchEntryType;
            record.LastAutoStopReason = null;
            record.LastLaunchAtUtc = DateTime.UtcNow.ToString("o");
            record.LastKnownStatus = "Running";
            UpsertProduct(record);
            RegisterProductLifecycleWatch(record, initialPid, backendPid, launchEntryType);
            return backendPid;
        }

        private static Process StartProcessWithRetry(ProcessStartInfo psi, string logPath)
        {
            Exception last = null;
            for (int attempt = 1; attempt <= 10; attempt++)
            {
                try
                {
                    Process process = Process.Start(psi);
                    if (process != null) return process;
                    last = new InvalidOperationException("Windows returned no process handle.");
                }
                catch (Win32Exception ex)
                {
                    last = ex;
                    WriteLog(logPath, "Process start attempt " + attempt + " failed with Windows error " + ex.NativeErrorCode + ": " + ex.Message);
                    if (ex.NativeErrorCode != 1460 && ex.Message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) < 0) throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    WriteLog(logPath, "Process start attempt " + attempt + " failed: " + ex.Message);
                    if (ex.Message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) < 0) throw;
                }
                Thread.Sleep(Math.Min(5000, 350 * attempt));
            }
            throw new InvalidOperationException("Windows timed out while starting the product after 10 attempts. LaunchBridge did not modify the working installation.", last);
        }

        private const int MaxCapturedProcessText = 65536;

        private static void AttachLaunchProcessDiagnostics(ProductRecord record, Process process, string entryPath, string entryType, string commandLine, string logPath, bool captureStreams)
        {
            if (record == null || process == null || process.Id <= 0) return;

            LaunchProcessCapture capture = new LaunchProcessCapture();
            capture.Process = process;
            capture.ProcessId = process.Id;
            capture.ProductId = record.ProductId;
            capture.DisplayName = record.DisplayName;
            capture.Version = record.Version;
            capture.InstallPath = record.InstallPath;
            capture.EntryPath = entryPath;
            capture.EntryType = entryType;
            capture.CommandLine = commandLine;
            capture.LogPath = logPath;
            capture.StartedAtUtc = DateTime.UtcNow;
            capture.CaptureStreams = captureStreams;

            if (captureStreams)
            {
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    AppendCapturedProcessLine(capture, capture.StandardOutput, args == null ? null : args.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    AppendCapturedProcessLine(capture, capture.StandardError, args == null ? null : args.Data);
                };
            }

            process.Exited += delegate { QueueLaunchProcessCompletion(capture); };
            lock (LaunchProcessSync) launchProcessCaptures[capture.ProcessId] = capture;

            try
            {
                process.EnableRaisingEvents = true;
                if (captureStreams)
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }
                if (process.HasExited) QueueLaunchProcessCompletion(capture);
            }
            catch (Exception ex)
            {
                WriteLog(logPath, "Process diagnostic attachment warning: " + ex.Message);
                try { if (process.HasExited) QueueLaunchProcessCompletion(capture); } catch { }
            }
        }

        private static void AppendCapturedProcessLine(LaunchProcessCapture capture, StringBuilder target, string line)
        {
            if (capture == null || target == null || line == null) return;
            lock (capture.TextSync)
            {
                if (target.Length >= MaxCapturedProcessText) return;
                int remaining = MaxCapturedProcessText - target.Length;
                string value = line.Length <= remaining ? line : line.Substring(0, remaining);
                target.AppendLine(value);
            }
        }

        private static void QueueLaunchProcessCompletion(LaunchProcessCapture capture)
        {
            if (capture == null || Interlocked.CompareExchange(ref capture.CompletionStarted, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate { CompleteLaunchProcessCapture(capture); });
        }

        private static void CompleteLaunchProcessCapture(LaunchProcessCapture capture)
        {
            if (capture == null) return;
            int exitCode = int.MinValue;
            string standardOutput = "";
            string standardError = "";
            bool suppressed = false;

            try
            {
                // Give LaunchProductAttempt time to persist the launch record before a very fast
                // script failure is converted into a durable Problems-tab item.
                Thread.Sleep(300);
                try { capture.Process.WaitForExit(); } catch { }
                try { exitCode = capture.Process.ExitCode; } catch { }
                lock (capture.TextSync)
                {
                    standardOutput = capture.StandardOutput.ToString();
                    standardError = capture.StandardError.ToString();
                }
            }
            finally
            {
                lock (LaunchProcessSync)
                {
                    launchProcessCaptures.Remove(capture.ProcessId);
                    suppressed = suppressedExitIssuePids.Remove(capture.ProcessId);
                }
            }

            try
            {
                WriteLog(capture.LogPath, "Product entry process exited. PID " + capture.ProcessId + ", exit code " + (exitCode == int.MinValue ? "unavailable" : exitCode.ToString()) + ".");
                if (!string.IsNullOrWhiteSpace(standardError)) WriteLog(capture.LogPath, "Standard error:\r\n" + LimitText(standardError, MaxCapturedProcessText));
                if (!string.IsNullOrWhiteSpace(standardOutput)) WriteLog(capture.LogPath, "Standard output:\r\n" + LimitText(standardOutput, MaxCapturedProcessText));
            }
            catch { }

            try { capture.Process.Dispose(); } catch { }
            if (suppressed || exitCode == int.MinValue || exitCode == 0) return;
            ReportProductProcessExit(capture, exitCode, standardOutput, standardError);
        }

        private static void ReportProductProcessExit(LaunchProcessCapture capture, int exitCode, string standardOutput, string standardError)
        {
            string firstDiagnostic = FirstUsefulDiagnosticLine(standardError);
            if (string.IsNullOrWhiteSpace(firstDiagnostic)) firstDiagnostic = FirstUsefulDiagnosticLine(standardOutput);

            RuntimeIssue issue = new RuntimeIssue();
            issue.ProductId = string.IsNullOrWhiteSpace(capture.ProductId) ? "unknown-product" : capture.ProductId;
            issue.Product = capture.DisplayName;
            issue.Version = capture.Version;
            issue.Severity = "error";
            issue.Type = "process-exit";
            issue.Message = (capture.DisplayName ?? capture.ProductId ?? "The product") + " exited unexpectedly with code " + exitCode + "." +
                (string.IsNullOrWhiteSpace(firstDiagnostic) ? "" : " " + firstDiagnostic);
            issue.Source = capture.EntryPath;
            issue.Title = "Entrypoint process exited";
            issue.InstallPath = capture.InstallPath;
            StringBuilder details = new StringBuilder();
            details.AppendLine("Process ID: " + capture.ProcessId);
            details.AppendLine("Entry type: " + (capture.EntryType ?? ""));
            details.AppendLine("Command: " + (capture.CommandLine ?? ""));
            details.AppendLine("Exit code: " + exitCode);
            details.AppendLine("Launch log: " + (capture.LogPath ?? ""));
            if (!string.IsNullOrWhiteSpace(standardError))
            {
                details.AppendLine();
                details.AppendLine("Standard error:");
                details.AppendLine(LimitText(standardError, 16000));
            }
            if (!string.IsNullOrWhiteSpace(standardOutput))
            {
                details.AppendLine();
                details.AppendLine("Standard output:");
                details.AppendLine(LimitText(standardOutput, 12000));
            }
            issue.Stack = details.ToString();
            ReceiveRuntimeIssue(issue);

            ProductRecord current = FindInstalledProduct(capture.ProductId);
            if (current != null && (current.LastLauncherProcessId == capture.ProcessId || current.LastProcessId == capture.ProcessId))
            {
                current.LastKnownStatus = "Crashed (exit code " + exitCode + ")";
                current.LastAutoStopReason = "The entry process exited unexpectedly with code " + exitCode + ".";
                current.LastProcessId = 0;
                current.LastLauncherProcessId = 0;
                current.LastLauncherEntryType = null;
                UpsertProduct(current);
            }
        }

        private static string FirstUsefulDiagnosticLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in lines)
            {
                string value = (line ?? "").Trim();
                if (value.Length == 0) continue;
                return value.Length <= 300 ? value : value.Substring(0, 300) + "...";
            }
            return "";
        }

        private static void SuppressProcessExitIssue(int processId)
        {
            if (processId <= 0) return;
            lock (LaunchProcessSync)
            {
                if (launchProcessCaptures.ContainsKey(processId)) suppressedExitIssuePids.Add(processId);
            }
        }

        private sealed class LocalLaunchHealth
        {
            public string ProductId;
            public string Version;
            public int ProcessId;
            public string Origin;
        }

        private sealed class PortConflictException : InvalidOperationException
        {
            public string ActualProductId;
            public string ActualVersion;
            public int ActualProcessId;
            public string ActualOrigin;

            public PortConflictException(string message, string productId, string version, int processId, string origin)
                : base(message)
            {
                ActualProductId = productId;
                ActualVersion = version;
                ActualProcessId = processId;
                ActualOrigin = origin;
            }
        }

        private sealed class BrowserTarget
        {
            public string id { get; set; }
            public string type { get; set; }
            public string url { get; set; }
            public string title { get; set; }
        }

        private static string ResolveLaunchUrl(string launchUrl, bool forceDynamicPort, out int assignedPort)
        {
            assignedPort = 0;
            if (string.IsNullOrWhiteSpace(launchUrl)) return launchUrl;

            bool hasToken = launchUrl.IndexOf("{port}", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hasToken && !forceDynamicPort) return launchUrl;

            assignedPort = FindFreeTcpPort();
            if (hasToken)
                return Regex.Replace(launchUrl, "\\{port\\}", assignedPort.ToString(), RegexOptions.IgnoreCase);

            Uri uri;
            if (!Uri.TryCreate(launchUrl, UriKind.Absolute, out uri) || !uri.IsLoopback) return launchUrl;
            UriBuilder builder = new UriBuilder(uri);
            builder.Port = assignedPort;
            return builder.Uri.ToString().TrimEnd('/');
        }

        private static bool IsLoopbackLaunchUrl(string launchUrl)
        {
            Uri uri;
            return !string.IsNullOrWhiteSpace(launchUrl) &&
                launchUrl.IndexOf("{port}", StringComparison.OrdinalIgnoreCase) < 0 &&
                Uri.TryCreate(launchUrl, UriKind.Absolute, out uri) && uri.IsLoopback;
        }

        private static bool TryReleaseConflictingProduct(PortConflictException conflict, string logPath)
        {
            if (conflict == null) return false;
            bool attempted = false;

            ProductRecord occupant = InstalledProductsSnapshot().FirstOrDefault(x => x != null &&
                !string.IsNullOrWhiteSpace(conflict.ActualProductId) &&
                string.Equals(x.ProductId, conflict.ActualProductId, StringComparison.OrdinalIgnoreCase));

            string origin = conflict.ActualOrigin;
            Uri originUri = null;
            bool hasLoopbackOrigin = !string.IsNullOrWhiteSpace(origin) &&
                Uri.TryCreate(origin, UriKind.Absolute, out originUri) && originUri.IsLoopback;
            int conflictPort = hasLoopbackOrigin ? originUri.Port : 0;

            // First request a graceful stop through every supported path.
            if (occupant != null)
            {
                string stopMessage;
                attempted = true;
                StopProduct(occupant, out stopMessage);
                WriteLog(logPath, "Fixed-port conflict handoff: " + stopMessage);
            }

            if (hasLoopbackOrigin)
            {
                try
                {
                    attempted = true;
                    string shutdownUrl = originUri.GetLeftPart(UriPartial.Authority) + "/__launchbridge/shutdown";
                    HttpRequestText(shutdownUrl, "POST", 2200);
                    WriteLog(logPath, "Requested clean shutdown from the conflicting managed runtime at " + originUri.GetLeftPart(UriPartial.Authority) + ".");
                }
                catch (Exception ex)
                {
                    WriteLog(logPath, "Conflicting runtime did not accept clean shutdown: " + ex.Message);
                }

                if (WaitForLoopbackPortRelease(conflictPort, 4500, logPath)) return true;
            }

            // A runtime that advertises an exact ProductId matching an installed product
            // is trusted enough for a targeted process-tree kill. This never applies to an
            // arbitrary localhost service and never kills the managed browser or LaunchBridge.
            bool trustedReportedPid = occupant != null &&
                conflict.ActualProcessId > 0 &&
                conflict.ActualProcessId != Process.GetCurrentProcess().Id &&
                string.Equals(occupant.ProductId, conflict.ActualProductId, StringComparison.OrdinalIgnoreCase);

            if (occupant != null)
            {
                int matched = KillProcessesMatchingToken(occupant.InstallPath, true, logPath);
                if (matched > 0)
                {
                    attempted = true;
                    WriteLog(logPath, "Forced closure of " + matched + " process(es) still using the conflicting product folder.");
                }
            }

            if (trustedReportedPid && IsProcessRunning(conflict.ActualProcessId))
            {
                attempted = true;
                WriteLog(logPath, "The registered managed runtime still owns PID " + conflict.ActualProcessId + "; force-killing its process tree.");
                if (!TryClosePid(conflict.ActualProcessId, true))
                    WriteLog(logPath, "PID " + conflict.ActualProcessId + " did not exit after taskkill.");
            }

            if (hasLoopbackOrigin)
            {
                bool released = WaitForLoopbackPortRelease(conflictPort, 10000, logPath);
                if (!released)
                    WriteLog(logPath, "Port " + conflictPort + " is still occupied after graceful shutdown and trusted force-kill recovery.");
                return released;
            }

            return attempted;
        }

        private static bool WaitForLoopbackPortRelease(int port, int timeoutMs, string logPath)
        {
            if (port <= 0) return false;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, timeoutMs));
            while (DateTime.UtcNow < deadline)
            {
                if (IsLoopbackPortFree(port))
                {
                    WriteLog(logPath, "Confirmed that loopback port " + port + " was released.");
                    return true;
                }
                Thread.Sleep(200);
            }
            return IsLoopbackPortFree(port);
        }

        private static bool IsLoopbackPortFree(int port)
        {
            if (port <= 0 || port > 65535) return false;
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            finally
            {
                if (listener != null)
                {
                    try { listener.Stop(); }
                    catch { }
                }
            }
        }

        private static int FindFreeTcpPort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }

        private static LocalLaunchHealth WaitForLaunchHealth(ProductRecord record, string launchUrl, int initialPid, string logPath)
        {
            Uri launchUri;
            if (!Uri.TryCreate(launchUrl, UriKind.Absolute, out launchUri))
                throw new InvalidOperationException("The launch URL is invalid: " + launchUrl);

            if (!launchUri.IsLoopback)
            {
                int delay = record.LaunchDelayMilliseconds <= 0 ? 350 : record.LaunchDelayMilliseconds;
                Thread.Sleep(delay);
                return null;
            }

            Uri healthUri = new Uri(launchUri.GetLeftPart(UriPartial.Authority) + "/__launchbridge/health");
            int timeoutMs = Math.Max(45000, (record.LaunchDelayMilliseconds <= 0 ? 350 : record.LaunchDelayMilliseconds) + 30000);
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            string lastDetail = "No health response was received.";

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    string json = HttpRequestText(healthUri.ToString(), "GET", 900);
                    Dictionary<string, object> data = serializer.Deserialize<Dictionary<string, object>>(json);
                    string actualStatus = DictionaryString(data, "status");
                    string actualProduct = DictionaryString(data, "productId");
                    string actualVersion = DictionaryString(data, "version");
                    int actualPid = DictionaryInt(data, "pid");
                    string origin = DictionaryString(data, "origin");

                    if (!string.IsNullOrWhiteSpace(actualProduct) && !string.Equals(actualProduct, record.ProductId, StringComparison.OrdinalIgnoreCase))
                    {
                        ProductRecord occupant = InstalledProductsSnapshot().FirstOrDefault(x => x != null && string.Equals(x.ProductId, actualProduct, StringComparison.OrdinalIgnoreCase));
                        string occupantName = occupant == null ? actualProduct : occupant.DisplayName;
                        string conflictOrigin = string.IsNullOrWhiteSpace(origin) ? launchUri.GetLeftPart(UriPartial.Authority) : origin;
                        throw new PortConflictException(
                            "Launch identity conflict at " + launchUri.GetLeftPart(UriPartial.Authority) + ": it is serving " +
                            (string.IsNullOrWhiteSpace(occupantName) ? "another product" : occupantName) +
                            " instead of " + record.DisplayName + ". LaunchBridge will attempt an automatic fixed-port handoff or a new dynamic port.",
                            actualProduct,
                            actualVersion,
                            actualPid,
                            conflictOrigin);
                    }

                    if (!string.IsNullOrWhiteSpace(actualProduct) && !string.IsNullOrWhiteSpace(record.Version) && !string.Equals(actualVersion, record.Version, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Launch health returned version " + actualVersion + " instead of " + record.Version + " for " + record.DisplayName + ".");

                    if (!string.Equals(actualStatus, "ready", StringComparison.OrdinalIgnoreCase))
                    {
                        lastDetail = "Health status is " + (string.IsNullOrWhiteSpace(actualStatus) ? "missing" : actualStatus) + "; waiting for ready.";
                    }
                    else if (string.IsNullOrWhiteSpace(actualProduct))
                    {
                        lastDetail = "Health response is missing productId; waiting for a complete identity.";
                    }
                    else
                    {
                        WriteLog(logPath, "Launch health passed: " + actualProduct + " " + actualVersion + " PID " + actualPid);
                        return new LocalLaunchHealth { ProductId = actualProduct, Version = actualVersion, ProcessId = actualPid, Origin = origin };
                    }
                }
                catch (WebException ex)
                {
                    lastDetail = DescribeWebException(ex);
                }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex)
                {
                    lastDetail = ex.Message;
                }

                Thread.Sleep(180);
            }

            bool starterExited = initialPid > 0 && !IsProcessRunning(initialPid);
            throw new InvalidOperationException(
                "Launch health validation failed for " + record.DisplayName + ". No browser tab was opened, preventing the wrong local site from appearing. " +
                (starterExited ? "The starter process exited. " : "") + "Last detail: " + lastDetail);
        }

        private static string DictionaryString(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.ContainsKey(key) || data[key] == null) return "";
            return Convert.ToString(data[key]);
        }

        private static int DictionaryInt(Dictionary<string, object> data, string key)
        {
            int value;
            return int.TryParse(DictionaryString(data, key), out value) ? value : 0;
        }

        private static string LaunchUrlInManagedTab(ProductRecord record, string url, string logPath)
        {
            string browser = FindBrowserExecutable();
            if (string.IsNullOrWhiteSpace(browser))
            {
                WriteLog(logPath, "No Edge or Chrome executable was found. Opening the URL with the Windows default browser without tab control.");
                Process.Start(url);
                record.LastUiMode = "UntrackedDefaultBrowser";
                record.LastUiTargetId = null;
                return null;
            }

            int port = EnsureManagedBrowser(browser, logPath);
            BrowserTarget target = CreateManagedBrowserTabWithRetry(port, url, logPath);

            if (target == null || string.IsNullOrWhiteSpace(target.id))
            {
                WriteLog(logPath, "Managed browser tab API did not become available. Trying command-line tab creation fallback.");
                StartManagedBrowserUrl(browser, url, logPath);
                target = WaitForManagedBrowserTarget(port, url, 12000, logPath);
            }

            if (target == null || string.IsNullOrWhiteSpace(target.id))
                throw new InvalidOperationException(
                    "The product backend is ready, but LaunchBridge could not create or identify its managed browser tab. " +
                    "The browser control endpoint remained unavailable after retries. Close the dedicated managed browser window and try Open file again.");

            record.LastUiMode = "ManagedBrowserTab";
            record.LastUiTargetId = target.id;
            record.LastUiUrl = url;
            WriteLog(logPath, "Opened managed browser tab " + target.id + " for " + record.DisplayName);
            CloseUnusedBlankManagedTabs(port, target.id, logPath);
            return target.id;
        }

        private static BrowserTarget CreateManagedBrowserTabWithRetry(int port, string url, string logPath)
        {
            string endpoint = "http://127.0.0.1:" + port + "/json/new?" + Uri.EscapeDataString(url);
            DateTime deadline = DateTime.UtcNow.AddSeconds(18);
            string lastDetail = "No response from the managed browser tab endpoint.";
            int attempt = 0;

            while (DateTime.UtcNow < deadline)
            {
                attempt++;
                try
                {
                    string json = HttpRequestText(endpoint, "PUT", 2500);
                    BrowserTarget target = serializer.Deserialize<BrowserTarget>(json);
                    if (target != null && !string.IsNullOrWhiteSpace(target.id)) return target;
                    lastDetail = "The browser returned a response without a target id.";
                }
                catch (WebException ex)
                {
                    lastDetail = DescribeWebException(ex);
                    int statusCode = WebExceptionStatusCode(ex);
                    WriteLog(logPath, "Managed tab attempt " + attempt + " waiting after " + lastDetail);
                    if (statusCode > 0 && statusCode < 500 && statusCode != 408 && statusCode != 429)
                        throw new InvalidOperationException("Managed browser tab creation failed: " + lastDetail);
                }
                catch (Exception ex)
                {
                    lastDetail = ex.Message;
                    WriteLog(logPath, "Managed tab attempt " + attempt + " waiting after " + lastDetail);
                }

                BrowserTarget existing = FindManagedBrowserTargetByUrl(port, url);
                if (existing != null) return existing;
                Thread.Sleep(300);
            }

            WriteLog(logPath, "Managed tab API retry window expired. Last detail: " + lastDetail);
            return FindManagedBrowserTargetByUrl(port, url);
        }

        private static void StartManagedBrowserUrl(string browser, string url, string logPath)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = browser;
            psi.Arguments = "--user-data-dir=\"" + managedBrowserProfileRoot + "\" \"" + url.Replace("\"", "") + "\"";
            psi.WorkingDirectory = Path.GetDirectoryName(browser);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process process = Process.Start(psi);
            WriteLog(logPath, "Requested managed-browser command-line tab fallback" + (process == null ? "." : " using PID " + process.Id + "."));
        }

        private static BrowserTarget WaitForManagedBrowserTarget(int port, string url, int timeoutMs, string logPath)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                BrowserTarget target = FindManagedBrowserTargetByUrl(port, url);
                if (target != null) return target;
                Thread.Sleep(250);
            }
            WriteLog(logPath, "The command-line browser fallback did not expose a target for " + url);
            return null;
        }

        private static BrowserTarget FindManagedBrowserTargetByUrl(int port, string url)
        {
            try
            {
                string json = HttpRequestText("http://127.0.0.1:" + port + "/json/list", "GET", 1200);
                List<BrowserTarget> targets = serializer.Deserialize<List<BrowserTarget>>(json) ?? new List<BrowserTarget>();
                string expected = NormalizeComparableUrl(url);
                return targets.FirstOrDefault(x => x != null && string.Equals(x.type, "page", StringComparison.OrdinalIgnoreCase) && string.Equals(NormalizeComparableUrl(x.url), expected, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        private static string NormalizeComparableUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return value.Trim().TrimEnd('/');
        }

        private static int WebExceptionStatusCode(WebException ex)
        {
            HttpWebResponse response = ex == null ? null : ex.Response as HttpWebResponse;
            return response == null ? 0 : (int)response.StatusCode;
        }

        private static string DescribeWebException(WebException ex)
        {
            if (ex == null) return "Unknown web error.";
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response == null) return ex.Message;
            string body = "";
            try
            {
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    body = reader.ReadToEnd();
            }
            catch { }
            string detail = "HTTP " + (int)response.StatusCode + " " + response.StatusDescription;
            if (!string.IsNullOrWhiteSpace(body)) detail += ": " + body.Trim();
            return detail;
        }

        private static int EnsureManagedBrowser(string browser, string logPath)
        {
            int existingPort = ReadManagedBrowserPort();
            if (existingPort > 0 && ManagedBrowserResponds(existingPort)) return existingPort;

            string activePortFile = Path.Combine(managedBrowserProfileRoot, "DevToolsActivePort");
            try { if (File.Exists(activePortFile)) File.Delete(activePortFile); } catch { }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = browser;
            psi.Arguments = "--remote-debugging-port=0 --user-data-dir=\"" + managedBrowserProfileRoot + "\" --no-first-run --disable-background-mode --new-window about:blank";
            psi.WorkingDirectory = Path.GetDirectoryName(browser);
            psi.UseShellExecute = false;
            WriteLog(logPath, "Starting one managed browser window for all web apps.");
            Process.Start(psi);

            DateTime deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                int port = ReadManagedBrowserPort();
                if (port > 0 && ManagedBrowserResponds(port)) return port;
                Thread.Sleep(150);
            }
            throw new InvalidOperationException("The managed Edge/Chrome window did not expose its control endpoint.");
        }

        private static int ReadManagedBrowserPort()
        {
            try
            {
                string path = Path.Combine(managedBrowserProfileRoot, "DevToolsActivePort");
                if (!File.Exists(path)) return 0;
                string first = File.ReadAllLines(path).FirstOrDefault();
                int port;
                return int.TryParse(first, out port) ? port : 0;
            }
            catch { return 0; }
        }

        private static bool ManagedBrowserResponds(int port)
        {
            try
            {
                HttpRequestText("http://127.0.0.1:" + port + "/json/version", "GET", 500);
                return true;
            }
            catch { return false; }
        }

        private static List<BrowserTarget> GetManagedBrowserTargets()
        {
            int port = ReadManagedBrowserPort();
            if (port <= 0) return new List<BrowserTarget>();
            try
            {
                string json = HttpRequestText("http://127.0.0.1:" + port + "/json/list", "GET", 650);
                return serializer.Deserialize<List<BrowserTarget>>(json) ?? new List<BrowserTarget>();
            }
            catch { return new List<BrowserTarget>(); }
        }

        private static void CloseUnusedBlankManagedTabs(int port, string keepTargetId, string logPath)
        {
            try
            {
                string json = HttpRequestText("http://127.0.0.1:" + port + "/json/list", "GET", 900);
                List<BrowserTarget> targets = serializer.Deserialize<List<BrowserTarget>>(json) ?? new List<BrowserTarget>();
                foreach (BrowserTarget target in targets)
                {
                    if (target == null || string.Equals(target.id, keepTargetId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(target.type, "page", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(target.url, "about:blank", StringComparison.OrdinalIgnoreCase)) continue;
                    try { HttpRequestText("http://127.0.0.1:" + port + "/json/close/" + target.id, "GET", 900); }
                    catch { }
                }
            }
            catch (Exception ex) { WriteLog(logPath, "Blank-tab cleanup warning: " + ex.Message); }
        }

        private static bool IsManagedTabOpen(string targetId)
        {
            if (string.IsNullOrWhiteSpace(targetId)) return false;
            return GetManagedBrowserTargets().Any(x => x != null && string.Equals(x.id, targetId, StringComparison.OrdinalIgnoreCase));
        }

        private static bool CloseManagedTab(string targetId)
        {
            if (string.IsNullOrWhiteSpace(targetId)) return false;
            int port = ReadManagedBrowserPort();
            if (port <= 0) return false;
            try
            {
                HttpRequestText("http://127.0.0.1:" + port + "/json/close/" + targetId, "GET", 1500);
                return true;
            }
            catch { return !IsManagedTabOpen(targetId); }
        }

        private static string HttpRequestText(string url, string method, int timeoutMs)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.Proxy = null;
            request.KeepAlive = false;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private static string FindBrowserExecutable()
        {
            List<string> candidates = new List<string>();
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"));
            candidates.Add(Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"));
            candidates.Add(Path.Combine(local, "Microsoft", "Edge", "Application", "msedge.exe"));
            candidates.Add(Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"));
            candidates.Add(Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"));
            candidates.Add(Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"));
            return candidates.FirstOrDefault(File.Exists);
        }

        private static string GetBrowserProfilePath(ProductRecord record)
        {
            string id = record == null ? "unknown-product" : SanitizeFolderName(record.ProductId);
            return Path.Combine(browserProfilesRoot, id);
        }

        private static void StartProductLifecycleSupervisor()
        {
            if (Interlocked.Exchange(ref lifecycleSupervisorStarted, 1) != 0) return;
            lifecycleSupervisorThread = new Thread(ProductLifecycleSupervisorLoop);
            lifecycleSupervisorThread.IsBackground = true;
            lifecycleSupervisorThread.Name = "LaunchBridge Product Lifecycle Supervisor";
            lifecycleSupervisorThread.Start();
        }

        private static void RestoreProductLifecycleWatches()
        {
            foreach (ProductRecord record in InstalledProductsSnapshot())
            {
                if (record == null || string.IsNullOrWhiteSpace(record.ProductId)) continue;
                int launcherPid = record.LastLauncherProcessId > 0 ? record.LastLauncherProcessId : record.LastProcessId;
                if (!IsProcessInstanceFromLaunch(launcherPid, record.LastLaunchAtUtc) &&
                    !IsProcessInstanceFromLaunch(record.LastProcessId, record.LastLaunchAtUtc)) continue;
                RegisterProductLifecycleWatch(record, launcherPid, record.LastProcessId, record.LastLauncherEntryType ?? record.EntryType);
            }
        }

        private static void RegisterProductLifecycleWatch(ProductRecord record, int launcherPid, int productPid, string entryType)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.ProductId) || launcherPid <= 0) return;
            LifecycleWatch watch = new LifecycleWatch();
            watch.ProductId = record.ProductId;
            watch.DisplayName = record.DisplayName;
            watch.InstallPath = record.InstallPath;
            watch.LauncherProcessId = launcherPid;
            watch.ProductProcessId = productPid;
            watch.LaunchAtUtc = record.LastLaunchAtUtc;
            watch.EntryType = entryType ?? "";
            watch.RegisteredAtUtc = DateTime.UtcNow;
            lock (LifecycleSync)
            {
                lifecycleWatches[record.ProductId] = watch;
            }
            Log("Lifecycle supervisor attached to " + record.DisplayName + " (launcher PID " + launcherPid + ", product PID " + productPid + ").");
        }

        private static void CancelProductLifecycleWatch(string productId)
        {
            if (string.IsNullOrWhiteSpace(productId)) return;
            lock (LifecycleSync)
            {
                lifecycleWatches.Remove(productId);
            }
        }

        private static bool IsLifecycleWatchCurrent(LifecycleWatch watch)
        {
            if (watch == null || string.IsNullOrWhiteSpace(watch.ProductId)) return false;
            lock (LifecycleSync)
            {
                LifecycleWatch current;
                return lifecycleWatches.TryGetValue(watch.ProductId, out current) && object.ReferenceEquals(current, watch);
            }
        }

        private static void ProductLifecycleSupervisorLoop()
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(1250);
                    List<LifecycleWatch> watches;
                    lock (LifecycleSync) watches = lifecycleWatches.Values.ToList();
                    if (watches.Count == 0) continue;
                    Dictionary<int, ProcessSnapshot> processTable = CaptureProcessTable();
                    foreach (LifecycleWatch watch in watches)
                    {
                        try { EvaluateLifecycleWatch(watch, processTable); }
                        catch (Exception ex) { Log("Lifecycle evaluation warning for " + watch.DisplayName + ": " + ex.Message); }
                    }
                }
                catch (Exception ex)
                {
                    Log("Lifecycle supervisor warning: " + ex.Message);
                    Thread.Sleep(1500);
                }
            }
        }

        private static void EvaluateLifecycleWatch(LifecycleWatch watch, Dictionary<int, ProcessSnapshot> processTable)
        {
            if (!IsLifecycleWatchCurrent(watch)) return;
            DateTime now = DateTime.UtcNow;
            HashSet<int> relevantIds = FindRelevantProcessIds(watch, processTable);
            List<int> liveRelevantIds = relevantIds.Where(x => processTable.ContainsKey(x)).ToList();
            bool launcherAlive = processTable.ContainsKey(watch.LauncherProcessId) &&
                IsProcessInstanceFromLaunch(watch.LauncherProcessId, watch.LaunchAtUtc);
            bool consoleAnchor = IsConsoleEntryType(watch.EntryType);

            if (consoleAnchor && launcherAlive && now.Subtract(watch.RegisteredAtUtc).TotalMilliseconds >= 3000)
            {
                watch.ConsoleAnchorConfirmed = true;
                watch.LauncherMissingSinceUtc = null;
            }
            else if (watch.ConsoleAnchorConfirmed && !launcherAlive)
            {
                if (!watch.LauncherMissingSinceUtc.HasValue) watch.LauncherMissingSinceUtc = now;
                if (now.Subtract(watch.LauncherMissingSinceUtc.Value).TotalMilliseconds >= 1000)
                {
                    AutoStopProductForAnchorExit(watch, "The command window or launcher process was closed.");
                    return;
                }
            }

            bool applicationWindowPresent = false;
            foreach (int processId in liveRelevantIds)
            {
                ProcessSnapshot snapshot;
                if (!processTable.TryGetValue(processId, out snapshot)) continue;
                if (IsApplicationWindowCandidate(snapshot) && HasVisibleMainWindow(processId))
                {
                    applicationWindowPresent = true;
                    break;
                }
            }

            if (applicationWindowPresent)
            {
                watch.ApplicationWindowSeen = true;
                watch.ApplicationWindowMissingSinceUtc = null;
            }
            else if (watch.ApplicationWindowSeen)
            {
                if (!watch.ApplicationWindowMissingSinceUtc.HasValue) watch.ApplicationWindowMissingSinceUtc = now;
                if (now.Subtract(watch.ApplicationWindowMissingSinceUtc.Value).TotalMilliseconds >= 2500)
                {
                    AutoStopProductForAnchorExit(watch, "The Electron or desktop application window was closed.");
                    return;
                }
            }

            ProductRecord record = FindInstalledProduct(watch.ProductId);
            if (liveRelevantIds.Count == 0)
            {
                CancelProductLifecycleWatch(watch.ProductId);
                if (record != null && IsLifecycleRecordStillCurrent(record, watch) && string.IsNullOrWhiteSpace(record.LastUiTargetId))
                {
                    record.LastKnownStatus = string.IsNullOrWhiteSpace(record.LastAutoStopReason) ? "Exited" : "Auto-stopped";
                    record.LastProcessId = 0;
                    record.LastLauncherProcessId = 0;
                    record.LastLauncherEntryType = null;
                    UpsertProduct(record);
                }
            }
        }

        private static bool IsLifecycleRecordStillCurrent(ProductRecord record, LifecycleWatch watch)
        {
            if (record == null || watch == null) return false;
            return string.Equals(record.ProductId, watch.ProductId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.LastLaunchAtUtc ?? "", watch.LaunchAtUtc ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<int, ProcessSnapshot> CaptureProcessTable()
        {
            Dictionary<int, ProcessSnapshot> table = new Dictionary<int, ProcessSnapshot>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject process in results)
                    {
                        ProcessSnapshot snapshot = new ProcessSnapshot();
                        snapshot.ProcessId = Convert.ToInt32((uint)process["ProcessId"]);
                        snapshot.ParentProcessId = process["ParentProcessId"] == null ? 0 : Convert.ToInt32((uint)process["ParentProcessId"]);
                        snapshot.Name = Convert.ToString(process["Name"]);
                        snapshot.ExecutablePath = Convert.ToString(process["ExecutablePath"]);
                        snapshot.CommandLine = Convert.ToString(process["CommandLine"]);
                        if (snapshot.ProcessId > 0) table[snapshot.ProcessId] = snapshot;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Lifecycle process snapshot warning: " + ex.Message);
            }
            return table;
        }

        private static HashSet<int> FindRelevantProcessIds(LifecycleWatch watch, Dictionary<int, ProcessSnapshot> processTable)
        {
            HashSet<int> relevant = new HashSet<int>();
            if (watch.LauncherProcessId > 0 &&
                (!processTable.ContainsKey(watch.LauncherProcessId) || IsProcessInstanceFromLaunch(watch.LauncherProcessId, watch.LaunchAtUtc)))
                relevant.Add(watch.LauncherProcessId);
            if (watch.ProductProcessId > 0 &&
                (!processTable.ContainsKey(watch.ProductProcessId) || IsProcessInstanceFromLaunch(watch.ProductProcessId, watch.LaunchAtUtc)))
                relevant.Add(watch.ProductProcessId);

            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (ProcessSnapshot snapshot in processTable.Values)
                {
                    if (snapshot.ProcessId <= 0 || relevant.Contains(snapshot.ProcessId)) continue;
                    if (relevant.Contains(snapshot.ParentProcessId))
                    {
                        relevant.Add(snapshot.ProcessId);
                        changed = true;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(watch.InstallPath))
            {
                foreach (ProcessSnapshot snapshot in processTable.Values)
                {
                    string command = snapshot.CommandLine ?? "";
                    string executable = snapshot.ExecutablePath ?? "";
                    if (command.IndexOf(watch.InstallPath, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        executable.IndexOf(watch.InstallPath, StringComparison.OrdinalIgnoreCase) >= 0)
                        relevant.Add(snapshot.ProcessId);
                }
            }

            relevant.Remove(Process.GetCurrentProcess().Id);
            return relevant;
        }

        private static bool IsConsoleEntryType(string entryType)
        {
            string type = (entryType ?? "").Trim().ToLowerInvariant();
            return type == "bat" || type == "cmd" || type == "ps1" || type == "powershell";
        }

        private static bool IsApplicationWindowCandidate(ProcessSnapshot snapshot)
        {
            if (snapshot == null) return false;
            string name = (snapshot.Name ?? "").Trim().ToLowerInvariant();
            if (name == "cmd.exe" || name == "powershell.exe" || name == "pwsh.exe" || name == "conhost.exe" ||
                name == "node.exe" || name == "npm.exe" || name == "npx.exe" || name == "yarn.exe" ||
                name == "msedge.exe" || name == "chrome.exe" || name == "taskkill.exe") return false;
            return true;
        }

        private static bool HasVisibleMainWindow(int processId)
        {
            if (processId <= 0) return false;
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (process.HasExited) return false;
                    process.Refresh();
                    return process.MainWindowHandle != IntPtr.Zero;
                }
            }
            catch { return false; }
        }

        private static void AutoStopProductForAnchorExit(LifecycleWatch watch, string reason)
        {
            if (!IsLifecycleWatchCurrent(watch)) return;
            CancelProductLifecycleWatch(watch.ProductId);
            ProductRecord record = FindInstalledProduct(watch.ProductId);
            if (record == null || !IsLifecycleRecordStillCurrent(record, watch)) return;

            string logPath = NewLogPath("lifecycle-auto-stop");
            WriteLog(logPath, record.DisplayName + ": " + reason);
            string stopMessage;
            StopProductInternal(record, true, out stopMessage);
            record.LastKnownStatus = "Auto-stopped";
            record.LastAutoStopReason = reason;
            record.LastProcessId = 0;
            record.LastLauncherProcessId = 0;
            record.LastLauncherEntryType = null;
            record.LastUiProcessId = 0;
            record.LastUiTargetId = null;
            record.LastUiUrl = null;
            record.LastUiMode = "Closed";
            UpsertProduct(record);
            WriteLog(logPath, stopMessage);
            Log(record.DisplayName + " was automatically stopped because its lifecycle anchor closed. " + reason);
        }

        public static bool IsProcessRunning(int processId)
        {
            if (processId <= 0) return false;
            try
            {
                using (Process process = Process.GetProcessById(processId))
                    return !process.HasExited;
            }
            catch { return false; }
        }

        public static bool IsTrackedProductProcessRunning(ProductRecord record)
        {
            if (record == null) return false;
            if (IsProcessInstanceFromLaunch(record.LastProcessId, record.LastLaunchAtUtc)) return true;
            return string.Equals(record.LastUiMode, "Legacy browser window", StringComparison.OrdinalIgnoreCase) &&
                IsProcessInstanceFromLaunch(record.LastUiProcessId, record.LastLaunchAtUtc);
        }

        private static bool IsProcessInstanceFromLaunch(int processId, string launchAtUtc)
        {
            if (processId <= 0 || string.IsNullOrWhiteSpace(launchAtUtc)) return false;
            DateTime launched;
            if (!DateTime.TryParse(launchAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out launched)) return false;
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (process.HasExited) return false;
                    DateTime started = process.StartTime.ToUniversalTime();
                    DateTime launchUtc = launched.ToUniversalTime();
                    return started >= launchUtc.AddMinutes(-10) && started <= launchUtc.AddMinutes(2);
                }
            }
            catch { return false; }
        }

        public static bool IsProductRunning(ProductRecord record)
        {
            if (record == null) return false;
            return IsTrackedProductProcessRunning(record) || IsManagedTabOpen(record.LastUiTargetId);
        }

        public static bool CloseProductUi(ProductRecord record, out string message)
        {
            message = "";
            if (record == null) { message = "No product was selected."; return false; }
            try
            {
                int closed = 0;
                if (!string.IsNullOrWhiteSpace(record.LastUiTargetId) && CloseManagedTab(record.LastUiTargetId)) closed++;
                if (closed == 0 && TryClosePid(record.LastUiProcessId, false)) closed++;
                if (closed == 0 && IsProcessRunning(record.LastUiProcessId) && TryClosePid(record.LastUiProcessId, true)) closed++;
                if (closed == 0 && string.Equals(record.LastUiMode, "Legacy browser window", StringComparison.OrdinalIgnoreCase))
                    closed += KillProcessesMatchingToken(GetBrowserProfilePath(record), true, null);
                record.LastUiProcessId = 0;
                record.LastUiTargetId = null;
                record.LastUiUrl = null;
                record.LastUiMode = "Closed";
                record.LastKnownStatus = IsProcessInstanceFromLaunch(record.LastProcessId, record.LastLaunchAtUtc) ? "Running without UI" : "Stopped";
                UpsertProduct(record);
                message = closed > 0 ? record.DisplayName + " browser tab was closed." : "No tracked product tab was open for " + record.DisplayName + ".";
                return true;
            }
            catch (Exception ex)
            {
                message = "Could not close the product tab: " + ex.Message;
                return false;
            }
        }

        public static bool StopProduct(ProductRecord record, out string message)
        {
            return StopProductInternal(record, false, out message);
        }

        public static bool ForceKillProduct(ProductRecord record, out string message)
        {
            return StopProductInternal(record, true, out message);
        }

        public static bool StopAllProducts(out string message)
        {
            List<ProductRecord> products = InstalledProductsSnapshot().Where(x => x != null).ToList();
            if (products.Count == 0)
            {
                message = "No installed products are tracked.";
                return true;
            }

            int completed = 0;
            List<string> failures = new List<string>();
            foreach (ProductRecord product in products)
            {
                string productMessage;
                if (StopProductInternal(product, true, out productMessage)) completed++;
                else failures.Add(product.DisplayName + ": " + productMessage);
            }

            if (failures.Count == 0)
            {
                message = "Stop all completed. " + completed + " tracked products were closed or had stale process tracking cleared. No products were uninstalled and no product data was deleted.";
                return true;
            }

            message = "Stop all completed with " + failures.Count + " warning(s). " + completed + " products were cleared.\r\n\r\n" + string.Join("\r\n", failures.ToArray());
            return false;
        }

        private static bool StopProductInternal(ProductRecord record, bool immediateForce, out string message)
        {
            message = "";
            if (record == null) { message = "No product was selected."; return false; }
            CancelProductLifecycleWatch(record.ProductId);
            try
            {
                HashSet<int> ids = new HashSet<int>();
                if (IsProcessInstanceFromLaunch(record.LastProcessId, record.LastLaunchAtUtc)) ids.Add(record.LastProcessId);
                if (IsProcessInstanceFromLaunch(record.LastLauncherProcessId, record.LastLaunchAtUtc)) ids.Add(record.LastLauncherProcessId);
                if (string.Equals(record.LastUiMode, "Legacy browser window", StringComparison.OrdinalIgnoreCase) &&
                    IsProcessInstanceFromLaunch(record.LastUiProcessId, record.LastLaunchAtUtc)) ids.Add(record.LastUiProcessId);
                foreach (int id in FindProcessIdsMatchingToken(record.InstallPath)) ids.Add(id);
                if (string.Equals(record.LastUiMode, "Legacy browser window", StringComparison.OrdinalIgnoreCase))
                    foreach (int id in FindProcessIdsMatchingToken(GetBrowserProfilePath(record))) ids.Add(id);
                ids.Remove(Process.GetCurrentProcess().Id);

                int affected = 0;
                if (!string.IsNullOrWhiteSpace(record.LastUiTargetId) && CloseManagedTab(record.LastUiTargetId)) affected++;
                foreach (int id in ids.ToArray())
                {
                    if (!IsProcessRunning(id)) continue;
                    if (!immediateForce && TryClosePid(id, false)) affected++;
                }
                if (!immediateForce) Thread.Sleep(900);
                foreach (int id in ids.ToArray())
                {
                    if (!IsProcessRunning(id)) continue;
                    if (TryClosePid(id, true)) affected++;
                }

                record.LastKnownStatus = immediateForce ? "Force killed" : "Stopped";
                record.LastProcessId = 0;
                record.LastLauncherProcessId = 0;
                record.LastLauncherEntryType = null;
                record.LastAutoStopReason = null;
                record.LastUiProcessId = 0;
                record.LastUiTargetId = null;
                record.LastUiUrl = null;
                record.LastUiMode = "Closed";
                UpsertProduct(record);
                message = affected > 0
                    ? record.DisplayName + (immediateForce ? " was force killed. Its process tree and managed browser tab were closed." : " was stopped. Its process tree and managed browser tab were closed.")
                    : "No running process or managed browser tab was found for " + record.DisplayName + ".";
                return true;
            }
            catch (Exception ex)
            {
                message = "Could not stop " + record.DisplayName + ": " + ex.Message;
                return false;
            }
        }

        private static bool TryClosePid(int processId, bool force)
        {
            if (processId <= 0 || processId == Process.GetCurrentProcess().Id || !IsProcessRunning(processId)) return false;
            SuppressProcessExitIssue(processId);
            try
            {
                if (!force)
                {
                    using (Process process = Process.GetProcessById(processId))
                    {
                        if (process.CloseMainWindow())
                        {
                            process.WaitForExit(700);
                            if (process.HasExited) return true;
                        }
                    }
                    return false;
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "taskkill.exe";
                psi.Arguments = "/PID " + processId + " /T /F";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process killer = Process.Start(psi))
                {
                    if (killer != null) killer.WaitForExit(5000);
                }
                return !IsProcessRunning(processId);
            }
            catch { return false; }
        }

        private static List<int> FindProcessIdsMatchingToken(string token)
        {
            List<int> ids = new List<int>();
            if (string.IsNullOrWhiteSpace(token)) return ids;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject process in results)
                    {
                        string command = Convert.ToString(process["CommandLine"]);
                        string executable = Convert.ToString(process["ExecutablePath"]);
                        if ((command != null && command.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) ||
                            (executable != null && executable.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            int id = Convert.ToInt32((uint)process["ProcessId"]);
                            if (id > 0 && id != Process.GetCurrentProcess().Id) ids.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Process discovery warning: " + ex.Message);
            }
            return ids.Distinct().ToList();
        }

        private static int KillProcessesMatchingToken(string token, bool force, string logPath)
        {
            int killed = 0;
            foreach (int id in FindProcessIdsMatchingToken(token))
            {
                if (TryClosePid(id, force))
                {
                    killed++;
                    if (!string.IsNullOrWhiteSpace(logPath)) WriteLog(logPath, "Closed matching process PID " + id + " for token " + token);
                }
            }
            return killed;
        }

        public static bool RollbackProduct(ProductRecord record, out string message)
        {
            string logPath = NewLogPath("rollback");
            try
            {
                if (record == null || string.IsNullOrWhiteSpace(record.RollbackPath) || !Directory.Exists(record.RollbackPath))
                    throw new InvalidOperationException("No rollback snapshot is available for this product.");
                string stopMessage;
                StopProduct(record, out stopMessage);
                WriteLog(logPath, stopMessage);
                Thread.Sleep(450);
                string failedCopy = record.InstallPath + ".failed-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                if (Directory.Exists(record.InstallPath)) Directory.Move(record.InstallPath, failedCopy);
                CopyDirectory(record.RollbackPath, record.InstallPath, true);
                string metadata = Path.Combine(record.InstallPath, ".devmind-installed.json");
                if (File.Exists(metadata))
                {
                    ProductRecord old = serializer.Deserialize<ProductRecord>(File.ReadAllText(metadata, Encoding.UTF8));
                    if (old != null) UpsertProduct(old);
                }
                message = "Rollback completed. The replaced build was preserved at " + failedCopy;
                WriteLog(logPath, message);
                SuccessSignal();
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                WriteLog(logPath, ex.ToString());
                ErrorSignal(ex, logPath);
                return false;
            }
        }

        public static bool UninstallProduct(ProductRecord record, out string message)
        {
            string logPath = NewLogPath("uninstall");
            try
            {
                if (record == null) throw new InvalidOperationException("No product was selected.");
                string stopMessage;
                StopProduct(record, out stopMessage);
                WriteLog(logPath, stopMessage);
                Thread.Sleep(450);
                if (Directory.Exists(record.InstallPath)) DeleteDirectoryRobust(record.InstallPath);
                lock (Sync)
                {
                    config.InstalledProducts = (config.InstalledProducts ?? new List<ProductRecord>())
                        .Where(x => x == null || !string.Equals(x.ProductId, record.ProductId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    SaveConfig();
                }
                message = record.DisplayName + " was removed.";
                WriteLog(logPath, message);
                SuccessSignal();
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                WriteLog(logPath, ex.ToString());
                ErrorSignal(ex, logPath);
                return false;
            }
        }

        private static void UpsertProduct(ProductRecord record)
        {
            if (record == null) return;
            lock (Sync)
            {
                List<ProductRecord> updated = (config.InstalledProducts ?? new List<ProductRecord>())
                    .Where(x => x == null || !string.Equals(x.ProductId, record.ProductId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                updated.Add(record);
                config.InstalledProducts = updated
                    .Where(x => x != null)
                    .OrderBy(x => x.DisplayName)
                    .ToList();
                SaveConfig();
            }
        }


        private static DevMindPackageManifest DeserializeManifestCompatible(string json, string logPath)
        {
            object root = serializer.DeserializeObject(json);
            Dictionary<string, object> raw = root as Dictionary<string, object>;
            if (raw == null)
                throw new InvalidDataException("The package manifest must be a JSON object.");

            DevMindPackageManifest manifest = new DevMindPackageManifest();
            manifest.SchemaVersion = ReadManifestInt(raw, 1, "SchemaVersion", "schemaVersion");
            manifest.ProductId = ReadManifestString(raw, "ProductId", "productId");
            manifest.DisplayName = ReadManifestString(raw, "DisplayName", "displayName");
            manifest.Version = ReadManifestString(raw, "Version", "version");
            manifest.Publisher = ReadManifestString(raw, "Publisher", "publisher");
            manifest.Description = ReadManifestString(raw, "Description", "description");
            manifest.PayloadRoot = ReadManifestString(raw, "PayloadRoot", "payloadRoot");
            manifest.InstallDirectoryName = ReadManifestString(raw, "InstallDirectoryName", "installDirectoryName");
            manifest.EntryPoint = ReadManifestString(raw, "EntryPoint", "entryPoint");
            manifest.EntryType = ReadManifestString(raw, "EntryType", "entryType");
            manifest.Arguments = ReadManifestString(raw, "Arguments", "arguments");
            manifest.WorkingDirectory = ReadManifestString(raw, "WorkingDirectory", "workingDirectory");
            manifest.LaunchUrl = ReadManifestString(raw, "LaunchUrl", "launchUrl");
            manifest.LaunchDelayMilliseconds = ReadManifestInt(raw, 350, "LaunchDelayMilliseconds", "launchDelayMilliseconds");
            manifest.MinimumNodeMajor = ReadManifestInt(raw, 0, "MinimumNodeMajor", "minimumNodeMajor");
            manifest.RequiredFiles = ReadManifestStringList(raw, logPath, "RequiredFiles", "requiredFiles");
            manifest.PreserveStatePaths = ReadManifestStringList(raw, logPath, "PreserveStatePaths", "preserveStatePaths");
            manifest.FileHashes = ReadManifestStringMap(raw, "FileHashes", "fileHashes");
            manifest.ReleaseChannel = ReadManifestString(raw, "ReleaseChannel", "releaseChannel");
            manifest.PackageNotes = ReadManifestString(raw, "PackageNotes", "packageNotes");

            if (string.IsNullOrWhiteSpace(manifest.PayloadRoot)) manifest.PayloadRoot = "payload";
            if (string.IsNullOrWhiteSpace(manifest.EntryType)) manifest.EntryType = "auto";
            if (string.IsNullOrWhiteSpace(manifest.WorkingDirectory)) manifest.WorkingDirectory = ".";
            if (string.IsNullOrWhiteSpace(manifest.ReleaseChannel)) manifest.ReleaseChannel = "stable";
            if (manifest.RequiredFiles == null) manifest.RequiredFiles = new List<string>();
            if (manifest.PreserveStatePaths == null) manifest.PreserveStatePaths = new List<string>();
            if (manifest.FileHashes == null) manifest.FileHashes = new Dictionary<string, string>();
            return manifest;
        }

        private static object ReadManifestValue(Dictionary<string, object> raw, params string[] names)
        {
            if (raw == null || names == null) return null;
            foreach (string name in names)
            {
                object value;
                if (raw.TryGetValue(name, out value)) return value;
            }
            foreach (KeyValuePair<string, object> item in raw)
            {
                foreach (string name in names)
                {
                    if (string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)) return item.Value;
                }
            }
            return null;
        }

        private static string ReadManifestString(Dictionary<string, object> raw, params string[] names)
        {
            object value = ReadManifestValue(raw, names);
            if (value == null) return "";
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        private static int ReadManifestInt(Dictionary<string, object> raw, int fallback, params string[] names)
        {
            object value = ReadManifestValue(raw, names);
            if (value == null) return fallback;
            int parsed;
            if (int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return fallback;
        }

        private static List<string> ReadManifestStringList(Dictionary<string, object> raw, string logPath, params string[] names)
        {
            object value = ReadManifestValue(raw, names);
            List<string> result = new List<string>();
            if (value == null) return result;

            string scalar = value as string;
            if (scalar != null)
            {
                string trimmed = scalar.Trim();
                if (trimmed.Length == 0) return result;
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    try
                    {
                        object embedded = serializer.DeserializeObject(trimmed);
                        return CoerceStringList(embedded);
                    }
                    catch { }
                }
                string[] pieces = trimmed.Split(new string[] { "\r\n", "\n", ";" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string piece in pieces)
                {
                    string item = piece.Trim();
                    if (item.Length > 0) result.Add(item);
                }
                if (result.Count == 0) result.Add(trimmed);
                WriteLog(logPath, "Compatibility: normalized manifest field " + names[0] + " from a string to an array.");
                return result;
            }

            return CoerceStringList(value);
        }

        private static List<string> CoerceStringList(object value)
        {
            List<string> result = new List<string>();
            if (value == null) return result;
            IEnumerable sequence = value as IEnumerable;
            if (sequence == null)
            {
                string single = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(single)) result.Add(single.Trim());
                return result;
            }
            foreach (object item in sequence)
            {
                string text = Convert.ToString(item, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
            }
            return result;
        }

        private static Dictionary<string, string> ReadManifestStringMap(Dictionary<string, object> raw, params string[] names)
        {
            object value = ReadManifestValue(raw, names);
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (value == null) return result;

            string scalar = value as string;
            if (scalar != null && scalar.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                try { value = serializer.DeserializeObject(scalar); }
                catch { return result; }
            }

            IDictionary map = value as IDictionary;
            if (map == null) return result;
            foreach (DictionaryEntry item in map)
            {
                string key = Convert.ToString(item.Key, CultureInfo.InvariantCulture);
                string text = Convert.ToString(item.Value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(text))
                    result[key.Replace('\\', '/')] = text.Trim().ToLowerInvariant();
            }
            return result;
        }

        private static void ValidateManifest(DevMindPackageManifest manifest)
        {
            if (manifest == null) throw new InvalidDataException("The package manifest could not be read.");
            if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported package schema version: " + manifest.SchemaVersion);
            if (string.IsNullOrWhiteSpace(manifest.ProductId) || !Regex.IsMatch(manifest.ProductId, "^[a-zA-Z0-9][a-zA-Z0-9._-]{2,80}$"))
                throw new InvalidDataException("The package productId is missing or invalid.");
            if (string.IsNullOrWhiteSpace(manifest.Version)) throw new InvalidDataException("The package version is missing.");
            if (string.IsNullOrWhiteSpace(manifest.EntryPoint) && string.IsNullOrWhiteSpace(manifest.LaunchUrl))
                throw new InvalidDataException("The package needs an entryPoint or launchUrl.");
            if (!string.IsNullOrWhiteSpace(manifest.EntryPoint) && Path.IsPathRooted(manifest.EntryPoint))
                throw new InvalidDataException("The entryPoint must be relative to the installed product folder.");
        }

        private static void VerifyPackageHashes(DevMindPackageManifest manifest, string payloadPath, string logPath)
        {
            if (manifest.FileHashes == null || manifest.FileHashes.Count == 0)
                throw new InvalidDataException("The package does not contain a file hash manifest.");
            int verified = 0;
            foreach (KeyValuePair<string, string> item in manifest.FileHashes)
            {
                string file = SafeCombine(payloadPath, item.Key);
                if (!File.Exists(file)) throw new InvalidDataException("Package file listed in hashes is missing: " + item.Key);
                string actual = ComputeSha256(file);
                if (!actual.Equals(item.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Hash verification failed for: " + item.Key);
                verified++;
            }
            WriteLog(logPath, "Verified " + verified + " file hashes.");
        }

        private static void VerifyPrerequisites(DevMindPackageManifest manifest, string logPath)
        {
            if (manifest.MinimumNodeMajor <= 0) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("node", "--version");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(3000);
                    int major = 0;
                    Match m = Regex.Match(text, "v?(\\d+)");
                    if (m.Success) int.TryParse(m.Groups[1].Value, out major);
                    if (major < manifest.MinimumNodeMajor)
                        throw new InvalidOperationException("This package needs Node.js " + manifest.MinimumNodeMajor + " or newer. Found: " + text);
                    WriteLog(logPath, "Node prerequisite passed: " + text);
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException("This package needs Node.js " + manifest.MinimumNodeMajor + " or newer, but Node.js was not found.");
            }
        }

        private static string BackupState(string oldInstall, List<string> statePaths, string logPath)
        {
            if (statePaths == null || statePaths.Count == 0) return null;
            string backup = Path.Combine(Path.GetTempPath(), "LaunchBridgeState", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            int copied = 0;
            foreach (string rel in statePaths)
            {
                string source = SafeCombine(oldInstall, rel);
                string dest = SafeCombine(backup, rel);
                if (Directory.Exists(source)) { CopyDirectory(source, dest, true); copied++; }
                else if (File.Exists(source)) { Directory.CreateDirectory(Path.GetDirectoryName(dest)); File.Copy(source, dest, true); copied++; }
            }
            WriteLog(logPath, "Preserved " + copied + " state path(s).");
            return backup;
        }

        private static void RestoreState(string stateBackup, string installPath, List<string> statePaths, string logPath)
        {
            if (string.IsNullOrWhiteSpace(stateBackup) || !Directory.Exists(stateBackup) || statePaths == null) return;
            int restored = 0;
            foreach (string rel in statePaths)
            {
                string source = SafeCombine(stateBackup, rel);
                string dest = SafeCombine(installPath, rel);
                if (Directory.Exists(source)) { CopyDirectory(source, dest, true); restored++; }
                else if (File.Exists(source)) { Directory.CreateDirectory(Path.GetDirectoryName(dest)); File.Copy(source, dest, true); restored++; }
            }
            WriteLog(logPath, "Restored " + restored + " state path(s).");
        }

        private static void TryRollbackFailedInstall(string installPath, string rollbackPath, string logPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(installPath) || string.IsNullOrWhiteSpace(rollbackPath) || !Directory.Exists(rollbackPath)) return;
                if (Directory.Exists(installPath)) DeleteDirectoryRobust(installPath);
                CopyDirectory(rollbackPath, installPath, true);
                WriteLog(logPath, "Automatic rollback completed after failure.");
            }
            catch (Exception rollbackEx)
            {
                WriteLog(logPath, "Automatic rollback failed: " + rollbackEx.Message);
            }
        }

        public static string BuildPackage(PackageBuildRequest request)
        {
            string logPath = NewLogPath("build-package");
            string tempRoot = null;
            try
            {
                if (request == null) throw new ArgumentNullException("request");
                if (!Directory.Exists(request.SourceFolder)) throw new DirectoryNotFoundException("Source folder not found.");
                if (string.IsNullOrWhiteSpace(request.ProductId)) throw new InvalidOperationException("Product ID is required.");
                if (string.IsNullOrWhiteSpace(request.DisplayName)) throw new InvalidOperationException("Display name is required.");
                if (string.IsNullOrWhiteSpace(request.Version)) throw new InvalidOperationException("Version is required.");
                if (string.IsNullOrWhiteSpace(request.EntryPoint)) throw new InvalidOperationException("Entry point is required.");
                string sourceEntry = SafeCombine(request.SourceFolder, request.EntryPoint);
                if (!File.Exists(sourceEntry)) throw new FileNotFoundException("Entry point not found inside source folder.", sourceEntry);

                string ext = NormalizeExtension(request.Extension);
                if (ext == null) throw new InvalidOperationException("Choose a valid custom package extension.");
                if (IsProtectedAssociation(ext)) throw new InvalidOperationException("Choose a custom extension rather than a common executable or archive extension.");

                string output = request.OutputFile;
                if (string.IsNullOrWhiteSpace(output))
                {
                    string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    output = Path.Combine(downloads, SanitizeFolderName(request.DisplayName) + "_v" + request.Version.Replace('.', '_') + ext);
                }
                if (!output.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) output = Path.ChangeExtension(output, ext.TrimStart('.'));

                tempRoot = Path.Combine(Path.GetTempPath(), "LaunchBridgeBuilder", Guid.NewGuid().ToString("N"));
                string payload = Path.Combine(tempRoot, "payload");
                Directory.CreateDirectory(payload);
                CopyDirectory(request.SourceFolder, payload, true);

                DevMindPackageManifest manifest = new DevMindPackageManifest();
                manifest.ProductId = request.ProductId.Trim();
                manifest.DisplayName = request.DisplayName.Trim();
                manifest.Version = request.Version.Trim();
                manifest.Publisher = string.IsNullOrWhiteSpace(request.Publisher) ? "Local builder" : request.Publisher.Trim();
                manifest.Description = request.Description ?? "";
                manifest.InstallDirectoryName = string.IsNullOrWhiteSpace(request.InstallDirectoryName) ? SanitizeFolderName(request.DisplayName) : SanitizeFolderName(request.InstallDirectoryName);
                manifest.EntryPoint = request.EntryPoint.Replace('\\', '/');
                manifest.EntryType = string.IsNullOrWhiteSpace(request.EntryType) ? "auto" : request.EntryType;
                manifest.Arguments = request.Arguments ?? "";
                manifest.LaunchUrl = request.LaunchUrl ?? "";
                manifest.MinimumNodeMajor = request.MinimumNodeMajor;
                manifest.RequiredFiles.Add(manifest.EntryPoint);
                manifest.FileHashes = ComputeFolderHashes(payload);

                ValidateManifest(manifest);
                File.WriteAllText(Path.Combine(tempRoot, "devmind.package.json"), serializer.Serialize(manifest), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(tempRoot, "PACKAGE_README.txt"),
                    "LaunchBridge package\r\n\r\nProduct: " + manifest.DisplayName + "\r\nVersion: " + manifest.Version + "\r\nPublisher: " + manifest.Publisher + "\r\n", Encoding.UTF8);

                Directory.CreateDirectory(Path.GetDirectoryName(output));
                if (File.Exists(output)) File.Delete(output);
                ZipFile.CreateFromDirectory(tempRoot, output, CompressionLevel.Optimal, false);
                WriteLog(logPath, "Built package: " + output);
                SuccessSignal();
                return output;
            }
            catch (Exception ex)
            {
                WriteLog(logPath, ex.ToString());
                ErrorSignal(ex, logPath);
                throw;
            }
            finally
            {
                TryDelete(tempRoot);
            }
        }

        private static Dictionary<string, string> ComputeFolderHashes(string root)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x))
            {
                string rel = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                map[rel] = ComputeSha256(file);
            }
            return map;
        }

        public static string ComputeSha256(string file)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(file))
            {
                byte[] hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string ExtractPackageForUser(string archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                throw new FileNotFoundException("The package file could not be found.", archivePath);

            string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(archivePath));
            string baseName = Path.GetFileNameWithoutExtension(archivePath);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "Extracted Package";

            string destination = Path.Combine(sourceDirectory, baseName);
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            {
                int suffix = 2;
                string candidate;
                do
                {
                    candidate = Path.Combine(sourceDirectory, baseName + " (" + suffix.ToString() + ")");
                    suffix++;
                }
                while (Directory.Exists(candidate));
                destination = candidate;
            }

            Directory.CreateDirectory(destination);
            Directory.CreateDirectory(logsRoot);
            string logPath = Path.Combine(logsRoot, "extract-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            WriteLog(logPath, "Extracting package for user: " + archivePath);
            SafeExtract(archivePath, destination, logPath);
            Log("Extracted package to " + destination);
            return destination;
        }

        private static void SafeExtract(string archivePath, string destination, string logPath)
        {
            int count = 0;
            long total = 0;
            const long maxTotal = 4L * 1024L * 1024L * 1024L;
            using (FileStream fs = File.OpenRead(archivePath))
            using (ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string target = SafeCombine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    total += entry.Length;
                    if (total > maxTotal) throw new InvalidDataException("The package expands beyond the 4 GB safety limit.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                    count++;
                }
            }
            WriteLog(logPath, "Safely extracted " + count + " files.");
        }

        public static string SafeCombine(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentNullException("root");
            if (string.IsNullOrWhiteSpace(relative)) return Path.GetFullPath(root);
            string clean = relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, clean));
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Blocked unsafe package path: " + relative);
            return full;
        }

        public static void CopyDirectory(string source, string destination, bool overwrite)
        {
            Directory.CreateDirectory(destination);
            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                string rel = dir.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destination, rel));
            }
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string target = Path.Combine(destination, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, overwrite);
            }
        }

        private static void DeleteDirectoryRobust(string path)
        {
            if (!Directory.Exists(path)) return;

            Exception lastError = null;
            for (int attempt = 1; attempt <= 20; attempt++)
            {
                try
                {
                    foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                    }

                    KillProcessesMatchingToken(path, true, null);
                    Directory.Delete(path, true);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Thread.Sleep(250 * Math.Min(attempt, 8));
                }
            }

            throw new IOException(
                "LaunchBridge could not replace the product folder after 20 attempts: " + path +
                ". Close any program using that folder and retry. Last error: " +
                (lastError == null ? "unknown" : lastError.Message),
                lastError);
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { if (Directory.Exists(path)) DeleteDirectoryRobust(path); } catch { }
        }

        private static string SanitizeFolderName(string input)
        {
            string value = string.IsNullOrWhiteSpace(input) ? "LaunchBridgeProduct" : input.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            value = Regex.Replace(value, "\\s+", "_");
            return value.Length > 100 ? value.Substring(0, 100) : value;
        }

        public static string NewLogPath(string prefix)
        {
            Directory.CreateDirectory(logsRoot);
            return Path.Combine(logsRoot, prefix + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".log");
        }

        public static void Log(string text)
        {
            string path = Path.Combine(logsRoot, "launchbridge.log");
            WriteLog(path, text);
        }

        public static void WriteLog(string path, string text)
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + text + Environment.NewLine, Encoding.UTF8);
            }
        }

        private static void SuccessSignal()
        {
            if (config.Dinger)
            {
                try { SystemSounds.Asterisk.Play(); } catch { }
            }
        }

        private static void ErrorSignal(Exception ex, string logPath)
        {
            if (config.AutoCopyErrors)
            {
                try { Clipboard.SetText("LaunchBridge error\r\n" + ex.Message + "\r\nLog: " + logPath); } catch { }
            }
            if (config.Dinger)
            {
                try { SystemSounds.Hand.Play(); } catch { }
            }
        }

        private static void ReportPackageOperationFailure(string packagePath, DevMindPackageManifest manifest, ProductRecord record, Exception ex, string logPath, string installPath)
        {
            try
            {
                string fileName = string.IsNullOrWhiteSpace(packagePath) ? "package" : Path.GetFileNameWithoutExtension(packagePath);
                string fallbackId = Regex.Replace((fileName ?? "package").ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-', '_', '.');
                if (string.IsNullOrWhiteSpace(fallbackId)) fallbackId = "package";

                RuntimeIssue issue = new RuntimeIssue();
                issue.ProductId = record != null && !string.IsNullOrWhiteSpace(record.ProductId)
                    ? record.ProductId
                    : (manifest != null && !string.IsNullOrWhiteSpace(manifest.ProductId) ? manifest.ProductId : "package." + fallbackId);
                issue.Product = record != null && !string.IsNullOrWhiteSpace(record.DisplayName)
                    ? record.DisplayName
                    : (manifest != null && !string.IsNullOrWhiteSpace(manifest.DisplayName) ? manifest.DisplayName : fileName);
                issue.Version = record != null ? record.Version : (manifest == null ? "" : manifest.Version);
                issue.Severity = "error";
                issue.Type = "install-or-launch-failure";
                issue.Message = ex == null ? "LaunchBridge could not install or launch the package." : ex.Message;
                issue.Stack = ex == null ? "" : ex.ToString();
                issue.Source = packagePath;
                issue.Title = "Package installation or launch failed";
                issue.InstallPath = record != null ? record.InstallPath : installPath;
                issue.BodySample = "Install log: " + (logPath ?? "");
                ReceiveRuntimeIssue(issue);
            }
            catch { }
        }

        public static void ReportFatal(Exception ex)
        {
            try
            {
                if (ex == null) ex = new Exception("Unknown fatal error");
                string log = NewLogPath("fatal");
                WriteLog(log, ex.ToString());
                try
                {
                    RuntimeIssue issue = new RuntimeIssue();
                    issue.ProductId = "devmind-launchbridge";
                    issue.Product = "LaunchBridge";
                    issue.Version = "0.3.3";
                    issue.Severity = "fatal";
                    issue.Type = "launchbridge-fatal";
                    issue.Message = ex.Message;
                    issue.Stack = ex.ToString();
                    issue.Source = "LaunchBridge.exe";
                    issue.Title = "LaunchBridge unexpected error";
                    issue.InstallPath = appRoot;
                    issue.BodySample = "Fatal log: " + log;
                    ReceiveRuntimeIssue(issue);
                }
                catch { }
                ErrorSignal(ex, log);
                MessageBox.Show("LaunchBridge encountered an unexpected error.\r\n\r\n" + ex.Message + "\r\n\r\nThe error was copied to your clipboard and saved in Problems.\r\nLog: " + log,
                    "LaunchBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }


        private static void LoadRecentRuntimeIssues()
        {
            try
            {
                DateTime clearedAtUtc = DateTime.MinValue;
                if (!string.IsNullOrWhiteSpace(runtimeIssueClearMarkerPath) && File.Exists(runtimeIssueClearMarkerPath))
                {
                    DateTime parsed;
                    if (DateTime.TryParse(File.ReadAllText(runtimeIssueClearMarkerPath, Encoding.UTF8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
                        clearedAtUtc = parsed.ToUniversalTime();
                }

                List<RuntimeIssue> loaded = new List<RuntimeIssue>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string[] files = Directory.GetFiles(runtimeIssuesRoot, "runtime-issues-*.jsonl", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                    .Take(14)
                    .ToArray();
                foreach (string file in files)
                {
                    string[] lines;
                    try { lines = File.ReadAllLines(file, Encoding.UTF8); }
                    catch { continue; }
                    for (int i = lines.Length - 1; i >= 0 && loaded.Count < 500; i--)
                    {
                        string line = lines[i];
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        RuntimeIssue issue;
                        try { issue = serializer.Deserialize<RuntimeIssue>(line); }
                        catch { continue; }
                        if (issue == null) continue;
                        DateTime received;
                        if (DateTime.TryParse(issue.ReceivedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out received) &&
                            received.ToUniversalTime() <= clearedAtUtc) continue;
                        string key = string.IsNullOrWhiteSpace(issue.IssueId)
                            ? (issue.ProductId ?? "") + "|" + (issue.Type ?? "") + "|" + (issue.Message ?? "") + "|" + (issue.ReceivedAtUtc ?? "")
                            : issue.IssueId;
                        if (!seen.Add(key)) continue;
                        loaded.Add(issue);
                    }
                    if (loaded.Count >= 500) break;
                }

                loaded = loaded.OrderByDescending(x =>
                {
                    DateTime value;
                    return DateTime.TryParse(x == null ? null : x.ReceivedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value)
                        ? value.ToUniversalTime()
                        : DateTime.MinValue;
                }).Take(500).ToList();
                lock (Sync)
                {
                    runtimeIssues.Clear();
                    runtimeIssues.AddRange(loaded);
                }
            }
            catch (Exception ex)
            {
                Log("Runtime issue history warning: " + ex.Message);
            }
        }

        public static List<RuntimeIssue> RuntimeIssuesSnapshot()
        {
            lock (Sync)
            {
                return runtimeIssues.ToList();
            }
        }

        public static void ClearRuntimeIssues()
        {
            lock (Sync)
            {
                runtimeIssues.Clear();
            }
            try
            {
                if (!string.IsNullOrWhiteSpace(runtimeIssueClearMarkerPath))
                    File.WriteAllText(runtimeIssueClearMarkerPath, DateTime.UtcNow.ToString("o"), new UTF8Encoding(false));
            }
            catch { }
        }

        public static string BuildRuntimeIssuePacket(RuntimeIssue issue)
        {
            if (issue == null) return "No runtime issue is selected.";
            StringBuilder b = new StringBuilder();
            b.AppendLine("LaunchBridge runtime repair packet");
            b.AppendLine("Issue ID: " + (issue.IssueId ?? ""));
            b.AppendLine("Received: " + (issue.ReceivedAtUtc ?? ""));
            b.AppendLine("Product: " + (issue.Product ?? issue.ProductId ?? ""));
            b.AppendLine("ProductId: " + (issue.ProductId ?? ""));
            b.AppendLine("Version: " + (issue.Version ?? ""));
            b.AppendLine("Severity: " + (issue.Severity ?? ""));
            b.AppendLine("Type: " + (issue.Type ?? ""));
            b.AppendLine("Message: " + (issue.Message ?? ""));
            b.AppendLine("URL: " + (issue.Url ?? ""));
            b.AppendLine("Title: " + (issue.Title ?? ""));
            b.AppendLine("Source: " + (issue.Source ?? ""));
            b.AppendLine("Line: " + issue.Line + ", Column: " + issue.Column);
            b.AppendLine("Install path: " + (issue.InstallPath ?? ""));
            b.AppendLine("Runtime issue log: " + (issue.LogPath ?? ""));
            b.AppendLine("Repair bundle: " + (issue.RepairBundlePath ?? ""));
            if (!string.IsNullOrWhiteSpace(issue.Stack))
            {
                b.AppendLine();
                b.AppendLine("Stack:");
                b.AppendLine(issue.Stack);
            }
            if (!string.IsNullOrWhiteSpace(issue.BodySample))
            {
                b.AppendLine();
                b.AppendLine("Visible page/body sample:");
                b.AppendLine(issue.BodySample);
            }
            b.AppendLine();
            b.AppendLine("Repair request: Identify the root cause, preserve all working features, build the corrected next semantic version, validate the final LaunchBridge package, and return the runnable .devmind first.");
            return b.ToString();
        }

        public static void ApplyRuntimeMonitorSettings()
        {
            if (RuntimeMonitorEnabled)
            {
                StartRuntimeIssueServer();
                InstrumentExistingWebProducts();
                return;
            }
            TcpListener listener = runtimeIssueListener;
            runtimeIssueListener = null;
            runtimeIssuePort = 0;
            runtimeIssueToken = null;
            try { if (listener != null) listener.Stop(); } catch { }
        }

        private static void StartRuntimeIssueServer()
        {
            if (!RuntimeMonitorEnabled || runtimeIssueListener != null) return;
            try
            {
                runtimeIssueToken = Guid.NewGuid().ToString("N");
                runtimeIssueListener = new TcpListener(IPAddress.Loopback, 0);
                runtimeIssueListener.Start();
                IPEndPoint endpoint = runtimeIssueListener.LocalEndpoint as IPEndPoint;
                runtimeIssuePort = endpoint == null ? 0 : endpoint.Port;
                runtimeIssueThread = new Thread(RuntimeIssueAcceptLoop);
                runtimeIssueThread.IsBackground = true;
                runtimeIssueThread.Name = "LaunchBridge Runtime Issue Intake";
                runtimeIssueThread.Start();
                Log("Runtime Error Cockpit intake listening on 127.0.0.1:" + runtimeIssuePort + ".");
            }
            catch (Exception ex)
            {
                runtimeIssuePort = 0;
                runtimeIssueListener = null;
                Log("Runtime Error Cockpit could not start: " + ex.Message);
            }
        }

        private static void RuntimeIssueAcceptLoop()
        {
            while (runtimeIssueListener != null)
            {
                try
                {
                    TcpClient client = runtimeIssueListener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate(object state) { HandleRuntimeIssueClient((TcpClient)state); }, client);
                }
                catch
                {
                    Thread.Sleep(100);
                }
            }
        }

        private static void HandleRuntimeIssueClient(TcpClient client)
        {
            if (client == null) return;
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 3500;
                    client.SendTimeout = 3500;
                    NetworkStream stream = client.GetStream();
                    MemoryStream headerBytes = new MemoryStream();
                    int matched = 0;
                    while (headerBytes.Length < 32768)
                    {
                        int value = stream.ReadByte();
                        if (value < 0) return;
                        headerBytes.WriteByte((byte)value);
                        if ((matched == 0 && value == 13) ||
                            (matched == 1 && value == 10) ||
                            (matched == 2 && value == 13) ||
                            (matched == 3 && value == 10))
                        {
                            matched++;
                            if (matched == 4) break;
                        }
                        else matched = value == 13 ? 1 : 0;
                    }
                    string headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
                    string[] headerLines = headerText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                    if (headerLines.Length == 0) return;
                    string[] parts = headerLines[0].Split(' ');
                    string method = parts.Length > 0 ? parts[0] : "";
                    string path = parts.Length > 1 ? parts[1] : "";
                    int contentLength = 0;
                    foreach (string headerLine in headerLines)
                    {
                        int colon = headerLine.IndexOf(':');
                        if (colon <= 0) continue;
                        string name = headerLine.Substring(0, colon).Trim();
                        string value = headerLine.Substring(colon + 1).Trim();
                        if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(value, out contentLength);
                    }
                    string expected = "/issue/" + runtimeIssueToken;
                    if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) || !string.Equals(path, expected, StringComparison.Ordinal))
                    {
                        WriteHttpResponse(stream, "404 Not Found", "Not found");
                        return;
                    }
                    if (contentLength <= 0 || contentLength > 1024 * 256)
                    {
                        WriteHttpResponse(stream, "400 Bad Request", "Invalid body");
                        return;
                    }
                    byte[] bodyBytes = new byte[contentLength];
                    int read = 0;
                    while (read < contentLength)
                    {
                        int amount = stream.Read(bodyBytes, read, contentLength - read);
                        if (amount <= 0) break;
                        read += amount;
                    }
                    string body = Encoding.UTF8.GetString(bodyBytes, 0, read);
                    RuntimeIssue issue = serializer.Deserialize<RuntimeIssue>(body);
                    if (issue != null) ReceiveRuntimeIssue(issue);
                    WriteHttpResponse(stream, "204 No Content", "");
                }
                catch
                {
                }
            }
        }

        private static void WriteHttpResponse(NetworkStream stream, string status, string body)
        {
            if (stream == null) return;
            byte[] data = Encoding.UTF8.GetBytes(body ?? "");
            string header = "HTTP/1.1 " + status + "\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: " + data.Length + "\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
            byte[] head = Encoding.ASCII.GetBytes(header);
            stream.Write(head, 0, head.Length);
            if (data.Length > 0) stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        private static string LimitText(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= max ? value : value.Substring(0, max) + Environment.NewLine + "...[truncated]";
        }

        private static void ReceiveRuntimeIssue(RuntimeIssue issue)
        {
            if (issue == null || string.IsNullOrWhiteSpace(issue.ProductId)) return;
            issue.IssueId = Guid.NewGuid().ToString("N");
            issue.ReceivedAtUtc = DateTime.UtcNow.ToString("o");
            issue.Severity = string.IsNullOrWhiteSpace(issue.Severity) ? "error" : LimitText(issue.Severity, 32);
            issue.Type = string.IsNullOrWhiteSpace(issue.Type) ? "runtime-error" : LimitText(issue.Type, 96);
            issue.Message = LimitText(issue.Message, 6000);
            issue.Stack = LimitText(issue.Stack, 16000);
            issue.Source = LimitText(issue.Source, 3000);
            issue.Url = LimitText(issue.Url, 3000);
            issue.Title = LimitText(issue.Title, 1000);
            issue.BodySample = LimitText(issue.BodySample, 8000);
            issue.UserAgent = LimitText(issue.UserAgent, 1500);
            ProductRecord product = FindInstalledProduct(issue.ProductId);
            if (product != null)
            {
                issue.Product = product.DisplayName;
                issue.InstallPath = product.InstallPath;
                if (string.IsNullOrWhiteSpace(issue.Version)) issue.Version = product.Version;
            }
            else issue.Product = issue.ProductId;

            string dayLog = Path.Combine(runtimeIssuesRoot, "runtime-issues-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".jsonl");
            issue.LogPath = dayLog;
            try { issue.RepairBundlePath = CreateRuntimeRepairBundle(issue); }
            catch (Exception ex) { Log("Repair bundle warning: " + ex.Message); }
            bool add = true;
            lock (Sync)
            {
                RuntimeIssue prior = runtimeIssues.FirstOrDefault(x => x != null &&
                    string.Equals(x.ProductId, issue.ProductId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Type, issue.Type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Message, issue.Message, StringComparison.Ordinal));
                if (prior != null)
                {
                    DateTime priorTime;
                    if (DateTime.TryParse(prior.ReceivedAtUtc, out priorTime) && DateTime.UtcNow.Subtract(priorTime.ToUniversalTime()).TotalSeconds < 4)
                        add = false;
                }
                if (add)
                {
                    runtimeIssues.Insert(0, issue);
                    if (runtimeIssues.Count > 500) runtimeIssues.RemoveRange(500, runtimeIssues.Count - 500);
                    Directory.CreateDirectory(runtimeIssuesRoot);
                    File.AppendAllText(dayLog, serializer.Serialize(issue) + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            if (add && config.Dinger)
            {
                try { SystemSounds.Exclamation.Play(); } catch { }
            }
        }

        public static string CreateRuntimeRepairBundle(RuntimeIssue issue)
        {
            if (issue == null) throw new ArgumentNullException("issue");
            string productFolder = SanitizeFolderName(issue.ProductId ?? "unknown-product");
            string folder = Path.Combine(repairPacketsRoot, productFolder);
            Directory.CreateDirectory(folder);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
            string output = Path.Combine(folder, stamp + "-" + (issue.IssueId ?? Guid.NewGuid().ToString("N")) + ".zip");
            long total = 0;
            const long maxTotal = 25L * 1024L * 1024L;
            issue.RepairBundlePath = output;
            using (FileStream fs = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None))
            using (ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                AddTextToZip(archive, "RUNTIME_ISSUE.txt", BuildRuntimeIssuePacket(issue));
                AddTextToZip(archive, "RUNTIME_ISSUE.json", serializer.Serialize(issue));
                if (!string.IsNullOrWhiteSpace(issue.InstallPath) && Directory.Exists(issue.InstallPath))
                {
                    string[] allowed = new string[] { ".html", ".htm", ".js", ".mjs", ".cjs", ".css", ".json", ".md", ".txt", ".cmd", ".bat", ".ps1", ".cs", ".ts", ".tsx", ".jsx" };
                    foreach (string file in Directory.GetFiles(issue.InstallPath, "*", SearchOption.AllDirectories).OrderBy(x => x))
                    {
                        string lower = file.ToLowerInvariant();
                        if (lower.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar) ||
                            lower.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar) ||
                            lower.Contains(Path.DirectorySeparatorChar + "dist" + Path.DirectorySeparatorChar + "cache" + Path.DirectorySeparatorChar)) continue;
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (!allowed.Contains(ext)) continue;
                        FileInfo info = new FileInfo(file);
                        if (!info.Exists || info.Length > 2L * 1024L * 1024L || total + info.Length > maxTotal) continue;
                        string rel = "product/" + file.Substring(issue.InstallPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                        ZipArchiveEntry entry = archive.CreateEntry(rel, CompressionLevel.Optimal);
                        using (Stream input = File.OpenRead(file))
                        using (Stream outputStream = entry.Open()) input.CopyTo(outputStream);
                        total += info.Length;
                    }
                }
            }
            return output;
        }

        private static void AddTextToZip(ZipArchive archive, string name, string text)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream stream = entry.Open())
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text ?? "");
        }

        private static void InstrumentExistingWebProducts()
        {
            if (!RuntimeMonitorEnabled || runtimeIssuePort <= 0) return;
            foreach (ProductRecord product in InstalledProductsSnapshot())
            {
                if (product == null || string.IsNullOrWhiteSpace(product.LaunchUrl) || !Directory.Exists(product.InstallPath)) continue;
                try { InstrumentProductHtml(product, NewLogPath("instrument")); }
                catch (Exception ex) { Log("Runtime monitor instrumentation warning for " + product.DisplayName + ": " + ex.Message); }
            }
        }

        private static IEnumerable<string> EnumerateHtmlFilesBounded(string root, int limit)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || limit <= 0) yield break;
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            int yielded = 0;
            while (pending.Count > 0 && yielded < limit)
            {
                string folder = pending.Pop();
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(folder, "*.htm*", SearchOption.TopDirectoryOnly); }
                catch { files = new string[0]; }
                foreach (string file in files)
                {
                    yield return file;
                    yielded++;
                    if (yielded >= limit) yield break;
                }

                IEnumerable<string> directories;
                try { directories = Directory.EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly); }
                catch { directories = new string[0]; }
                foreach (string directory in directories)
                {
                    string name = Path.GetFileName(directory);
                    if (string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "licenses", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "docs", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, ".cache", StringComparison.OrdinalIgnoreCase)) continue;
                    pending.Push(directory);
                }
            }
        }

        private static void InstrumentProductHtml(ProductRecord record, string logPath)
        {
            if (record == null || runtimeIssuePort <= 0 || !RuntimeMonitorEnabled || !Directory.Exists(record.InstallPath)) return;
            int changed = 0;
            int checkedCount = 0;
            foreach (string file in EnumerateHtmlFilesBounded(record.InstallPath, 32))
            {
                checkedCount++;
                FileInfo info = new FileInfo(file);
                if (!info.Exists || info.Length > 25L * 1024L * 1024L) continue;
                string html = File.ReadAllText(file, Encoding.UTF8);
                string bridge = BuildRuntimeBridgeMarkup(record);
                string markerPattern = "<!-- DEVMIND_ERROR_BRIDGE_BEGIN v1 -->.*?<!-- DEVMIND_ERROR_BRIDGE_END -->";
                if (Regex.IsMatch(html, markerPattern, RegexOptions.Singleline))
                {
                    string refreshed = Regex.Replace(html, markerPattern, bridge, RegexOptions.Singleline);
                    if (!string.Equals(refreshed, html, StringComparison.Ordinal))
                    {
                        File.WriteAllText(file, refreshed, new UTF8Encoding(false));
                        changed++;
                    }
                    continue;
                }
                int insert = -1;
                Match head = Regex.Match(html, "<head(?:\\s[^>]*)?>", RegexOptions.IgnoreCase);
                if (head.Success) insert = head.Index + head.Length;
                else
                {
                    Match firstScript = Regex.Match(html, "<script(?:\\s[^>]*)?>", RegexOptions.IgnoreCase);
                    if (firstScript.Success) insert = firstScript.Index;
                }
                if (insert < 0) continue;
                html = html.Insert(insert, Environment.NewLine + bridge + Environment.NewLine);
                File.WriteAllText(file, html, new UTF8Encoding(false));
                changed++;
            }
            WriteLog(logPath, "Runtime Error Cockpit instrumented " + changed + " HTML file(s) for " + record.DisplayName + ".");
        }

        private static string BuildRuntimeBridgeMarkup(ProductRecord record)
        {
            string encoded = "KGZ1bmN0aW9uKCl7CiAgJ3VzZSBzdHJpY3QnOwogIGlmICh3aW5kb3cuX19kZXZtaW5kRXJyb3JCcmlkZ2VJbnN0YWxsZWQpIHJldHVybjsKICB3aW5kb3cuX19kZXZtaW5kRXJyb3JCcmlkZ2VJbnN0YWxsZWQgPSB0cnVlOwogIHZhciBlbmRwb2ludCA9IF9fRU5EUE9JTlRfSlNPTl9fOwogIHZhciBwcm9kdWN0SWQgPSBfX1BST0RVQ1RfSURfSlNPTl9fOwogIHZhciBwcm9kdWN0VmVyc2lvbiA9IF9fVkVSU0lPTl9KU09OX187CiAgdmFyIHNlbmRpbmcgPSBmYWxzZTsKICB2YXIgb3JpZ2luYWxGZXRjaCA9IHR5cGVvZiB3aW5kb3cuZmV0Y2ggPT09ICdmdW5jdGlvbicgPyB3aW5kb3cuZmV0Y2guYmluZCh3aW5kb3cpIDogbnVsbDsKICB2YXIgb3JpZ2luYWxDb25zb2xlRXJyb3IgPSBjb25zb2xlLmVycm9yID8gY29uc29sZS5lcnJvci5iaW5kKGNvbnNvbGUpIDogZnVuY3Rpb24oKXt9OwogIHZhciBvcmlnaW5hbENvbnNvbGVXYXJuID0gY29uc29sZS53YXJuID8gY29uc29sZS53YXJuLmJpbmQoY29uc29sZSkgOiBmdW5jdGlvbigpe307CiAgZnVuY3Rpb24gY2xlYW4odmFsdWUsIGxpbWl0KXsKICAgIHZhciB0ZXh0ID0gJyc7CiAgICB0cnkgewogICAgICBpZiAodHlwZW9mIHZhbHVlID09PSAnc3RyaW5nJykgdGV4dCA9IHZhbHVlOwogICAgICBlbHNlIGlmICh2YWx1ZSAmJiB2YWx1ZS5zdGFjaykgdGV4dCA9IFN0cmluZyh2YWx1ZS5zdGFjayk7CiAgICAgIGVsc2UgdGV4dCA9IEpTT04uc3RyaW5naWZ5KHZhbHVlKTsKICAgIH0gY2F0Y2ggKF8pIHsKICAgICAgdHJ5IHsgdGV4dCA9IFN0cmluZyh2YWx1ZSk7IH0gY2F0Y2ggKF9fKSB7IHRleHQgPSAnW3VucHJpbnRhYmxlXSc7IH0KICAgIH0KICAgIGlmICh0ZXh0ID09IG51bGwpIHRleHQgPSAnJzsKICAgIHRleHQgPSBTdHJpbmcodGV4dCk7CiAgICByZXR1cm4gdGV4dC5sZW5ndGggPiBsaW1pdCA/IHRleHQuc2xpY2UoMCwgbGltaXQpICsgJ1xuLi4uW3RydW5jYXRlZF0nIDogdGV4dDsKICB9CiAgZnVuY3Rpb24gcmVwb3J0KHNldmVyaXR5LCB0eXBlLCBtZXNzYWdlLCBleHRyYSl7CiAgICBpZiAoc2VuZGluZykgcmV0dXJuOwogICAgdmFyIHBheWxvYWQgPSB7CiAgICAgIHByb2R1Y3RJZDogcHJvZHVjdElkLAogICAgICB2ZXJzaW9uOiBwcm9kdWN0VmVyc2lvbiwKICAgICAgc2V2ZXJpdHk6IHNldmVyaXR5IHx8ICdlcnJvcicsCiAgICAgIHR5cGU6IHR5cGUgfHwgJ3J1bnRpbWUtZXJyb3InLAogICAgICBtZXNzYWdlOiBjbGVhbihtZXNzYWdlLCA0MDAwKSwKICAgICAgc3RhY2s6IGNsZWFuKGV4dHJhICYmIGV4dHJhLnN0YWNrLCAxMjAwMCksCiAgICAgIHNvdXJjZTogY2xlYW4oZXh0cmEgJiYgZXh0cmEuc291cmNlLCAyMDAwKSwKICAgICAgbGluZTogTnVtYmVyKGV4dHJhICYmIGV4dHJhLmxpbmUpIHx8IDAsCiAgICAgIGNvbHVtbjogTnVtYmVyKGV4dHJhICYmIGV4dHJhLmNvbHVtbikgfHwgMCwKICAgICAgdXJsOiBjbGVhbihsb2NhdGlvbi5ocmVmLCAyMDAwKSwKICAgICAgdGl0bGU6IGNsZWFuKGRvY3VtZW50LnRpdGxlLCAxMDAwKSwKICAgICAgYm9keVNhbXBsZTogY2xlYW4oZXh0cmEgJiYgZXh0cmEuYm9keVNhbXBsZSwgNjAwMCksCiAgICAgIHVzZXJBZ2VudDogY2xlYW4obmF2aWdhdG9yLnVzZXJBZ2VudCwgMTAwMCkKICAgIH07CiAgICB2YXIgYm9keSA9IEpTT04uc3RyaW5naWZ5KHBheWxvYWQpOwogICAgdHJ5IHsKICAgICAgc2VuZGluZyA9IHRydWU7CiAgICAgIGlmIChuYXZpZ2F0b3Iuc2VuZEJlYWNvbikgbmF2aWdhdG9yLnNlbmRCZWFjb24oZW5kcG9pbnQsIGJvZHkpOwogICAgICBlbHNlIGlmIChvcmlnaW5hbEZldGNoKSBvcmlnaW5hbEZldGNoKGVuZHBvaW50LCB7IG1ldGhvZDonUE9TVCcsIG1vZGU6J25vLWNvcnMnLCBoZWFkZXJzOnsnQ29udGVudC1UeXBlJzondGV4dC9wbGFpbjtjaGFyc2V0PVVURi04J30sIGJvZHk6Ym9keSwga2VlcGFsaXZlOnRydWUgfSkuY2F0Y2goZnVuY3Rpb24oKXt9KTsKICAgIH0gY2F0Y2ggKF8pIHsKICAgIH0gZmluYWxseSB7CiAgICAgIHNldFRpbWVvdXQoZnVuY3Rpb24oKXsgc2VuZGluZyA9IGZhbHNlOyB9LCAwKTsKICAgIH0KICB9CiAgd2luZG93LmFkZEV2ZW50TGlzdGVuZXIoJ2Vycm9yJywgZnVuY3Rpb24oZXZlbnQpewogICAgdmFyIHRhcmdldCA9IGV2ZW50ICYmIGV2ZW50LnRhcmdldDsKICAgIGlmICh0YXJnZXQgJiYgdGFyZ2V0ICE9PSB3aW5kb3cpIHsKICAgICAgdmFyIHJlc291cmNlID0gdGFyZ2V0LnNyYyB8fCB0YXJnZXQuaHJlZiB8fCB0YXJnZXQuY3VycmVudFNyYyB8fCB0YXJnZXQudGFnTmFtZSB8fCAndW5rbm93biByZXNvdXJjZSc7CiAgICAgIHJlcG9ydCgnZXJyb3InLCAncmVzb3VyY2UtbG9hZC1lcnJvcicsICdGYWlsZWQgdG8gbG9hZCByZXNvdXJjZTogJyArIHJlc291cmNlLCB7IHNvdXJjZTogcmVzb3VyY2UgfSk7CiAgICAgIHJldHVybjsKICAgIH0KICAgIHJlcG9ydCgnZXJyb3InLCAnamF2YXNjcmlwdC1lcnJvcicsIGV2ZW50ICYmIGV2ZW50Lm1lc3NhZ2UgfHwgJ1Vua25vd24gSmF2YVNjcmlwdCBlcnJvcicsIHsKICAgICAgc3RhY2s6IGV2ZW50ICYmIGV2ZW50LmVycm9yICYmIGV2ZW50LmVycm9yLnN0YWNrLAogICAgICBzb3VyY2U6IGV2ZW50ICYmIGV2ZW50LmZpbGVuYW1lLAogICAgICBsaW5lOiBldmVudCAmJiBldmVudC5saW5lbm8sCiAgICAgIGNvbHVtbjogZXZlbnQgJiYgZXZlbnQuY29sbm8KICAgIH0pOwogIH0sIHRydWUpOwogIHdpbmRvdy5hZGRFdmVudExpc3RlbmVyKCd1bmhhbmRsZWRyZWplY3Rpb24nLCBmdW5jdGlvbihldmVudCl7CiAgICB2YXIgcmVhc29uID0gZXZlbnQgJiYgZXZlbnQucmVhc29uOwogICAgcmVwb3J0KCdlcnJvcicsICd1bmhhbmRsZWQtcmVqZWN0aW9uJywgY2xlYW4ocmVhc29uLCA0MDAwKSwgeyBzdGFjazogcmVhc29uICYmIHJlYXNvbi5zdGFjayB9KTsKICB9KTsKICBjb25zb2xlLmVycm9yID0gZnVuY3Rpb24oKXsKICAgIHZhciBhcmdzID0gQXJyYXkucHJvdG90eXBlLnNsaWNlLmNhbGwoYXJndW1lbnRzKTsKICAgIG9yaWdpbmFsQ29uc29sZUVycm9yLmFwcGx5KGNvbnNvbGUsIGFyZ3MpOwogICAgcmVwb3J0KCdlcnJvcicsICdjb25zb2xlLWVycm9yJywgYXJncy5tYXAoZnVuY3Rpb24oeCl7IHJldHVybiBjbGVhbih4LCAyMDAwKTsgfSkuam9pbignICcpLCB7IHN0YWNrOiAobmV3IEVycm9yKCkpLnN0YWNrIH0pOwogIH07CiAgY29uc29sZS53YXJuID0gZnVuY3Rpb24oKXsKICAgIHZhciBhcmdzID0gQXJyYXkucHJvdG90eXBlLnNsaWNlLmNhbGwoYXJndW1lbnRzKTsKICAgIG9yaWdpbmFsQ29uc29sZVdhcm4uYXBwbHkoY29uc29sZSwgYXJncyk7CiAgICBpZiAoYXJncy5qb2luKCcgJykudG9Mb3dlckNhc2UoKS5pbmRleE9mKCdlcnJvcicpID49IDAgfHwgYXJncy5qb2luKCcgJykudG9Mb3dlckNhc2UoKS5pbmRleE9mKCdmYWlsZWQnKSA+PSAwKQogICAgICByZXBvcnQoJ3dhcm5pbmcnLCAnY29uc29sZS13YXJuaW5nJywgYXJncy5tYXAoZnVuY3Rpb24oeCl7IHJldHVybiBjbGVhbih4LCAyMDAwKTsgfSkuam9pbignICcpLCB7IHN0YWNrOiAobmV3IEVycm9yKCkpLnN0YWNrIH0pOwogIH07CiAgaWYgKG9yaWdpbmFsRmV0Y2gpIHsKICAgIHdpbmRvdy5mZXRjaCA9IGZ1bmN0aW9uKCl7CiAgICAgIHZhciBhcmdzID0gYXJndW1lbnRzOwogICAgICByZXR1cm4gb3JpZ2luYWxGZXRjaC5hcHBseSh3aW5kb3csIGFyZ3MpLnRoZW4oZnVuY3Rpb24ocmVzcG9uc2UpewogICAgICAgIGlmICghcmVzcG9uc2Uub2spIHJlcG9ydCgnZXJyb3InLCAnZmV0Y2gtaHR0cC1lcnJvcicsICdGZXRjaCByZXR1cm5lZCBIVFRQICcgKyByZXNwb25zZS5zdGF0dXMgKyAnIGZvciAnICsgcmVzcG9uc2UudXJsLCB7IHNvdXJjZTogcmVzcG9uc2UudXJsIH0pOwogICAgICAgIHJldHVybiByZXNwb25zZTsKICAgICAgfSkuY2F0Y2goZnVuY3Rpb24oZXJyb3IpewogICAgICAgIHJlcG9ydCgnZXJyb3InLCAnZmV0Y2gtcmVqZWN0aW9uJywgY2xlYW4oZXJyb3IsIDQwMDApLCB7IHN0YWNrOiBlcnJvciAmJiBlcnJvci5zdGFjaywgc291cmNlOiBjbGVhbihhcmdzWzBdLCAyMDAwKSB9KTsKICAgICAgICB0aHJvdyBlcnJvcjsKICAgICAgfSk7CiAgICB9OwogIH0KICBpZiAod2luZG93LlhNTEh0dHBSZXF1ZXN0KSB7CiAgICB2YXIgT3JpZ2luYWxYSFIgPSB3aW5kb3cuWE1MSHR0cFJlcXVlc3Q7CiAgICB2YXIgb3BlbiA9IE9yaWdpbmFsWEhSLnByb3RvdHlwZS5vcGVuOwogICAgdmFyIHNlbmQgPSBPcmlnaW5hbFhIUi5wcm90b3R5cGUuc2VuZDsKICAgIE9yaWdpbmFsWEhSLnByb3RvdHlwZS5vcGVuID0gZnVuY3Rpb24obWV0aG9kLCB1cmwpeyB0aGlzLl9fZGV2bWluZFVybCA9IHVybDsgcmV0dXJuIG9wZW4uYXBwbHkodGhpcywgYXJndW1lbnRzKTsgfTsKICAgIE9yaWdpbmFsWEhSLnByb3RvdHlwZS5zZW5kID0gZnVuY3Rpb24oKXsKICAgICAgdGhpcy5hZGRFdmVudExpc3RlbmVyKCdsb2FkZW5kJywgZnVuY3Rpb24oKXsKICAgICAgICBpZiAodGhpcy5zdGF0dXMgPT09IDAgfHwgdGhpcy5zdGF0dXMgPj0gNDAwKSByZXBvcnQoJ2Vycm9yJywgJ3hoci1odHRwLWVycm9yJywgJ1hIUiByZXR1cm5lZCBzdGF0dXMgJyArIHRoaXMuc3RhdHVzICsgJyBmb3IgJyArICh0aGlzLl9fZGV2bWluZFVybCB8fCAnJyksIHsgc291cmNlOiB0aGlzLl9fZGV2bWluZFVybCB8fCAnJyB9KTsKICAgICAgfSk7CiAgICAgIHJldHVybiBzZW5kLmFwcGx5KHRoaXMsIGFyZ3VtZW50cyk7CiAgICB9OwogIH0KICBmdW5jdGlvbiBpbnNwZWN0VmlzaWJsZVBhZ2UoKXsKICAgIHRyeSB7CiAgICAgIGlmICghZG9jdW1lbnQuYm9keSkgcmV0dXJuOwogICAgICB2YXIgdGV4dCA9IChkb2N1bWVudC5ib2R5LmlubmVyVGV4dCB8fCAnJykudHJpbSgpOwogICAgICB2YXIgZWxlbWVudENvdW50ID0gZG9jdW1lbnQuYm9keS5xdWVyeVNlbGVjdG9yQWxsKCcqJykubGVuZ3RoOwogICAgICB2YXIgY29kZVNpZ25hbHMgPSAodGV4dC5tYXRjaCgvXGIoZnVuY3Rpb258Y29uc3R8bGV0fHZhcnxyZXR1cm58cmVnaXN0cnlcLmdldHxjb25zb2xlXC5lcnJvcnx0aHJvd3xjYXRjaHw9PilcYi9nKSB8fCBbXSkubGVuZ3RoOwogICAgICBpZiAodGV4dC5sZW5ndGggPiA3MDAgJiYgY29kZVNpZ25hbHMgPj0gNiAmJiBlbGVtZW50Q291bnQgPCAxMjApIHsKICAgICAgICByZXBvcnQoJ2Vycm9yJywgJ3JlbmRlcmVkLXNvdXJjZS1sZWFrJywgJ1RoZSBwYWdlIGFwcGVhcnMgdG8gYmUgcmVuZGVyaW5nIEphdmFTY3JpcHQgc291cmNlIGFzIHZpc2libGUgY29udGVudCBpbnN0ZWFkIG9mIHRoZSBhcHBsaWNhdGlvbiBVSS4nLCB7IGJvZHlTYW1wbGU6IHRleHQuc2xpY2UoMCwgNjAwMCkgfSk7CiAgICAgIH0KICAgICAgdmFyIGJvZHlTdHlsZSA9IHdpbmRvdy5nZXRDb21wdXRlZFN0eWxlKGRvY3VtZW50LmJvZHkpOwogICAgICB2YXIgY2FudmFzQ291bnQgPSBkb2N1bWVudC5xdWVyeVNlbGVjdG9yQWxsKCdjYW52YXMnKS5sZW5ndGg7CiAgICAgIGlmICh0ZXh0Lmxlbmd0aCA8IDQgJiYgY2FudmFzQ291bnQgPT09IDAgJiYgZWxlbWVudENvdW50IDwgNSAmJiBib2R5U3R5bGUuZGlzcGxheSAhPT0gJ25vbmUnKSB7CiAgICAgICAgcmVwb3J0KCd3YXJuaW5nJywgJ2JsYW5rLXBhZ2UnLCAnVGhlIHBhZ2UgbG9hZGVkIGJ1dCBwcm9kdWNlZCBubyB2aXNpYmxlIGludGVyZmFjZSBjb250ZW50LicsIHsgYm9keVNhbXBsZTogZG9jdW1lbnQuZG9jdW1lbnRFbGVtZW50Lm91dGVySFRNTC5zbGljZSgwLCA0MDAwKSB9KTsKICAgICAgfQogICAgfSBjYXRjaCAoXykge30KICB9CiAgaWYgKGRvY3VtZW50LnJlYWR5U3RhdGUgPT09ICdsb2FkaW5nJykgZG9jdW1lbnQuYWRkRXZlbnRMaXN0ZW5lcignRE9NQ29udGVudExvYWRlZCcsIGZ1bmN0aW9uKCl7IHNldFRpbWVvdXQoaW5zcGVjdFZpc2libGVQYWdlLCAxMjAwKTsgfSk7CiAgZWxzZSBzZXRUaW1lb3V0KGluc3BlY3RWaXNpYmxlUGFnZSwgMTIwMCk7CiAgc2V0VGltZW91dChpbnNwZWN0VmlzaWJsZVBhZ2UsIDQwMDApOwp9KSgpOwo=";
            string script = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            string endpoint = "http://127.0.0.1:" + runtimeIssuePort + "/issue/" + runtimeIssueToken;
            script = script.Replace("__ENDPOINT_JSON__", serializer.Serialize(endpoint));
            script = script.Replace("__PRODUCT_ID_JSON__", serializer.Serialize(record.ProductId ?? ""));
            script = script.Replace("__VERSION_JSON__", serializer.Serialize(record.Version ?? ""));
            return "<!-- DEVMIND_ERROR_BRIDGE_BEGIN v1 --><script>" + script + "</script><!-- DEVMIND_ERROR_BRIDGE_END -->";
        }

        public static string RunSelfTest()
        {
            List<string> lines = new List<string>();
            lines.Add("LaunchBridge self-test");
            lines.Add("Executable: " + CurrentExePath);
            lines.Add("Config: " + configPath);
            lines.Add("Work root: " + config.WorkRoot);
            lines.Add("Work root writable: " + TestWritable(config.WorkRoot));
            lines.Add("Log root writable: " + TestWritable(logsRoot));
            lines.Add("Registered extensions: " + string.Join(", ", config.RegisteredExtensions.ToArray()));
            lines.Add("Extension Profiles: " + (config.ExtensionProfiles == null ? 0 : config.ExtensionProfiles.Count));
            lines.Add("Manifest-free smart packages: enabled");
            lines.Add("Installed products tracked: " + config.InstalledProducts.Count);
            lines.Add("Turbo Launch enabled: " + TurboLaunchEnabled);
            lines.Add("Runtime Error Cockpit enabled: " + RuntimeMonitorEnabled);
            lines.Add("Runtime issue intake port: " + runtimeIssuePort);
            lines.Add("Recorded app problems loaded: " + RuntimeIssuesSnapshot().Count);
            lines.Add("Result: PASS");
            string report = string.Join(Environment.NewLine, lines.ToArray());
            File.WriteAllText(Path.Combine(appRoot, "SELF_TEST.txt"), report, Encoding.UTF8);
            return report;
        }

        private static bool TestWritable(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string test = Path.Combine(folder, ".launchbridge-write-test");
                File.WriteAllText(test, "ok");
                File.Delete(test);
                return true;
            }
            catch { return false; }
        }

        private static string MakeRelativePath(string root, string file)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A root path is required.", "root");
            if (string.IsNullOrWhiteSpace(file)) throw new ArgumentException("A file path is required.", "file");

            string fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string fullFile = Path.GetFullPath(file);

            Uri rootUri = new Uri(fullRoot, UriKind.Absolute);
            Uri fileUri = new Uri(fullFile, UriKind.Absolute);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
