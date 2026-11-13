# Contributing to PrintBridge

Use [GitHub Issues](https://github.com/develmax/PrintBridge/issues) for bug reports, compatibility results, and feature proposals. Include the PrintBridge version, Windows version and architecture, printer model, driver, and steps to reproduce. For network or VPN issues, describe the topology and export diagnostics with private details removed. Keep document contents, credentials, and private addresses out of public reports.

## Submit a pull request

1. Fork the repository and create a branch from `main`.
2. Make a focused change. For networking, queue, or installer changes, include the failure and recovery paths in your checks.
3. Run `./scripts/build.ps1 -Configuration Release` on Windows with .NET 10. If the change affects installation, build the installer and describe what you tested.
4. Open a pull request against `main`. Describe the user-visible change, link the issue if applicable, and report test results. Include screenshots for UI changes and identify any hardware behavior you could not verify.

Keep English strings in source. Add or update Russian translations in `src/PrintBridge.Core/Localization.ru.json` and installer strings in `installer/PrintBridge.iss` when applicable.

## Contribution terms

Submit code you have the right to contribute. By submitting a pull request, you agree to license your contribution to recipients under the [PrintBridge Source License](LICENSE). You also grant the PrintBridge maintainer a perpetual, worldwide, nonexclusive, royalty-free license to use, modify, distribute, sell, and sublicense the contribution as part of PrintBridge under separate terms. You retain ownership of your contribution. Identify third-party code and its license in the pull request.
