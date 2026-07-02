using System;
using System.Collections.Generic;

namespace DevMind.LaunchBridge
{
    public class AppConfig
    {
        public string WorkRoot { get; set; }
        public List<string> RegisteredExtensions { get; set; }
        public List<ExtensionProfile> ExtensionProfiles { get; set; }
        public string DefaultExtension { get; set; }
        public bool AutoLaunch { get; set; }
        public bool KeepRollback { get; set; }
        public bool Dinger { get; set; }
        public bool AutoCopyErrors { get; set; }
        public bool AddZipContextMenu { get; set; }
        public bool? RuntimeMonitorEnabled { get; set; }
        public bool? OpenErrorCockpitOnIssue { get; set; }
        public bool? TurboLaunchEnabled { get; set; }
        public bool? ExtensionTourCompleted { get; set; }
        public bool? SmartClickEnabled { get; set; }
        public List<ProductRecord> InstalledProducts { get; set; }

        public static AppConfig CreateDefault()
        {
            string root = System.IO.Directory.Exists("D:\\") ? "D:\\Work" :
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LaunchBridgeWork");
            AppConfig c = new AppConfig();
            c.WorkRoot = root;
            c.RegisteredExtensions = new List<string>();
            c.RegisteredExtensions.Add(".devmind");
            c.ExtensionProfiles = new List<ExtensionProfile>();
            c.ExtensionProfiles.Add(ExtensionProfile.CreateDefault(".devmind", "LaunchBridge Package", "AI-built runnable application package"));
            c.DefaultExtension = ".devmind";
            c.AutoLaunch = true;
            c.KeepRollback = true;
            c.Dinger = true;
            c.AutoCopyErrors = true;
            c.AddZipContextMenu = true;
            c.RuntimeMonitorEnabled = true;
            c.OpenErrorCockpitOnIssue = true;
            c.TurboLaunchEnabled = true;
            c.ExtensionTourCompleted = false;
            c.SmartClickEnabled = true;
            c.InstalledProducts = new List<ProductRecord>();
            return c;
        }
    }


    public class ExtensionProfile
    {
        public int SchemaVersion { get; set; }
        public string Extension { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string PackageMode { get; set; }
        public bool ManifestRequired { get; set; }
        public bool ManifestAllowed { get; set; }
        public bool AutoLaunch { get; set; }
        public bool ProcessSupervision { get; set; }
        public bool PreserveState { get; set; }
        public List<string> PreserveStatePaths { get; set; }
        public string IconSourcePath { get; set; }
        public string IconIcoPath { get; set; }
        public string FullPrompt { get; set; }
        public string ShortPrompt { get; set; }
        public string IconPrompt { get; set; }
        public string CreatedAtUtc { get; set; }
        public string UpdatedAtUtc { get; set; }

        public static ExtensionProfile CreateDefault(string extension, string displayName, string description)
        {
            ExtensionProfile profile = new ExtensionProfile();
            profile.SchemaVersion = 1;
            profile.Extension = extension;
            profile.DisplayName = displayName;
            profile.Description = description;
            profile.PackageMode = "smart";
            profile.ManifestRequired = false;
            profile.ManifestAllowed = true;
            profile.AutoLaunch = true;
            profile.ProcessSupervision = true;
            profile.PreserveState = true;
            profile.PreserveStatePaths = new List<string>();
            profile.PreserveStatePaths.Add("data");
            profile.PreserveStatePaths.Add("user-data");
            profile.PreserveStatePaths.Add("projects");
            profile.PreserveStatePaths.Add("workspace");
            profile.PreserveStatePaths.Add("config");
            profile.CreatedAtUtc = DateTime.UtcNow.ToString("o");
            profile.UpdatedAtUtc = profile.CreatedAtUtc;
            return profile;
        }
    }

