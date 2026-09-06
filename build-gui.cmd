@echo off
rem Builds the profiler GUI, gui\NapGui.exe, from the repository root. The script that does
rem the work lives next to the GUI sources; this one only forwards to it.
call "%~dp0gui\build-gui.cmd" %*
