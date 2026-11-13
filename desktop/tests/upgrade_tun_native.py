"""Read native upgrade-fixture identity without loading Wintun or its cleanup worker.

The layouts/properties match the core's IPHelper/SetupAPI ownership check. Only
the final fixture cleanup may remove an exact, previously captured devnode.
That method also requires the captured creator to have exited; alias-only
cleanup, OpenAdapter and CreateAdapter are deliberately absent here.
"""

from __future__ import annotations

import ctypes
import hashlib
import os
from pathlib import Path
import subprocess
import sys
import uuid

from windows_smoke import require, wait_until


DWORD = ctypes.c_uint32
NETWORK_CLASS = "{4D36E972-E325-11CE-BFC1-08002BE10318}"


class GUID(ctypes.Structure):
    _fields_ = [("data1", DWORD), ("data2", ctypes.c_uint16),
                ("data3", ctypes.c_uint16), ("data4", ctypes.c_ubyte * 8)]

    @classmethod
    def parse(cls, text: str):
        return cls.from_buffer_copy(uuid.UUID(text.strip("{}")).bytes_le)

    def text(self) -> str:
        return "{" + str(uuid.UUID(bytes_le=bytes(self))).upper() + "}"


class DeviceInfo(ctypes.Structure):
    _fields_ = [("size", DWORD), ("class_guid", GUID),
                ("devinst", DWORD), ("reserved", ctypes.c_size_t)]


class PropertyKey(ctypes.Structure):
    _fields_ = [("guid", GUID), ("pid", DWORD)]


class InterfaceRow(ctypes.Structure):
    # Fixed-width fields keep the ABI assertions useful on the Linux lint job.
    # The unused suffix contains MTU/status/counters; sizeof(MIB_IF_ROW2)=1352.
    _fields_ = [("luid", ctypes.c_uint64), ("index", DWORD), ("guid", GUID),
                ("alias", ctypes.c_uint16 * 257),
                ("description", ctypes.c_uint16 * 257),
                ("unused", ctypes.c_ubyte * 296)]


class InterfaceTable(ctypes.Structure):
    _fields_ = [("count", DWORD), ("rows", InterfaceRow * 1)]


def expected_guid(name: str, *, exclusive: bool) -> str:
    namespace = "mihomo-optimized/wintun-exclusive/v1\0" if exclusive else "wintun"
    value = hashlib.md5((namespace + name).encode("utf-8"), usedforsecurity=False).digest()
    return "{" + str(uuid.UUID(bytes_le=value)).upper() + "}"


def wide_string(value) -> str:
    return bytes(value).decode("utf-16-le").split("\0", 1)[0]


def same_identity(first: dict, second: dict) -> bool:
    keys = ("guid", "name", "instanceId", "classGuid", "tunnelType", "friendlyName", "hardwareIds")
    if any(first.get(key) != second.get(key) for key in keys):
        return False
    # A phantom can outlive its IPHelper row. Any row that still exists must
    # retain the original LUID/index/alias, never a renamed or adopted adapter.
    return second.get("interface") is None or second["interface"] == first.get("interface")


