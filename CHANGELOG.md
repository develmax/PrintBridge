# Changelog

## 1.3.0

- Redesigned the main window with a clear printer status and expandable connection and routing details.
- Added links to Windows Printer Properties and the print queue. The queue card shows the number of jobs reported by Windows.
- Expanded the application and EXE installer to 15 languages.

## 1.2.8

- Added printer discovery and printing through physical Ethernet adapters alongside Wi-Fi.
- Added automatic adapter selection with preference for the last confirmed path and a manual adapter override.
- Renamed the connection panel to **Local network** and identified the adapter type in routing diagnostics.

## 1.2.7

- Verify the installed executable before starting the print bridge in signed releases. A failed check stops startup and records the error.

## 1.2.6

- Added the PrintBridge icon to the window header and overlaid the connection indicator on it.
- Signed Windows packages with the PrintBridge publisher key and made the updater require a signed release manifest.

## 1.2.5

- Smoothed toolbar and menu icons and added a connection indicator beside the PrintBridge title.
- Displayed Wi-Fi and Windows routes on separate lines, followed by a list of detected VPN interfaces.

## 1.2.4

- Reduced idle printer checks to once every five minutes by default. Incoming jobs still connect immediately and retry if needed.
- Moved common actions above the printer queue, with system icons and a **More** menu for maintenance and support.

## 1.2.3

- Suppressed initial printer status notifications when starting with Windows or restarting after an update.

## 1.2.2

- Added **Check for updates now** in Settings. PrintBridge announces queued updates and asks for approval when automatic updates are disabled.

## 1.2.1

- Added **Report a bug**, with a redacted diagnostic preview, copy and save actions, and a prefilled GitHub issue draft.
- Enabled automatic updates by default for profiles without a saved update preference.

## 1.2.0

- Added optional automatic updates from stable GitHub releases. PrintBridge verifies the installer digest, waits for an idle queue and bridge, and restarts after the update.
- Added versioned EXE, MSI, and ZIP downloads for x64 and ARM64. Each ZIP contains the matching EXE installer.

## 1.1.0

- Added English and Russian app languages, with English selected by default and a language selector in Settings.
- The installer offers both languages and supplies the initial app language. Upgrades preserve the language saved in Settings.
- Added an About dialog with the version, author, repository, and license.
- Added the public source license, English documentation, and automated Windows release builds.
- Includes the local 1.0.x improvements for intermittent printer connections, resizable diagnostics, and short names for newly created printer ports.
