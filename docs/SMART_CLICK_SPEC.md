# LaunchBridge Smart Click — First-Pass Protocol

Browser companion ID: `lkjjnhlhobcpmhpgkmkbgjioclgohjdk`  
Native host name: `com.launchbridge.smartclick`

The content script records a trusted user click on a likely download link from an approved AI site. The service worker matches the next completed browser download and sends the completed local path through Chromium Native Messaging. The native host verifies that the file is a normal ZIP or a registered LaunchBridge package type, then starts the resident LaunchBridge broker with `--smart-click`.

The browser companion does not execute code and does not inspect archive content. LaunchBridge performs the package safety and lifecycle work.
