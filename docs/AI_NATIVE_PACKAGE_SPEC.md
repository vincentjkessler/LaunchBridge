# LaunchBridge AI-Native Package Specification v0.2

## Goal

A user tells an AI:

> Export this completed runnable build as `<ProductName>_v<Version>.<extension>`.

The AI returns a ZIP-compatible archive using the registered custom extension. LaunchBridge identifies, installs, launches, supervises, updates, and repairs the application.

## Standard package mode

A standard package does not require `devmind.package.json`.

The contract is derived from:

1. The registered Extension Profile.
2. The package filename.
3. The archive contents.
4. A remembered launch target from an earlier version, when available.

Recommended filename:

```text
<ProductName>_v<SemanticVersion>.<extension>
```

Examples:

```text
BadMoth_v1.4.20.badmoth
CouncilRelay_v0.8.2.council
HeightGrid_v0.3.1.heightgrid
```

## Archive rules

- The file is a valid ZIP-compatible archive.
- No unnecessary outer folder is required.
- All runtime dependencies are included.
- The product is runnable without dependency installation or compilation.
- Archive paths must be relative and may not escape the extraction root.

## Smart launch-target priority

1. Remembered launch target for the same product.
2. Product-name-matching executable.
3. Most likely packaged executable.
4. `start`, `launch`, or `run` CMD/BAT/PowerShell launcher.
5. Complete Node/Electron application with runtime dependencies.
6. `index.html` local web application.

## Identity

LaunchBridge derives a stable key from the extension and product name. Version changes therefore update the same installed product.

## Advanced compatibility mode

Packages containing `devmind.package.json` remain supported. Advanced mode is appropriate for custom health endpoints, launch URLs, nonstandard state preservation, strict required-file lists, or externally supplied file hashes.

## Security

A registered extension is an interpretation rule, not a trust grant. LaunchBridge still performs safe extraction, package fingerprinting, executable/script identification, install records, process supervision, and runtime logging.
