!include LogicLib.nsh

Var MihomoMaintenanceStage
Var MihomoMaintenanceError
Var MihomoMaintenanceDetails
Var MihomoMaintenanceDirectory
Var MihomoRuntimeTrusted

; Maintenance uses the helper embedded in the current installer/uninstaller.
; Create a unique directory directly beneath the OS Program Files folder with
; its final administrator-only DACL at creation, then extract the new payload.
!macro DefineMaintenanceBootstrap PREFIX
Function ${PREFIX}MihomoEnsureMaintenance
  ${If} $MihomoMaintenanceDirectory != ""
    Return
  ${EndIf}
  System::Store "s"
  StrCpy $MihomoMaintenanceError 0
  StrCpy $MihomoMaintenanceStage "create_private_maintenance_directory"
  StrCpy $1 0
  StrCpy $2 0
  StrCpy $3 0
  System::Alloc 16
  Pop $0
  ${If} $0 P= 0
    StrCpy $MihomoMaintenanceError 8
    Goto mihomo_bootstrap_done
  ${EndIf}
  System::Call 'ole32::CoCreateGuid(p r0) i .r4'
  ${If} $4 != 0
    StrCpy $MihomoMaintenanceError $4
    Goto mihomo_bootstrap_done
  ${EndIf}
  System::Call 'ole32::StringFromGUID2(p r0, w .r5, i ${NSIS_MAX_STRLEN}) i .r4'
  ${If} $4 = 0
    StrCpy $MihomoMaintenanceError 87
    Goto mihomo_bootstrap_done
  ${EndIf}
  StrCpy $MihomoMaintenanceDirectory "$PROGRAMFILES64\MihomoMaintenance-$5"
  System::Call 'advapi32::ConvertStringSecurityDescriptorToSecurityDescriptorW(w "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", i 1, *p .r1, p 0) i .r4 ?e'
  Pop $6
  ${If} $4 = 0
    StrCpy $MihomoMaintenanceError $6
    Goto mihomo_bootstrap_done
  ${EndIf}
  ; NSIS and the System plug-in are 32-bit, so SECURITY_ATTRIBUTES is 12 bytes.
  System::Call '*(i 12, p r1, i 0) p .r2'
  ${If} $2 P= 0
    StrCpy $MihomoMaintenanceError 8
    Goto mihomo_bootstrap_done
  ${EndIf}
  System::Call 'kernel32::CreateDirectoryW(w "$MihomoMaintenanceDirectory", p r2) i .r3 ?e'
  Pop $6
  ${If} $3 = 0
    StrCpy $MihomoMaintenanceError $6
    ; Never write to or delete a directory that this invocation did not create.
    StrCpy $MihomoMaintenanceDirectory ""
    Goto mihomo_bootstrap_done
  ${EndIf}
  SetOutPath "$MihomoMaintenanceDirectory"
  SetOverwrite on
  ClearErrors
  File /oname=mihomo-maintenance.exe "..\dist-native\mihomo-desktop-service.exe"
  File /oname=installer-files.json "generated\installer-files.json"
  ${If} ${Errors}
    StrCpy $MihomoMaintenanceError 5
  ${EndIf}
  mihomo_bootstrap_done:
    ${If} $3 = 0
      StrCpy $MihomoMaintenanceDirectory ""
    ${EndIf}
    ${If} $2 P<> 0
      System::Free $2
    ${EndIf}
    ${If} $1 P<> 0
      System::Call 'kernel32::LocalFree(p r1)'
    ${EndIf}
    ${If} $0 P<> 0
      System::Free $0
    ${EndIf}
    System::Store "l"
    ${If} $MihomoMaintenanceError != 0
      Call ${PREFIX}MihomoCleanupMaintenance
      DetailPrint "Unable to prepare private maintenance: stage=$MihomoMaintenanceStage, Win32=$MihomoMaintenanceError"
      SetErrorLevel 1
      Abort "Unable to create the protected installation maintenance directory."
    ${EndIf}
FunctionEnd

Function ${PREFIX}MihomoCleanupMaintenance
  ${If} $MihomoMaintenanceDirectory != ""
    SetOutPath "$PROGRAMFILES64"
    Delete "$MihomoMaintenanceDirectory\mihomo-maintenance.exe"
    Delete "$MihomoMaintenanceDirectory\installer-files.json"
    RMDir "$MihomoMaintenanceDirectory"
    StrCpy $MihomoMaintenanceDirectory ""
  ${EndIf}
