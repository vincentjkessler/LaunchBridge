# LaunchBridge Architecture — v0.2

## Category

LaunchBridge is an AI-native local application delivery runtime for Windows.

## Runtime layers

### 1. Browser/Windows handoff

A registered custom extension routes the downloaded file to the resident LaunchBridge process through the Windows file association.

### 2. Extension Profile

The extension selects a persistent profile containing package behavior, AI prompts, state-preservation conventions, and the Windows association icon.

### 3. Package interpretation

LaunchBridge supports two modes:

- **Smart mode:** no JSON manifest is required. Identity, version, and launch behavior are derived from the profile, filename, and archive.
- **Advanced mode:** `devmind.package.json` supplies an explicit contract and file hashes.

### 4. Safe installation

The archive is extracted through path-traversal-safe logic. The product is fingerprinted, existing processes are stopped, configured state folders are backed up, a rollback may be created, and the new payload replaces the old installation.

### 5. Launch and supervision

LaunchBridge starts EXE, Electron, CMD, BAT, PowerShell, Node, or HTML products. It tracks launcher/backend/UI identity, closes remaining process trees when the visible app or command anchor closes, and exposes Stop, Force Kill, and Stop All.

### 6. Runtime observability

Local web products can emit JavaScript, console, network, failed-resource, blank-page, and rendering errors into Error Cockpit repair packets.

### 7. Turbo Launch

The tray broker accepts browser Open file handoffs. Verified installed versions can be launched without reinstalling. Smart packages use source metadata for the exact original package and SHA-256 fingerprints for equivalent packages at another path.

## Persistent state

The existing internal state root remains unchanged for upgrade compatibility. It contains:

- configuration;
- Extension Profile mirrors;
- extension icons;
- installed-product records;
- logs;
- rollbacks;
- runtime issues and repair packets;
- managed browser profiles.
