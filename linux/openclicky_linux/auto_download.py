"""The offline models download on their own once setup is done (the Mac's `startAutomaticModelDownloads`):
Parakeet (dictation) and Kokoro (the voice), one after the other, in the background, a little after
launch. Only on a network that is not metered, only with 2 GB free, and never again for a model the
person skipped in setup or removed in Settings (`preferences.py`, "declined").
"""

from __future__ import annotations

import shutil
import threading
from typing import Callable

from . import applog, offline, preferences

DECLINED_KEY = "offlineModelsDeclined"  # a list of model folder names
MINIMUM_FREE_BYTES = 2 * 1024 ** 3
DELAY_SECONDS = 8.0


def declined() -> set[str]:
    value = preferences.get(DECLINED_KEY, [])
    return set(value) if isinstance(value, list) else set()


def set_declined(asset: offline.ModelAsset, is_declined: bool) -> None:
    names = declined() - {asset.folder_name} | ({asset.folder_name} if is_declined else set())
    preferences.set_values(**{DECLINED_KEY: sorted(names) or None})


def network_is_metered() -> bool:
    try:
        from gi.repository import Gio

        return Gio.NetworkMonitor.get_default().get_network_metered()
    except Exception:  # noqa: BLE001 - not knowing is treated as not metered, as on the Mac
        return False


def models_to_fetch(assets: list[offline.ModelAsset]) -> list[offline.ModelAsset]:
    """The models that should download now, or none when the conditions are not met."""
    wanted = [asset for asset in assets if not offline.is_installed(asset) and asset.folder_name not in declined()]
    if not wanted or not offline.is_engine_available() or network_is_metered():
        return []
    try:
        directory = offline.models_directory()
        directory.mkdir(parents=True, exist_ok=True)
        if shutil.disk_usage(directory).free < MINIMUM_FREE_BYTES:
            return []
    except OSError:
        return []
    return wanted


def start(assets: list[offline.ModelAsset], on_finished: Callable[[offline.ModelAsset], None] = lambda _asset: None,
          delay_seconds: float = DELAY_SECONDS) -> None:
    """After `delay_seconds`, each model still wanted downloads in turn; failures are only logged."""
    def run() -> None:
        for asset in models_to_fetch(assets):
            applog.append(f"models: downloading {asset.folder_name} on its own")
            try:
                offline.download(asset)
            except (offline.OfflineEngineError, OSError) as failure:
                applog.append(f"models: {asset.folder_name} did not download ({failure})")
                return
            on_finished(asset)

    timer = threading.Timer(delay_seconds, run)
    timer.daemon = True
    timer.start()
