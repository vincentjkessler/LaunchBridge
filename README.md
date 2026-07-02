# LaunchBridge

LaunchBridge is a Windows package host for AI-native software artifacts. It helps users inspect, install, update, and launch `.devmind` packages from a consistent local runtime.

The current public release is `v0.3.1`, the Smart Click reliability fix.

## Download

Use the latest GitHub Release for the tested `.devmind` package. The repository contains the source, documentation, package scripts, and browser companion files. Release assets contain the tested downloadable package intended for end users.

## Smart Click

Smart Click removes the second browser action from the AI-download workflow:

1. A user clicks a runnable package link in ChatGPT, Gemini, Claude, or another approved source.
2. The browser finishes the download.
3. The LaunchBridge browser companion sends the local file path to LaunchBridge.
4. LaunchBridge inspects, installs, and launches the package.

Normal documents, images, and unsupported downloads are ignored.

## v0.3.1 Reliability Fix

This release repairs the first-pass Smart Click event chain:

- Records download intent on pointer-down, before the browser starts the download.
- Persists pending clicks and tracked download IDs in `chrome.storage.session`.
- Retroactively matches downloads created before the click message arrives.
- Uses browser download referrer as a fallback for approved AI sites.
- Recovers tracked downloads after Manifest V3 service-worker restarts.
- Adds a native-helper connection test from the extension icon.
- Shows badge states for seen clicks, successful handoff, and connection failure.

After updating the unpacked browser companion, reload the extension once from the browser Extensions page. Restarting the browser also reloads it.

## Repository Layout

- `src/` - C# LaunchBridge runtime source.
- `browser-extension/` - Chromium companion extension for Smart Click.
- `docs/` - package format, architecture, profile, and Smart Click specs.
- `examples/` - package and extension-profile examples.
- `*.cmd` and `*.ps1` - local install, update, build, and uninstall scripts.
- `QA_REPORT.txt` and `launchbridge.qa.json` - release validation evidence.

## License

LaunchBridge is open source under the Apache License 2.0.
