# PrintBridge roadmap

These are proposed features. Use [GitHub Issues](https://github.com/develmax/PrintBridge/issues) to share use cases or discuss implementation.

## Multiple printers

Manage several printers from one PrintBridge instance. Each printer would have its own Windows queue, local bridge port, device identity, and connection status. An upgrade would import the current printer as the first profile.

## Remote printing

Allow authorized users to submit jobs to a printer on another network. The design should use authenticated, encrypted transport, give owners control over who can print, and keep the printer's RAW port on the local network.

## In-app error reporting

Let users review and submit an error report from PrintBridge. Start with a redacted diagnostic report, show exactly what will be sent, and require confirmation before sending.
