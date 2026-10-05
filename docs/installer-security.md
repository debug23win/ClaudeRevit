# Installer detection investigation

## Updated scan result

After an official Defender intelligence update to `1.459.565.0`, the exact original
v3.8.3 EXE was downloaded again from GitHub and scanned on 5 October 2026 at
20:48 UTC. It returned exit code 0 with no threats, its SHA-256 matched the published
asset below, and its signature status was `NotSigned`. The binary was not rebuilt,
modified, run, restored from quarantine or excluded from protection. The result of
detection therefore changed with the intelligence version. The exact classifier
trigger is unknown; no Microsoft analyst determination was requested or received.

If affected, update Defender intelligence and retry the official GitHub download.
Do not disable protection to work around a remaining detection. A scan is evidence
from one engine at one time and cannot establish universal safety.

## Initial detection and comparison

On 5 October 2026 Microsoft Defender blocked the published
`ClaudeRevit-Setup-v3.8.3.exe` as `Program:Win32/Contebrew.A!ml`.
The SHA-256 recorded when the GitHub asset was verified is:

```
79e18adbd79402ef9b7a5f49ce35cc1e19aaebbde0d9e1e351843afdaa84288e
```

The local Defender event reports quarantine, with intelligence `1.459.560.0`
and product `4.18.26080.4`. The detection record for this installer says it did
not execute. This is an antivirus detection, not merely a SmartScreen reputation
warning. The cause has not been established and a false positive is not confirmed.
Do not restore the quarantined executable, add an exclusion or disable protection
to install this version.

A separate custom scan of the unpacked 2025, 2026 and 2027 ZIP payloads reported
no threats with those definitions. That result does not establish installer safety
or authorize using another package to bypass the detection. The quarantined
installer could not be inspected further by reading its bytes or signature.

The same scanner/intelligence also scanned the original local installers v3.8.0,
v3.8.1 and v3.8.2: each returned no threats. All three are unsigned. This narrows
the reproduced detection to the v3.8.3 installer; lack of a signature alone does
not explain the difference. It still does not identify the offending byte pattern
or prove that the new installer is a false positive.

The Inno script in this repository installs the per-version add-in files and checks
whether Revit is running. Source review alone does not establish that every byte
of the released executable matches the intended build. Retain the exact asset
hash when requesting analysis; rebuilding does not resolve an existing detection.
The installer script, release workflow and dependency declarations are unchanged
between tags v3.8.2 and v3.8.3. Application payload code did change; the exact
classification trigger in the resulting executable remains unknown.

Microsoft's [developer procedure](https://learn.microsoft.com/en-us/defender-xdr/developer-faq)
is to submit the original file for analysis and wait for the final determination.
The [submission portal](https://www.microsoft.com/en-us/wdsi/filesubmission)
requires sign-in for developer submissions. The user chose local checks only;
no file was submitted. The updated scan result above resolved the reproduced
local download detection without requiring a submission.

## Release checks

`scripts/Check-ReleaseArtifacts.ps1` scans staged payloads, the final ZIP archives
and the original installer before a release is published. Missing/inactive Defender,
old definitions, scan errors, detections or unreadable files stop publication.
The report records source commit, time, engine/intelligence versions, hashes and
signature status. Unsigned files are reported as unsigned; signing requires a
trusted publisher certificate and is not a substitute for malware investigation.

The custom scan uses Microsoft's documented `-DisableRemediation` option so
archive contents and exclusions are handled by the scanner without changing
real-time protection or restoring files. See the
[official command reference](https://learn.microsoft.com/en-us/defender-endpoint/command-line-arguments-microsoft-defender-antivirus).
A passing scan is a time-specific result from one engine, not a safety guarantee.
