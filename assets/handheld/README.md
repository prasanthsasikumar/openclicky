# Handheld device models

3D models of the handheld devices the Mac app's Handheld plugin supports, one file per device
(`handheld-<device id>.glb`). They are **not in the app**: only someone who owns the device has a
use for them. The Handheld settings page offers to download the model for the connected device
from this folder (the `main` branch, checked against a SHA-256 pinned in the app) into
`~/.openclicky/handheld/<device id>.glb`.

Changing a file here changes its checksum: update `modelDownload` in the device's description
(`macos/OpenClicky/OpenClicky/Plugins/Handheld/Devices/`) in the same commit, or every download
is refused.

These models are pictures of their makers' products and belong to those makers. They are not
covered by this repository's MIT licence: `handheld-whisprompt.glb` is WhisPrompt's model of its
handheld. If you are the maker and want one changed or removed, open an issue.
