# Presets

Encode presets anyone can load from the app: **Encode → Online presets → Load** reads every `.json`
file in this folder (on `main`) and offers each preset for download.

A file holds one preset, a JSON array of presets, or a bundle:

```json
{ "format": "videoquality-presets", "version": 1, "presets": [ { "name": "…" } ] }
```

The easiest way to make one is **Export…** on the Encode page. Fields left out take the defaults of
`EncodePreset` (VideoQuality.Core/Encode/EncodePreset.cs). To share yours, open a pull request
adding a file here, or point the app at your own repository: `owner/repo/folder@branch`, a GitHub
folder link, or the URL of a single `.json` file.
