Unicode true
RequestExecutionLevel admin
ManifestDPIAware true
SetCompressor /SOLID lzma
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"

!ifndef APP_VERSION
  !error "APP_VERSION must identify the built native application"
!endif
!define PRODUCTNAME "Mihomo Optimized"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Mihomo Optimized"
Name "${PRODUCTNAME}"
OutFile "..\artifacts\desktop\mihomo-desktop-${APP_VERSION}-windows-x64-setup.exe"
InstallDir "$PROGRAMFILES64\${PRODUCTNAME}"
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "${PRODUCTNAME}"
VIAddVersionKey "FileDescription" "Mihomo Optimized native Windows installer"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" "Mihomo Optimized contributors"
!define MUI_ICON "..\src-tauri\icons\icon.ico"
!define MUI_UNICON "..\src-tauri\icons\icon.ico"
!define MUI_PAGE_CUSTOMFUNCTION_PRE MihomoSkipPassive
!insertmacro MUI_PAGE_WELCOME
!define MUI_PAGE_CUSTOMFUNCTION_PRE MihomoSkipPassive
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_PAGE_CUSTOMFUNCTION_PRE MihomoSkipPassive
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

Var PassiveMode
Var UpdateMode
Var MihomoCloseError
Var MihomoPreviousLocation
Var MihomoLegacy32Location
!include "..\src-tauri\windows\hooks.nsh"

; Validate actual mapped files, not a process name. Never terminate a recovery
; watchdog or installer monitor: those must survive a normal application exit.
!macro DefineCloseApplication PREFIX
Function ${PREFIX}MihomoCloseApplication
  System::Store "s"
  StrCpy $MihomoCloseError 0
  StrCpy $R4 0
  System::Call 'kernel32::GetTickCount() i .R0'
  mihomo_close_poll:
    StrCpy $R5 0
    System::Call 'kernel32::CreateFileW(w "$INSTDIR\mihomo-optimized.exe", i 0x40000000, i 7, p 0, i 3, i 0x80, p 0) p .r0 ?e'
    Pop $1
    ${If} $0 P<> -1
      System::Call 'kernel32::CloseHandle(p r0)'
    ${ElseIf} $1 != 2
    ${AndIf} $1 != 3
      StrCpy $R5 1
    ${EndIf}
    System::Call 'kernel32::CreateFileW(w "$INSTDIR\mihomo-desktop-host.exe", i 0x40000000, i 7, p 0, i 3, i 0x80, p 0) p .r0 ?e'
    Pop $1
    ${If} $0 P<> -1
      System::Call 'kernel32::CloseHandle(p r0)'
    ${ElseIf} $1 != 2
    ${AndIf} $1 != 3
      StrCpy $R5 1
    ${EndIf}
    ${If} $R5 = 0
      Goto mihomo_close_done
    ${EndIf}
    ${If} $R4 = 0
      StrCpy $R4 1
      ; The verified-update handoff already asked UI and host to exit. Issuing
      ; another quit while their pipes close could turn success into a race.
      ${If} $UpdateMode = 1
        Goto mihomo_close_continue
      ${EndIf}
      ; Presence of the native host identifies versions supporting --quit.
      ; Legacy versions must be exited using their tray menu before upgrading.
      IfFileExists "$INSTDIR\mihomo-desktop-host.exe" 0 mihomo_close_legacy
      nsExec::ExecToStack /TIMEOUT=45000 '"$INSTDIR\mihomo-optimized.exe" --quit'
      Pop $2
      Pop $3
      ${If} $2 != 0
        StrCpy $MihomoCloseError 1
        Goto mihomo_close_done
      ${EndIf}
      Goto mihomo_close_continue
      mihomo_close_legacy:
      IfSilent mihomo_close_continue 0
      ${If} $PassiveMode = 1
        Goto mihomo_close_continue
      ${EndIf}
      MessageBox MB_OK|MB_ICONINFORMATION "请先从托盘退出 Mihomo Optimized，再继续安装或卸载。$\r$\nPlease exit Mihomo Optimized from its tray menu before continuing."
    ${EndIf}
    mihomo_close_continue:
    System::Call 'kernel32::GetTickCount() i .R1'
    IntOp $R1 $R1 - $R0
    ${If} $R1 U>= 60000
      StrCpy $MihomoCloseError 1460
      Goto mihomo_close_done
    ${EndIf}
    Sleep 100
    Goto mihomo_close_poll
  mihomo_close_done:
    System::Store "l"
    ${If} $MihomoCloseError != 0
      DetailPrint "Application shutdown failed: Windows error $MihomoCloseError. Program files were not replaced."
      IfSilent mihomo_close_abort 0
      ${If} $PassiveMode != 1
        MessageBox MB_ICONSTOP|MB_OK "无法退出程序。请先保存配置并从托盘退出，再重试。$\r$\nSave your configuration and exit from the tray, then retry."
      ${EndIf}
      mihomo_close_abort:
      SetErrorLevel 1
      Abort
    ${EndIf}
FunctionEnd
!macroend
!insertmacro DefineCloseApplication ""
!insertmacro DefineCloseApplication "un."

