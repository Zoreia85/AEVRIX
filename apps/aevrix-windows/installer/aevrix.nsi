Unicode True
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetOverwrite on
ShowInstDetails show
ShowUninstDetails show

!ifndef PRODUCT_VERSION
  !error "PRODUCT_VERSION must be defined"
!endif
!ifndef FILE_VERSION
  !error "FILE_VERSION must be defined"
!endif
!ifndef PUBLISH_DIR
  !error "PUBLISH_DIR must be defined"
!endif
!ifndef OUTFILE
  !error "OUTFILE must be defined"
!endif

!define PRODUCT_NAME "AEVRIX"
!define PRODUCT_PUBLISHER "AEVRIX"
!define PRODUCT_EXE "AEVRIX.Desktop.exe"
!define PRODUCT_ENGINE_EXE "AEVRIX.EngineHost.exe"
!define APP_REG_KEY "Software\AEVRIX"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AEVRIX"

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\AEVRIX"
InstallDirRegKey HKCU "${APP_REG_KEY}" "InstallDir"

VIProductVersion "${FILE_VERSION}"
VIAddVersionKey /LANG=1033 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1033 "CompanyName" "${PRODUCT_PUBLISHER}"
VIAddVersionKey /LANG=1033 "FileDescription" "AEVRIX Windows Installer"
VIAddVersionKey /LANG=1033 "FileVersion" "${FILE_VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "AEVRIX"

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "WordFunc.nsh"

!insertmacro GetParameters
!insertmacro GetOptions
!insertmacro VersionCompare

!define MUI_ABORTWARNING
!define MUI_ICON "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"
!define MUI_UNICON "${NSISDIR}\Contrib\Graphics\Icons\modern-uninstall.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${PRODUCT_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Iniciar AEVRIX"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "PortugueseBR"
!insertmacro MUI_LANGUAGE "English"

Var ExistingVersion
Var InstallMode
Var Params
Var OptionValue

Function .onInit
  StrCpy $InstallMode "install"
  ${GetParameters} $Params

  ClearErrors
  ${GetOptions} $Params "/REPAIR" $OptionValue
  IfErrors +2 0
    StrCpy $InstallMode "repair"

  ReadRegStr $ExistingVersion HKCU "${UNINSTALL_KEY}" "DisplayVersion"
  StrCmp $ExistingVersion "" done

  ${VersionCompare} "${PRODUCT_VERSION}" "$ExistingVersion" $0
  StrCmp $0 "2" downgrade 0
  StrCmp $0 "0" equal done
  StrCpy $InstallMode "upgrade"
  Goto done

downgrade:
  MessageBox MB_ICONSTOP|MB_OK "Uma versão mais nova do AEVRIX ($ExistingVersion) já está instalada. O downgrade para ${PRODUCT_VERSION} foi bloqueado." /SD IDOK
  SetErrorLevel 1638
  Abort

equal:
  StrCpy $InstallMode "repair"

done:
FunctionEnd

Section "AEVRIX" SEC_MAIN
  SetShellVarContext current
  SetOutPath "$INSTDIR"

  ; Product payload is pre-published and validated by build-installer.ps1.
  File /r "${PUBLISH_DIR}\*.*"

  IfFileExists "$INSTDIR\${PRODUCT_EXE}" +2 0
    Abort "O payload instalado não contém ${PRODUCT_EXE}."
  IfFileExists "$INSTDIR\${PRODUCT_ENGINE_EXE}" +2 0
    Abort "O payload instalado não contém ${PRODUCT_ENGINE_EXE}."

  ; Keep a repair-capable copy of this exact installer beside the product.
  StrCmp $EXEPATH "$INSTDIR\AEVRIX-Setup.exe" +2 0
    CopyFiles /SILENT "$EXEPATH" "$INSTDIR\AEVRIX-Setup.exe"

  CreateDirectory "$SMPROGRAMS\AEVRIX"
  CreateShortcut "$SMPROGRAMS\AEVRIX\AEVRIX.lnk" "$INSTDIR\${PRODUCT_EXE}" "" "$INSTDIR\${PRODUCT_EXE}" 0
  CreateShortcut "$SMPROGRAMS\AEVRIX\Desinstalar AEVRIX.lnk" "$INSTDIR\uninstall.exe"

  WriteRegStr HKCU "${APP_REG_KEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${APP_REG_KEY}" "Version" "${PRODUCT_VERSION}"

  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "AEVRIX"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${PRODUCT_EXE}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "ModifyPath" '"$INSTDIR\AEVRIX-Setup.exe" /REPAIR'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 0
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 0

  WriteUninstaller "$INSTDIR\uninstall.exe"

  DetailPrint "AEVRIX ${PRODUCT_VERSION}: modo $InstallMode concluído."
SectionEnd

Section "Uninstall"
  SetShellVarContext current

  ; Best-effort process shutdown. Failure is tolerated; locked files still make removal fail visibly.
  nsExec::ExecToStack '"$SYSDIR\taskkill.exe" /IM "${PRODUCT_EXE}" /T /F'
  Pop $0
  Pop $1
  nsExec::ExecToStack '"$SYSDIR\taskkill.exe" /IM "${PRODUCT_ENGINE_EXE}" /T /F'
  Pop $0
  Pop $1

  Delete "$SMPROGRAMS\AEVRIX\AEVRIX.lnk"
  Delete "$SMPROGRAMS\AEVRIX\Desinstalar AEVRIX.lnk"
  RMDir "$SMPROGRAMS\AEVRIX"

  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "${APP_REG_KEY}"

  ; Application binaries only. User workspaces/data live under AevrixDataPaths and are preserved.
  RMDir /r "$INSTDIR"
SectionEnd