class NativeTunIdentity:
    def __init__(self) -> None:
        require(sys.platform == "win32" and ctypes.sizeof(ctypes.c_void_p) == 8,
                "upgrade_native_identity_requires_windows_x64")
        require(ctypes.sizeof(GUID) == 16 and ctypes.sizeof(DeviceInfo) == 32
                and ctypes.sizeof(InterfaceRow) == 1352 and InterfaceTable.rows.offset == 8
                and InterfaceRow.guid.offset == 12 and InterfaceRow.alias.offset == 28,
                "upgrade_native_identity_abi_mismatch")
        self.ip = ctypes.WinDLL("iphlpapi", use_last_error=True)
        self.setup = ctypes.WinDLL("setupapi", use_last_error=True)
        self.cm = ctypes.WinDLL("cfgmgr32", use_last_error=True)
        pointer = ctypes.c_void_p
        device = ctypes.POINTER(DeviceInfo)
        self.ip.GetIfTable2Ex.argtypes = [ctypes.c_int, ctypes.POINTER(pointer)]
        self.ip.GetIfTable2Ex.restype = DWORD
        self.ip.FreeMibTable.argtypes = [pointer]
        self.ip.FreeMibTable.restype = None
        self.setup.SetupDiGetClassDevsW.argtypes = [ctypes.POINTER(GUID), ctypes.c_wchar_p, pointer, DWORD]
        self.setup.SetupDiGetClassDevsW.restype = pointer
        self.setup.SetupDiDestroyDeviceInfoList.argtypes = [pointer]
        self.setup.SetupDiDestroyDeviceInfoList.restype = ctypes.c_int
        self.setup.SetupDiEnumDeviceInfo.argtypes = [pointer, DWORD, device]
        self.setup.SetupDiEnumDeviceInfo.restype = ctypes.c_int
        self.setup.SetupDiGetDeviceInstanceIdW.argtypes = [pointer, device, pointer, DWORD, ctypes.POINTER(DWORD)]
        self.setup.SetupDiGetDeviceInstanceIdW.restype = ctypes.c_int
        self.setup.SetupDiGetDeviceRegistryPropertyW.argtypes = [pointer, device, DWORD,
            ctypes.POINTER(DWORD), pointer, DWORD, ctypes.POINTER(DWORD)]
        self.setup.SetupDiGetDeviceRegistryPropertyW.restype = ctypes.c_int
        self.setup.SetupDiGetDevicePropertyW.argtypes = [pointer, device, ctypes.POINTER(PropertyKey),
            ctypes.POINTER(DWORD), pointer, DWORD, ctypes.POINTER(DWORD), DWORD]
        self.setup.SetupDiGetDevicePropertyW.restype = ctypes.c_int
        self.setup.SetupDiOpenDevRegKey.argtypes = [pointer, device, DWORD, DWORD, DWORD, DWORD]
        self.setup.SetupDiOpenDevRegKey.restype = pointer
        self.cm.CM_Get_DevNode_Status.argtypes = [ctypes.POINTER(DWORD), ctypes.POINTER(DWORD), DWORD, DWORD]
        self.cm.CM_Get_DevNode_Status.restype = DWORD

    @staticmethod
    def check(result, code: str) -> None:
        require(result, code + "_" + str(ctypes.get_last_error()))

    def interfaces(self, name: str | None = None) -> list[dict]:
        table = ctypes.c_void_p()
        code = self.ip.GetIfTable2Ex(0, ctypes.byref(table))
        require(code == 0 and table.value, "upgrade_interface_table_failed_" + str(code))
        try:
            count = ctypes.cast(table, ctypes.POINTER(DWORD)).contents.value
            require(count <= 16384, "upgrade_interface_table_too_large")
            rows = (InterfaceRow * count).from_address(table.value + InterfaceTable.rows.offset)
            return [{"name": wide_string(row.alias), "guid": row.guid.text(),
                     "luid": str(row.luid), "index": row.index}
                    for row in rows if name is None or wide_string(row.alias).casefold() == name.casefold()]
        finally:
            self.ip.FreeMibTable(table)

    def property(self, devices, device, property_id: int) -> tuple[int, list[str]]:
        kind, needed = DWORD(), DWORD()
        buffer = (ctypes.c_ubyte * 8192)()
        self.check(self.setup.SetupDiGetDeviceRegistryPropertyW(devices, ctypes.byref(device), property_id,
            ctypes.byref(kind), buffer, len(buffer), ctypes.byref(needed)), "upgrade_device_property_failed")
        require(0 < needed.value <= len(buffer) and needed.value % 2 == 0,
                "upgrade_device_property_invalid")
        return kind.value, bytes(buffer[:needed.value]).decode("utf-16-le").rstrip("\0").split("\0")

    def wintun_name(self, devices, device, *, required: bool) -> str | None:
        key = PropertyKey(GUID.parse("{3361C968-2F2E-4660-B47E-699CDC4C32B9}"), 3)
        raw = (ctypes.c_ubyte * 4096)()
        kind, needed = DWORD(), DWORD()
        result = self.setup.SetupDiGetDevicePropertyW(devices, ctypes.byref(device), ctypes.byref(key),
            ctypes.byref(kind), raw, len(raw), ctypes.byref(needed), 0)
        if not result and not required:
            return None
        self.check(result, "upgrade_wintun_name_failed")
        valid = kind.value == 0x12 and 0 < needed.value <= len(raw) and needed.value % 2 == 0
        if not valid and not required:
            return None
        require(valid, "upgrade_wintun_name_invalid")
        return bytes(raw[:needed.value]).decode("utf-16-le").rstrip("\0")

    def query(self, name: str, guid: str) -> dict | None:
        import winreg

        guid = GUID.parse(guid).text()
        network_class = GUID.parse(NETWORK_CLASS)
        devices = self.setup.SetupDiGetClassDevsW(ctypes.byref(network_class), None, None, 0)
        require(devices not in (None, ctypes.c_void_p(-1).value), "upgrade_device_list_failed")
        found = None
        try:
            for index in range(16384):
                device = DeviceInfo(size=ctypes.sizeof(DeviceInfo))
                if not self.setup.SetupDiEnumDeviceInfo(devices, index, ctypes.byref(device)):
                    self.check(ctypes.get_last_error() == 259, "upgrade_device_enumeration_failed")
                    return found
                handle = self.setup.SetupDiOpenDevRegKey(devices, ctypes.byref(device), 1, 0, 2, winreg.KEY_QUERY_VALUE)
                actual_guid = None
                if handle in (None, ctypes.c_void_p(-1).value):
                    registry_error = ctypes.get_last_error()
                else:
                    registry_error = None
                    try:
                        value, kind = winreg.QueryValueEx(handle, "NetCfgInstanceId")
                        if kind == winreg.REG_SZ and isinstance(value, str):
                            actual_guid = GUID.parse(value).text()
                    except (OSError, ValueError) as error:
                        registry_error = getattr(error, "winerror", None)
                    finally:
                        winreg.CloseKey(handle)
                if actual_guid is None:
                    # Unrelated, uninstalled network devices can have no driver
                    # key. A named Wintun candidate with unreadable identity is
                    # an error, never evidence that this fixture name is free.
                    candidate_name = self.wintun_name(devices, device, required=False)
                    require(candidate_name is None or candidate_name.casefold() != name.casefold(),
                            "upgrade_named_device_registry_unreadable_" + str(registry_error))
                    continue
                if actual_guid != guid:
                    continue
                require(found is None, "upgrade_duplicate_device_network_guid")
                buffer = ctypes.create_unicode_buffer(1024)
                needed = DWORD()
                self.check(self.setup.SetupDiGetDeviceInstanceIdW(devices, ctypes.byref(device), buffer,
                    len(buffer), ctypes.byref(needed)), "upgrade_instance_id_failed")
                actual_name = self.wintun_name(devices, device, required=True)
                description_kind, description = self.property(devices, device, 0)
                friendly_kind, friendly = self.property(devices, device, 12)
                hardware_kind, hardware = self.property(devices, device, 1)
                require(description_kind == 1 and len(description) == 1 and description[0].endswith(" Tunnel")
                        and friendly_kind == 1 and len(friendly) == 1 and hardware_kind == 7,
                        "upgrade_wintun_markers_invalid")
                status, problem = DWORD(), DWORD()
                result = self.cm.CM_Get_DevNode_Status(ctypes.byref(status), ctypes.byref(problem), device.devinst, 0)
                require(result in (0, 13), "upgrade_devnode_status_unknown_" + str(result))
                # Look up the GUID, not only the old alias, so an unexpected
                # rename cannot appear to be an absent IPHelper row.
                rows = [row for row in self.interfaces() if row["guid"] == guid]
                require(len(rows) <= 1, "upgrade_interface_identity_ambiguous")
                found = {"name": actual_name, "guid": guid, "instanceId": buffer.value.upper(),
                    "classGuid": device.class_guid.text(), "tunnelType": description[0][:-len(" Tunnel")],
                    "friendlyName": friendly[0], "hardwareIds": hardware,
                    "phantom": result == 13, "cmResult": result, "cmStatus": status.value,
                    "cmProblem": problem.value, "interface": rows[0] if rows else None}
            raise RuntimeError("upgrade_device_enumeration_limit")
        finally:
            self.setup.SetupDiDestroyDeviceInfoList(devices)

    def capture(self, name: str, *, exclusive: bool) -> dict:
        guid = expected_guid(name, exclusive=exclusive)
        actual = self.query(name, guid)
        tunnel_type = "Mihomo Optimized" if exclusive else "Meta"
        require(actual and actual["name"] == name and actual["classGuid"] == NETWORK_CLASS
                and actual["tunnelType"] == tunnel_type and actual["friendlyName"] == tunnel_type + " Tunnel"
                and [value.casefold() for value in actual["hardwareIds"]] == ["wintun"]
                and not actual["phantom"] and actual["interface"]
                and actual["interface"]["name"] == name and actual["interface"]["guid"] == guid,
                "upgrade_live_adapter_identity_mismatch")
        return actual

    def require_unused(self, name: str) -> None:
        require(not self.interfaces(name)
                and self.query(name, expected_guid(name, exclusive=False)) is None
                and self.query(name, expected_guid(name, exclusive=True)) is None,
                "upgrade_fixture_adapter_already_exists")

    def remove_fixture(self, expected: dict, creator_handle, processes) -> str:
        require(os.environ.get("GITHUB_ACTIONS") == "true"
                and creator_handle and processes.api.WaitForSingleObject(creator_handle, 0) == 0,
                "upgrade_cleanup_creator_exit_unconfirmed")
        actual = self.query(expected["name"], expected["guid"])
        if actual is None:
            wait_until(lambda: all(row["guid"] != expected["guid"] for row in self.interfaces()),
                       "upgrade_removed_device_interface_remains", 15)
            return "already_absent"
        require(same_identity(expected, actual), "upgrade_cleanup_adapter_identity_changed")
        # The caller captured this exact devnode while its verified child was
        # alive, after an absence check. Never derive deletion from alias alone.
        result = subprocess.run([str(Path(os.environ["SystemRoot"]) / "System32" / "pnputil.exe"),
            "/remove-device", actual["instanceId"]], stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30,
            creationflags=subprocess.CREATE_NO_WINDOW)
        require(result.returncode == 0, "upgrade_fixture_device_remove_failed")
        wait_until(lambda: self.query(expected["name"], expected["guid"]) is None,
                   "upgrade_fixture_device_remove_timeout", 15)
        return "exact_captured_instance_removed"