FunctionEnd

Function ${PREFIX}MihomoValidateRuntime
  ; A complete, protected runtime may receive a graceful --quit. Cleanup does
  ; not execute installed binaries when runtime validation fails.
  System::Store "s"
  StrCpy $MihomoRuntimeTrusted 0
  nsExec::ExecToStack /TIMEOUT=45000 '"$MihomoMaintenanceDirectory\mihomo-maintenance.exe" --validate-directory "$INSTDIR"'
  Pop $0
  Pop $1
  ${If} $0 = 0
    StrCpy $MihomoRuntimeTrusted 1
  ${EndIf}
  System::Store "l"
FunctionEnd
!macroend
!insertmacro DefineMaintenanceBootstrap ""
!insertmacro DefineMaintenanceBootstrap "un."

!macro MihomoRunMaintenance PREFIX COMMAND STAGE
  System::Store "s"
  StrCpy $MihomoMaintenanceStage "${STAGE}"
  nsExec::ExecToStack /TIMEOUT=240000 '${COMMAND}'
  Pop $0
  Pop $1
  StrCpy $MihomoMaintenanceError $0
  StrCpy $MihomoMaintenanceDetails $1
  System::Store "l"
  ${If} $MihomoMaintenanceError != 0
    DetailPrint "Mihomo maintenance failed: stage=$MihomoMaintenanceStage, helper=$MihomoMaintenanceError; $MihomoMaintenanceDetails"
    IfSilent mihomo_maintenance_skip_message_${STAGE} 0
    ${If} $PassiveMode != 1
      MessageBox MB_ICONSTOP|MB_OK "无法完成操作。阶段：$MihomoMaintenanceStage$\r$\n$MihomoMaintenanceDetails"
    ${EndIf}
    mihomo_maintenance_skip_message_${STAGE}:
    Call ${PREFIX}MihomoCleanupMaintenance
    SetErrorLevel 1
    Abort
  ${EndIf}
!macroend

!macro NSIS_HOOK_CHECKINSTALL
  Call MihomoEnsureMaintenance
  !insertmacro MihomoRunMaintenance "" '"$MihomoMaintenanceDirectory\mihomo-maintenance.exe" --installer-check "$INSTDIR" "$MihomoMaintenanceDirectory\installer-files.json"' "preflight"
  Call MihomoValidateRuntime
!macroend

!macro NSIS_HOOK_PREINSTALL
  !insertmacro MihomoRunMaintenance "" '"$MihomoMaintenanceDirectory\mihomo-maintenance.exe" --installer-prepare "$INSTDIR" "$MihomoMaintenanceDirectory\installer-files.json"' "prepare_files"
!macroend

!macro NSIS_HOOK_POSTINSTALL
  !insertmacro MihomoRunMaintenance "" '"$MihomoMaintenanceDirectory\mihomo-maintenance.exe" --installer-finalize "$INSTDIR"' "finalize"
!macroend

!macro NSIS_HOOK_REGISTERINSTALL
  ; The helper creates protected registration or validates its existing key.
  ; NSIS only fills metadata after the identity and ACL boundary already exist.
  !insertmacro MihomoRunMaintenance "" '"$MihomoMaintenanceDirectory\mihomo-maintenance.exe" --installer-register "$INSTDIR"' "register_installation"
!macroend

!macro NSIS_HOOK_CHECKUNINSTALL PREFIX
  Call ${PREFIX}MihomoEnsureMaintenance
  !insertmacro MihomoRunMaintenance "${PREFIX}" '"$MihomoMaintenanceDirectory\mihomo-maintenance.exe" --uninstall-check "$INSTDIR"' "uninstall_preflight"
  Call ${PREFIX}MihomoValidateRuntime
!macroend

!macro NSIS_HOOK_PREUNINSTALL PREFIX
  System::Call 'kernel32::GetCurrentProcessId() i .r0'
  StrCpy $MihomoMaintenanceDetails $0
  !insertmacro MihomoRunMaintenance "${PREFIX}" '"$MihomoMaintenanceDirectory\mihomo-maintenance.exe" --remove-installation "$INSTDIR" "$MihomoMaintenanceDirectory\installer-files.json" $MihomoMaintenanceDetails' "remove_installation"
!macroend
