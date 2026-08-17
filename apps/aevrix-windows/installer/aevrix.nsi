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
!ifndef WASDK_RUNTIME_DIR
  !error "WASDK_RUNTIME_DIR must be defined"
!endif
!ifndef WASDK_RUNTIME_HELPER
  !error "WASDK_RUNTIME_HELPER must be defined"
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
Var AvaInterruptHoldMs

Function .onInit
  StrCpy $InstallMode "install"
  StrCpy $AvaInterruptHoldMs ""
  ${GetParameters} $Params

  ; AVA-only deterministic interruption hook. Production installs never pass this switch.
  ; Accept exactly the harness value so arbitrary delays cannot be injected accidentally.
  ClearErrors
  ${GetOptions} $Params "/AVAINTERRUPTHOLD=" $AvaInterruptHoldMs
  IfErrors ava_hold_done 0
  StrCmp $AvaInterruptHoldMs "15000" ava_hold_done ava_hold_invalid

ava_hold_invalid:
  SetErrorLevel 87
  Abort "Parâmetro AVA de interrupção inválido."

ava_hold_done:
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

  ; Fail closed before mutating the AEVRIX product surface. The Microsoft Windows App Runtime
  ; is a shared per-user prerequisite and is intentionally preserved by AEVRIX uninstall.
  InitPluginsDir
  SetOutPath "$PLUGINSDIR\wasdk-runtime"
  File /oname=install-windows-app-runtime.ps1 "${WASDK_RUNTIME_HELPER}"
  File /oname=Microsoft.WindowsAppRuntime.2.msix "${WASDK_RUNTIME_DIR}\Microsoft.WindowsAppRuntime.2.msix"
  File /oname=Microsoft.WindowsAppRuntime.Main.2.msix "${WASDK_RUNTIME_DIR}\Microsoft.WindowsAppRuntime.Main.2.msix"
  File /oname=Microsoft.WindowsAppRuntime.Singleton.2.msix "${WASDK_RUNTIME_DIR}\Microsoft.WindowsAppRuntime.Singleton.2.msix"
  File /oname=Microsoft.WindowsAppRuntime.DDLM.2.msix "${WASDK_RUNTIME_DIR}\Microsoft.WindowsAppRuntime.DDLM.2.msix"

  DetailPrint "Verificando pré-requisito Microsoft Windows App Runtime 2.3.1..."
  nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\wasdk-runtime\install-windows-app-runtime.ps1" -RuntimeRoot "$PLUGINSDIR\wasdk-runtime"'
  Pop $0
  Pop $1
  StrCmp $0 "0" runtime_ready 0
    DetailPrint "Falha ao preparar o Windows App Runtime. Código: $0"
    DetailPrint "$1"
    SetErrorLevel $0
    Abort "O AEVRIX não foi instalado porque o pré-requisito Microsoft Windows App Runtime não pôde ser validado/instalado."

runtime_ready:
  DetailPrint "Microsoft Windows App Runtime validado."

  ; SetOutPath creates the first AEVRIX-owned installation surface. The AVA harness may request
  ; one exact 15-second hold here so it can terminate the installer deterministically and then
  ; prove recovery. Normal production invocation has no hold and follows immediately to payload.
  SetOutPath "$INSTDIR"
  StrCmp $AvaInterruptHoldMs "" payload_begin 0
    DetailPrint "AVA: janela determinística de interrupção iniciada ($AvaInterruptHoldMs ms)."
    Sleep $AvaInterruptHoldMs

payload_begin:
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
  ; The Microsoft Windows App Runtime prerequisite is shared and intentionally remains installed.
  RMDir /r "$INSTDIR"
SectionEnd