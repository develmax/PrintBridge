# <img src="docs/printbridge.png" alt="" width="30" height="30"> PrintBridge

[![Build and tests](https://github.com/develmax/PrintBridge/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/develmax/PrintBridge/actions/workflows/build.yml)
[![CodeQL](https://github.com/develmax/PrintBridge/actions/workflows/codeql.yml/badge.svg?branch=main)](https://github.com/develmax/PrintBridge/actions/workflows/codeql.yml)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/develmax/PrintBridge/badge)](https://scorecard.dev/viewer/?uri=github.com/develmax/PrintBridge)
[![Latest release](https://img.shields.io/github/v/release/develmax/PrintBridge?label=release)](https://github.com/develmax/PrintBridge/releases/latest)

PrintBridge is a Windows utility that routes RAW printing to a network printer over local Wi-Fi or Ethernet. It helps when a VPN changes Windows routing or a printer receives a new IP address. It runs in the system tray and uses your installed Windows printer driver.

[Website](https://develmax.github.io/PrintBridge/) · [Download the latest release](https://github.com/develmax/PrintBridge/releases/latest) · [Wiki](https://github.com/develmax/PrintBridge/wiki) · [Report an issue](https://github.com/develmax/PrintBridge/issues)

## Compatibility

- Windows 10 22H2 or Windows 11, with x64 and ARM64 installers.
- An IPv4 Wi-Fi or Ethernet connection, a printer that accepts RAW TCP on port 9100, and a compatible installed Windows driver.
- A device identifier such as a MAC address, serial number, or UUID so PrintBridge can confirm that it is sending jobs to the same printer after an address change.
- Physical printing has been verified on a Canon LBP631Cw with a VPN active, including a successful print after sleep. Other models and ARM64 hardware still need validation.
- IPP, IPPS, LPR, and WSD help with discovery. The bridge sends jobs over RAW TCP. VPN policies that block local access can prevent discovery or printing.

## Getting started

1. Download the installer for your architecture. The installer includes the .NET runtime. Choose your preferred language in EXE setup or in PrintBridge under **Settings → General → Language**.
2. Turn on the printer, connect the PC to the same local network, and open PrintBridge. Use **Automatic** to search connected Wi-Fi and Ethernet adapters, or select an adapter manually. Select the print queue and verify the physical printer.
3. Choose **Create a separate queue** to keep the original queue available or **Switch the selected queue** to keep using its existing name. Approve the Windows request to change the queue or create its port.
4. Print a test page and check the physical output. For normal printing, select the print queue you connected in the wizard.

Closing the window sends PrintBridge to the tray. You can enable automatic startup under **Settings**. To remove the integration, choose **Restore original port**. The EXE uninstaller also offers to clean up the queue integration before removing the app. A per-user upgrade preserves the profile and any language saved in Settings.

PrintBridge and EXE setup support English, Dutch, French, German, Italian, Japanese, Korean, Polish, Brazilian Portuguese, Russian, Serbian (Cyrillic), Simplified Chinese, Spanish, Turkish, and Ukrainian. The MSI setup runs in English. The installed app offers the same language options with either installer.

Releases include a direct EXE download, a ZIP containing the same EXE, and an MSI for Windows Installer deployments. Use the same installer type for upgrades. To switch between EXE and MSI, restore the original port, uninstall PrintBridge, and then install the other package. Restore the original port before uninstalling the MSI.

PrintBridge checks shortly after launch and every 12 hours while running, and installs newer stable GitHub releases in the background. It verifies the package against GitHub's SHA-256 digest, defers installation while printing or configuration is in progress, and returns to the tray after setup. Automatic updates are enabled by default. To turn them off, clear **Automatically check for and install updates** in **Settings**. **Check for updates now** checks on demand and queues a compatible release for installation, asking for approval if automatic updates are off. PrintBridge shows a tray notification when it queues an update.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| The printer is missing from discovery | Check that it is on, reachable from the selected local network, and accepts RAW TCP on port 9100. |
| The bridge cannot connect while a VPN is active | Use **Diagnostics** to compare the Windows route with the selected adapter's route. Check whether the VPN allows local LAN traffic. |
| A job disappears without printing | Check **Last connection error** under **Printer connection** and check the printer's own job log to confirm delivery. |
| Restore or uninstall reports a conflict | Wait for pending jobs to finish and check whether another queue uses the bridge port. Keep the saved profile until restoration finishes. |

The default monitor interval is five minutes while idle. A print job starts connecting immediately and the bridge retries if the printer is temporarily unavailable. The bridge listens on loopback. It does not change VPN policies or send diagnostics to an external service. The diagnostic export can omit network and queue identifiers.

Use **Report a bug** to preview, copy, or save a report with network and queue identifiers removed. **Open GitHub draft** fills in the app version and prompts for reproduction steps. You can paste the report into the public issue and submit it from GitHub.

## Build and contribute

Install the .NET 10 SDK and Inno Setup 6.7 or later on Windows. Run these commands from the repository root.

```powershell
./scripts/build.ps1 -Configuration Release
./scripts/build-installer.ps1 -Runtime win-x64
./scripts/build-installer.ps1 -Runtime win-arm64
```

To build all release formats, install the WiX 6.0.2 .NET tool and run `./scripts/build-release-packages.ps1` once per architecture.

See the [contribution guide](CONTRIBUTING.md), [roadmap](ROADMAP.md), [acceptance checklist](docs/acceptance.md), and [changelog](CHANGELOG.md). See [release signature verification](docs/release-signing.md) for signed packages and maintainer instructions.

## Support development

If PrintBridge helps you keep printing, you can support its maintenance, testing, and documentation. Donations are optional.

[Patreon](https://www.patreon.com/develmax) · [Boosty](https://boosty.to/develmax/donate) · [YooMoney](https://yoomoney.ru/to/4100119529133322) · [PayPal](https://paypal.me/develmax)

## Author and license

Created by [develmax](https://github.com/develmax). The [PrintBridge Source License](LICENSE) permits free use, including business use, and allows modification and free redistribution with attribution. Selling copies requires separate written permission. See [third-party notices](THIRD-PARTY-NOTICES.md) for bundled components.
