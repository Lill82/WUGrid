# Third-party notices

WUGrid is MIT-licensed (see [`LICENSE`](LICENSE)), except for the third-party material below, which keeps its own
licence.

## Error code descriptions — Microsoft Learn (CC BY 4.0)

`src/WUGrid.Core/Errors/error-codes.microsoft.tsv` contains error code descriptions and mitigation steps taken from the
following Microsoft Learn pages, © Microsoft Corporation, licensed under
[Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/):

- [Windows Update error code list by component](https://learn.microsoft.com/windows/deployment/update/windows-update-error-reference)
- [Common Windows Update errors](https://learn.microsoft.com/troubleshoot/windows-client/deployment/common-windows-update-errors)
- [Fix Windows Update corruptions and installation failures](https://learn.microsoft.com/troubleshoot/windows-server/deployment/fix-windows-update-errors)
- [.NET Framework 3.5 deployment errors and resolution steps](https://learn.microsoft.com/windows-hardware/manufacture/desktop/net-framework-35-deployment-errors-and-resolution-steps)
- [WMI Error Constants](https://learn.microsoft.com/windows/win32/wmisdk/wmi-error-constants)

Changes: extracted from the pages' tables by `tools/update_error_catalog.py`; whitespace normalised; where the common
errors page writes "NAME; message", the message is moved into the description; entries for the same code and name from
several pages are merged. Each entry records which page(s) it came from, and WUGrid shows that source with it.

`src/WUGrid.Core/Errors/error-codes.wugrid.tsv` is WUGrid's own text.
