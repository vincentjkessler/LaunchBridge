# Contributing to LaunchBridge

LaunchBridge is an open, AI-native local application delivery runtime for Windows.

## Contribution priorities

1. Deterministic package identification and safe extraction.
2. Reliable smart entry-point detection.
3. Extension Profile portability and icon handling.
4. Process lifecycle supervision and repair packets.
5. Browser handoff and native messaging.
6. Code-signing, publisher trust, and sandboxed first launch.

## Ground rules

- Keep existing manifest packages backward-compatible.
- Never weaken archive traversal, executable, or script safety checks.
- Prefer conventions that work across ChatGPT, Gemini, Claude, and other AIs.
- Avoid provider-specific lock-in.
- Preserve installed product state during updates.
- Add a reproducible test case for package-detection changes.

## Windows build

LaunchBridge targets the installed .NET Framework compiler and Windows Forms. The self-update package compiles before replacing a working installation.


Smart Click contributions should keep the browser companion thin. Package validation and execution decisions belong in the desktop runtime, not the extension.
