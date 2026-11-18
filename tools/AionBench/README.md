# AionBench

Runs BrainSpike's 20 planner cases on **Aion Instruct Preview**, Windows' on-device NPU model (the successor to Phi
Silica). Not part of the app build. Preview licence: for developer evaluation only, not for shipping.

Setup (once):

1. Download the framework package from the
   [Aion Instruct Preview sample releases](https://github.com/microsoft/Aion-Instruct-Preview-Sample/releases): the
   ARM64 `.msix.001`–`.003` parts and their `.parts.json`. Join them with the sample's `scripts/ReleaseParts.ps1`
   (`Join-ReleaseParts`), which checks every SHA-256, then `Add-AppxPackage` the joined `.msix`. No Developer Mode or
   admin rights are needed.
2. Put `AionInstructPreview.Text.Framework.1.1.1.nupkg` from the same release in `nuget-local\`.

```powershell
dotnet run -- -v    # loads the model (first run compiles for the NPU), warms up, runs the 20 cases
```

`AionFramework.cs` adds the framework package to this unpackaged process at run time. The build uses Windows'
own metadata (`CsWinRTWindowsMetadata=local`), so the Windows SDK isn't needed.
