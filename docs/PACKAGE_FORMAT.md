# LaunchBridge Package Formats — v0.2

## Standard smart package

The standard format is a ZIP-compatible archive with a registered custom extension:

```text
<ProductName>_v<Version>.<extension>
```

No JSON manifest is required.

LaunchBridge derives:

- package profile from the extension;
- product name and version from the filename;
- stable product identity from extension + product name;
- launch target from archive inspection;
- update target from the stable identity;
- state-preservation paths from the Extension Profile.

The archive should contain a complete runnable build with all dependencies.

## Advanced manifest package

The existing format remains supported:

```text
devmind.package.json
payload/
```

Advanced mode is appropriate for explicit launch URLs, health endpoints, strict file hashes, custom required files, unusual working directories, or custom state preservation.

## Filename examples

```text
BadMoth_v1.4.20.badmoth
CouncilRelay_v0.8.2.council
HeightGrid_v0.3.1.heightgrid
```

Without a parseable version, LaunchBridge records `unversioned` and update detection is less precise.

## Icon candidates inside a product

AI prompts recommend:

```text
launchbridge-icon.png
product-icon.png
app-icon.png
icon.png
app.ico
```

The Extension Profile icon controls how the custom extension appears in Windows before opening. Product-specific artwork can be used by the installed application and future shortcut features.
