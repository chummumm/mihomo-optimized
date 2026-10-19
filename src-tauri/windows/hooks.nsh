!include LogicLib.nsh

Var MihomoMaintenanceStage
Var MihomoMaintenanceError

; A previous uninstaller can leave only the helper behind. Do not execute that
; old helper: its installation validation may require files already removed.
Function MihomoPrepareInstallation
  System::Store "s"
  StrCpy $MihomoMaintenanceError 0
  StrCpy $MihomoMaintenanceStage "open_scm"
  StrCpy $0 0 ; SCM handle
  StrCpy $1 0 ; service handle
  StrCpy $2 0 ; SERVICE_STATUS_PROCESS buffer
  StrCpy $7 0 ; a STOP request has been accepted
  StrCpy $9 -1 ; helper file handle
  StrCpy $R3 0 ; service is absent or stopped
  StrCpy $R4 1460 ; timeout error, or the last file/deletion error
  StrCpy $R5 0 ; observed a service marked for deletion
  System::Call 'kernel32::GetTickCount() i .R0'

  ; SC_MANAGER_CONNECT; this elevated installer only stops the fixed service.
  System::Call 'advapi32::OpenSCManagerW(p 0, p 0, i 0x1) p .r0 ?e'
  Pop $6
  ${If} $0 P= 0
    Goto mihomo_prepare_failed
  ${EndIf}
  System::Alloc 36 ; nine DWORDs, independent of pointer width
  Pop $2
  ${If} $2 P= 0
    StrCpy $MihomoMaintenanceStage "allocate_service_status"
    StrCpy $6 8
    Goto mihomo_prepare_failed
  ${EndIf}

  mihomo_prepare_poll:
    ; One deadline covers service transitions, deletion and helper release.
    ; Unsigned subtraction also handles the GetTickCount rollover.
    System::Call 'kernel32::GetTickCount() i .R1'
    IntOp $R1 $R1 - $R0
    ${If} $R1 U>= 40000
      StrCpy $6 $R4
      Goto mihomo_prepare_failed
    ${EndIf}

    ${If} $R3 = 1
      Goto mihomo_prepare_helper
    ${EndIf}
    ${If} $1 P= 0
      StrCpy $MihomoMaintenanceStage "open_service"
      ; SERVICE_STOP | SERVICE_QUERY_STATUS, no dependency on installed EXEs.
      System::Call 'advapi32::OpenServiceW(p r0, w "MihomoOptimizedService", i 0x24) p .r1 ?e'
      Pop $6
      ${If} $1 P= 0
        ${If} $6 = 1060 ; ERROR_SERVICE_DOES_NOT_EXIST
          StrCpy $R3 1
          Goto mihomo_prepare_helper
        ${ElseIf} $6 = 1072 ; ERROR_SERVICE_MARKED_FOR_DELETE
          StrCpy $R5 1
          StrCpy $R4 1072
          StrCpy $MihomoMaintenanceStage "wait_service_deleted"
          Goto mihomo_prepare_pause
        ${EndIf}
        Goto mihomo_prepare_failed
      ${EndIf}
    ${EndIf}

    StrCpy $MihomoMaintenanceStage "query_service"
    System::Call 'advapi32::QueryServiceStatusEx(p r1, i 0, p r2, i 36, *i .r8) i .r3 ?e'
    Pop $6
    ${If} $3 = 0
      Goto mihomo_prepare_failed
    ${EndIf}
    System::Call '*$2(i, i .r4)'
    ${If} $4 = 1 ; SERVICE_STOPPED
      System::Call 'advapi32::CloseServiceHandle(p r1)'
      StrCpy $1 0
      ${If} $R5 = 1
        ; Do not keep an open service handle while waiting for actual deletion.
        StrCpy $MihomoMaintenanceStage "wait_service_deleted"
        Goto mihomo_prepare_pause
      ${EndIf}
      StrCpy $R3 1
      Goto mihomo_prepare_helper
    ${EndIf}

    StrCpy $R4 1460
    StrCpy $MihomoMaintenanceStage "wait_service_stop"
    ${If} $4 = 2 ; SERVICE_START_PENDING
      StrCpy $MihomoMaintenanceStage "wait_service_start"
      Goto mihomo_prepare_pause
    ${ElseIf} $4 = 3 ; SERVICE_STOP_PENDING
      Goto mihomo_prepare_pause
    ${EndIf}
    ${If} $7 = 0
      StrCpy $MihomoMaintenanceStage "stop_service"
      ; ControlService writes SERVICE_STATUS into the buffer's first 28 bytes.
      System::Call 'advapi32::ControlService(p r1, i 1, p r2) i .r3 ?e'
      Pop $6
      ${If} $3 = 0
        ${If} $6 = 1061 ; state changed before STOP could be accepted
        ${OrIf} $6 = 1062 ; service became inactive; confirm with another query
          Goto mihomo_prepare_pause
        ${EndIf}
        Goto mihomo_prepare_failed
      ${EndIf}
      StrCpy $7 1
    ${EndIf}
    Goto mihomo_prepare_pause

  mihomo_prepare_helper:
    StrCpy $MihomoMaintenanceStage "wait_helper_file"
    ; OPEN_EXISTING never truncates or writes. GENERIC_WRITE also detects an
    ; executable image still mapped by the service or proxy recovery watchdog.
    ; Allow all sharing; do not terminate the watchdog before proxy restoration.
    System::Call 'kernel32::CreateFileW(w "$INSTDIR\mihomo-desktop-service.exe", i 0x40000000, i 0x7, p 0, i 3, i 0x80, p 0) p .r9 ?e'
    Pop $6
    ${If} $9 P<> -1
      Goto mihomo_prepare_cleanup
    ${EndIf}
    ${If} $6 = 2 ; ERROR_FILE_NOT_FOUND
    ${OrIf} $6 = 3 ; ERROR_PATH_NOT_FOUND
      Goto mihomo_prepare_cleanup
    ${EndIf}
    ${If} $6 = 5 ; ERROR_ACCESS_DENIED, including a delete-pending file
    ${OrIf} $6 = 32 ; ERROR_SHARING_VIOLATION, including a mapped image
      StrCpy $R4 $6
      Goto mihomo_prepare_pause
    ${EndIf}
    Goto mihomo_prepare_failed

  mihomo_prepare_pause:
    System::Call 'kernel32::GetTickCount() i .R1'
    IntOp $R1 $R1 - $R0
    ${If} $R1 U>= 40000
      StrCpy $6 $R4
      Goto mihomo_prepare_failed
    ${EndIf}
    IntOp $R2 40000 - $R1
    ${If} $R2 > 100
      StrCpy $R2 100
    ${EndIf}
    Sleep $R2
    Goto mihomo_prepare_poll

  mihomo_prepare_failed:
    StrCpy $MihomoMaintenanceError $6
  mihomo_prepare_cleanup:
    ${If} $9 P<> -1
      System::Call 'kernel32::CloseHandle(p r9)'
    ${EndIf}
    ${If} $1 P<> 0
      System::Call 'advapi32::CloseServiceHandle(p r1)'
    ${EndIf}
    ${If} $0 P<> 0
      System::Call 'advapi32::CloseServiceHandle(p r0)'
    ${EndIf}
    ${If} $2 P<> 0
      System::Free $2
    ${EndIf}
    System::Store "l"
