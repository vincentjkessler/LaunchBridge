# LaunchBridge

LaunchBridge is a small Windows app that opens local app packages.

It helps you try apps that come as `.devmind` packages. It can check the package, install it on your computer, and start it.

## What It Does

- Opens `.devmind` app packages.
- Keeps installed apps in one place.
- Helps your browser send a downloaded package to LaunchBridge.
- Shows clear problem reports when an app does not start.
- Lets you copy problem details so you can paste them into an AI chat for help.

## Get It

Use the latest GitHub Release for the ready-to-run package.

This repo has the source code and helper files. Most people should download the release file, not build the app by hand.

## What Is New In v0.3.3

The Problems tab is easier to use.

- The **Copy problem details** button is now easier to find.
- The button stays visible in shorter windows.
- LaunchBridge tells you when the problem details were copied.
- If copying fails, LaunchBridge tells you to copy the visible text by hand.

## Browser Helper

LaunchBridge includes a browser helper for Chromium browsers.

When you click a supported package download in ChatGPT, Gemini, Claude, or another approved site, the helper can pass the finished download to LaunchBridge.

Normal files like pictures and documents are left alone.

## Build From Source

On Windows, run:

```bat
BUILD_PORTABLE.cmd
```

LaunchBridge uses Windows Forms and the .NET Framework tools that are already on many Windows systems.

## Main Folders

- `src` has the Windows app source code.
- `browser-extension` has the browser helper.
- `assets` has the app icon and image.

## License

LaunchBridge is open source under the MIT License.
