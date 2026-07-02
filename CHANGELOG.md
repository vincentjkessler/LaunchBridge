# Changelog

## 0.3.1 — Smart Click reliability fix

- Added a Chromium browser companion for ChatGPT, Gemini, and Claude.
- Added a native messaging host that hands completed user-clicked downloads to LaunchBridge.
- Added one-time Smart Click setup controls in Settings.
- Added manifest-free normal ZIP smart-package support.
- Unsupported images and documents remain ordinary downloads.
- Kept custom Extension Profiles and advanced manifest packages backward-compatible.

## 0.3.1 — AI-native local application delivery

- Replaced basic custom-extension registration with persistent Extension Profiles.
- Automatically migrates every existing registered extension into a smart profile.
- Immediately copies and displays a full AI export prompt after profile creation.
- Added short export and icon-generation prompt variants.
- Added profile display name, purpose, smart-package rules, state-preservation conventions, and icon paths.
- Added ICO and raster-image import; raster artwork is converted into a Windows association icon.
- Standard custom-extension packages no longer require `devmind.package.json`.
- Derives product name and semantic version from `<ProductName>_v<Version>.<extension>`.
- Added smart entry-point detection for packaged EXEs, Electron apps, CMD/BAT/PowerShell launchers, complete Node apps, and `index.html`.
- Reuses remembered launch targets for later versions of the same product.
- Added smart-package fingerprints and source metadata for Turbo Launch.
- Retains full backward compatibility with advanced manifest packages.
- Added an MIT license, contribution guide, AI-native package specification, and Extension Profile specification.

## 0.1.27 — Branding cleanup and native high-DPI text

- Removed the visible DevMind name from the application title, header, tray tooltip, dialogs, package descriptions, context-menu text, error packets, and self-test output.
- Renamed the installed executable from `DevMindLaunchBridge.exe` to `LaunchBridge.exe`.
- Migrates existing desktop and Start menu shortcuts to the LaunchBridge name and removes legacy startup/association entries.
- Added an embedded Windows application manifest declaring `PerMonitorV2` DPI awareness.
- Added runtime DPI-awareness initialization before any WinForms UI is created, with a legacy Windows fallback.
- Added DPI-based WinForms autoscaling and ClearType-compatible text rendering across ordinary controls.
- Preserved the existing product ID and state directory internally to keep upgrades, settings, installed-product records, logs, rollbacks, and browser profiles intact.
- Retained Turbo Launch, lifecycle cleanup, Stop all, accurate status, complete scrolling, Error Cockpit, and compile-before-replace safety.

## 0.1.26 — Turbo Launch

- Added a fast single-instance handoff before configuration loading, runtime monitor startup, or WinForms construction. Browser Open file invocations now forward directly to the resident LaunchBridge broker.
- Added Turbo Launch mode, enabled by default, which starts a lightweight resident broker with Windows and keeps it available in the notification area after the main window closes.
- Browser package requests no longer force the LaunchBridge window to the foreground; failures still surface the Activity tab and diagnostic message.
- Added verified package-manifest signatures to installed-product records. Reopening the exact installed package version can skip extraction, full hash verification, rollback creation, and file copying, then launch the existing verified installation immediately.
- Added a one-time safe migration path for the exact original package file used by pre-0.1.26 installations when Windows confirms that the source file has not changed since installation.
- Added a Settings toggle for Turbo Launch and automatic cleanup of the Windows startup registration during LaunchBridge uninstall.
- Retained automatic lifecycle cleanup, Stop all, accurate runtime status, Activity-first navigation, complete scrolling, and compile-before-replace self-update safety.

## 0.1.25 — Automatic Electron and command lifecycle cleanup

- Added a background product lifecycle supervisor that uses one batched Windows process snapshot for all active products.
- Records the original launcher PID separately from the backend/product PID.
- Automatically terminates the remaining product process tree when a confirmed `.cmd`, `.bat`, or PowerShell launcher window is closed.
- Automatically terminates remaining launchers, Node processes, Electron helpers, backends, and children after a previously visible Electron/desktop application window closes.
- Uses a three-second command-anchor arming delay and a 2.5-second missing-window confirmation to prevent startup transitions from being treated as closure.
- Matches detached product processes by exact installation path and validates launcher identity against `LastLaunchAtUtc` to reject PID reuse.
- Displays `Auto-stopped` in Installed Products and Activity and records the exact automatic-stop reason.
- Manual Stop, Force kill, Stop all, update, rollback, and uninstall cancel the lifecycle watcher before terminating a product.
- Retains Activity-first navigation, full scrolling, accurate status, Settings self-uninstall, batched monitoring, and compile-before-replace updates.

## 0.1.25 — Stop All and verified runtime status

- Added a guarded red **Stop all** button to Activity and Installed Products.
- Stop all closes all tracked managed app tabs, terminates product process trees, clears stale tracking, and leaves installed products and product data untouched.
- Runs bulk stopping on a worker thread and pauses runtime polling during the operation.
- Replaced raw PID-exists checks with process-instance validation against the recorded product launch time.
- Stopped treating a shared Edge/Chrome process as proof that every product tab is running.
- Removed startup seeding from stale status text; Activity now recovers only products verified by the background scan.
- Installed Products now hides stale PID/UI values and displays Stopped when no verified runtime exists.
- Retained Settings self-uninstall, Activity-first navigation, full scrolling, batched monitoring, and compile-before-replace updates.

