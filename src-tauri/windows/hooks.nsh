; Service installation stays opt-in. Upgrades stop an existing service so its
; executable is replaceable, then start it again with its existing owner SID.
!macro NSIS_HOOK_PREINSTALL
  IfFileExists "$INSTDIR\mihomo-desktop-service.exe" 0 mihomo_preinstall_done
  nsExec::ExecToStack '"$INSTDIR\mihomo-desktop-service.exe" --stop-for-update'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_ICONSTOP|MB_OK "Unable to stop the Mihomo desktop service. Close Mihomo and retry."
    Abort
  ${EndIf}
  mihomo_preinstall_done:
!macroend

!macro NSIS_HOOK_POSTINSTALL
  nsExec::ExecToStack '"$INSTDIR\mihomo-desktop-service.exe" --start-if-installed'
  Pop $0
  Pop $1
  ${If} $0 != 0
    DetailPrint "The optional TUN service could not restart. Open desktop settings to repair it."
  ${EndIf}
!macroend

!macro NSIS_HOOK_PREUNINSTALL
  IfFileExists "$INSTDIR\mihomo-desktop-service.exe" 0 mihomo_preuninstall_done
  nsExec::ExecToStack '"$INSTDIR\mihomo-desktop-service.exe" --uninstall'
  Pop $0
  Pop $1
  ${If} $0 != 0
    MessageBox MB_ICONSTOP|MB_OK "Unable to remove the Mihomo desktop service. Close Mihomo and retry."
    Abort
  ${EndIf}
  mihomo_preuninstall_done:
!macroend
