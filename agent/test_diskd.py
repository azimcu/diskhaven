"""Tests for the disk agent's safety rules, with made-up lsblk output (no real disk is touched).

Run: python3 -m unittest discover -s agent
"""
import importlib.util, json, os, sys, tempfile, unittest
from unittest import mock

HERE = os.path.dirname(os.path.abspath(__file__))


def load_agent(cfg):
    tmp = tempfile.mkdtemp()
    path = os.path.join(tmp, "cfg.json")
    cfg = {"stateFile": os.path.join(tmp, "known.json"), "fstab": os.path.join(tmp, "fstab"), **cfg}
    with open(path, "w") as f:
        json.dump(cfg, f)
    old = sys.argv
    sys.argv = ["diskd", path]
    try:
        spec = importlib.util.spec_from_file_location("diskd_under_test", os.path.join(HERE, "diskhaven-diskd.py"))
        m = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(m)
    finally:
        sys.argv = old
    return m, cfg


def write(path, text):
    with open(path, "w") as f:
        f.write(text)


def part(name, uuid, fstype="ext4", mounts=None, label="", partuuid="", typ="part"):
    return {"name": name, "path": f"/dev/{name}", "type": typ, "size": 1000, "fstype": fstype, "label": label,
            "uuid": uuid, "partuuid": partuuid, "mountpoints": mounts or [None], "tran": None, "children": None}


def disk(name, tran, serial, children, model="Disk"):
    return {"name": name, "path": f"/dev/{name}", "type": "disk", "size": 4000, "model": model, "serial": serial,
            "tran": tran, "fstype": None, "uuid": None, "mountpoints": [None], "children": children}


ROOT = "/srv/diskhaven/disks"


def world():
    return [
        disk("nvme0n1", "nvme", "SSD1", [part("nvme0n1p1", "EFI-1", "vfat", ["/boot/efi"]),
                                        part("nvme0n1p3", "lvm-1", "LVM2_member")]),
        # immich disk: in fstab with nofail, NOT mounted right now (did not come up at boot)
        disk("sdb", "sata", "IMMICH", [part("sdb1", "immich-uuid")]),
        # backup disk mounted by the system
        disk("sda", "sata", "YEDEK", [part("sda1", "yedek-uuid", mounts=["/mnt/yedek"])]),
        # the new 4 TB: free, not in fstab
        disk("sdc", "sata", "NEW4TB", [part("sdc1", "arsiv-uuid", label="arsiv")]),
        # a USB disk mounted by DiskHaven itself
        disk("sdd", "usb", "USB1", [part("sdd1", "usb-uuid", "exfat", [f"{ROOT}/usb-uuid"])]),
        # a disk named in fstab by LABEL
        disk("sde", "sata", "LBL", [part("sde1", "lbl-uuid", label="medya")]),
        # protected by serial
        disk("sdf", "usb", "PROT", [part("sdf1", "prot-uuid", "ntfs")]),
        # a disk with an unsupported filesystem only
        disk("sdg", "sata", "ODD", [part("sdg1", "zfs-uuid", "zfs_member")]),
        {"name": "zram0", "path": "/dev/zram0", "type": "disk", "tran": None, "serial": None, "children": None},
    ]


FSTAB = """
UUID=immich-uuid /mnt/immich ext4 defaults,nofail 0 2
LABEL=medya /mnt/medya ext4 defaults,nofail 0 2
/dev/disk/by-uuid/EFI-1 /boot/efi vfat defaults 0 1
# UUID=arsiv-uuid /mnt/arsiv ext4 defaults 0 2   (commented out: must not count)
"""


