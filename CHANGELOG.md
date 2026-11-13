# Changelog

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