FunctionEnd

; Keep the other installed binaries until the helper is actually deletable.
; In particular, let a proxy recovery watchdog finish naturally after GUI exit.
Function un.MihomoDeleteHelper
  System::Store "s"
  StrCpy $MihomoMaintenanceStage "delete_helper_file"
  StrCpy $MihomoMaintenanceError 0
  System::Call 'kernel32::GetTickCount() i .R0'
  mihomo_delete_helper_poll:
    System::Call 'kernel32::DeleteFileW(w "$INSTDIR\mihomo-desktop-service.exe") i .r0 ?e'
    Pop $1
    ${If} $0 <> 0
    ${OrIf} $1 = 2 ; ERROR_FILE_NOT_FOUND
    ${OrIf} $1 = 3 ; ERROR_PATH_NOT_FOUND
      Goto mihomo_delete_helper_done
    ${EndIf}
    ${If} $1 <> 5 ; ERROR_ACCESS_DENIED, including a mapped image
    ${AndIf} $1 <> 32 ; ERROR_SHARING_VIOLATION
      Goto mihomo_delete_helper_failed
    ${EndIf}
    System::Call 'kernel32::GetTickCount() i .R1'
    IntOp $R1 $R1 - $R0
    ${If} $R1 U>= 40000
      Goto mihomo_delete_helper_failed
    ${EndIf}
    IntOp $R2 40000 - $R1
    ${If} $R2 > 100
      StrCpy $R2 100
    ${EndIf}
    Sleep $R2
    Goto mihomo_delete_helper_poll
  mihomo_delete_helper_failed:
    StrCpy $MihomoMaintenanceError $1
  mihomo_delete_helper_done:
    System::Store "l"