class SafetyTests(unittest.TestCase):
    def setUp(self):
        self.m, self.cfg = load_agent({"mountRoot": ROOT, "usb": True, "sata": True, "protected": ["PROT"]})
        write(self.cfg["fstab"], FSTAB)
        p = mock.patch.object(self.m, "block_devices", side_effect=lambda: json.loads(json.dumps(world())))
        p.start()
        self.addCleanup(p.stop)

    def scan(self):
        return self.m.scan()

    def test_unmounted_fstab_disk_is_system(self):
        self.assertEqual(self.scan()["immich-uuid"]["systemReason"], "fstab")

    def test_mounted_disk_is_system(self):
        self.assertEqual(self.scan()["yedek-uuid"]["systemReason"], "mounted")

    def test_fstab_label_is_system(self):
        self.assertEqual(self.scan()["lbl-uuid"]["systemReason"], "fstab")

    def test_protected_serial_is_system(self):
        self.assertEqual(self.scan()["prot-uuid"]["systemReason"], "protected")

    def test_nvme_is_system_and_lvm_listed(self):
        s = self.scan()
        self.assertTrue(s["EFI-1"]["system"])
        self.assertTrue(s["lvm-1"]["system"])

    def test_commented_fstab_line_ignored_and_new_disk_free(self):
        s = self.scan()
        self.assertFalse(s["arsiv-uuid"]["system"])
        self.assertEqual(s["arsiv-uuid"]["kind"], "sata")

    def test_own_mount_does_not_make_system(self):
        self.assertFalse(self.scan()["usb-uuid"]["system"])

    def test_candidates_are_only_free_disks(self):
        self.assertEqual(set(self.m.candidates()), {"arsiv-uuid", "usb-uuid"})

    def test_unsupported_fs_on_free_disk_hidden(self):
        # zfs_member makes the disk "system" anyway; nothing of it is a candidate
        self.assertNotIn("zfs-uuid", self.m.candidates())

    def test_zram_ignored(self):
        self.assertFalse(any(v["device"] == "/dev/zram0" for v in self.scan().values()))

    def test_every_operation_refused_on_system_disk(self):
        with mock.patch.object(self.m, "run") as run:
            for op in ("mount", "unmount", "standby", "powerOff"):
                for uid in ("immich-uuid", "yedek-uuid", "EFI-1", "prot-uuid", "lbl-uuid"):
                    with self.assertRaises(self.m.Refused, msg=f"{op} {uid}"):
                        self.m.OPS[op]({"id": uid})
            with self.assertRaises(self.m.Refused):
                self.m.op_power_on({"id": "yedek-uuid"})
            run.assert_not_called()

    def test_mount_is_read_only_by_default_with_kernel_lock(self):
        calls = []
        with mock.patch.object(self.m, "run", side_effect=lambda *a, **k: calls.append(a) or ""), \
             mock.patch.object(self.m.os, "makedirs"), mock.patch.object(self.m, "mount_modes", return_value={}):
            self.m.op_mount({"id": "arsiv-uuid"})
        self.assertEqual(calls[0], ("blockdev", "--setro", "/dev/sdc1"))
        opts = calls[1][calls[1].index("-o") + 1].split(",")
        self.assertIn("ro", opts)
        self.assertIn("noload", opts)

    def test_writable_mount_only_when_asked(self):
        calls = []
        with mock.patch.object(self.m, "run", side_effect=lambda *a, **k: calls.append(a) or ""), \
             mock.patch.object(self.m.os, "makedirs"), mock.patch.object(self.m, "mount_modes", return_value={}):
            self.m.op_mount({"id": "arsiv-uuid", "readOnly": False})
        self.assertEqual(calls[0], ("blockdev", "--setrw", "/dev/sdc1"))
        self.assertNotIn("ro", calls[1][calls[1].index("-o") + 1].split(","))

    def test_never_stacks_a_second_mount_on_an_occupied_folder(self):
        # e.g. a cloned disk with the same UUID: the first one is mounted, the second must not land on top of it
        with mock.patch.object(self.m, "run") as run, mock.patch.object(self.m.os, "makedirs"), \
             mock.patch.object(self.m, "mount_modes", return_value={}), \
             mock.patch.object(self.m.os.path, "ismount", return_value=True):
            with self.assertRaises(self.m.Refused):
                self.m.op_mount({"id": "arsiv-uuid", "readOnly": False})
            run.assert_not_called()

    def test_non_boolean_read_only_means_read_only(self):
        calls = []
        with mock.patch.object(self.m, "run", side_effect=lambda *a, **k: calls.append(a) or ""), \
             mock.patch.object(self.m.os, "makedirs"), mock.patch.object(self.m, "mount_modes", return_value={}):
            self.m.op_mount({"id": "arsiv-uuid", "readOnly": "false"})
        self.assertEqual(calls[0][1], "--setro")

    def test_fresh_root_gets_owner_only_when_empty(self):
        with tempfile.TemporaryDirectory() as d:
            fake = mock.Mock(st_uid=0)
            with mock.patch.object(self.m.os, "stat", return_value=fake), mock.patch.object(self.m.os, "chown") as chown:
                os.mkdir(os.path.join(d, "lost+found"))
                self.m.adopt_fresh_root(d)
                chown.assert_called_once_with(d, 1000, 1000)
                chown.reset_mock()
                write(os.path.join(d, "veri.txt"), "x")      # a disk with data is never changed
                self.m.adopt_fresh_root(d)
                chown.assert_not_called()

    def test_root_owned_by_someone_else_is_left_alone(self):
        with tempfile.TemporaryDirectory() as d:
            with mock.patch.object(self.m.os, "stat", return_value=mock.Mock(st_uid=1000)), \
                 mock.patch.object(self.m.os, "chown") as chown:
                self.m.adopt_fresh_root(d)
                chown.assert_not_called()

    def test_sata_cannot_be_powered_off(self):
        with mock.patch.object(self.m, "run") as run:
            with self.assertRaises(self.m.Refused):
                self.m.op_power_off({"id": "arsiv-uuid"})
            run.assert_not_called()

    def test_bad_ids_refused(self):
        for bad in ("../etc", "/dev/sda1", "", None, 5, "a" * 100):
            with self.assertRaises(self.m.Refused):
                self.m.op_mount({"id": bad})

    def test_sata_disabled_means_not_allowed(self):
        m, cfg = load_agent({"mountRoot": ROOT, "usb": True, "sata": False})
        write(cfg["fstab"], "")
        with mock.patch.object(m, "block_devices", side_effect=lambda: json.loads(json.dumps(world()))):
            s = m.scan()
        self.assertEqual(s["arsiv-uuid"]["systemReason"], "not-allowed")


if __name__ == "__main__":
    unittest.main()