    public class DevMindPackageManifest
    {
        public int SchemaVersion { get; set; }
        public string ProductId { get; set; }
        public string DisplayName { get; set; }
        public string Version { get; set; }
        public string Publisher { get; set; }
        public string Description { get; set; }
        public string PayloadRoot { get; set; }
        public string InstallDirectoryName { get; set; }
        public string EntryPoint { get; set; }
        public string EntryType { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public string LaunchUrl { get; set; }
        public int LaunchDelayMilliseconds { get; set; }
        public int MinimumNodeMajor { get; set; }
        public List<string> RequiredFiles { get; set; }
        public List<string> PreserveStatePaths { get; set; }
        public Dictionary<string, string> FileHashes { get; set; }
        public string ReleaseChannel { get; set; }
        public string PackageNotes { get; set; }

        public DevMindPackageManifest()
        {
            SchemaVersion = 1;
            PayloadRoot = "payload";
            EntryType = "auto";
            WorkingDirectory = ".";
            LaunchDelayMilliseconds = 350;
            RequiredFiles = new List<string>();
            PreserveStatePaths = new List<string>();
            FileHashes = new Dictionary<string, string>();
            ReleaseChannel = "stable";
        }
    }

    public class ProductRecord
    {
        public string ProductId { get; set; }
        public string DisplayName { get; set; }
        public string Version { get; set; }
        public string Publisher { get; set; }
        public string InstallPath { get; set; }
        public string EntryPoint { get; set; }
        public string EntryType { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public string LaunchUrl { get; set; }
        public int LaunchDelayMilliseconds { get; set; }
        public string LastResolvedLaunchUrl { get; set; }
        public string InstalledAtUtc { get; set; }
        public string SourcePackage { get; set; }
        public long SourcePackageLength { get; set; }
        public string SourcePackageWriteUtc { get; set; }
        public string PackageManifestSignature { get; set; }
        public string SourceExtension { get; set; }
        public string PackageMode { get; set; }
        public string RollbackPath { get; set; }
        public int LastProcessId { get; set; }
        public int LastLauncherProcessId { get; set; }
        public string LastLauncherEntryType { get; set; }
        public string LastAutoStopReason { get; set; }
        public int LastUiProcessId { get; set; }
        public string LastUiTargetId { get; set; }
        public string LastUiUrl { get; set; }
        public string LastUiMode { get; set; }
        public string LastLaunchAtUtc { get; set; }
        public string LastKnownStatus { get; set; }
    }

    public class InstallResult
    {
        public bool Success { get; set; }
        public bool Launched { get; set; }
        public bool TurboLaunched { get; set; }
        public string Message { get; set; }
        public string InstallPath { get; set; }
        public ProductRecord Product { get; set; }
        public string LogPath { get; set; }
        public string RepairBundlePath { get; set; }
        public int ProcessId { get; set; }
        public int UiProcessId { get; set; }
        public string UiTargetId { get; set; }
    }

    public class PackageBuildRequest
    {
        public string SourceFolder { get; set; }
        public string OutputFile { get; set; }
        public string ProductId { get; set; }
        public string DisplayName { get; set; }
        public string Version { get; set; }
        public string Publisher { get; set; }
        public string Description { get; set; }
        public string InstallDirectoryName { get; set; }
        public string EntryPoint { get; set; }
        public string EntryType { get; set; }
        public string Arguments { get; set; }
        public string LaunchUrl { get; set; }
        public int MinimumNodeMajor { get; set; }
        public string Extension { get; set; }
    }
}

namespace DevMind.LaunchBridge
{
    public class ActivityRecord
    {
        public string ActivityId { get; set; }
        public string ProductId { get; set; }
        public string Product { get; set; }
        public string Version { get; set; }
        public string Status { get; set; }
        public int ProcessId { get; set; }
        public int UiProcessId { get; set; }
        public string UiTargetId { get; set; }
        public string QueuedAtUtc { get; set; }
        public string StartedAtUtc { get; set; }
        public string CompletedAtUtc { get; set; }
        public string Source { get; set; }
        public string PackagePath { get; set; }
        public string InstallPath { get; set; }
        public string Message { get; set; }
    }
}


namespace DevMind.LaunchBridge
{
    public class RuntimeIssue
    {
        public string IssueId { get; set; }
        public string ReceivedAtUtc { get; set; }
        public string ProductId { get; set; }
        public string Product { get; set; }
        public string Version { get; set; }
        public string Severity { get; set; }
        public string Type { get; set; }
        public string Message { get; set; }
        public string Stack { get; set; }
        public string Source { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public string BodySample { get; set; }
        public string UserAgent { get; set; }
        public string InstallPath { get; set; }
        public string LogPath { get; set; }
        public string RepairBundlePath { get; set; }
    }
}
