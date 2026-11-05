@echo off
rem Appelé par Inno Setup (SignTool=signtool, voir build.ps1) avec le fichier à signer en %1. Les arguments de signtool (certificat,
rem horodatage...) viennent de l'environnement : aucun secret sur une ligne de commande qui finirait dans un journal.
signtool sign %CDX_SIGN_ARGS% %1
exit /b %errorlevel%
