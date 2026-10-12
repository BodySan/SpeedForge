# Development

Use Windows x64 and .NET SDK 10. Build the portable application with `./build-windows.ps1`.

Run `./tests/run-sandbox.ps1 -NoBuild` after changing measurements or UI. This starts actual HTTP/iPerf servers on loopback, executes parser and measurement regressions, simulates directed failures, and checks the published executable. The script stops only the processes it started. Results are saved in `sandbox-output` and excluded from Git.

Public server audits are opt-in: `--audit-catalog <directory>` and `--audit-catalog <directory> --udp`. Do not make the regular test suite depend on a public server being available.

`--audit-beeline <directory>` checks the eight supplied load profiles and reverse against `speedcheck.gldn.net`, using three seconds and a total 10 Mbit/s cap. It records missing ACKs as unconfirmed rather than reporting the sender's offered load as measured throughput.

`python static_audit.py` checks XML, resources, catalog integrity, event handlers, and basic source structure. It uses only the Python standard library and does not replace compilation or runtime tests.

The icon source is `assets/SpeedForge.svg`. On Windows, run `./assets/build-icon.ps1` to regenerate the PNG and the ICO with ten sizes. Keep the vector and rendering geometry consistent and inspect `assets/Icon-preview.png` at 16–256 pixels.

Original SpeedForge code and artwork are MIT licensed. Keep third-party license notices and source archives intact. Do not commit personal diagnostics, credentials, private keys, or local trusted-tool settings.
