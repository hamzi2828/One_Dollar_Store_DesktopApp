@echo off
REM Developer build -> bin-local\, pointed at a backend running on this PC
REM (desktopApp.local.config.json, http://localhost:5000/).
call "%~dp0build.cmd" local
