"""Build the per-user Windows Installer package using Python 3.12's MSI bindings."""

from pathlib import Path
import ctypes
import sys
from tempfile import TemporaryDirectory

import msilib
from msilib import schema, sequence


def use_russian_codepage(target, work):
    # msilib doesn't expose MsiDatabaseImport, which is required for _ForceCodepage.
    codepage = work / "_ForceCodepage.idt"
    codepage.write_bytes(b"\r\n\r\n1251\t_ForceCodepage\r\n")
    installer = ctypes.WinDLL("msi.dll")
    handle = ctypes.c_uint()
    result = installer.MsiOpenDatabaseW(str(target), ctypes.c_void_p(1), ctypes.byref(handle))
    if result:
        raise OSError(result, "Opening MSI database for localization")
    try:
        result = installer.MsiDatabaseImportW(handle, str(work), codepage.name)
        if not result:
            result = installer.MsiDatabaseCommit(handle)
        if result:
            raise OSError(result, "Setting MSI database code page")
    finally:
        installer.MsiCloseHandle(handle)


def setup_dialogs():
    return {
        "Dialog": [
            ("InstallOptions", 50, 50, 290, 135, 3, "Установка [ProductName]", "Desktop", "Install", "Cancel"),
            ("InstallDone", 50, 50, 290, 146, 3, "Установка [ProductName]", "Close", "Close", "Close"),
        ],
        "Control": [
            ("InstallOptions", "Heading", "Text", 16, 14, 258, 20, 3, None, "{\\TitleFont}Установка SSH Manager", None, None),
            ("InstallOptions", "Description", "Text", 16, 42, 258, 29, 3, None,
             "Ярлык SSH Manager появится в меню «Пуск». Можно также добавить ярлык на рабочий стол.", None, None),
            ("InstallOptions", "Desktop", "CheckBox", 16, 76, 256, 16, 3, "DESKTOPSHORTCUT", "Создать ярлык на рабочем столе", "Install", None),
            ("InstallOptions", "Install", "PushButton", 143, 107, 66, 18, 3, None, "Установить", "Cancel", None),
            ("InstallOptions", "Cancel", "PushButton", 217, 107, 57, 18, 3, None, "Отмена", "Desktop", None),
            ("InstallDone", "Heading", "Text", 16, 14, 258, 22, 3, None, "{\\TitleFont}SSH Manager установлен", None, None),
            ("InstallDone", "PinTitle", "Text", 16, 45, 258, 17, 3, None, "{\\BoldFont}Закрепить на панели задач", None, None),
            ("InstallDone", "PinHelp", "Text", 16, 65, 258, 40, 3, None,
             "Найдите SSH Manager через «Пуск», нажмите правой кнопкой и выберите «Закрепить на панели задач».", None, None),
            ("InstallDone", "Close", "PushButton", 211, 116, 63, 18, 3, None, "Готово", "Close", None),
        ],
        "ControlEvent": [
            ("InstallOptions", "Install", "EndDialog", "Return", "1", 1),
            ("InstallOptions", "Cancel", "EndDialog", "Exit", "1", 1),
            ("InstallDone", "Close", "EndDialog", "Return", "1", 1),
        ],
        "CheckBox": [("DESKTOPSHORTCUT", "1")],
        "TextStyle": [("UiFont", "Segoe UI", 9, 0, 0), ("BoldFont", "Segoe UI", 9, 0, 1), ("TitleFont", "Segoe UI", 13, 0, 1)],
    }


