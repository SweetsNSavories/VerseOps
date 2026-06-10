# XrmToolBox plugin demo assets

Capture region: XrmToolBox window (1280 × 820), bottom 110 px cropped to
exclude the XTB connection status bar (which prints the connected org URL).
Sign-in toolbar inside the plugin is overlaid with a neutral "Sign-in controls
hidden for demo capture" label so no tenant identity is visible.

All assets are safe for public plugin-store publication and README embedding.

| File | Purpose | Dimensions |
|---|---|---|
| `xtb-plugin-00-discovery.png` | Discovery shot: Tools tab with `verseops` filter, VerseOps tile hovered showing description + author. Use as primary store-listing hero. | 1280 × 720 |
| `xtb-plugin-01-detail.png` | Plugin open: catalog tree expanded showing BAP (22) + PPAC (165) surfaces, Form / Raw body / Description / Response tabs. Use to show the catalog at a glance. | 1280 × 710 |
| `xtb-plugin-02-endpoint.png` | Same view with Recommendations leaf selected and Description tab active. Pair with shot 01 to show the drill-down flow. | 1280 × 710 |
| `xtb-plugin-demo.mp4` | 29 s screen recording of search → tile → catalog browse. Use as embedded video on store listing where supported. | 1280 × 820 @ 24 fps |

## Re-capturing

The Sign-in row is unconditionally redacted by `tools/redact-xtb-stills.ps1`
(planned). To refresh:

1. Launch XrmToolBox portable, sign in (any tenant — captures are redacted).
2. Open VerseOps API Explorer.
3. Resize XTB window to (10, 10) 1280 × 820 (`tools\capture-window.ps1`).
4. Run the still-capture script for each of the three viewpoints.
5. Re-run the redaction script to overlay the neutral toolbar.

The MP4 was recorded with ffmpeg gdigrab at 24 fps with libx264 veryfast / crf 22.
