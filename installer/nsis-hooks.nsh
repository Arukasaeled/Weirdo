; Keep application binaries separate from the persistent Weirdo data directory.
!define MUI_CUSTOMFUNCTION_GUIINIT WeirdoDefaultInstallDirectory

Function WeirdoDefaultInstallDirectory
  StrCmp $INSTDIR "$LOCALAPPDATA\Weirdo" 0 done
  StrCpy $INSTDIR "$LOCALAPPDATA\Programs\Weirdo"
  done:
FunctionEnd

!macro NSIS_HOOK_PREINSTALL
  ; Silent installs skip the GUI callback.
  StrCmp $INSTDIR "$LOCALAPPDATA\Weirdo" 0 +2
  StrCpy $INSTDIR "$LOCALAPPDATA\Programs\Weirdo"
  SetOutPath $INSTDIR
!macroend