## 0.1.23 — Settings self-uninstall

- Added a clearly separated red **Uninstall LaunchBridge** action to Settings.
- Added a default-No confirmation that explicitly identifies removed LaunchBridge-owned data and preserved installed products.
- Added a detached PowerShell cleanup stage so LaunchBridge can safely remove its own running executable and installation folder.
- Removes desktop and Start menu shortcuts plus all registered LaunchBridge file associations.
- Retries locked-file deletion and opens a timestamped uninstall log on failure.
- Leaves installed product folders, product project files, and product-owned state untouched.
- Retained Activity-first navigation, full operational-tab scrolling, non-blocking runtime monitoring, and compile-before-replace updates.

## 0.1.22 — Compiler-safe Activity-first release

- Fixed the .NET Framework compiler error `CS0136` in `BuildActivityTab`.
- Renamed the Downloads path local from `folder` to `downloadsFolder`.
- Renamed the Activity Open Folder button local from `folder` to `openActivityFolderButton`.
- Added an explicit compiler-compatibility regression check for local-variable shadowing.
- Retained Activity-first navigation, removal of Home, Package Builder clarification, full scrolling, and all v0.1.19 performance improvements.

## 0.1.21 — Activity-first navigation and complete scrolling

- Made Activity the first tab and default workspace.
- Removed the redundant Home tab.
- Moved Open package, drag-and-drop intake, Downloads access, and package status into Activity.
- Renamed Build Package to Package Builder and clarified that it converts a finished product folder into an installable `.devmind` package.
- Added explicit resizable columns plus horizontal and vertical scrolling to Installed Products.
- Froze the Installed Products Product column and preserved selection, first visible row, and horizontal offset during refreshes.
- Added horizontal and vertical scrolling to the Error Cockpit issue grid.
- Added forced horizontal and vertical scrolling to the Error Cockpit repair-packet viewer.
- Preserved Error Cockpit grid scroll position during live updates.
- Replaced the Logs viewer with a forced-scrollbar control and enabled whole-tab scrolling for smaller windows.
- Retained all v0.1.20 Activity scrolling and v0.1.19 performance improvements.

## 0.1.20 — Activity scrolling and responsiveness

- Added explicit horizontal and vertical scrollbars to the Activity data grid.
- Replaced fill-to-window activity columns with practical fixed starting widths so long UI targets, sources, timestamps, and messages remain readable by scrolling.
- Kept the Product column frozen while horizontally scrolling through runtime details.
- Preserved the selected activity, first visible row, and horizontal scroll position across live status refreshes.
- Added whole-tab scrolling for smaller LaunchBridge window sizes so the activity controls cannot become unreachable.
- Retained the v0.1.19 non-blocking runtime monitor, single batched managed-browser query, unchanged-grid suppression, and deferred bounded instrumentation.

## 0.1.18 — Error Cockpit compile fix

- Added the missing `RuntimeIssue.RepairBundlePath` model property used by repair packet creation and Error Cockpit actions.
- Added a model-member contract audit so every `RuntimeIssue` property referenced by the core and UI must exist before packaging.
- Preserved the complete Runtime Error Cockpit, automatic clipboard repair packets, bounded repair bundles, managed tabs, port recovery, and compile-before-replace updater.

## 0.1.18 — Runtime Error Cockpit

- Added a dedicated Error Cockpit tab for runtime failures in launched local web products.
- Added automatic instrumentation for JavaScript exceptions, unhandled rejections, console errors, failed resources, failed fetch/XHR requests, blank pages, and rendered-source/code leaks.
- Added automatic clipboard repair packets and optional automatic focus of the Error Cockpit.
- Added per-error JSONL logs and a bounded repair bundle containing the issue packet plus relevant source files.
- Added one-click Copy + ChatGPT, Open product folder, Open issue logs, and Open repair bundle actions.
- Refreshes instrumentation endpoints on every LaunchBridge start so existing installed products keep reporting after the intake port changes.
- Preserved all v0.1.16 package validation, process control, port recovery, managed-tab, and self-update behavior.

## 0.1.18

- Waits until a conflicting loopback port is actually free before retrying.
- Force-kills a reported backend PID only when its ProductId exactly matches a registered installed product.
- Re-scans the conflicting product install path for detached child processes.
- Preserves strict refusal to kill unknown localhost services or open the wrong product.

- Added automatic recovery when a requested local URL is already serving another managed product.
- Dynamic `{port}` packages now receive a new free port and retry automatically.
- Legacy fixed-port packages now attempt a clean handoff by stopping the tracked conflicting product and calling its LaunchBridge shutdown endpoint.
- If a fixed port remains unavailable, LaunchBridge retries the requested package on a free port through `DEVMIND_PORT`.
- Preserves strict ProductId and Version verification, so the wrong product is never opened.
- Preserves response-file compilation, full compiler diagnostics, compile-before-replace, unique TEMP staging, SHA-256 executable verification, timeout retry, transient HTTP retry, managed-tab recovery, collection safety, and legacy manifest normalization.

## 0.1.14

- Fixed CS0160 in `LaunchBridgeCore.cs` by catching `WebException` before its base type `InvalidOperationException`.
- Added release QA that explicitly rejects the unreachable catch ordering that blocked v0.1.13 compilation.
