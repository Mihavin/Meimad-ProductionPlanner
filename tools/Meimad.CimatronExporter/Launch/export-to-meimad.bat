@echo off
rem Meimad Cimatron export launcher (exporter definition section 12 C).
rem Run it after the NC procedures of one Operation are calculated, from Cimatron's
rem post-processor hook or by hand. It only orchestrates: the exporter reads Cimatron
rem through its API and writes <output>\cimatron-export with COMPLETE written last.
rem
rem   export-to-meimad.bat <Case working folder> <Case name> <Operation number> <post name> <UCS name> [machine id]
rem
rem The package goes to <Case working folder>\Gcode\<Case name>\<Operation number>\cimatron-export,
rem beside the Operation's NC programs, so the Meimad Server can read it from the network folder.

setlocal
if "%~5"=="" (
  echo Usage: %~nx0 ^<Case working folder^> ^<Case name^> ^<Operation number^> ^<post name^> ^<UCS name^> [machine id]
  exit /b 2
)

set "CASE_FOLDER=%~1"
set "CASE_NAME=%~2"
set "OP_NUMBER=%~3"
set "POST_NAME=%~4"
set "PROCEDURE_UCS=%~5"
set "MACHINE_ID=%~6"
set "EXPORTER=%~dp0Meimad.CimatronExporter.exe"

set "MACHINE_ARG="
if not "%MACHINE_ID%"=="" set "MACHINE_ARG=--machine-id "%MACHINE_ID%""

"%EXPORTER%" ^
  --active-cimatron ^
  --output "%CASE_FOLDER%\Gcode\%CASE_NAME%\%OP_NUMBER%" ^
  --post "%POST_NAME%" ^
  --ucs "%PROCEDURE_UCS%" ^
  --operation "%OP_NUMBER%" ^
  --part-number "%CASE_NAME%" ^
  %MACHINE_ARG%

set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" (
  echo.
  echo The Meimad export did not complete ^(exit code %RESULT%^). See last-export-report.txt in the output folder.
  pause
)
exit /b %RESULT%