def package(app, output, file_version):
    product_version = ".".join(file_version.split(".")[:3])
    # Stable for this release. Change ProductCode when publishing a new product version.
    product = "{9CB8F93C-824A-4F68-8498-2EF8FE42DEF2}"
    upgrade = "{7EDB1B69-D679-4CE9-9AF4-5EE3D7BC3E0D}"
    file_id = "AppExecutable"

    with TemporaryDirectory(dir=output.parent) as staging:
        work = Path(staging)
        cabinet = work / "payload.cab"
        msilib.FCICreate(str(cabinet), [(str(app), file_id)])
        target = work / output.name
        db = msilib.init_database(str(target), schema, "SSH Manager", product, product_version, "SSH Manager")
        db.Close()
        use_russian_codepage(target, work)
        db = msilib.OpenDatabase(str(target), msilib.MSIDBOPEN_TRANSACT)
        language = db.OpenView("UPDATE `Property` SET `Value` = '1049' WHERE `Property` = 'ProductLanguage'")
        language.Execute(None)
        language.Close()
        del language
        summary = db.GetSummaryInformation(5)
        summary.SetProperty(msilib.PID_CODEPAGE, 1251)
        summary.SetProperty(msilib.PID_TEMPLATE, "Intel;1049")  # App is AnyCPU, package uses a 32-bit directory layout.
        summary.SetProperty(msilib.PID_WORDCOUNT, 2 | 8)  # Compressed media, per-user install without elevation.
        summary.Persist()
        del summary

        tables = {
            "Property": [("UpgradeCode", upgrade), ("ARPNOMODIFY", "1"), ("DefaultUIFont", "UiFont"),
                         ("SecureCustomProperties", "OLDERPRODUCTS;NEWERPRODUCTS;DESKTOPSHORTCUT")],
            "Directory": [
                ("TARGETDIR", None, "SourceDir"),
                ("LocalAppDataFolder", "TARGETDIR", "."),
                ("LocalPrograms", "LocalAppDataFolder", "Programs:."),
                ("INSTALLDIR", "LocalPrograms", "SSHMG~1|SSH Manager:."),
                ("ProgramMenuFolder", "TARGETDIR", "."),
                ("DesktopFolder", "TARGETDIR", "."),
            ],
            "Component": [
                ("Application", "{840C57C8-0F49-4498-B685-696B1D69B484}", "INSTALLDIR", 0, None, file_id),
                ("MenuShortcut", "{F7922992-0F55-400D-877A-0CD29D697073}", "ProgramMenuFolder", 4, None, "ShortcutKey"),
                ("DesktopShortcut", "{65891664-0782-4E24-9625-F536DD0C67F4}", "DesktopFolder", 4, None, "DesktopShortcutKey"),
            ],
            "Registry": [("ShortcutKey", 1, r"Software\SshManager\Installer", "Shortcut", "#1", "MenuShortcut"),
                         ("DesktopShortcutKey", 1, r"Software\SshManager\Installer", "DesktopShortcut", "#1", "DesktopShortcut")],
            "Feature": [("App", None, "SSH Manager", None, 1, 1, "INSTALLDIR", 0),
                        ("DesktopShortcut", "App", "Ярлык на рабочем столе", None, 0, 2, None, 0)],
            "Condition": [("DesktopShortcut", 1, "DESKTOPSHORTCUT=1")],
            "FeatureComponents": [("App", "Application"), ("App", "MenuShortcut"), ("DesktopShortcut", "DesktopShortcut")],
            "File": [(file_id, "Application", "SSHMAN~1.EXE|SshManager.exe", app.stat().st_size, file_version, None, 512, 1)],
            "Shortcut": [("StartLink", "ProgramMenuFolder", "SSHMG~1|SSH Manager", "MenuShortcut", f"[#{file_id}]",
                          None, "Управление подключениями OpenSSH", None, None, None, 1, "INSTALLDIR"),
                         ("DesktopLink", "DesktopFolder", "SSHMG~1|SSH Manager", "DesktopShortcut", f"[#{file_id}]",
                          None, "Управление подключениями OpenSSH", None, None, None, 1, "INSTALLDIR")],
            "Media": [(1, 1, None, "#payload.cab", None, None)],
            "Upgrade": [(upgrade, None, product_version, None, 0, None, "OLDERPRODUCTS"),
                        (upgrade, product_version, None, None, 2, None, "NEWERPRODUCTS")],
            "LaunchCondition": [("NOT ALLUSERS", "SSH Manager устанавливается для текущего пользователя."),
                                ("NOT NEWERPRODUCTS", "Уже установлена более новая версия SSH Manager.")],
            "InstallUISequence": [("InstallOptions", "NOT Installed", 1250), ("ExecuteAction", None, 1300),
                                  ("InstallDone", "NOT Installed", -1)],
            "AdminUISequence": [("ExecuteAction", None, 1300)],
            "AdminExecuteSequence": sequence.AdminExecuteSequence,
        }
        tables.update(setup_dialogs())
        actions = {
            "FindRelatedProducts", "LaunchConditions", "CostInitialize", "FileCost", "CostFinalize", "InstallValidate",
            "InstallInitialize", "ProcessComponents", "UnpublishFeatures", "RemoveRegistryValues", "RemoveShortcuts",
            "RemoveFiles", "RemoveFolders", "InstallFiles", "CreateShortcuts", "WriteRegistryValues", "RegisterUser",
            "RegisterProduct", "PublishFeatures", "PublishProduct", "InstallFinalize",
        }
        tables["InstallExecuteSequence"] = [action for action in sequence.InstallExecuteSequence if action[0] in actions]
        tables["InstallExecuteSequence"].append(("RemoveExistingProducts", "OLDERPRODUCTS", 1520))
        for name, rows in tables.items():
            msilib.add_data(db, name, rows)
        msilib.add_stream(db, "payload.cab", str(cabinet))
        db.Commit()
        db.Close()
        del db
        target.replace(output)


if __name__ == "__main__":
    package(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve(), sys.argv[3])
