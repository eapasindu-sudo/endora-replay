# lib

Optional, machine-generated interop assemblies. Nothing here is committed.

- `Interop.BMDSwitcherAPI.dll`: created by `tools/Generate-AtemInterop.ps1` from the Blackmagic ATEM
  Switchers SDK that ships with ATEM Software Control. When present, the **Blackmagic ATEM SDK** control
  method is compiled in (`BMD_SDK`). When absent, Endora uses the LibAtem network protocol and reports the
  SDK option as unavailable.

Do not redistribute Blackmagic files until their SDK licence has been checked (see docs/COMPATIBILITY.md).
