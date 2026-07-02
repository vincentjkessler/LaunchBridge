# Extension Profile Specification v1

An Extension Profile defines how LaunchBridge interprets a custom package extension.

Core properties:

```json
{
  "schemaVersion": 1,
  "extension": ".badmoth",
  "displayName": "BadMoth Package",
  "description": "A complete runnable BadMoth application or update",
  "packageMode": "smart",
  "manifestRequired": false,
  "manifestAllowed": true,
  "autoLaunch": true,
  "processSupervision": true,
  "preserveState": true,
  "preserveStatePaths": [
    "data",
    "user-data",
    "projects",
    "workspace",
    "config"
  ],
  "iconSourcePath": "",
  "iconIcoPath": ""
}
```

Profiles are stored under the LaunchBridge application-state directory and mirrored as readable profile JSON files. Existing registered extensions are migrated automatically.

## AI prompts

Each profile stores or regenerates:

- Full AI export prompt.
- Short AI export prompt.
- Icon-generation prompt.

The prompt instructs the AI to return a runnable ZIP-compatible archive using the profile extension and to generate a recognizable product/package icon.

## Icons

LaunchBridge accepts ICO and common image formats. Imported raster images are converted to a Windows ICO and registered as the extension association icon.