Function MihomoSkipPassive
  ${If} $PassiveMode = 1
    Abort
  ${EndIf}
FunctionEnd

; Legacy Tauri installs stored InstallLocation surrounded by literal quotes.
; Both native and legacy registry views must resolve to the same actual path.
Function MihomoNormalizeLocation
  Exch $0
  Push $1
  Push $2
  StrCpy $1 $0 1
  StrCpy $2 $0 1 -1
  ${If} $1 == '"'
  ${AndIf} $2 == '"'
    StrLen $1 $0
    IntOp $1 $1 - 2
    StrCpy $0 $0 $1 1
  ${EndIf}
  Pop $2
  Pop $1
  Exch $0
FunctionEnd

Function .onInit
  StrCpy $PassiveMode 0
  StrCpy $UpdateMode 0
  ${GetOptions} $CMDLINE "/P" $0
  ${IfNot} ${Errors}
    StrCpy $PassiveMode 1
  ${EndIf}
  ${GetOptions} $CMDLINE "/UPDATE" $0
  ${IfNot} ${Errors}
    StrCpy $PassiveMode 1
    StrCpy $UpdateMode 1
  ${EndIf}
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP|MB_OK "This application requires 64-bit Windows."
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP|MB_OK "This application requires Windows 10 or later."
    Abort
  ${EndIf}
  SetRegView 64
  SetShellVarContext all
  ReadRegStr $1 HKLM "Software\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
  ${If} $1 < 17763
    MessageBox MB_ICONSTOP|MB_OK "This application requires Windows 10 version 1809 or later."
    Abort
  ${EndIf}
  ReadRegStr $0 HKLM "${UNINSTALL_KEY}" "InstallLocation"
  Push $0
  Call MihomoNormalizeLocation
  Pop $MihomoPreviousLocation
  SetRegView 32
  ReadRegStr $0 HKLM "${UNINSTALL_KEY}" "InstallLocation"
  Push $0
  Call MihomoNormalizeLocation
  Pop $MihomoLegacy32Location
  SetRegView 64
  ${If} $MihomoPreviousLocation == ""
    StrCpy $MihomoPreviousLocation $MihomoLegacy32Location
  ${ElseIf} $MihomoLegacy32Location != ""
  ${AndIf} $MihomoPreviousLocation != $MihomoLegacy32Location
    SetErrorLevel 1
    Abort "Conflicting application installations were found in the 32-bit and 64-bit registry views."
  ${EndIf}
  ${If} $MihomoPreviousLocation != ""
    StrCpy $INSTDIR $MihomoPreviousLocation
  ${EndIf}
FunctionEnd

Function un.onInit
  StrCpy $PassiveMode 0
  StrCpy $UpdateMode 0
  SetRegView 64
  SetShellVarContext all
FunctionEnd

Section "Install"
  ${If} $MihomoPreviousLocation != ""
  ${AndIf} $INSTDIR != $MihomoPreviousLocation
    SetErrorLevel 1
    Abort "An upgrade must use the existing installation directory. Uninstall first to change it."
  ${EndIf}
  Call MihomoCloseApplication
  !insertmacro NSIS_HOOK_PREINSTALL
  SetOutPath "$INSTDIR"
  SetOverwrite ifdiff
  ClearErrors
  File /r "..\dist-native\*"
  ${If} ${Errors}
    SetErrorLevel 1
    Abort "Unable to copy the complete application."
  ${EndIf}
  WriteUninstaller "$INSTDIR\uninstall.exe"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayName" "${PRODUCTNAME}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "Publisher" "Mihomo Optimized contributors"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "MainBinaryName" "mihomo-optimized.exe"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKLM "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\mihomo-optimized.exe"
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1
  ; Retain the legacy root Start Menu shortcut instead of leaving a duplicate.
  CreateShortcut "$SMPROGRAMS\${PRODUCTNAME}.lnk" "$INSTDIR\mihomo-optimized.exe"
  ${If} $MihomoLegacy32Location != ""
    SetRegView 32
    DeleteRegKey HKLM "${UNINSTALL_KEY}"
    SetRegView 64
  ${EndIf}
  !insertmacro NSIS_HOOK_POSTINSTALL
  ${If} $PassiveMode = 1
    SetAutoClose true
  ${EndIf}
  SetErrorLevel 0
SectionEnd

Section "Uninstall"
  Call un.MihomoCloseApplication
  !insertmacro NSIS_HOOK_PREUNINSTALL
  ; Generated exact file list. Never recursively remove application/user data.
  !include "generated\uninstall-files.nsh"
  Delete "$SMPROGRAMS\${PRODUCTNAME}.lnk"
  Delete "$DESKTOP\${PRODUCTNAME}.lnk"
  ; Remove only this installation's autostart value for the current user.
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "MihomoOptimized"
  ${If} $0 == '"$INSTDIR\mihomo-optimized.exe" --tray'
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "MihomoOptimized"
  ${EndIf}
  DeleteRegKey HKLM "${UNINSTALL_KEY}"
  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"
  SetErrorLevel 0
SectionEnd
