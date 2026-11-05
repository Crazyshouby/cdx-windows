@echo off
rem Called by Inno Setup (SignTool=signtool, see build.ps1) with the file to sign
rem as %1. The signtool arguments (certificate, timestamp...) come from the
rem environment so that no secret appears on a command line shown in a log.
rem The password of a .pfx comes separately (SIGN_PFX_PASSWORD), given only to the step that builds the installer.
if defined SIGN_PFX_PASSWORD (signtool sign /p "%SIGN_PFX_PASSWORD%" %AL_SIGN_ARGS% %1) else (signtool sign %AL_SIGN_ARGS% %1)
exit /b %errorlevel%
