# Third-party notices

This project is released under the [MIT License](LICENSE). It includes or depends on the following third-party components.
The dependency list below was checked against `dotnet list package --include-transitive` and the package metadata (`.nuspec`) of the pinned versions in `Directory.Packages.props` / `packages.lock.json`; update it when those change.

## Embedded in the executable

| Component | Files | License | Source |
|---|---|---|---|
| PP-OCRv6 text detection and recognition models (small) | `src/Pocofarm/Models/PP-OCRv6_det_small.onnx`, `src/Pocofarm/Models/PP-OCRv6_rec_small.onnx` | Apache License 2.0 (as stated by the PaddleOCR project) | [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) |
| UI icon templates | `src/Pocofarm/Models/ui_stream_camera.png`, `src/Pocofarm/Models/ui_tab_camera.png` | Not recorded (they look like crops of the VRChat world UI; see the trademark note below) | Not recorded |

The ONNX files carry `p2o.pd_op.*` node names, i.e. they were converted from PaddlePaddle with Paddle2ONNX. The exact upstream release, the converter version, and where the converted files were obtained from are **not recorded in this repository and could not be verified**. If you know the origin, please document it here. The recognizer's character dictionary is read from the model's own metadata.

## NuGet packages (restored at build time; native libraries are bundled into the published executable)

| Package | Version | License | Notes |
|---|---|---|---|
| Microsoft.ML.OnnxRuntime | 1.31.1 | MIT | Ships `onnxruntime.dll` and `onnxruntime_providers_shared.dll` (win-x64), extracted next to the executable on first start. See also the package's `ThirdPartyNotices.txt`. |
| Microsoft.ML.OnnxRuntime.Managed | 1.31.1 | MIT | Transitive |
| System.Numerics.Tensors | 9.0.0 | MIT | Transitive (OnnxRuntime) |
| System.Drawing.Common | 10.0.14 | MIT | |
| Microsoft.Win32.SystemEvents | 10.0.14 | MIT | Transitive (System.Drawing.Common) |
| Vortice.Direct3D11 | 3.8.3 | MIT | |
| Vortice.DXGI | 3.8.3 | MIT | |
| Vortice.DirectX | 3.8.3 | MIT | Transitive |
| Vortice.Mathematics | 2.1.0 | MIT | Transitive |
| SharpGen.Runtime / SharpGen.Runtime.COM | 2.4.2-beta | MIT | Transitive |

The .NET runtime itself is included in the self-contained executable (MIT; see <https://github.com/dotnet/runtime>).

## Development-only (not redistributed)

xunit.v3 (Apache-2.0), xunit.runner.visualstudio (Apache-2.0), Microsoft.NET.Test.Sdk (MIT), coverlet.collector (MIT). Licenses are taken from the respective projects and were not re-verified against the package metadata for this notice.

## Trademarks

"VRChat" and the world "Pocopoco Farm" (its title is written in Japanese in the game) belong to their respective owners. This project is unofficial and not affiliated with them.
