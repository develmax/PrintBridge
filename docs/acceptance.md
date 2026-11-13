# Acceptance checklist

## Automated checks

Run `dotnet test PrintBridge.sln -c Release` on Windows. Tests cover printer identity, address changes, queue rollback, binary relay, half-close, occupied ports, and diagnostic logging. To opt in to the temporary Windows spooler test, set `PRINTBRIDGE_TEST_SPOOLER=1` and provide an installed RAW printer driver.

## Hardware and installer checks

- Install, upgrade, and uninstall under a regular Windows account. Check that existing profiles survive upgrades and the original port is restored when requested.
- Test both installer languages. Check the app's initial language, switch languages in Settings, restart, and verify the saved selection.
- Print a Windows test page through each queue mode. Check an actual sheet, including after printer sleep and a DHCP address change.
- Repeat with a VPN that allows local traffic. Verify the diagnostic state with a VPN that blocks local traffic.
- Check the tray, automatic startup, and diagnostics on Windows 10 and Windows 11. Run the ARM64 installer on native hardware.

## Observed results

Earlier x64 builds were exercised on Windows 10 with install, upgrade, and uninstall. Temporary Windows queues were created and restored in both modes. Physical printing succeeded on a Canon LBP631Cw with a VPN active, including one print after sleep. Other models, native ARM64, Windows 11, and live DHCP changes remain unverified.