FunctionEnd

; Service installation stays opt-in. Stop existing installations before copying
; new files, then restart an installed service with its existing owner SID.
!macro NSIS_HOOK_PREINSTALL
  ; The template's GUI check is after this hook; close it before awaiting its
  ; proxy recovery watchdog. The later template check can safely run again.
  !insertmacro CheckIfAppIsRunning "$INSTDIR\mihomo-optimized.exe" "${PRODUCTNAME}"
  Call MihomoPrepareInstallation
  ${If} $MihomoMaintenanceError != 0
    DetailPrint "Mihomo installation preparation failed: stage=$MihomoMaintenanceStage, Win32=$MihomoMaintenanceError"
    IfSilent mihomo_preinstall_abort 0
    ${If} $PassiveMode != 1
      MessageBox MB_ICONSTOP|MB_OK "Unable to prepare the Mihomo installation.$\r$\nStage: $MihomoMaintenanceStage$\r$\nWindows error: $MihomoMaintenanceError"
    ${EndIf}
    mihomo_preinstall_abort:
    SetErrorLevel 1
    Abort
  ${EndIf}
!macroend

!macro NSIS_HOOK_POSTINSTALL
  System::Store "s"
  nsExec::ExecToStack '"$INSTDIR\mihomo-desktop-service.exe" --start-if-installed'
  Pop $0
  Pop $1
  ${If} $0 != 0
    DetailPrint "The optional TUN service could not restart: helper exit=$0; $1"
  ${EndIf}
  System::Store "l"
!macroend

!macro NSIS_HOOK_PREUNINSTALL
  !insertmacro CheckIfAppIsRunning "$INSTDIR\mihomo-optimized.exe" "${PRODUCTNAME}"
  IfFileExists "$INSTDIR\mihomo-desktop-service.exe" 0 mihomo_preuninstall_delete_helper
  System::Store "s"
  nsExec::ExecToStack '"$INSTDIR\mihomo-desktop-service.exe" --uninstall'
  Pop $0
  Pop $1
  ${If} $0 != 0
    DetailPrint "Mihomo service removal failed: helper exit=$0; $1"
  ${EndIf}
  StrCpy $MihomoMaintenanceError $0
  System::Store "l"
  ${If} $MihomoMaintenanceError != 0
    IfSilent mihomo_preuninstall_abort 0
    ${If} $PassiveMode != 1
      MessageBox MB_ICONSTOP|MB_OK "Unable to remove the Mihomo desktop service.$\r$\nHelper exit: $MihomoMaintenanceError"
    ${EndIf}
    mihomo_preuninstall_abort:
    SetErrorLevel 1
    Abort
  ${EndIf}
  mihomo_preuninstall_delete_helper:
  Call un.MihomoDeleteHelper
  ${If} $MihomoMaintenanceError != 0
    DetailPrint "Mihomo helper removal failed: stage=$MihomoMaintenanceStage, Win32=$MihomoMaintenanceError"
    IfSilent mihomo_preuninstall_delete_abort 0
    ${If} $PassiveMode != 1
      MessageBox MB_ICONSTOP|MB_OK "Unable to remove the Mihomo desktop helper.$\r$\nStage: $MihomoMaintenanceStage$\r$\nWindows error: $MihomoMaintenanceError"
    ${EndIf}
    mihomo_preuninstall_delete_abort:
    SetErrorLevel 1
    Abort
  ${EndIf}
!macroend
